# Contributing

Thanks for looking under the hood. This repo is a **hero app first, a library set second**: a
self-hosted AT Protocol stack (PDS, Relay, AppView) that runs as one .NET Aspire solution, with the
reusable pieces already split into cleanly layered libraries. This page is the short version of how to
build, test, run, and change it. For how to build *on* it, read [docs/extending.md](docs/extending.md).

## Prerequisites

- **.NET SDK 10.0.300**, pinned in [global.json](global.json).
- The **Aspire CLI** (`aspire`) to run the whole stack together.
- A valid **HTTPS dev certificate** for the Aspire dashboard:

  ```bash
  dotnet dev-certs https --clean
  dotnet dev-certs https --trust
  ```

## Build

```bash
dotnet build atproto-net-selfhost-aspire.slnx
```

## Test

The suite is xUnit and validates against real AT Protocol fixtures (repo CARs, CIDs, MST roots).

```bash
# whole solution
dotnet test atproto-net-selfhost-aspire.slnx

# one project while iterating
dotnet test tests/AtProto.Pds.Tests/AtProto.Pds.Tests.csproj
```

Keep the pure pieces (stores, encoders, projections) testable without timing or a network, the way the
existing tests do. New behavior should come with a test.

## Run the whole stack

```bash
aspire run --project src/aspire/AtProto.AppHost
```

On **WSL**, the HTTPS dashboard bind can fail even with a dev cert. Use the all-HTTP launch profile
(the [README](README.md#quickstart) documents this):

```bash
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
  dotnet run --project src/aspire/AtProto.AppHost/AtProto.AppHost.csproj --launch-profile http
```

Each service also runs standalone; see [docs/services.md](docs/services.md) for per-service commands and
ports.

## Repo map

```
src/services/   the hero app: PDS, Relay, AppView (not packaged)
src/aspire/     AppHost (the topology) + AtProto.Hosting.Atproto (the Aspire integration)
tools/          AtProto.Lexicon.CodeGen (lexicon JSON -> typed C# record)
tests/          xUnit service and integration suites + real fixtures under tests/fixtures/
lexicons/       record schemas (place.selfhost.status, place.selfhost.instance)
docs/           the docs set + docs/diagrams (the SVG generator)
```

## The one structural rule: layering

The shared protocol libraries are maintained in the
[`atproto-dotnet`](https://github.com/luisquintanilla/atproto-dotnet) repository. Its layering rule
means `AtProto.Firehose` may use
`AtProto.Repo`/`AtProto.Car`/`AtProto.Cbor`/`AtProto.Cid`/`AtProto.Lexicon`, but nothing in
this repository's `src/services/`. See
[docs/packaging.md](docs/packaging.md) for the full layer map and the `IsPackable` gate (services,
tools, and tests are never packaged).

## Generate a lexicon record

```bash
dotnet run --project tools/AtProto.Lexicon.CodeGen -- \
  lexicons/place.selfhost.status.json Status.g.cs --namespace YourApp.Lexicons
```

With no output path it writes to stdout.

## Diagrams

The SVGs under `docs/img/` are generated, not hand-edited. Change
[`docs/diagrams/generate.mjs`](docs/diagrams/generate.mjs) and regenerate:

```bash
node docs/diagrams/generate.mjs
```

## Conventions

- **Small, focused commits.** One change per commit, with a message that says why.
- **Docs voice:** we/you, plain and concrete. Avoid em dashes in the public docs (use commas or a
  short " - "); `docs/plan.md` is the dense internal log and is the one exception. Avoid filler words
  like *delve*, *leverage*, *robust*, *seamless*.
- **Package gating:** a library opts into packing with `<IsPackable>true</IsPackable>` plus a
  `<Description>`; do not pack services, tools, or tests.
- **Local-only git.** This is an experiment repo with a single `main` branch.

## Where to start

- [docs/extending.md](docs/extending.md) for the extension paths (reuse a library, add a lexicon, build
  a projection, swap storage, compose a topology).
- [docs/README.md](docs/README.md) for the reading path through the docs.
- [docs/plan.md](docs/plan.md) for milestone evidence and history.
