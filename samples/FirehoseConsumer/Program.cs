// A minimal external consumer of the AtProto.Firehose package.
//
// It subscribes to an atproto Relay's com.atproto.sync.subscribeRepos firehose, decodes each
// #commit frame (CBOR header + CAR block slice), and prints the record ops. By default it points at
// the live public Bluesky relay, so a fresh clone streams real global activity in seconds.
//
// Run it after putting the packages on a feed (see README.md):
//   dotnet run                         # live Bluesky relay
//   dotnet run -- your-relay.host      # your self-hosted Relay
using AtProto.Firehose;

string host = args.Length > 0 ? args[0] : "relay1.us-west.bsky.network";
const int max = 20;

Console.WriteLine($"Subscribing to {host} via the AtProto.Firehose package (first {max} commits)...");
Console.WriteLine();

// Stop after ~30s so the sample always terminates on its own.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

FirehoseClient client = FirehoseClient.ForRelay(host);

int commits = 0;
try
{
    // SubscribeAsync reconnects and resumes automatically; null cursor starts at the live tip.
    await foreach (RepoEvent ev in client.SubscribeAsync(cancellationToken: cts.Token))
    {
        if (ev is not RepoCommitEvent commit)
            continue;

        foreach (RepoOp op in commit.Ops)
            Console.WriteLine($"seq {commit.Seq}  {op.Action,-6} {op.Collection}/{op.Rkey}  {commit.Did}");

        if (++commits >= max)
            break;
    }
}
catch (OperationCanceledException)
{
    // timeout or Ctrl+C
}

Console.WriteLine();
Console.WriteLine($"Done. Decoded {commits} commit events from {host} using the published packages.");
