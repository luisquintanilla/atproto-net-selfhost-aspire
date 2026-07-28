using System.Collections.Concurrent;
using AtProto.Firehose;

namespace AtProto.AppView;

/// <summary>
/// The materialized "latest status per account" read model — conceptually the result of
/// <c>GroupBy(did).Replay(1)</c>, kept as a concurrent map so it stays leak-free at public-firehose
/// volume. Updates are latest-wins by seq; deletes remove the account. Pure and deterministic
/// (no Rx), so latest-wins correctness is unit-testable without timing.
/// </summary>
public sealed class PresenceStore
{
    private readonly ConcurrentDictionary<string, PresenceEntry> _latest = new(StringComparer.Ordinal);
    private long _totalUpdates;
    private long _lastSeq;

    public int UniqueUsers => _latest.Count;
    public long TotalUpdates => Interlocked.Read(ref _totalUpdates);
    public long LastSeq => Interlocked.Read(ref _lastSeq);

    /// <summary>Apply one update; returns the resulting entry (or null if it was a delete/no-op).</summary>
    public PresenceEntry? Apply(StatusUpdate update)
    {
        Interlocked.Increment(ref _totalUpdates);
        InterlockedMax(ref _lastSeq, update.Seq);

        if (update.Action == RepoOpAction.Delete)
        {
            _latest.TryRemove(update.Did, out _);
            return null;
        }

        var candidate = new PresenceEntry(update.Did, update.Rkey, update.Cid, update.Seq, update.UpdatedAt);
        return _latest.AddOrUpdate(
            update.Did,
            candidate,
            (_, existing) => update.Seq >= existing.Seq ? candidate : existing);
    }

    public PresenceEntry? Get(string did) => _latest.TryGetValue(did, out PresenceEntry? e) ? e : null;

    /// <summary>Most-recently-updated accounts first.</summary>
    public IReadOnlyList<PresenceEntry> Snapshot(int limit) =>
        _latest.Values.OrderByDescending(e => e.Seq).Take(limit).ToList();

    private static void InterlockedMax(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long prior = Interlocked.CompareExchange(ref target, value, current);
            if (prior == current)
                return;
            current = prior;
        }
    }
}
