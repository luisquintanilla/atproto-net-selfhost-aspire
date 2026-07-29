using AtProto.Firehose;

namespace AtProto.Relay;

/// <summary>
/// The relay engine. For each upstream host it opens a resilient <c>subscribeRepos</c> subscription
/// (BCL pull ingest with reconnect/resume), validates commits, assigns a <b>global</b>
/// monotonic seq, and re-emits the frame through a shared <see cref="FirehoseBroadcaster"/> that
/// downstream consumers (our AppView) subscribe to. Per-host upstream cursors and the global seq are
/// persisted so a restart resumes without gaps; re-emit is at-least-once, and the rev-monotonic
/// check plus the AppView's idempotent latest-wins store absorb the rare duplicate.
/// </summary>
public sealed class RelayService : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly HostRegistry _registry;
    private readonly ILogger<RelayService> _logger;
    private readonly string _cursorDir;
    private readonly FileCursorStore _globalSeqStore;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _crawlGate = new();
    private readonly HashSet<string> _crawling = new(StringComparer.Ordinal);
    private bool _disposed;

    public RelayService(RelayOptions options, HostRegistry registry, ILogger<RelayService> logger)
    {
        _options = options;
        _registry = registry;
        _logger = logger;
        _cursorDir = options.CursorDir ?? Path.Combine(AppContext.BaseDirectory, "relay-cursors");
        Directory.CreateDirectory(_cursorDir);
        _globalSeqStore = new FileCursorStore(Path.Combine(_cursorDir, "global-seq.txt"));
        Firehose = new FirehoseBroadcaster(initialSeq: ReadSeq(Path.Combine(_cursorDir, "global-seq.txt")));
    }

    /// <summary>The relay's aggregated firehose (global seq), served by <c>subscribeRepos</c>.</summary>
    public FirehoseBroadcaster Firehose { get; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() =>
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* already torn down */ }
        });
        foreach (string upstream in _options.Upstreams)
            StartCrawler(NormalizeUpstream(upstream));
        return Task.CompletedTask;
    }

    /// <summary>Register and begin crawling a host (the <c>com.atproto.sync.requestCrawl</c> path).</summary>
    public void Crawl(string hostname) => StartCrawler(NormalizeUpstream(hostname));

    private void StartCrawler(string url)
    {
        lock (_crawlGate)
        {
            if (!_crawling.Add(url))
                return; // already crawling this host
        }
        _registry.GetOrAddHost(url);
        _ = Task.Run(() => CrawlLoop(url, _cts.Token));
    }

    private async Task CrawlLoop(string url, CancellationToken ct)
    {
        HostState host = _registry.GetOrAddHost(url);
        var cursorStore = new FileCursorStore(Path.Combine(_cursorDir, HostFile(url)));
        // First crawl (no persisted cursor) replays the upstream's retained backfill from 0 so a
        // fresh relay picks up records already written; a restart resumes from the saved seq.
        long cursor = await cursorStore.GetAsync(ct) ?? 0;

        var client = FirehoseClient.ForUrl(url);
        var options = new FirehoseOptions
        {
            OnReconnect = ex =>
            {
                host.Status = "reconnecting";
                host.LastError = ex.Message;
                _logger.LogWarning("relay: upstream {Host} dropped, reconnecting: {Message}", url, ex.Message);
            },
        };

        host.Status = "active";
        host.ConnectedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("relay: crawling {Host}, resume {Cursor}", url, cursor);

        try
        {
            await foreach (RepoEvent ev in client.SubscribeAsync(cursor, options, ct))
            {
                host.Status = "active";
                await ProcessAsync(url, host, ev, ct);
                host.LastUpstreamSeq = ev.Seq;
                await cursorStore.SetAsync(ev.Seq, ct); // checkpoint AFTER publish (at-least-once)
            }
        }
        catch (OperationCanceledException)
        {
            host.Status = "stopped";
        }
        catch (Exception ex)
        {
            host.Status = "errored";
            host.LastError = ex.Message;
            _logger.LogError(ex, "relay: crawl loop for {Host} failed", url);
        }
    }

    private async Task ProcessAsync(string url, HostState host, RepoEvent ev, CancellationToken ct)
    {
        if (ev is RepoCommitEvent commit)
        {
            if (string.IsNullOrEmpty(commit.Did))
            {
                _logger.LogDebug("relay: dropping commit from {Host} with empty did", url);
                return;
            }
            if (!_registry.IsRevMonotonic(commit.Did, commit.Rev))
            {
                _logger.LogDebug("relay: dropping non-monotonic rev {Rev} for {Did}", commit.Rev, commit.Did);
                return;
            }
            if (_options.StrictCommitValidation && !RelayCommitValidator.TryValidate(commit, out string error))
            {
                _logger.LogDebug("relay: dropping invalid commit {Commit} for {Did}: {Error}", commit.Commit, commit.Did, error);
                return;
            }
        }

        Func<long, byte[]>? encode = ReEncoder(ev);
        if (encode is null)
            return; // event type we don't re-emit (e.g. unknown); upstream cursor still advances

        long globalSeq = Firehose.PublishNext(encode);
        await _globalSeqStore.SetAsync(globalSeq, ct);

        switch (ev)
        {
            case RepoCommitEvent c:
                _registry.RecordCommit(c.Did, url, c.Rev, globalSeq);
                break;
            case RepoAccountEvent a:
                _registry.RecordAccount(a.Did, url, a.Active);
                break;
        }
    }

    /// <summary>Re-encode a decoded upstream event with a new global seq (null = don't re-emit).</summary>
    private static Func<long, byte[]>? ReEncoder(RepoEvent ev) => ev switch
    {
        RepoCommitEvent c => seq => FrameEncoder.EncodeCommit(c with { Seq = seq }),
        RepoIdentityEvent i => seq => FrameEncoder.EncodeIdentity(i with { Seq = seq }),
        RepoAccountEvent a => seq => FrameEncoder.EncodeAccount(a with { Seq = seq }),
        RepoSyncEvent s => seq => FrameEncoder.EncodeSync(s with { Seq = seq }),
        _ => null,
    };

    /// <summary>Normalize an upstream identifier to a base URL (default https for a bare hostname).</summary>
    private static string NormalizeUpstream(string hostname)
    {
        string h = hostname.Trim().TrimEnd('/');
        return h.Contains("://", StringComparison.Ordinal) ? h : "https://" + h;
    }

    private static string HostFile(string url)
    {
        var sb = new System.Text.StringBuilder("host-");
        foreach (char ch in url)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.Append(".txt").ToString();
    }

    private static long ReadSeq(string path)
    {
        try
        {
            return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out long seq) ? seq : 0;
        }
        catch
        {
            return 0;
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose();
    }
}
