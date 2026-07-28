using AtProto.AppView;
using AtProto.Firehose;

namespace AtProto.AppView.Tests;

/// <summary>
/// Deterministic latest-wins correctness for the presence read model. No Rx, no timing — the store
/// is the authoritative <c>GroupBy(did).Replay(1)</c> materialization, so these assertions pin the
/// exact behavior the M2 acceptance depends on (latest-wins by seq, out-of-order safe, delete removes).
/// </summary>
public class PresenceStoreTests
{
    private static StatusUpdate Update(string did, long seq, RepoOpAction action = RepoOpAction.Create) =>
        new(
            Did: did,
            Collection: "place.selfhost.status",
            Rkey: "self",
            Cid: $"cid-{seq}",
            Seq: seq,
            UpdatedAt: DateTimeOffset.UnixEpoch.AddSeconds(seq),
            Action: action);

    [Fact]
    public void Apply_keeps_highest_seq_per_did()
    {
        var store = new PresenceStore();

        store.Apply(Update("did:web:alice", 10));
        store.Apply(Update("did:web:alice", 30));
        store.Apply(Update("did:web:alice", 20)); // out-of-order, must NOT overwrite the newer seq

        PresenceEntry? alice = store.Get("did:web:alice");
        Assert.NotNull(alice);
        Assert.Equal(30, alice!.Seq);
        Assert.Equal("cid-30", alice.Cid);
        Assert.Equal(1, store.UniqueUsers);
    }

    [Fact]
    public void Apply_tracks_multiple_accounts_independently()
    {
        var store = new PresenceStore();

        store.Apply(Update("did:web:alice", 1));
        store.Apply(Update("did:web:bob", 2));
        store.Apply(Update("did:web:alice", 3));

        Assert.Equal(2, store.UniqueUsers);
        Assert.Equal(3, store.Get("did:web:alice")!.Seq);
        Assert.Equal(2, store.Get("did:web:bob")!.Seq);
        Assert.Equal(3, store.TotalUpdates);
        Assert.Equal(3, store.LastSeq);
    }

    [Fact]
    public void Delete_removes_the_account()
    {
        var store = new PresenceStore();

        store.Apply(Update("did:web:alice", 5));
        Assert.NotNull(store.Get("did:web:alice"));

        PresenceEntry? result = store.Apply(Update("did:web:alice", 6, RepoOpAction.Delete));

        Assert.Null(result);
        Assert.Null(store.Get("did:web:alice"));
        Assert.Equal(0, store.UniqueUsers);
        Assert.Equal(6, store.LastSeq); // seq still advances even on delete
    }

    [Fact]
    public void Snapshot_orders_by_seq_descending_and_respects_limit()
    {
        var store = new PresenceStore();
        for (int i = 1; i <= 5; i++)
            store.Apply(Update($"did:web:user{i}", i));

        IReadOnlyList<PresenceEntry> top3 = store.Snapshot(3);

        Assert.Equal(3, top3.Count);
        Assert.Equal(new long[] { 5, 4, 3 }, top3.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Equal_seq_update_wins_so_reprocessed_events_are_idempotent()
    {
        var store = new PresenceStore();

        store.Apply(Update("did:web:alice", 7));
        // Re-delivery of the same seq after a cursor replay must be safe (>= keeps latest form).
        PresenceEntry? replayed = store.Apply(Update("did:web:alice", 7));

        Assert.NotNull(replayed);
        Assert.Equal(7, replayed!.Seq);
        Assert.Equal(1, store.UniqueUsers);
    }
}
