# Packaging

The self-hosting application consumes the reusable AT Protocol implementation as
versioned packages. The shared libraries and their tests are maintained in the
[`atproto-dotnet`](https://github.com/luisquintanilla/atproto-dotnet) repository;
this repository contains the PDS, Relay, AppView, Aspire composition, and
hosting integration.

## Package boundaries

The shared package set is framework-neutral and targets `net8.0;net10.0`:

| Package | What it provides |
| --- | --- |
| `AtProto.Cid` | CIDv1 content identifiers |
| `AtProto.Cbor` | Canonical DAG-CBOR encoding and decoding |
| `AtProto.Car` | CARv1 repository archive reading and writing |
| `AtProto.Crypto` | secp256k1 and P-256 signing primitives |
| `AtProto.Lexicon` | Lexicon, NSID, and AT-URI value types |
| `AtProto.Identity` | DID documents and `did:web` resolution |
| `AtProto.Repo` | Signed commits and the Merkle Search Tree |
| `AtProto.Firehose` | Firehose frame decoding and pull-based ingest |
| `AtProto.OAuth` | AT Protocol OAuth, PKCE, DPoP, and client metadata primitives |
| `AtProto.Xrpc` | Framework-neutral XRPC queries and procedures |
| `AtProto.Lexicon.SourceGeneration` | Roslyn source generation for Lexicon schemas |

The only package built from this repository is
`AtProto.Hosting.Atproto`, the .NET Aspire hosting integration for the local
PDS, Relay, and AppView topology. Service, app, tool, and test projects are
not packaged.

## Consuming the shared packages

Project files use centrally managed exact versions:

```xml
<PackageReference Include="AtProto.Firehose" />
<PackageReference Include="AtProto.Repo" />
```

The current preview line is `0.3.0-preview.1`. The package dependency graph
keeps protocol libraries below the hosting and service layers; consuming a
single package brings in only its shared-library dependencies.

The repository's CI configures GitHub Packages with the ephemeral
`GITHUB_TOKEN` before restore. Local development needs a PAT with the
`read:packages` scope:

```bash
dotnet nuget add source https://nuget.pkg.github.com/luisquintanilla/index.json \
  --name github-atproto \
  --username "$GITHUB_USERNAME" \
  --password "$GITHUB_TOKEN" \
  --store-password-in-clear-text
```

Then install an exact preview version:

```bash
dotnet add package AtProto.Firehose --version 0.3.0-preview.1 \
  --source github-atproto
```

## Building and publishing

The shared repository owns the build, test, pack, and GitHub Packages
publication workflow for the reusable libraries. Its workflow validates the
full shared solution and publishes packages on a version tag or a manual
dispatch.

From the shared repository:

```bash
dotnet restore atproto-dotnet.slnx
dotnet build atproto-dotnet.slnx --configuration Release --no-restore
dotnet test atproto-dotnet.slnx --configuration Release --no-build
dotnet pack atproto-dotnet.slnx --configuration Release --no-restore \
  --output artifacts/packages
```

From this repository, the local Aspire hosting integration can be packed with:

```bash
dotnet pack src/aspire/AtProto.Hosting.Atproto/AtProto.Hosting.Atproto.csproj \
  --configuration Release --output artifacts/packages
```

The self-hosting CI restores and tests against the published shared packages.
Its package job only produces the hosting integration artifact; shared package
publication does not happen from this repository.

## Versioning

Pin preview package versions while the public API evolves. A shared-library
release changes the version in `atproto-dotnet/Directory.Build.props`, updates
the consumer's central package versions here, and publishes from the shared
repository. Hosting integration versions remain controlled by this repository's
`Directory.Build.props`.

The package split is intentional: protocol behavior can be consumed without
Aspire, while the self-hosting repository can evolve its service topology
without duplicating protocol implementations.
