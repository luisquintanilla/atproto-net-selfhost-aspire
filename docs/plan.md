# atproto-net-selfhost-aspire — Implementation Plan

A from-scratch .NET mono-repo that implements the AT Protocol (atproto) core services —
**PDS, Relay, AppView** — with **ASP.NET Core** + **BCL reactive primitives** (`IObservable<T>`,
`IAsyncEnumerable<T>`, `System.Threading.Channels`) as the spine, **Rx.NET v7** as a *scoped, opt-in*
projection layer, and **.NET Aspire** as the orchestrator. The end goal: these services become reusable
**components** you plug in as **native Aspire integrations** to *host your own atproto stack*.

Working repo name: **`atproto-net-selfhost-aspire`** — standalone git repo at `~/dev/experiments/atproto-net-selfhost-aspire`.

---

## 0. Progress (live status — updated 2026-07-28)

**Autopilot lane `M0 → T0 → M1 → M2 → M3 → M4` is COMPLETE and verified. The full three-tier self-hosted stack
(our PDS → our Relay → AppView presence board) runs end-to-end under `aspire run`, with a StatusSeeder writing
live `place.selfhost.status` records that flow PDS→Relay→AppView and light up the board with decoded emoji.
Next on autopilot: M5 (Aspire native integrations).**

| Phase | Status | Commit | Evidence |
|-------|--------|--------|----------|
| **M0** bootstrap & Aspire skeleton | ✅ done | `147d823` | AppHost/ServiceDefaults/probe, CPM, `.slnx`, CI |
| **T0** golden-vectors harness | ✅ done | `ab0cf0e` | real repo.car 703KB + record proofs + 12 firehose frames frozen; Fixtures.Tests 5/5 |
| **M1** reactive firehose core (public) | ✅ done | `85ba41f`, `4d49bc7`, `0cfbb97` | codecs verified vs real repo (Core 15/15, Firehose 5/5); probe live-verified ~55 posts/s + cursor resume + `aspire run` |
| **M2** AppView presence board (public) | ✅ done | `6456fab` | AppView 7/7; **live smoke** vs relay1.us-west (zero creds): board populated, getStats advanced (totalUpdates 1970→2934, lastSeq rising ~50–56/s), latest-wins confirmed |
| **Orchestration** `aspire run` end-to-end | ✅ verified | `7a3f4d8` | dashboard (`:17046`, HTTP 302) + FirehoseProbe + AppView all up; AppView served live board (6358 users, ~71/s) through DCP-assigned ports. WSL dev-cert fix documented in README |
| **M3 (gate)** MST **write** core | ✅ done | `f56b25d` | rebuilt MST root == reference `commit.data` **exactly** (`bafyrei…v6ubfi`); **462/462** node blocks byte-identical; canonical encoder round-trips **2133/2133** real blocks |
| **M3** PDS writes: signing · CAR write · XRPC · firehose | ✅ done | `ce0d5a5`, `f4e4cf7`, `5aa01dc`, `4058ee6` | k256/p256 commit signing (verifies the real bsky sig); CARv1 writer byte-exact vs the 720478-byte export; `AtProto.Pds` XRPC + did:web; **Pds e2e 4/4** — an independent firehose client reads a signature-verifiable `#commit`, `getRepo` CARv1 validates |
| **Capstone** self-hosted PDS → AppView loop under `aspire run` | ✅ verified | `62eac75` | AppView repointed at **our** PDS (`Firehose__Url`); `StatusSeeder` writes `place.selfhost.status`; board shows the **decoded emoji**, latest-wins per DID. **Live**: 6 users, getStats advancing (totalUpdates 76→78, lastSeq 88→90), board HTTP 200. New `SelfHostedLoopTests` drives the real ingest→Rx-projection in-process |
| **M4** our own Relay (crawl · global seq · re-emit) | ✅ done | *(this change)* | `AtProto.Relay` crawls the upstream PDS, assigns a **global** seq via `FirehoseBroadcaster.PublishNext`, re-emits `subscribeRepos`; lenient validate (DID + rev-monotonic); `listHosts`/`getRepoStatus`/`getRepo`-redirect; per-host cursor + global-seq persisted across restart. **Live under `aspire run`**: PDS→**Relay**→AppView — relay `active` (lastUpstreamSeq 1510), AppView `lastSeq` tracks the relay's global seq, board 6 users w/ emoji, relay tracks per-repo rev. **Pds.Tests 7/7** incl. `RelayLoopTests` (full loop + restart-resume: no reset, no reprocess) |
| M5 Aspire integrations | ⚪ pending | — | autopilot after M4 |
| M6 stretch | ⚪ deferred | — | not in autopilot run |

