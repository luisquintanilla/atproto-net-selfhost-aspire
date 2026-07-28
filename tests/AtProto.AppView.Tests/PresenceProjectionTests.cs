using System.Reactive.Subjects;
using AtProto.AppView;
using AtProto.Firehose;

namespace AtProto.AppView.Tests;

/// <summary>
/// The Rx wiring seam: <see cref="PresenceProjection.Connect"/> subscribes the store to the update
/// stream synchronously, so a pushed update is reflected immediately (the 1-second stats window is
/// time-driven and not asserted here — this pins the deterministic apply path only).
/// </summary>
public class PresenceProjectionTests
{
    private static StatusUpdate Update(string did, long seq, RepoOpAction action = RepoOpAction.Create) =>
        new(did, "place.selfhost.status", "self", $"cid-{seq}", seq, DateTimeOffset.UnixEpoch.AddSeconds(seq), action);

    [Fact]
    public void Connect_applies_pushed_updates_to_the_store()
    {
        var store = new PresenceStore();
        using var projection = new PresenceProjection(store);
        var source = new Subject<StatusUpdate>();

        using IDisposable pipeline = projection.Connect(source);

        source.OnNext(Update("did:web:alice", 1));
        source.OnNext(Update("did:web:bob", 2));
        source.OnNext(Update("did:web:alice", 3));

        Assert.Equal(2, store.UniqueUsers);
        Assert.Equal(3, store.Get("did:web:alice")!.Seq);
        Assert.Same(store, projection.Store);
    }

    [Fact]
    public void Disposing_the_pipeline_stops_further_application()
    {
        var store = new PresenceStore();
        using var projection = new PresenceProjection(store);
        var source = new Subject<StatusUpdate>();

        IDisposable pipeline = projection.Connect(source);
        source.OnNext(Update("did:web:alice", 1));
        pipeline.Dispose();
        source.OnNext(Update("did:web:alice", 2)); // ignored: pipeline torn down

        Assert.Equal(1, store.Get("did:web:alice")!.Seq);
    }
}
