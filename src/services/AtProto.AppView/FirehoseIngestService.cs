using System.Reactive.Subjects;
using AtProto.Car;
using AtProto.Cbor;
using AtProto.Firehose;

namespace AtProto.AppView;

/// <summary>
/// Pull-based firehose ingest (BCL <c>await foreach</c> over the bounded-channel client), mapping
/// commit ops for the configured collection into <see cref="StatusUpdate"/>s pushed onto a hot
/// <see cref="Subject{T}"/>. The Rx projection subscribes to that subject; a slow projection
/// backpressures this loop (synchronous OnNext), preserving ordered, cursor-checkpointed ingest.
/// </summary>
public sealed class FirehoseIngestService(
    PresenceProjection projection,
    IConfiguration config,
    ILogger<FirehoseIngestService> logger) : BackgroundService
{
    private readonly Subject<StatusUpdate> _updates = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string relayHost = config["Firehose:RelayHost"] ?? "relay1.us-west.bsky.network";
        string? firehoseUrl = config["Firehose:Url"];
        string collection = config["AppView:Collection"] ?? "place.selfhost.status";
        string cursorPath = config["Firehose:CursorPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "appview-cursor.txt");

        var cursorStore = new FileCursorStore(cursorPath);
        long? startCursor = await cursorStore.GetAsync(stoppingToken);

        using IDisposable pipeline = projection.Connect(_updates);

        // Prefer an explicit firehose URL (our self-hosted PDS/Relay, injected by Aspire); otherwise
        // fall back to a public relay host.
        var client = firehoseUrl is not null
            ? FirehoseClient.ForUrl(firehoseUrl)
            : FirehoseClient.ForRelay(relayHost);
        var options = new FirehoseOptions
        {
            OnReconnect = ex => logger.LogWarning("firehose dropped, reconnecting: {Message}", ex.Message),
        };

        logger.LogInformation(
            "AppView ingest: source {Source}, collection '{Collection}', resume {Cursor}",
            firehoseUrl ?? relayHost, collection, startCursor?.ToString() ?? "none");

        await foreach (RepoEvent ev in client.SubscribeAsync(startCursor, options, stoppingToken))
        {
            if (ev is RepoCommitEvent commit)
            {
                foreach (RepoOp op in commit.Ops)
                {
                    if (op.Collection != collection)
                        continue;
                    if (op.Action is RepoOpAction.Unknown)
                        continue;

                    _updates.OnNext(new StatusUpdate(
                        Did: commit.Did,
                        Collection: op.Collection,
                        Rkey: op.Rkey,
                        Cid: op.Cid?.ToString(),
                        Status: ReadStatus(commit, op),
                        Seq: commit.Seq,
                        UpdatedAt: commit.Time ?? DateTimeOffset.UtcNow,
                        Action: op.Action));
                }
            }

            await cursorStore.SetAsync(ev.Seq, stoppingToken); // checkpoint after processing
        }
    }

    /// <summary>
    /// Pull the human-facing <c>status</c> emoji out of the commit's CAR slice for a create/update
    /// op (deletes carry no record). Best-effort: a decode failure just yields a null status so the
    /// board still lists the account.
    /// </summary>
    private string? ReadStatus(RepoCommitEvent commit, RepoOp op)
    {
        if (op.Cid is not { } recordCid || op.Action == RepoOpAction.Delete || commit.Blocks.Length == 0)
            return null;
        try
        {
            CarArchive archive = CarReader.Read(commit.Blocks);
            if (!archive.TryGetBlock(recordCid, out byte[] record))
                return null;
            return DagCbor.Decode(record).Field("status") as string;
        }
        catch (Exception ex)
        {
            logger.LogDebug("status decode failed for {Did}/{Rkey}: {Message}", commit.Did, op.Rkey, ex.Message);
            return null;
        }
    }

    public override void Dispose()
    {
        _updates.Dispose();
        base.Dispose();
    }
}
