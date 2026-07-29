using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.AspNetCore.SignalR;

namespace AtProto.AppView;

/// <summary>
/// The final hop that closes the reactive spine: <c>firehose IObservable → Rx projection → IObserver
/// → browser</c>. Subscribes to the projection's live streams and fans them out to every connected
/// board over SignalR — <c>"presence"</c> (per-entry deltas, coalesced to a modest rate so a
/// firehose-volume source can't flood clients) and <c>"stats"</c> (the once-per-second windowed
/// activity). Pure fan-out: it never touches the store or the ingest loop, so it can't apply
/// backpressure to ingest (a slow/absent client just misses deltas and re-seeds on reconnect).
/// </summary>
public sealed class PresenceBroadcaster(
    PresenceProjection projection,
    IHubContext<PresenceHub> hub,
    ILogger<PresenceBroadcaster> logger) : IHostedService
{
    private IDisposable? _subscriptions;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Coalesce per-entry changes into at most ~4 pushes/sec; at self-host volume each batch is
        // usually a single status landing, so the board still updates within a quarter second.
        IDisposable deltas = projection.Changes
            .Buffer(TimeSpan.FromMilliseconds(250))
            .Where(batch => batch.Count > 0)
            .Subscribe(batch => Broadcast("presence", batch.Select(ToDelta).ToArray()));

        IDisposable stats = projection.Stats
            .Subscribe(snapshot => Broadcast("stats", ToStats(snapshot)));

        _subscriptions = new CompositeDisposable(deltas, stats);
        logger.LogInformation("Presence broadcaster live: pushing board deltas + stats over SignalR");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriptions?.Dispose();
        return Task.CompletedTask;
    }

    private void Broadcast(string method, object payload)
    {
        // Fire-and-forget onto the hub: a send failure to one client must not break the pipeline.
        _ = hub.Clients.All.SendAsync(method, payload).ContinueWith(
            t => logger.LogDebug("SignalR {Method} send failed: {Message}", method, t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // Lowercase-keyed DTOs so the browser payload is independent of the SignalR naming policy.
    private static object ToDelta(PresenceChange change) => new
    {
        did = change.Did,
        rkey = change.Entry?.Rkey,
        cid = change.Entry?.Cid,
        status = change.Entry?.Status,
        seq = change.Seq,
        updatedAt = change.UpdatedAt,
        removed = change.Removed,
    };

    private static object ToStats(BoardStats stats) => new
    {
        totalUpdates = stats.TotalUpdates,
        uniqueUsers = stats.UniqueUsers,
        updatesPerSecond = stats.UpdatesPerSecond,
        lastSeq = stats.LastSeq,
        at = stats.At,
    };
}
