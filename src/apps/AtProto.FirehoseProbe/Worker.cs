using System.Diagnostics;
using AtProto.Firehose;

namespace AtProto.FirehoseProbe;

/// <summary>
/// M1 acceptance probe: subscribe to the public relay firehose, filter one collection, and
/// log the live rate + samples. Persists the cursor so kill/restart resumes from the last seq.
/// </summary>
public sealed class Worker(ILogger<Worker> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string relayHost = config["Firehose:RelayHost"] ?? "relay1.us-west.bsky.network";
        string filterCollection = config["Firehose:Collection"] ?? "app.bsky.feed.post";
        string cursorPath = config["Firehose:CursorPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "firehose-cursor.txt");

        var cursorStore = new FileCursorStore(cursorPath);
        long? startCursor = await cursorStore.GetAsync(stoppingToken);

        var client = FirehoseClient.ForRelay(relayHost);
        var options = new FirehoseOptions
        {
            OnReconnect = ex => logger.LogWarning("firehose dropped, reconnecting: {Message}", ex.Message),
        };

        logger.LogInformation(
            "subscribing to {Relay} (filter '{Collection}', resume cursor {Cursor})",
            relayHost, filterCollection, startCursor?.ToString() ?? "none");

        long totalCommits = 0, matched = 0, windowMatched = 0;
        var sw = Stopwatch.StartNew();

        await foreach (RepoEvent ev in client.SubscribeAsync(startCursor, options, stoppingToken))
        {
            if (ev is RepoCommitEvent commit)
            {
                totalCommits++;
                foreach (RepoOp op in commit.Ops)
                {
                    if (op.Action is RepoOpAction.Create && op.Collection == filterCollection)
                    {
                        matched++;
                        windowMatched++;
                        if (matched <= 5 || matched % 500 == 0)
                        {
                            logger.LogInformation(
                                "#{Matched} {Collection} by {Did} rkey={Rkey} cid={Cid} seq={Seq}",
                                matched, op.Collection, commit.Did, op.Rkey, op.Cid, commit.Seq);
                        }
                    }
                }
            }

            // checkpoint AFTER processing the event (at-least-once on restart)
            await cursorStore.SetAsync(ev.Seq, stoppingToken);

            if (sw.Elapsed >= TimeSpan.FromSeconds(10))
            {
                double rate = windowMatched / sw.Elapsed.TotalSeconds;
                logger.LogInformation(
                    "rate: {Rate:F1} {Collection}/s | {Matched} matched / {Commits} commits | seq {Seq}",
                    rate, filterCollection, matched, totalCommits, ev.Seq);
                windowMatched = 0;
                sw.Restart();
            }
        }
    }
}
