# atproto-net-selfhost-aspire (preview packages)

These are the reusable building blocks behind
[**atproto-net-selfhost-aspire**](https://github.com/luisquintanilla/atproto-net-selfhost-aspire),
a from-scratch .NET stack for self-hosting the [AT Protocol](https://atproto.com/) (the protocol
behind Bluesky): a Personal Data Server (PDS), a Relay, and an AppView, wired together with .NET Aspire.

The libraries are split so you can take only what you need:

| Package | What it gives you |
| --- | --- |
| `AtProto.Cid` | Content identifiers (CIDv1, dag-cbor, sha-256) |
| `AtProto.Cbor` | Canonical DAG-CBOR encoding/decoding |
| `AtProto.Car` | CARv1 read/write (the repo export format) |
| `AtProto.Crypto` | k256/p256 signing primitives |
| `AtProto.Lexicon` | Lexicon/NSID + AT-URI types |
| `AtProto.Identity` | DID documents and did:web resolution |
| `AtProto.Repo` | Signed commits + the MST (Merkle Search Tree) |
| `AtProto.Firehose` | Firehose frame decoding + a reactive ingest core |
| `AtProto.Hosting.Atproto` | .NET Aspire hosting integration for the services |

> **Preview.** The API surface is still settling, so these are published as preview artifacts (build
> outputs and, optionally, GitHub Packages) rather than to nuget.org. Pin exact versions if you depend
> on them. See the repo's `docs/packaging.md` for the extraction path and layering.

New to the protocol? Start with the repo's
[primer](https://github.com/luisquintanilla/atproto-net-selfhost-aspire/blob/main/docs/primer.md) and
[how-it-works](https://github.com/luisquintanilla/atproto-net-selfhost-aspire/blob/main/docs/how-it-works.md)
walkthrough.

Licensed under the MIT License.
