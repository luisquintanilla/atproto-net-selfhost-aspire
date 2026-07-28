using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AtProto.Firehose;

/// <summary>Tuning for a resilient firehose subscription.</summary>
public sealed record FirehoseOptions
{
    /// <summary>Bounded buffer between the socket reader and the consumer (backpressure point).</summary>
    public int ChannelCapacity { get; init; } = 1024;

    /// <summary>Initial reconnect backoff (doubles up to <see cref="MaxReconnectDelay"/>).</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Invoked when a connection drops, before the backoff wait.</summary>
    public Action<Exception>? OnReconnect { get; init; }
}

/// <summary>
/// A <c>com.atproto.sync.subscribeRepos</c> client. The ingest path is pull-based: the socket
/// reader produces <see cref="RepoEvent"/>s into a bounded <see cref="Channel"/>
/// (<see cref="BoundedChannelFullMode.Wait"/>) so a slow consumer applies backpressure to the
/// socket. <see cref="SubscribeAsync"/> reconnects on drop, resuming from the last seen seq.
/// A BCL <see cref="IObservable{T}"/> seam is available via
/// <see cref="FirehoseObservable.ToObservable{T}"/> — no System.Reactive dependency.
/// </summary>
public sealed class FirehoseClient
{
    private const string SubscribePath = "/xrpc/com.atproto.sync.subscribeRepos";

    private readonly Uri _endpoint;

    public FirehoseClient(Uri endpoint) => _endpoint = endpoint;

    /// <summary>Build a client for a relay host (e.g. <c>relay1.us-west.bsky.network</c>).</summary>
    public static FirehoseClient ForRelay(string host)
    {
        string trimmed = host.TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "wss://" + trimmed;
        return new FirehoseClient(new Uri(trimmed + SubscribePath));
    }

    /// <summary>
    /// Build a client from a service base URL of any scheme. <c>http</c>/<c>https</c> are normalized
    /// to <c>ws</c>/<c>wss</c> and the <c>subscribeRepos</c> path is appended — so an Aspire
    /// <c>http://</c> endpoint reference for a self-hosted PDS/Relay just works.
    /// </summary>
    public static FirehoseClient ForUrl(string baseUrl)
    {
        string u = baseUrl.TrimEnd('/');
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            u = "ws://" + u["http://".Length..];
        else if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "wss://" + u["https://".Length..];
        else if (!u.Contains("://", StringComparison.Ordinal))
            u = "wss://" + u;
        return new FirehoseClient(new Uri(u + SubscribePath));
    }

    /// <summary>
    /// Subscribe with automatic reconnect/resume. Yields events in seq order across reconnects;
    /// the resume cursor advances as events are produced.
    /// </summary>
    public async IAsyncEnumerable<RepoEvent> SubscribeAsync(
        long? startCursor = null,
        FirehoseOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new FirehoseOptions();
        var channel = Channel.CreateBounded<RepoEvent>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task producer = Task.Run(() => ProduceAsync(channel.Writer, startCursor, options, cts.Token), cts.Token);

        try
        {
            await foreach (RepoEvent ev in channel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                yield return ev;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try { await producer.ConfigureAwait(false); }
            catch { /* producer cancellation/teardown */ }
        }
    }

    private async Task ProduceAsync(
        ChannelWriter<RepoEvent> writer, long? startCursor, FirehoseOptions options, CancellationToken ct)
    {
        long? cursor = startCursor;
        TimeSpan delay = options.ReconnectDelay;
        Exception? fatal = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (RepoEvent ev in StreamOnceAsync(cursor, ct).ConfigureAwait(false))
                    {
                        cursor = ev.Seq;
                        await writer.WriteAsync(ev, ct).ConfigureAwait(false);
                        delay = options.ReconnectDelay; // progress resets backoff
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    options.OnReconnect?.Invoke(ex);
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    delay = TimeSpan.FromMilliseconds(
                        Math.Min(delay.TotalMilliseconds * 2, options.MaxReconnectDelay.TotalMilliseconds));
                }
            }
        }
        catch (Exception ex)
        {
            fatal = ex;
        }
        finally
        {
            writer.TryComplete(fatal);
        }
    }

    /// <summary>Stream events over a single connection (no reconnect); throws if the socket drops.</summary>
    public async IAsyncEnumerable<RepoEvent> StreamOnceAsync(
        long? cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(BuildUrl(cursor), cancellationToken).ConfigureAwait(false);

        await foreach (byte[] frame in ReadFramesAsync(ws, cancellationToken).ConfigureAwait(false))
        {
            RepoEvent? ev;
            try
            {
                ev = FrameDecoder.Decode(frame);
            }
            catch (FirehoseErrorException)
            {
                throw; // server-signalled error: drop and let the caller reconnect
            }
            catch
            {
                continue; // skip an individual malformed frame
            }

            if (ev is not null)
                yield return ev;
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadFramesAsync(
        ClientWebSocket ws, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    yield break;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            yield return message.ToArray();
        }
    }

    private Uri BuildUrl(long? cursor) =>
        cursor is long c ? new Uri($"{_endpoint}?cursor={c}") : _endpoint;
}
