using AtProto.AnalyticsView;

namespace AtProto.AnalyticsView.Tests;

// Exercises the DuckDB analytics store end to end against a real in-memory DuckDB instance (which
// also proves the bundled native library loads and runs on this platform). Drives a small set of
// synthetic commits, then asserts the OLAP aggregates the presence board cannot answer: totals,
// per-collection and per-action breakdowns, top-N repos, distinct-repo counts, and minute buckets.
public sealed class AnalyticsStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static AnalyticsStore Seed()
    {
        var store = new AnalyticsStore(":memory:");

        store.RecordCommit(1, "did:plc:alice", Base, new[]
        {
            new AnalyticsOp("place.selfhost.status", "self", "create", "bafyA1"),
            new AnalyticsOp("app.bsky.feed.post", "p1", "create", "bafyA2"),
        });
        store.RecordCommit(2, "did:plc:bob", Base.AddMinutes(1), new[]
        {
            new AnalyticsOp("app.bsky.feed.post", "p1", "create", "bafyB1"),
            new AnalyticsOp("app.bsky.feed.like", "l1", "create", "bafyB2"),
            new AnalyticsOp("app.bsky.feed.like", "l2", "create", "bafyB3"),
        });
        store.RecordCommit(3, "did:plc:alice", Base.AddMinutes(1).AddSeconds(10), new[]
        {
            new AnalyticsOp("place.selfhost.status", "self", "update", "bafyA3"),
        });
        store.RecordCommit(4, "did:plc:carol", Base.AddMinutes(2), new[]
        {
            new AnalyticsOp("app.bsky.feed.post", "p9", "create", "bafyC1"),
            new AnalyticsOp("app.bsky.feed.post", "p9", "delete", null),
        });

        return store;
    }

    [Fact]
    public void Total_counts_every_op()
    {
        using AnalyticsStore store = Seed();
        Assert.Equal(8, store.Total());
    }

    [Fact]
    public void Stats_break_down_by_collection_and_action()
    {
        using AnalyticsStore store = Seed();
        StatsResult stats = store.GetStats();

        Assert.Equal(8, stats.Total);

        CollectionCount top = stats.ByCollection[0];
        Assert.Equal("app.bsky.feed.post", top.Collection);
        Assert.Equal(4, top.Count);
        Assert.Equal(3, stats.ByCollection.Count);

        Assert.Equal(6, stats.ByAction.Single(a => a.Action == "create").Count);
        Assert.Equal(1, stats.ByAction.Single(a => a.Action == "update").Count);
        Assert.Equal(1, stats.ByAction.Single(a => a.Action == "delete").Count);
    }

    [Fact]
    public void TopRepos_rank_by_event_count()
    {
        using AnalyticsStore store = Seed();
        IReadOnlyList<RepoCount> repos = store.GetTopRepos(10);

        Assert.Equal(3, repos.Count);
        Assert.Equal(3, repos[0].Count);
        Assert.Contains(repos, r => r.Did == "did:plc:alice" && r.Count == 3);
        Assert.Contains(repos, r => r.Did == "did:plc:bob" && r.Count == 3);
        Assert.Contains(repos, r => r.Did == "did:plc:carol" && r.Count == 2);
    }

    [Fact]
    public void Collections_report_distinct_repos()
    {
        using AnalyticsStore store = Seed();
        IReadOnlyList<CollectionBreakdown> collections = store.GetCollections();

        CollectionBreakdown post = collections.Single(c => c.Collection == "app.bsky.feed.post");
        Assert.Equal(4, post.Count);
        Assert.Equal(3, post.Repos); // alice, bob, carol each posted

        CollectionBreakdown status = collections.Single(c => c.Collection == "place.selfhost.status");
        Assert.Equal(2, status.Count);
        Assert.Equal(1, status.Repos); // alice only
    }

    [Fact]
    public void Activity_buckets_by_minute_oldest_first()
    {
        using AnalyticsStore store = Seed();
        IReadOnlyList<ActivityBucket> buckets = store.GetActivity("minute", 60);

        Assert.Equal(3, buckets.Count);
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(4, buckets[1].Count);
        Assert.Equal(2, buckets[2].Count);
        Assert.True(buckets[0].Time < buckets[1].Time);
        Assert.True(buckets[1].Time < buckets[2].Time);
    }

    [Fact]
    public void Empty_store_returns_zeroes()
    {
        using var store = new AnalyticsStore(":memory:");

        Assert.Equal(0, store.Total());
        StatsResult stats = store.GetStats();
        Assert.Equal(0, stats.Total);
        Assert.Empty(stats.ByCollection);
        Assert.Empty(stats.ByAction);
        Assert.Empty(store.GetTopRepos(10));
        Assert.Empty(store.GetCollections());
        Assert.Empty(store.GetActivity("minute", 60));
    }

    [Fact]
    public void File_backed_store_persists_across_reopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"analytics-{Guid.NewGuid():N}.duckdb");
        try
        {
            using (AnalyticsStore store = Seed(path))
                Assert.Equal(8, store.Total());

            using var reopened = new AnalyticsStore(path);
            Assert.Equal(8, reopened.Total());
            Assert.Equal("app.bsky.feed.post", reopened.GetStats().ByCollection[0].Collection);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static AnalyticsStore Seed(string path)
    {
        var store = new AnalyticsStore(path);
        store.RecordCommit(1, "did:plc:alice", Base, new[]
        {
            new AnalyticsOp("place.selfhost.status", "self", "create", "bafyA1"),
            new AnalyticsOp("app.bsky.feed.post", "p1", "create", "bafyA2"),
        });
        store.RecordCommit(2, "did:plc:bob", Base.AddMinutes(1), new[]
        {
            new AnalyticsOp("app.bsky.feed.post", "p1", "create", "bafyB1"),
            new AnalyticsOp("app.bsky.feed.like", "l1", "create", "bafyB2"),
            new AnalyticsOp("app.bsky.feed.like", "l2", "create", "bafyB3"),
        });
        store.RecordCommit(3, "did:plc:alice", Base.AddMinutes(1).AddSeconds(10), new[]
        {
            new AnalyticsOp("place.selfhost.status", "self", "update", "bafyA3"),
        });
        store.RecordCommit(4, "did:plc:carol", Base.AddMinutes(2), new[]
        {
            new AnalyticsOp("app.bsky.feed.post", "p9", "create", "bafyC1"),
            new AnalyticsOp("app.bsky.feed.post", "p9", "delete", null),
        });
        return store;
    }
}
