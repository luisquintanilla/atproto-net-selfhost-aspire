using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AtProto.Firehose;

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

    private readonly Subject<PresenceChange> _changes = new();

    public PresenceStore Store { get; } = store;

    /// <summary>A once-per-second stream of board statistics (rate is updates in the last window).</summary>
    public IObservable<BoardStats> Stats => _stats;

    /// <summary>
    /// A live, per-update stream of applied changes (the resulting latest-wins entry, or a delete).
    /// Drives incremental board pushes to clients — the reactive read model surfaced as an event feed.
    /// </summary>
    public IObservable<PresenceChange> Changes => _changes;

    /// <summary>The most recent board statistics (for a point-in-time query endpoint).</summary>
    public BoardStats Current => _stats.Value;

    /// <summary>A once-per-second snapshot of the top <paramref name="limit"/> accounts for live push.</summary>
    public IObservable<IReadOnlyList<PresenceEntry>> LiveBoard(int limit) =>
        _stats.Select(_ => Store.Snapshot(limit));

    /// <summary>
    /// Wire an update stream into the projection. Each update is applied to the store exactly once
    /// (authoritative, latest-wins) and the resulting change is republished on <see cref="Changes"/>;
    /// a 1-second Rx window over those changes drives the derived stats. Both the stats window and the
    /// SignalR broadcaster observe <see cref="Changes"/>. Returns a handle to tear the pipeline down.
    /// </summary>
    public IDisposable Connect(IObservable<StatusUpdate> updates)
    {
        IDisposable apply = updates.Subscribe(u =>
        {
            PresenceEntry? entry = Store.Apply(u);
            _changes.OnNext(new PresenceChange(u.Did, entry, u.Action == RepoOpAction.Delete, u.Seq, u.UpdatedAt));
        });

        IDisposable stats = _changes
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

    public void Dispose()
    {
        _stats.Dispose();
        _changes.Dispose();
    }
}
