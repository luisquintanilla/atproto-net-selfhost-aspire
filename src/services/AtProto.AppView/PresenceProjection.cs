using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace AtProto.AppView;

/// <summary>
/// The Rx.NET projection layer (this is the ONLY project that references System.Reactive — see
/// plan §2a). Per-account latest-wins is materialized in <see cref="PresenceStore"/> (the
/// <c>GroupBy(did).Replay(1)</c> read model); Rx owns the time domain, where hand-rolling timers
/// is painful: <see cref="Observable.Buffer{TSource}(IObservable{TSource}, TimeSpan)"/> for a
/// once-per-second activity/rate stream and a sampled live-board snapshot for push.
/// </summary>
public sealed class PresenceProjection(PresenceStore store) : IDisposable
{
    private readonly BehaviorSubject<BoardStats> _stats =
        new(new BoardStats(0, 0, 0, 0, DateTimeOffset.UtcNow));

    public PresenceStore Store { get; } = store;

    /// <summary>A once-per-second stream of board statistics (rate is updates in the last window).</summary>
    public IObservable<BoardStats> Stats => _stats;

    /// <summary>The most recent board statistics (for a point-in-time query endpoint).</summary>
    public BoardStats Current => _stats.Value;

    /// <summary>A once-per-second snapshot of the top <paramref name="limit"/> accounts for live push.</summary>
    public IObservable<IReadOnlyList<PresenceEntry>> LiveBoard(int limit) =>
        _stats.Select(_ => Store.Snapshot(limit));

    /// <summary>
    /// Wire an update stream into the projection. The store receives every update (authoritative);
    /// a 1-second Rx window drives the derived stats. Returns a handle to tear the pipeline down.
    /// </summary>
    public IDisposable Connect(IObservable<StatusUpdate> updates)
    {
        IDisposable apply = updates.Subscribe(u => Store.Apply(u));

        IDisposable stats = updates
            .Buffer(TimeSpan.FromSeconds(1))
            .Where(batch => batch.Count > 0)
            .Subscribe(batch => _stats.OnNext(new BoardStats(
                TotalUpdates: Store.TotalUpdates,
                UniqueUsers: Store.UniqueUsers,
                UpdatesPerSecond: batch.Count,
                LastSeq: Store.LastSeq,
                At: DateTimeOffset.UtcNow)));

        return new CompositeDisposable(apply, stats);
    }

    public void Dispose() => _stats.Dispose();
}
