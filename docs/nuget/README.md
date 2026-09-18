# AtProto.Hosting.Atproto

This package provides the .NET Aspire hosting integration for the self-hosted
AT Protocol stack. It composes the PDS, Relay, and AppView resources and is
targeted at `net10.0`.

The reusable protocol implementation is published separately as
`AtProto.*` packages from the
[`atproto-dotnet`](https://github.com/luisquintanilla/atproto-dotnet)
repository:

| Package | Purpose |
| --- | --- |
| `AtProto.Cid` | CIDv1 content identifiers |
| `AtProto.Cbor` | Canonical DAG-CBOR encoding and decoding |
| `AtProto.Car` | CARv1 repository archive reading and writing |
| `AtProto.Crypto` | secp256k1 and P-256 signing primitives |
| `AtProto.Lexicon` | Lexicon, NSID, and AT-URI value types |
| `AtProto.Identity` | DID documents and `did:web` resolution |
| `AtProto.Repo` | Signed commits and Merkle Search Trees |
| `AtProto.Firehose` | Firehose frame decoding and pull-based ingest |
| `AtProto.OAuth` | OAuth, PKCE, DPoP, and client metadata primitives |
| `AtProto.Xrpc` | Framework-neutral XRPC client |
| `AtProto.Lexicon.SourceGeneration` | Roslyn Lexicon source generator |

The shared packages are distributed through GitHub Packages. See the
self-hosting repository's [packaging guide](../../docs/packaging.md) for
authentication and exact-version installation instructions.

Licensed under the MIT License.
