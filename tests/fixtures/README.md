# Golden fixtures (T0)

Frozen AT Protocol test vectors captured from the **public** network (no credentials),
used to verify the codec libraries (CBOR / CID / CAR / MST / signing / firehose) against
real-world data with no network in the test loop.

Source: `atproto.com` (`did:plc:ewvi7nxzyoun6zhxrhs64oiz`). All data here is public.

## Contents

| Path | What it is |
|---|---|
| `manifest.json` | Index of everything, incl. SHA-256 of every file, the head commit CID/rev, collections, and sampled record CIDs |
| `car/repo.car` | Full repository export (`com.atproto.sync.getRepo`) - CARv1 |
| `car/record-proof.*.car` | Single-record MST-path proof (`com.atproto.sync.getRecord`) - small CARv1 |
| `json/did-doc.json` | DID document (signing key + PDS endpoint) |
| `json/latest-commit.json` | Authoritative head (`com.atproto.sync.getLatestCommit`) - the expected repo root CID |
| `json/describe-repo.json` | `com.atproto.repo.describeRepo` (collections, didDoc) |
| `json/listRecords.*.json` | Sampled records with their authoritative CIDs |
| `firehose/frame-*.bin` | Raw `com.atproto.sync.subscribeRepos` frames (header-CBOR ‖ payload-CBOR) |

The CIDs in `manifest.json` / `latest-commit.json` are the **golden values**: M1 codecs must
parse `repo.car`, recompute the MST root and commit CID, and match `head.commitCid`.

## Regenerate

```bash
dotnet run --project tools/AtProto.Fixtures.Tool -- --out tests/fixtures --handle atproto.com
```

Firehose frames are live samples and differ each run (structure is stable, content is not).
The CAR/JSON vectors are stable snapshots of the source repo at capture time.

Integrity is asserted by `tests/AtProto.Fixtures.Tests`.
