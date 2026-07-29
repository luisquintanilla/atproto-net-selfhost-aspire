# Services reference

This project has three protocol services: Personal Data Server (PDS), Relay, and AppView. Aspire runs them together, but each service can run on its own.

## Personal Data Server (PDS)

The PDS is your data home, like an email provider. It creates accounts, stores signed repos, serves DID documents, exposes repo typed HTTP (XRPC) methods, and emits a source firehose.

### Responsibilities

- Create accounts and sessions.
- Serve `did:web` documents for the service and hosted accounts.
- Resolve local handles.
- Create, update, delete, get, and list repo records.
- Export repos as Content Addressable aRchive (CAR) v1 with `application/vnd.ipld.car`.
- Emit `#account`, `#identity`, and `#commit` frames over `subscribeRepos`.

### Typed HTTP (XRPC) and HTTP surface

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/xrpc/com.atproto.server.describeServer` | Service DID, available domains, invite requirement. |
| POST | `/xrpc/com.atproto.server.createAccount` | Body includes `handle` and `password`; returns DID, handle, access JWT, refresh JWT. |
| POST | `/xrpc/com.atproto.server.createSession` | Body includes `identifier` and `password`; returns session tokens. |
| GET | `/xrpc/com.atproto.server.getSession` | Requires bearer token. |
| POST | `/xrpc/com.atproto.repo.createRecord` | Creates a record, optional caller rkey. |
| POST | `/xrpc/com.atproto.repo.putRecord` | Updates or creates a known rkey, used by status `self`. |
| POST | `/xrpc/com.atproto.repo.deleteRecord` | Deletes a known rkey. |
| GET | `/xrpc/com.atproto.repo.getRecord` | Query `repo`, `collection`, `rkey`; returns `{ uri, cid, value }`. |
| GET | `/xrpc/com.atproto.repo.listRecords` | Query `repo`, `collection`, optional `limit`. |
| GET | `/xrpc/com.atproto.sync.getRepo` | Query `did`; returns CARv1 as `application/vnd.ipld.car`. |
| GET | `/xrpc/com.atproto.sync.getLatestCommit` | Query `did`; returns commit CID and rev. |
| WS | `/xrpc/com.atproto.sync.subscribeRepos` | Source firehose, optional `cursor`. |
| GET | `/xrpc/com.atproto.identity.resolveHandle` | Query `handle`; returns DID. |
| GET | `/.well-known/did.json` | PDS service DID document. |
| GET | `/pds/{id}/did.json` | Hosted account DID document. |

Code pointer: `src/services/AtProto.Pds/PdsHost.cs`.

### Config

| Key | Default | Meaning |
| --- | --- | --- |
| `Pds__PublicUrl` | `http://localhost:5100` | Externally reachable URL, used to derive did:web and service endpoint. Aspire sets this. |
| `Pds__HandleDomain` | `pds.localhost` | Default domain for bare handles. |
| `Pds__JwtSecret` | dev-only value | HMAC secret for session JWTs. Override outside local demos. |

Code pointer: `src/services/AtProto.Pds/PdsOptions.cs`.

### Run standalone

```bash
dotnet run --project src/services/AtProto.Pds/AtProto.Pds.csproj --launch-profile http
```

Default HTTP port: `5271`.

## Relay

The Relay is the newswire. It crawls one or more upstream PDS or Relay firehoses, assigns a global sequence, tracks host and repo state, and republishes an aggregated firehose.

### Responsibilities

- Auto-crawl configured upstreams.
- Accept new crawl requests.
- Re-emit an aggregated `subscribeRepos` stream with global sequence numbers.
- Track hosts, upstream cursors, and latest repo revisions.
- Redirect repo downloads to the authoritative PDS.

### XRPC surface

| Method | Route | Notes |
| --- | --- | --- |
| WS | `/xrpc/com.atproto.sync.subscribeRepos` | Aggregated firehose, optional `cursor`. |
| POST | `/xrpc/com.atproto.sync.requestCrawl` | Body `{ "hostname": "http://localhost:5271" }`. |
| GET | `/xrpc/com.atproto.sync.listHosts` | Lists crawled hosts and live status. |
| GET | `/xrpc/com.atproto.sync.getRepoStatus` | Query `did`; returns active flag and latest rev. |
| GET | `/xrpc/com.atproto.sync.getRepo` | Query `did`; redirects to the source PDS. |

