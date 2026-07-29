using AtProto.Firehose;

namespace AtProto.AnalyticsView;

/// <summary>
/// Pull-based firehose ingest for the analytics projection. Mirrors the presence AppView's ingest
/// loop (BCL <c>await foreach</c> over the bounded-channel client, cursor-checkpointed), but writes
/// <em>every op from every collection</em> into DuckDB rather than filtering to one collection and
/// decoding record bodies. No Rx here: analytics is an append-and-aggregate workload, so the loop
/// appends straight to the columnar store.
/// </summary>
public sealed class AnalyticsIngestService(
    AnalyticsStore store,
    IConfiguration config,
    ILogger<AnalyticsIngestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string relayHost = config["Firehose:RelayHost"] ?? "relay1.us-west.bsky.network";
        string? firehoseUrl = config["Firehose:Url"];
        string cursorPath = config["Firehose:CursorPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "analyticsview-cursor.txt");

        var cursorStore = new FileCursorStore(cursorPath);
        long? startCursor = await cursorStore.GetAsync(stoppingToken);

        // Prefer an explicit firehose URL (our self-hosted Relay, injected by Aspire); otherwise
        // fall back to a public relay host.
        FirehoseClient client = firehoseUrl is not null
            ? FirehoseClient.ForUrl(firehoseUrl)
            : FirehoseClient.ForRelay(relayHost);
        var options = new FirehoseOptions
        {
            OnReconnect = ex => logger.LogWarning("firehose dropped, reconnecting: {Message}", ex.Message),
        };

        logger.LogInformation(
            "Analytics ingest: source {Source}, resume {Cursor}",
            firehoseUrl ?? relayHost, startCursor?.ToString() ?? "none");

        await foreach (RepoEvent ev in client.SubscribeAsync(startCursor, options, stoppingToken))
        {
            if (ev is RepoCommitEvent commit && commit.Ops.Count > 0)
            {
                var ops = new List<AnalyticsOp>(commit.Ops.Count);
                foreach (RepoOp op in commit.Ops)
                {
                    if (op.Action is RepoOpAction.Unknown)
                        continue;
                    ops.Add(new AnalyticsOp(
                        op.Collection,
                        op.Rkey,
                        op.Action.ToString().ToLowerInvariant(),
                        op.Cid?.ToString()));
                }

                if (ops.Count > 0)
                    store.RecordCommit(commit.Seq, commit.Did, commit.Time ?? DateTimeOffset.UtcNow, ops);
            }

            await cursorStore.SetAsync(ev.Seq, stoppingToken); // checkpoint after processing
        }
    }
}