**Totals:** 62/62 tests green (Fixtures 5, Core 38, Firehose 5, AppView 7, Pds 7). Reactive layering held exactly
as decided (§2a): BCL pull ingest everywhere; Rx.NET scoped to the AppView projection only.

**M4 is complete: the full three-tier stack is live.** Our own **PDS** (did:web, MST write, k256/p256 commit
signing, CARv1 write, `subscribeRepos`) → our own **Relay** (crawls the PDS, assigns a global seq, re-emits an
aggregated firehose, persists per-host + global cursors) → **AppView** (Rx presence projection) runs end-to-end
under `aspire run`, with a `StatusSeeder` writing real `place.selfhost.status` records that flow all the way to
the board with their decoded emoji (latest-wins per account). Proven three ways: the MST root-CID gate (462/462
node blocks byte-identical to the reference), the PDS e2e suite (a `#commit` an independent consumer verifies),
and `RelayLoopTests` (the actual AppView ingest against our Relay + a restart that resumes cursors with no reset
and no reprocessing).
**Next action:** **M5** (Aspire "native" integrations — a `AtProto.Hosting.Atproto` library exposing
`AddAtprotoPds/Relay/AppView` + `.WithReference()` auto-wiring so the AppHost collapses to a short declarative
chain), on autopilot.

---

## 1. Problem & Approach

atproto has "the usual shape": users' repositories on **PDSes** → a **Relay** aggregating repo events
into a firehose → an **AppView** indexing that firehose into something clients query. This maps almost
perfectly onto **reactive dataflow**: the firehose is a naturally push-based, ordered, resumable event
stream, i.e. an `IObservable<RepoEvent>`; the AppView is a set of stream projections; Aspire wires the
graph and gives us service discovery + telemetry.

**Chosen strategy (user-selected): phased.** Build the shared **reactive core + codecs** first, validate
the firehose pipeline **against the PUBLIC Bluesky relay early** (de-risks the hardest streaming bits with
real data before any server exists), then grow into the **full self-hosted three-tier stack**, and finally
package the services as **Aspire hosting integrations**.