Code pointer: `src/services/AtProto.Relay/RelayHost.cs`.

### Config

| Key | Default | Meaning |
| --- | --- | --- |
| `Relay__PublicUrl` | unset | Informational public URL for this relay. |
| `Relay__Upstreams__0` | none | Upstream base URL to crawl. Aspire sets this from `.WithUpstream(pds)`. |
| `Relay__CursorDir` | app default | Directory for per-host cursors and global sequence persistence. |

Code pointer: `src/services/AtProto.Relay/RelayOptions.cs`.

### Run standalone

```bash
dotnet run --project src/services/AtProto.Relay/AtProto.Relay.csproj --launch-profile http
```

Default HTTP port: `5333`.

To crawl a local PDS after startup:

```bash
curl -X POST http://localhost:5333/xrpc/com.atproto.sync.requestCrawl \
  -H 'Content-Type: application/json' \
  -d '{ "hostname": "http://localhost:5271" }'
```

## AppView

The AppView is the newspaper or search index. It reads the Relay firehose, filters the status collection, keeps the latest status per account, serves queries, and pushes live updates to browsers.

### Responsibilities

- Subscribe to a firehose from a Relay or PDS.
- Decode `place.selfhost.status` records from commit CAR slices.
- Maintain latest-wins presence in `PresenceStore`.
- Emit stats and deltas from `PresenceProjection`.
- Serve the static board UI and SignalR hub.
- Provide explorer endpoints for compose, inspect, repo download, and raw commit ticker in the app explorer workstream.

### Endpoint surface

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/xrpc/place.selfhost.getPresence` | Optional `limit`; returns latest statuses, most recent first. |
| GET | `/xrpc/place.selfhost.getStatus` | Query `did`; returns one account's latest status. |
| GET | `/xrpc/place.selfhost.getStats` | Returns total updates, unique users, rate, last seq. |
| SignalR | `/hub/presence` | Emits `presence` and `stats`; explorer also uses `commit` ticker events. |
| POST | `/compose` | Body `{ "status": "🌤" }`; writes as `you.pds.localhost`; returns `{ uri, cid, did, handle }`. |
| GET | `/inspect/record` | Query `did`, `collection`, `rkey`; returns `{ uri, cid, value }`. |
| GET | `/inspect/repo` | Query `did`; returns `{ did, handle, pds, commit, didDoc, collection, records }`. |
| GET | `/inspect/car` | Query `did`; downloads a CAR file. |

Code pointer for current board API and hub: `src/services/AtProto.AppView/Program.cs`. The explorer endpoints are part of the concurrent app explorer surface described by this doc set.

### Config

| Key | Default | Meaning |
| --- | --- | --- |
| `Firehose__Url` | unset | Explicit PDS or Relay base URL. Aspire sets this from `.WithFirehose(relay)`. |
| `Firehose__RelayHost` | `relay1.us-west.bsky.network` | Public relay fallback when `Firehose__Url` is unset. |
| `Firehose__CursorPath` | app base dir cursor file | Cursor checkpoint path. |
| `AppView__Collection` | `place.selfhost.status` | Collection to index. |

Code pointer: `src/services/AtProto.AppView/FirehoseIngestService.cs`.

### Run standalone

```bash
dotnet run --project src/services/AtProto.AppView/AtProto.AppView.csproj --launch-profile http
```

Default HTTP port: `5193`.

By default, standalone AppView can use the public Bluesky relay. To point at the local Relay:

```bash
Firehose__Url=http://localhost:5333 \
  dotnet run --project src/services/AtProto.AppView/AtProto.AppView.csproj --launch-profile http
```

## Run the whole stack with Aspire

Recommended from the repo root:

```bash
dotnet dev-certs https --clean
dotnet dev-certs https --trust
aspire run --project src/aspire/AtProto.AppHost
```

On Windows Subsystem for Linux (WSL), use the all-HTTP launch profile because the HTTPS dashboard bind can fail even with a valid dev certificate:

```bash
ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
  dotnet run --project src/aspire/AtProto.AppHost/AtProto.AppHost.csproj --launch-profile http
```

Ports in the HTTP launch profile:

| Thing | URL |
| --- | --- |
| Aspire dashboard | `http://localhost:15194` |
| PDS | `http://localhost:5271` |
| Relay | `http://localhost:5333` |
| AppView board | `http://localhost:5193` |
