# FirehoseConsumer sample

The smallest useful program that consumes this stack as **NuGet packages**: a standalone console app
that references `AtProto.Firehose`, subscribes to an atproto Relay firehose, decodes each `#commit`
frame, and prints the record ops. By default it points at the live public Bluesky relay, so you see
real global activity within seconds.

This sample is deliberately **not** part of the mono-repo solution. It builds against the published
packages exactly the way an external adopter would, which is the whole point: it proves the extracted
libraries are consumable on their own, outside the repository's project references.

```
seq 32301037167  Create app.bsky.feed.like/3mrul6vozsr2l  did:plc:cpzjninzmn63klxsjy7hy2im
seq 32301037170  Create app.bsky.graph.follow/3mrul6vsjss2s  did:plc:uuxayaya5jmaoukwl73r4eb3
seq 32301037174  Create app.bsky.feed.repost/3mrul6vsnrk2f  did:plc:mjpcpykai3c64myx6oflb5y4
...
Done. Decoded 20 commit events from relay1.us-west.bsky.network using the published packages.
```

## One package reference

The entire program depends on a single line (`FirehoseConsumer.csproj`):

```xml
<PackageReference Include="AtProto.Firehose" Version="0.3.0-preview.1" />
```

`AtProto.Firehose` pulls in its siblings (`AtProto.Cid`, `AtProto.Cbor`, `AtProto.Car`, `AtProto.Repo`,
`AtProto.Lexicon`, and transitively `AtProto.Crypto` and `AtProto.Identity`) as package dependencies, so
that one reference is enough to decode the firehose.

## Run it

You need the packages on a feed the sample can restore from. Two options.

### Option A: local feed (no authentication, works offline)

From the repository root, pack the libraries into the feed this sample expects, then run it:

```bash
dotnet pack atproto-net-selfhost-aspire.slnx -c Release -o samples/FirehoseConsumer/local-feed
cd samples/FirehoseConsumer
dotnet run
```

`local-feed/` is git-ignored; it is build output you regenerate whenever you like. The bundled
`nuget.config` points at it by default.

### Option B: GitHub Packages (the real published feed)

The libraries are published to GitHub Packages on every version tag. To consume them from there, add the
GitHub source with a Personal Access Token that has `read:packages`, as described in
[docs/packaging.md](../../docs/packaging.md). GitHub Packages requires authentication even for public
packages, which is why the local feed is the zero-setup default here.

## Point it somewhere else

```bash
dotnet run                              # live public Bluesky relay (default)
dotnet run -- your-relay.example.com    # your own self-hosted Relay
```

Pointing the same code at the live Bluesky relay and at your self-hosted Relay is the same demonstration
the [analytics view](../../docs/analytics.md#point-it-at-the-live-bluesky-network) makes: the wire format
is the wire format, so one client decodes both.

## See also

- [docs/packaging.md](../../docs/packaging.md), how the packages are published and consumed.
- [docs/reactive-design.md](../../docs/reactive-design.md), why the firehose client is pull-based.
- [docs/extending.md](../../docs/extending.md), the seams you extend to build your own components.