**Demo lexicon:** an emoji/short **status** record with *current-state (latest-wins)* semantics → a
**"presence board" AppView**. Chosen because it is the smallest legible schema (keeps the spotlight on the
*self-hosted stack*, not app features), and "latest-status-per-user" is an elegant Rx projection
(`GroupBy(did)` → `Replay(1)`/`scan`). Optional later stretch: a **service-advertisement** record ("evil
atproto" federation discovery, per the inspiration article).

---

## 2. Guiding Principles

- **Reactive spine, correctly layered.** The **ingest hot path is pull-based** (`IAsyncEnumerable<T>` +
  a **bounded `Channel`**, `FullMode.Wait`) because it must be ordered, at-least-once, and backpressured —
  and **Rx has no backpressure by design**. The firehose library hands back the **BCL `IObservable<T>`
  interface** (in `System.Runtime`, no package) via a tiny adapter, so **core libs take no Rx dependency**.
  **Rx.NET v7 is opt-in** in the *projection* layer (AppView, live UI) where its operators
  (`GroupBy`/`Replay(1)`/`Scan`/`Buffer`/`Throttle`) are exactly the read-model algebra. The cursor is
  checkpointed **after** durable write.
- **Own the seams, borrow the plumbing.** Nothing in .NET implements the *server* side (MST write, CAR
  write, commit signing, Relay, AppView). We build those. For the *read/client* side, an existing (alpha)
  library — **CarpaNet** — already does DAG-CBOR/CID/CAR-read/MST-read/firehose/identity; we use it to move
  fast early, behind our own `AtProto.*` interfaces so we can swap/vendor later.
- **Interop is the safety net.** Cross-check every codec (CBOR/CID/CAR/MST/signing) against reference test
  vectors and real Bluesky repos. MST is the highest-risk component.
- **Aspire-native end state.** Services expose `builder.AddAtprotoPds()/AddAtprotoRelay()/AddAtprotoAppView()`
  with `.WithReference()` wiring (relay auto-crawls the pds; appview auto-subscribes the relay).
- **Analog-simple MVP.** did:web (no external PLC infra), legacy JWT sessions (no OAuth AS yet), SQLite for
  actor stores, Postgres (Aspire) for indexes. Everything runs with `aspire run`.

---

## 2a. Rx.NET vs. BCL primitives — the decision

**Question:** take a dependency on `System.Reactive` (dotnet/reactive), or are BCL primitives enough?
**Answer: both, scoped.** BCL primitives are the backbone; Rx v7 is an opt-in projection layer behind the
BCL `IObservable<T>` seam.

| Layer | Shape | Choice | Rationale |
|---|---|---|---|
| Firehose ingest / transport (PDS, Relay, firehose client) | **pull** | **BCL only:** `IAsyncEnumerable<T>` + `System.Threading.Channels` | Ordered, at-least-once, backpressured, cursor-checkpointed. Rx has **no backpressure**; push here risks unbounded buffering. Keeps cores lean + AOT-friendly. |
| Public API seam of `AtProto.Firehose` | either | **BCL `IObservable<RepoEvent>`** (interface only) via ~30-line adapter | The interface ships in `System.Runtime` — no package needed to *expose* it. Consumers opt into Rx for operators. |
| Read-model projections (AppView), live UI/demos | **push** | **Rx.NET v7** (`System.Reactive`) | `GroupBy(did)→Replay(1)` (latest-per-user), windowed top-N, fan-out, live push are precisely Rx's algebra; hand-rolling Subjects + schedulers + time-windows is a lot of subtle code. |

**Why the dependency is cheap now (Rx v7):** v7's headline change removes the old **90 MB WPF/WinForms bloat**
(UI support moved to separate packages). On a non-Windows TFM like **`net10.0`** it's just the lean ~1.5 MB
`System.Reactive.dll` — no transitive UI junk. (v7 ships no `net10` target, but its `net8` target fully
supports net8/9/10.) So scoping Rx into the AppView costs almost nothing.
*Not used:* `AsyncRx` (`IAsyncObservable`) is still `6.0-alpha` → skip; pull-loop + `Channels` covers async sinks.

---

## 3. Research Summary (grounding)

### 3.1 Protocol facts that shape the design (atproto.com/specs)
- **Firehose = `com.atproto.sync.subscribeRepos`** over WebSocket. Each frame = **two DAG-CBOR objects**
  concatenated: a **header** `{op, t}` (op=1 message / op=-1 error; `t` = `#commit` etc.) + **payload**.
  Monotonic **`seq`** + **`cursor`** query param give a **backfill window** (Kafka-consumer-like resume).
- **Sync v1.1 (current).** `#commit` carries a **CAR slice** (`blocks`) + `ops[]` (`create|update|delete`,
  `path=collection/rkey`, `cid`, `prev`) + **`prevData`** (MST root before commit) enabling **MST inversion**
  validation without storing full repos. Event types: **`#commit`, `#sync`, `#identity`, `#account`** (ignore
  `#info`). `tooBig` and `blobs` are **deprecated** (`false`/`[]`). Limits: 2 MB blocks, 200 ops, 1 MB/record,
  5 MB frame.
- **Repository** = content-addressed **Merkle Search Tree** (SHA-256, fanout 4, depth = leading-zero-bit-pairs);
  keys `collection/rkey`; **commit** `{did, version:3, data(MST root CID), rev(TID), prev:null, sig}`; sign =
  `secp256k1|p256(sha256(dag-cbor(unsigned commit)))`. Export as **CARv1** via `com.atproto.sync.getRepo`.
- **Identity.** DIDs (`did:plc`, `did:web`); **did:web** = serve `/.well-known/did.json` (simplest for MVP,
  no PLC). Handles resolve via **DNS TXT `_atproto`** or **HTTPS `/.well-known/atproto-did`**, bidirectionally
  linked to the DID doc `alsoKnownAs`. DID doc has signing key (`#atproto`, Multikey) + PDS service endpoint.
- **XRPC.** `/xrpc/<nsid>`; GET=query, POST=procedure; JSON error `{error, message}`.
- **Inter-service auth** = short-lived **service JWT** (`ES256K`/`ES256`, `kid:#atproto`, `iss`=caller DID,
  `aud`=target `did#service`, `lxm`=method, `exp`, `jti`).
- **You don't need a Relay for a single PDS**; and an AppView can just consume the **public** relay firehose
  (`wss://relay1.us-west.bsky.network/...`) and filter one collection — this is exactly the Statusphere
  pattern and our M1/M2 shortcut.

### 3.2 .NET build-vs-buy (as of 2026-07; versions verified via NuGet)
| Capability | Existing .NET | Plan |
|---|---|---|
| DAG-CBOR read/write | CarpaNet `DagCbor*` over `System.Formats.Cbor` 10.x | **Adopt** (behind our seam), vendor if needed |
| CID v1 | CarpaNet `ATCid` (NetCid is **net10-only**, but we roll our own/adopt CarpaNet) | **Adopt** |
| CAR **read** | CarpaNet `CarReader` | **Adopt** |
| CAR **write** | none | **Build** (~0.5–1k LOC) |
| MST **read** | CarpaNet `MstNode`/`Repository` | **Adopt** |
| MST **build/mutate** | none | **Build** (highest risk, ~2k LOC, test vs TS ref) |
| DID/handle resolve | CarpaNet `IdentityResolver` | **Adopt** |
| did:plc create/rotate | none | **Build** (defer; did:web first) |
| XRPC client | CarpaNet / idunno.AtProto | **Adopt** |
| Firehose consumer | CarpaNet `EventStreamClient` (`IAsyncEnumerable`) | **Adopt** → expose BCL `IObservable` seam |
| Repo signing (k256/p256) | `Secp256k1.Net` 2.0 / BCL `ECDsa` | **Build on top** (atproto format) |
| PDS / Relay / AppView servers | none | **Build** (the whole point) |
| Backpressure | `System.Threading.Channels` (BCL) | **Use** |
- **Rx.NET** `System.Reactive` **7.0.0** (stable; `net8` target covers net8/9/10; **no built-in backpressure**).
  v7 removed the 90 MB WPF/WinForms bloat (UI in separate packages) → lean ~1.5 MB on `net10.0`. **Scoped to the
  AppView/projection layer only**; ingest cores stay BCL-only (see §2a). AsyncRx `IAsyncObservable` still
  `6.0-alpha` → skip.
- **.NET Aspire** `Aspire.Hosting` **13.4.6** (GA). Custom **hosting integration** = class library referencing
  `Aspire.Hosting`, a `ContainerResource`/project resource + `builder.AddX()` extension + endpoint wiring;
  `CommunityToolkit.Aspire` is the scaffolding reference. **Verified locally**: Aspire CLI 13.4.6 + templates present.
- **CarpaNet is alpha** (`1.1.0-alpha.x`, "no support") → mitigations: pin versions, wrap behind our
  interfaces, be ready to vendor the CBOR/CID/CAR/MST-read code (~2k LOC, `System.Formats.Cbor` does heavy lifting).

### 3.3 Local toolchain (verified)
.NET SDK **10.0.300** (also 9.0.308), ASP.NET Core **10.0.8**, **Aspire CLI 13.4.6** + templates, git 2.43,
node 22. `~/src` not yet created. → **Target `net10.0`** for apps (can still consume CarpaNet's net9 assets);
multi-target core libs (`net9.0;net10.0`) later for NuGet.

---

## 4. Target Architecture

### 4.1 Mono-repo layout
```
atproto-net-selfhost-aspire/
  atproto-net-selfhost-aspire.slnx
  Directory.Build.props · Directory.Packages.props (Central Package Mgmt) · global.json (SDK pin) · .editorconfig
  src/
    core/
      AtProto.Cbor/        DAG-CBOR reader/writer (constraints over System.Formats.Cbor)
      AtProto.Cid/         CIDv1 (sha-256, dag-cbor/raw), multibase/multihash
      AtProto.Car/         CARv1 read + write
      AtProto.Repo/        MST (read + build/mutate), commit objects, signing/verify, TID
      AtProto.Identity/    DID (plc/web) + handle resolution, DID docs, Multikey
      AtProto.Lexicon/     NSID/AT-URI/record-key types, lexicon model, (later) codegen
      AtProto.Xrpc/        XRPC client + ASP.NET server helpers, error model, service-JWT
      AtProto.Firehose/    subscribeRepos client (IAsyncEnumerable + bounded Channel, backpressure);
                           exposes BCL IObservable<RepoEvent> seam (NO System.Reactive dep);
                           cursor store; server-side broadcaster (used by PDS + Relay)
    services/
      AtProto.Pds/         ASP.NET: accounts/sessions, repo CRUD, subscribeRepos server, getRepo,
                           requestCrawl client, did:web, handle resolution
      AtProto.Relay/       ASP.NET: requestCrawl, upstream firehose subscriber(s), aggregate + re-emit,
                           host/cursor state, getRepoStatus/listHosts/getRepo-redirect
      AtProto.AppView/     ASP.NET: firehose consumer → **Rx.NET v7** projection → index → XRPC query API (+ presence UI)
    aspire/
      AtProto.AppHost/         Aspire orchestration (pds + relay + appview + postgres [+ redis])
      AtProto.ServiceDefaults/ OTel, health, resilience defaults
      AtProto.Hosting.Atproto/ custom Aspire hosting integration: AddAtprotoPds/Relay/AppView
    samples/
      StatusWeb/           minimal client: sign in, write status, view presence board
  lexicons/                <nsid>.status.json (+ later <nsid>.instance.json)
  tests/
      AtProto.Core.Tests/         CBOR/CID/CAR/MST/signing interop vectors
      AtProto.Firehose.Tests/     reconnect/cursor/backpressure
      AtProto.Integration.Tests/  end-to-end via Aspire.Hosting.Testing
  docs/  architecture.md · adr/ · runbook.md
```

### 4.2 Reactive firehose design (the heart)
```
INGEST CORE  (BCL, pull; used by PDS / Relay / firehose-client) — NO System.Reactive:
  ClientWebSocket.ReceiveAsync ─▶ IAsyncEnumerable<RepoEvent>
      └─▶ Channel<RepoEvent>(bounded, FullMode.Wait)      ← backpressure
            └─▶ await foreach: durable write ─▶ checkpoint seq   (ordered, at-least-once)
      exposes  IObservable<RepoEvent>  (BCL interface, ~30-line adapter — no Rx package)

PROJECTION  (Rx.NET v7, opt-in; AppView & live UI):
  firehose.IObservable<RepoEvent>
     .Where(op.collection == nsid)
     .GroupBy(op.did).Select(g => g.Replay(1))     ── latest-status-per-user
     .Buffer(1s, 100) / CombineLatest              ── windowed top-N, live views
     ─▶ materialized read-model ─▶ /xrpc/<nsid>.getPresence ─▶ presence UI
  on reconnect: pass ?cursor=lastSeq
```
`AtProto.Firehose` exposes both a **client** (consume upstream) and a **server broadcaster** (re-emit with
CBOR framing + seq + backfill window) reused by both PDS and Relay. Its core uses **only BCL primitives** and
hands back the BCL `IObservable<RepoEvent>` **interface** (no `System.Reactive` dependency); operators like
`GroupBy`/`Replay(1)`/`Buffer` are added **by the consumer** — only the AppView (and samples) reference Rx.NET v7.

### 4.3 Full self-hosted data flow (end state)
```
StatusWeb ──createSession/createRecord──▶ AtProto.Pds ──#commit firehose──▶ AtProto.Relay
                                              ▲ requestCrawl                      │ aggregated firehose (global seq)
                                              └───────── did:web / getRepo ◀──────┤
AtProto.AppView ──subscribeRepos(relay)──▶ Rx.NET v7 projection ──▶ index ──▶ /xrpc/<nsid>.getPresence ──▶ StatusWeb
```

---

## 5. Milestones (phased)

> Each milestone is independently demoable. Acceptance criteria are concrete "it works" gates.
> Readiness tags — 🟢 autopilot-ready · 🟡 autopilot after a re-scope · 🔴 autopilot + one human gate — are
> defined in **§5a (Autopilot readiness)**.

### M0 — Repo bootstrap & Aspire skeleton  🟢 *(autopilot-ready — run now)*
- Create standalone git repo, `.slnx`, `Directory.Build.props`/`Directory.Packages.props` (CPM), `global.json`
  (pin SDK 10.0.300), `.editorconfig`, `.gitignore`, README, LICENSE (MIT), CI stub.
- `AtProto.ServiceDefaults` + `AtProto.AppHost` (empty) + one placeholder worker; `aspire run` shows dashboard.
- **Accept:** `dotnet build` clean; `aspire run` boots the dashboard with the placeholder service healthy.

### M1 — Reactive firehose core, validated on the PUBLIC firehose  🟢 *(autopilot-ready once T0 lands; first tangible MVP)*
- **T0 (gates M1) — golden-vectors harness (run first):** a small tool/worker fetches real repo CARs
  (`com.atproto.sync.getRepo`) + sample records + a short firehose capture from the **public** network and
  freezes them under `tests/fixtures/` (CAR bytes, expected CIDs, expected MST roots). This is the enabler that
  makes every correctness-critical lib **self-verifiable** — the agent checks its output against frozen
  real-world data with no network or human in the test loop. **No secrets** (all public reads).
- Core libs (read path): `AtProto.Cbor`, `AtProto.Cid`, `AtProto.Car` (read), `AtProto.Repo` (MST read +
  record extraction), `AtProto.Identity`, `AtProto.Lexicon` (NSID/AT-URI/record-key). Adopt CarpaNet behind
  our interfaces.
- `AtProto.Firehose` client: connect to `wss://relay1.us-west.bsky.network/xrpc/com.atproto.sync.subscribeRepos`;
  `IAsyncEnumerable` + bounded `Channel` (backpressure); expose the **BCL `IObservable<RepoCommitEvent>` seam**
  (no Rx dependency); CAR-slice decode; collection filter; cursor persistence (SQLite/Redis); reconnect-with-resume.
- `FirehoseProbe` worker (in AppHost): subscribe, filter `app.bsky.feed.post`, log live rate + samples.
- Tests: decode known commit/CAR vectors; CID round-trip; reconnect resumes from cursor without gaps.
- **Accept:** `aspire run` → probe shows a live, filtered post stream from the real network; kill/restart →
  resumes from last seq; core codec tests green against reference vectors.

### M2 — AppView (presence board) over the public firehose  🟡 *(autopilot on the public stream; real-PDS write is an optional human gate)*
- Define custom lexicon `lexicons/<nsid>.status.json` (`{status: emoji, createdAt}`) + .NET record type.
- `AtProto.AppView`: consume firehose filtered to the status collection; **Rx.NET v7 projection** (the AppView
  references `System.Reactive`) maintaining latest-status-per-user (`GroupBy(did)`→`Replay(1)`) + top-N
  aggregation; index into Postgres (Aspire) / SQLite; XRPC endpoints `<nsid>.getPresence` / `<nsid>.getStatus`;
  minimal presence-board web UI.
- **Autopilot validation (no secrets):** point the projection at a **live public collection** off the public
  relay (the demo status NSID if any records exist, else `app.bsky.feed.post` for volume) and assert
  latest-wins-per-user updates in real time — proves the "appview over public relay" pattern with **zero
  credentials**.
- **Optional enrichment (human-gated, off the critical path):** with a Bluesky **app-password** (a secret a
  human supplies), write real `<nsid>.status` records to an existing PDS and watch them land on our board.
  Skippable — true self-authored-record validation also arrives for free at **M3** (our own PDS,
  `did:web:localhost`, no external account).
- **Accept (autopilot):** the presence board updates live from the public stream and `getPresence` returns
  correct latest-wins state; restart reconsumes from cursor — all with no credentials.

### M3 — Our own PDS (did:web)  🔴 *(autopilot-built against T0 vectors; ONE human gate: root-CID matches reference)*
- Server-side core: `AtProto.Repo` **MST build/mutate**, commit **signing** (k256 via `Secp256k1.Net`, p256 via
  BCL `ECDsa`), CAR **write**, TID gen, **sequencer** (SQLite).
- `AtProto.Pds` ASP.NET: `describeServer`, `createAccount` (did:web + signing key), `createSession`/`refresh`/
  `delete`/`getSession` (JWT), `com.atproto.repo.{createRecord,putRecord,deleteRecord,getRecord,listRecords,
  applyWrites}`, `com.atproto.sync.{subscribeRepos server, getRepo CAR, getRepoStatus, getLatestCommit}`,
  `requestCrawl` client, handle resolution (`/.well-known/atproto-did` + DNS), `/.well-known/did.json`.
- Emit Sync v1.1 `#commit` (with `prevData`/`ops[].prev`), `#identity`, `#account`, `#sync`. SQLite actor store.
- Tests: our `getRepo` CAR verifies with our reader **and** a third-party reader (CarpaNet); signatures verify;
  MST root matches after insert/delete sequences (vs reference vectors).
- Defer: OAuth AS, blobs, email, invites, PLC, service proxy.
- **Accept:** create account + write status via `StatusWeb` → PDS emits a valid, signature-verifiable
  `#commit` firehose that an independent consumer can read; `getRepo` returns a valid CARv1.

### M4 — Our own Relay  ✅ *(done — verified live under `aspire run`)*
- `AtProto.Relay` ASP.NET: accept `requestCrawl`; subscribe upstream PDS firehose(s) with per-host cursor
  persistence + redialer; validate (lenient: DID match + rev monotonic; **signature + MST inversion deferred to
  M6/strict — see the `// TODO M6` in `RelayService`**); assign **global monotonic seq**; re-emit `subscribeRepos`
  via the `AtProto.Firehose` broadcaster; `getRepoStatus`, `listHosts`, `getRepo` **redirect** to source PDS.
  Cursor state persisted to files (`relay-cursors/`: per-host upstream seq + a global seq seeded into the
  broadcaster on restart so downstream cursors stay valid). *(Postgres host/cursor state deferred; files suffice
  for the MVP.)*
- Repoint the AppView to consume **our** relay (via Aspire service discovery — AppHost `Firehose__Url` → relay).
- **Accept (met):** full **PDS → Relay → AppView** runs under `aspire run` (relay `active`, AppView `lastSeq`
  tracks the relay's global seq, board populated with emoji); `RelayLoopTests` proves a status write flows
  PDS→relay→appview→board **and** a relay restart resumes the per-host cursor + global seq with no reset and no
  reprocessing.

### M5 — Aspire orchestration + custom "native" integrations  🟢 *(autopilot after M4 — NEXT)*
- `AtProto.AppHost` wires pds + relay + appview + postgres [+ redis] with service discovery / health / OTel;
  appview firehose URL and pds `requestCrawl` target resolved via discovery.
- `AtProto.Hosting.Atproto`: `builder.AddAtprotoPds(name)`, `.AddAtprotoRelay(name)`, `.AddAtprotoAppView(name)`
  + `.WithReference()` so **relay auto-crawls the pds** and **appview auto-subscribes the relay**. Package naming
  per community-toolkit convention (`*.Aspire.Hosting.Atproto`).
- `AtProto.Integration.Tests` via `Aspire.Hosting.Testing`: boot AppHost → create account → write status →
  assert presence board; chaos: restart relay/appview, assert cursor resume.
- **Accept:** a 3-line AppHost (`AddAtprotoPds/Relay/AppView`) stands up a working self-hosted stack;
  integration test green.

### M6 — Stretch: "evil atproto" federation & polish  ⚪ *(deferred — not in the autopilot run)*
- Service-advertisement record `<nsid>.instance` published by each stack's embedded PDS + `requestCrawl`
  announce; a directory view discovers instances; two AppHosts discover each other (federation demo).
- did:plc support; OAuth 2.1 AS; Relay MST strict inversion; blob support; lexicon codegen; NuGet packaging of
  core libs + Aspire integrations; multi-target core libs (`net9.0;net10.0`).

---

## 5a. Autopilot readiness — is it shovel-ready?

**Short answer: the front half is genuinely "just run it"; the crux (MST write) is autopilot-*built* but keeps
one human gate.** "Shovel-ready" here = a task an agent can **run and self-verify with no human in the loop**.
A task clears the bar only when it has all five:

1. **Deterministic acceptance** — `dotnet build` clean / `dotnet test` green / a named endpoint returns a named
   shape. Not "looks right."
2. **Self-verifiable** — the agent runs the check itself; no human eyeballs, no external account.
3. **No blocking decision** — defaults already chosen (store, NSID, TFM, Rx scope…).
4. **No secrets** — nothing needs a credential the agent doesn't have.
5. **Bounded** — one lib / one endpoint / one suite.

**Readiness by milestone**

| Stage | Autopilot? | What makes it (un)ready |
|---|---|---|
| **M0** bootstrap | 🟢 run now | Pure scaffolding; `dotnet build` + `aspire run` self-check. |
| **T0** golden-vectors harness *(new, gates M1)* | 🟢 run now | Agent fetches real repo CARs + records from the **public** network and freezes them as fixtures. The enabler that makes all correctness-critical code self-verifiable. No secrets. |
| **M1** reactive core + firehose | 🟢 with T0 | Codecs TDD'd against T0 fixtures; firehose probe self-checks against the live public stream. |
| **M2** AppView projection | 🟡 re-scoped | *Autopilot acceptance:* project the **live public stream** (zero credentials) and assert latest-wins. The old "write to a real Bluesky PDS" needs an **app-password (secret)** → moved to an **optional human-gated** enrichment, off the critical path. |
| **M3** our PDS (**MST write**) | 🔴 one gate | Autopilot-*able* but is the ~2k-LOC correctness crux with no .NET reference. Build TDD against T0 vectors, but **insert one human review gate — "our root CID == reference root CID" — before wiring PDS writes.** |
| **M4** relay / **M5** Aspire integrations | 🟢 after M3 | Deterministic; `Aspire.Hosting.Testing` end-to-end self-check. |
| **M6** stretch | ⚪ deferred | Not in the autopilot run. |

**So, can we "just run it"?** **Yes for the `M0 → T0 → M1 → M2` lane** — a genuinely shovel-ready autopilot run
that lands a live, self-verified firehose + presence board with **no secrets and no human decisions**. Then
**M3 is the single checkpoint:** autopilot builds the MST against golden vectors, a human confirms the root-CID
match, and M4/M5 resume on autopilot. Without T0 the correctness-critical libs would *not* be shovel-ready
(nothing to self-check against) — which is why T0 is promoted to run first.

**Fleet fan-out (for `/fleet`).** The milestone **spine is sequential** (M0→T0→M1→M2/M3→M4→M5); parallelism lives
*inside* milestones:
- *After T0:* `AtProto.Cbor` ∥ `AtProto.Identity` ∥ `AtProto.Lexicon` (independent) → then `AtProto.Cid` (needs
  Cbor) → `AtProto.Car` (needs Cid+Cbor) → `AtProto.Repo` read (needs Car). Firehose client is independent of
  the codec libs and can proceed in parallel.
- *In M3:* the XRPC handlers (`repo.*`, `sync.*`, session/`describeServer`) are largely independent once the
  repo core + signing exist.

**Human gates, explicitly (everything else is autopilot):**
1. 🔴 **MST root-CID sign-off** (M3) — the one correctness gate.
2. 🟡 *(optional)* real-Bluesky **app-password**, only for the M2 "write to a live PDS" enrichment.
3. ⚪ final **NSID/domain** for the status lexicon (defaulted to a configurable NSID + `did:web:localhost`;
   non-blocking).

**Recommendation:** kick off autopilot on `M0 → T0 → M1 → M2` now (optionally `/fleet` the M1 codec libs), stop
at the M3 root-CID gate for a quick human confirm, then let autopilot finish M3→M5.

---

## 6. Key Technical Decisions & Defaults
- **TFM:** `net10.0` for apps/services; core libs multi-target later for NuGet.
- **Identity:** **did:web** for MVP (self-served `did.json`, no PLC); did:plc deferred to M6.
- **Auth:** legacy **JWT sessions** + app-password style for MVP; **service JWTs** for inter-service; OAuth AS deferred.
- **Persistence:** **SQLite** per-actor store + sequencer (PDS, atproto convention); **Postgres** (Aspire) for
  Relay host/cursor state and AppView index (JSONB for records); **Redis** optional for cursors/cache.
- **Crypto:** `Secp256k1.Net` (k256) + BCL `ECDsa` (p256, `nistP256`), low-S; sign =
  `sign(sha256(dag-cbor(unsigned)))`.
- **Reactive (see §2a):** BCL primitives (`IAsyncEnumerable` + bounded `Channel`, `FullMode.Wait`) on **all
  ingest hot paths**; core libs expose the **BCL `IObservable<T>` seam with no `System.Reactive` dependency**.
  **Rx.NET v7 is scoped to the AppView/projection layer** (lean ~1.5 MB on net10). Cursor checkpoint after
  durable write. AsyncRx (alpha) not used.
- **CarpaNet:** adopt for read/client path (M1–M2) behind `AtProto.*` interfaces; pin versions; vendor
  CBOR/CID/CAR/MST-read if stability bites; **own** all server-side code regardless.

## 7. Risks & Mitigations
- **MST correctness (highest risk).** Deterministic tree; subtle depth/fanout/prune rules. → Build against the
  spec + **golden test vectors** (T0); cross-verify root CIDs vs real repos and CarpaNet/indigo before wiring PDS
  writes. This is the **one human review gate** in the autopilot run (§5a): a human confirms our root CID equals
  the reference before PDS writes go live.
- **CarpaNet alpha instability.** → interface seam + version pin + vendor-ready plan (~2k LOC).
- **Firehose backpressure / ordering / at-least-once.** → bounded Channel (`Wait`), single-writer cursor
  checkpoint after durable write, treat gaps/dupes as errors, resume via seq.
- **Sync v1.1 details** (`prevData`, `ops[].prev`, deprecated `tooBig`/`blobs`, seq bases). → bake into codecs +
  conformance tests; validate our emitted firehose with an independent consumer.
- **Scope creep.** → milestones are hard gates; OAuth/PLC/blobs/labels explicitly deferred to M6.

## 8. Testing Strategy
- **Unit/interop:** CBOR, CID round-trip, CAR read+write round-trip, MST insert/delete → root CID vs vectors,
  commit signing/verification, TID monotonicity.
- **Component:** firehose reconnect/cursor/backpressure; PDS emits consumer-verifiable `#commit`; relay seq
  monotonic + per-host cursor resume.
- **End-to-end:** `Aspire.Hosting.Testing` — account→write→relay→appview→presence assertions + restart/chaos.
- **External conformance:** read a real bsky repo CAR; (M2) index real records written to a real PDS.

## 9. Open Decisions (defaults chosen; confirm at review or as we go)
1. **NSID + domain** for the status lexicon (e.g. `com.example.status` vs a domain you own like `place.selfhost.status`).
   Default: configurable NSID, `did:web:localhost` for local dev. *(Not blocking.)*
2. **Depend-on vs vendor CarpaNet** for the read path. Default: **depend** (pinned) in M1–M2, vendor if needed.
3. **AppView store:** Postgres (Aspire) vs SQLite. Default: **Postgres**.
4. **M2 validation:** write demo status records to a real Bluesky PDS (app-password/OAuth) to exercise the public
   relay path before our PDS exists. Default: **yes**.

## 10. References
- Specs: atproto.com/specs {event-stream, sync, repository, did, handle, xrpc, lexicon}; guides/{overview, the-at-stack}.
- Reference impls: bluesky-social/{atproto (TS), indigo (Go relay/tap), pds, statusphere-example-app}.
- .NET: drasticactions/CarpaNet (alpha), blowdart/idunno.Bluesky, dotnet/reactive (System.Reactive 7),
  learn.microsoft.com Aspire custom integrations, CommunityToolkit/Aspire.
- Inspiration: nekomimi.leaflet.pub "syndicating radio with evil atproto" (embedded tiny PDS + requestCrawl +
  indexer-as-library = our Aspire-component thesis).
