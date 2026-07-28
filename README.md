# atproto-net-selfhost-aspire

A from-scratch **.NET** mono-repo that implements the [AT Protocol](https://atproto.com) (atproto)
core services - **PDS**, **Relay**, and **AppView** - and wires them together with
**.NET Aspire** so you can *host your own atproto stack*.

The end goal: each service becomes a reusable **component** you plug in as a native Aspire
integration (`builder.AddAtprotoPds()`, `.AddAtprotoRelay()`, `.AddAtprotoAppView()`).

> Status: early. Milestone **M0** (repo + Aspire skeleton) is in place. See the roadmap below.

## Why this exists

atproto has a common shape: user repositories live on **PDSes**, a **Relay** aggregates their
events into a firehose, and an **AppView** indexes that firehose into something clients can query.
That maps cleanly onto reactive dataflow in .NET, so the design uses:

- **BCL reactive primitives** (`IAsyncEnumerable<T>`, `System.Threading.Channels`,
  the `IObservable<T>` interface) as the ingest backbone - ordered, at-least-once, backpressured.
- **Rx.NET v7** as an opt-in projection layer, scoped to the AppView read models.
- **.NET Aspire** to orchestrate the services and expose them as pluggable integrations.

## Reactive layering (the one design rule)

- **Ingest hot path = pull.** PDS, Relay, and the firehose client use `IAsyncEnumerable<T>` plus a
  bounded `Channel` (`FullMode.Wait`). Rx has no backpressure, so it is not used here.
- **Seam = the BCL `IObservable<T>` interface.** The firehose library hands back
  `IObservable<RepoEvent>` via a tiny adapter and takes **no `System.Reactive` dependency**.
- **Projection = push.** Only the AppView (and samples) reference **Rx.NET v7** for
  `GroupBy`/`Replay(1)`/`Buffer`/`Throttle` read-model algebra.

## Layout

```
src/
  core/      AtProto.Cbor · Cid · Car · Repo (MST) · Identity · Lexicon · Xrpc · Firehose
  services/  AtProto.Pds · AtProto.Relay · AtProto.AppView
  apps/      AtProto.FirehoseProbe (diagnostic worker)
  aspire/    AtProto.AppHost · AtProto.ServiceDefaults · AtProto.Hosting.Atproto
  samples/   StatusWeb
lexicons/    custom lexicon JSON (the demo status record)
tests/       core interop vectors · firehose · integration
docs/        architecture notes and ADRs
```

## Prerequisites

- .NET SDK **10.0.300** (pinned in `global.json`)
- .NET Aspire CLI (`aspire`) 13.x

## Build & run

```bash
# build everything
dotnet build atproto-net-selfhost-aspire.slnx

# run the orchestrated stack (dashboard + services)
aspire run --project src/aspire/AtProto.AppHost
```

`aspire run` boots the Aspire dashboard and the placeholder `FirehoseProbe` worker.

## Roadmap

The build is phased. Each milestone is independently demoable.

- **M0** - repo bootstrap and Aspire skeleton *(done)*
- **T0** - golden-vectors fixture harness (real repo CARs/CIDs/MST roots, frozen for tests)
- **M1** - reactive firehose core, validated live against the public Bluesky relay
- **M2** - AppView "presence board" over the public firehose (emoji status, latest-wins)
- **M3** - our own PDS (`did:web`), MST build + commit signing + CAR write
- **M4** - our own Relay (aggregate PDS firehoses, global sequence)
- **M5** - Aspire orchestration + custom "native" integrations
- **M6** - stretch: federation discovery, did:plc, OAuth, packaging

## License

[MIT](LICENSE)
