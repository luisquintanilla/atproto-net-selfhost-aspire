# OAuth: how a real client logs in to your PDS

This is the design and threat model for the atproto OAuth Authorization Server that the self-hosted
Personal Data Server (PDS) exposes. It is written to be read before the code, because OAuth is the most
security-critical surface in the stack. If you only want to try it, jump to
[Trying it with a real client](#trying-it-with-a-real-client). If you want to understand or audit it,
read straight through.

> Status: this document describes the target design. The
> [MVP boundary](#what-ships-first-the-mvp-boundary) section is explicit about what is built first and
> what is a fast follow.

## Why OAuth is the interop gate

Today the PDS signs you in with a dev-grade token. You post a handle and password to
`com.atproto.server.createSession` and get back an HMAC-signed session JWT (the old "app password"
model). That is fine for seeding demo data and for tests, and we keep it as the zero-config default.
It is not, however, how the wider atproto network authenticates.

A *real* atproto client (a browser app, a mobile app, a "Login with atproto" button on someone else's
site) never sees your password. It authenticates you through **OAuth**, gets a token that is
cryptographically bound to a key only that client holds, and uses that token to make authorized writes
to your repository. OAuth is therefore the gate: it is the difference between "a server that speaks the
demo dialect" and "a server the network can actually log in to."

This document is about closing that gap, correctly and safely.

## atproto OAuth is a specific profile (a primer)

If you know OAuth 2.0, most of your intuition carries over: there is a client, an authorization server
(AS) that authenticates the user and issues tokens, and a resource server (RS) that accepts those
tokens. The authorization-code flow with a redirect is the same shape you have seen before.

What is different is that atproto pins down a **profile** of OAuth 2.1 with four parts that are unusual
if you have only used classic OAuth. These four are the whole game, so it is worth internalizing them
up front.

### 1. The client has no secret, and its ID is a URL

In classic OAuth you register your app with the provider ahead of time and receive a `client_id` and a
`client_secret`. atproto has no registration step and no secret. Instead:

- The `client_id` **is an HTTPS URL** that points at a public JSON document
  (`client-metadata.json`), following the `draft-parecki-oauth-client-id-metadata-document` draft.
- The AS **fetches that URL at request time** and reads the client's redirect URIs, allowed scopes,
  token-auth method, and (for confidential clients) its public keys from the document.
- There is a single special case for local development: a `client_id` under `http://localhost` is
  allowed, and the AS synthesizes a *virtual* metadata document for it instead of fetching anything.

The upside is that anyone can stand up a client by hosting one JSON file. The cost is that fetching a
URL supplied by a caller is a Server-Side Request Forgery (SSRF) risk, so the fetch has to be hardened.
That hardening is a first-class part of this design.

### 2. Pushed Authorization Requests (PAR) are mandatory

Rather than putting the authorization parameters in the browser's address bar, the client first
**POSTs them to a PAR endpoint** (RFC 9126) over a direct back-channel call. The AS stores them and
returns a short-lived, single-use `request_uri` handle. Only then does the client send the user's
browser to `/authorize`, carrying just `client_id` and `request_uri`. Nothing sensitive rides in the
URL, and the parameters cannot be tampered with in the browser.

### 3. PKCE is mandatory, and only S256

Proof Key for Code Exchange (RFC 7636) stops an attacker who intercepts the authorization code from
redeeming it. The client picks a random `code_verifier`, sends its SHA-256 hash (`code_challenge`) in
the PAR request, and reveals the verifier only when it redeems the code. atproto **requires the S256
method** and bans the `plain` method. Codes are single-use, and reuse revokes the session.

### 4. DPoP binds every token to a key the client holds

This is the part that most changes the security posture. Under classic OAuth, an access token is a
bearer token: whoever holds it can use it. atproto tokens are **sender-constrained** with DPoP
(Demonstrating Proof of Possession, RFC 9449):

- The client generates an ephemeral `ES256` (P-256) keypair.
- On **every** token request and **every** resource request, the client attaches a small signed JWT
  (the "DPoP proof") proving it holds the private key.
- The AS binds the issued token to the public key's thumbprint (`cnf.jkt`). A stolen token is useless
  without the matching private key.
- The server also hands out a **nonce** (via the `DPoP-Nonce` header) that the client must echo in its
  next proof. This stops proofs from being pre-computed or replayed. The client learns the first nonce
  by making a request, getting a `400 use_dpop_nonce` response, and retrying.

Put together: a URL-identified, secretless client pushes its request, proves possession of a key on
every call, and receives a token that is worthless to anyone who steals it. The rest of this document
is how the PDS implements exactly that.

## The actors

- **You (the account holder).** A DID hosted on this PDS, with a repository and a password.
- **The client.** Any app that wants to act on your behalf. Identified by its `client_id` URL. Holds an
  ephemeral DPoP key.
- **The Authorization Server (AS).** Part of the PDS. Authenticates you (password + cookie), shows the
  consent screen, and issues tokens.
- **The Resource Server (RS).** Also part of the PDS. Accepts DPoP-bound access tokens on the XRPC write
  endpoints. Here the AS and RS are the same process, so there is no separate "entryway" to coordinate
  with.
- **Identity.** Your DID document (served by the PDS) advertises the PDS endpoint. That is what lets a
  client start from a handle and discover where to authenticate.

## The flow, end to end

The client starts from your handle or DID and finishes with a token it can use to write to your repo.

```
  client                              AS + RS (your PDS)                       you (browser)
    |                                        |                                      |
    | 1. resolve handle -> DID -> DID doc    |                                      |
    |    -> PDS endpoint                     |                                      |
    | 2. GET /.well-known/oauth-protected-   |                                      |
    |    resource  ------------------------> | { authorization_servers: [PDS] }     |
    | 3. GET /.well-known/oauth-authorization|                                      |
    |    -server  -------------------------> | issuer, endpoints, S256, ES256, PAR  |
    |    (verify issuer == PDS origin)       |                                      |
    |                                        |                                      |
    | 4. POST /oauth/par (params + PKCE      |  validate client-metadata.json,      |
    |    challenge + DPoP proof) ----------> |  redirect_uri, scope, PKCE; store;   |
    |    <---- request_uri + DPoP-Nonce ---- |  return single-use request_uri       |
    |                                        |                                      |
    | 5. send browser to /oauth/authorize? --------------------------------------->  |
    |    client_id & request_uri             |  login form (password) + consent     |
    |                                        | <----- password, approve ----------  |
    |                                        |  bind DPoP jkt; mint single-use code  |
    |    <--- 302 redirect_uri?code&state&iss ------------------------------------   |
    |                                        |                                      |
    | 6. POST /oauth/token (code +           |  verify PKCE, DPoP jkt, redirect_uri; |
    |    code_verifier + DPoP proof) ------> |  issue DPoP-bound access + refresh    |
    |    <-- access, refresh, sub=DID, scope,|  tokens; return sub + scope           |
    |        DPoP-Nonce --------------------  |                                      |
    |                                        |                                      |
    | 7. XRPC write + access token + DPoP -> |  RS: validate cnf.jkt, htu/htm/ath,  |
    |    <---- 200 (or 400 use_dpop_nonce) -- |  scope; write to repo; issue nonce   |
```

> The polished SVG version of this sequence:

![OAuth login flow: the client discovers and verifies the server, pushes the request (PAR), sends you to the browser to log in and consent, exchanges the code for sender-constrained DPoP-bound tokens, and writes to your repo with a fresh proof on every request.](img/oauth-flow.svg)

Read the numbered steps as: **discover** the server and verify it is really yours (1 to 3),
**push** the request and get a handle (4), **log in and consent** in the browser (5), **exchange** the
code for sender-constrained tokens (6), and **use** the token with a fresh proof on every write (7).

## The trust model: why the client believes this AS is yours

A login is only meaningful if the client can be sure it is talking to *your* authorization server and
not an impostor. atproto anchors that trust in your DID:

1. The client resolves your handle to a DID, and resolves the DID to a DID document.
2. The DID document advertises the `#atproto_pds` service endpoint (this PDS). This stack already serves
   that document for both `did:web` and `did:plc` accounts.
3. The client fetches `/.well-known/oauth-protected-resource` from the PDS to learn which AS to trust,
   then fetches `/.well-known/oauth-authorization-server` from that AS.
4. The AS metadata's `issuer` **must equal** the PDS origin the client arrived at. The AS also returns
   the `iss` value on the authorization redirect (`authorization_response_iss_parameter_supported` is
   true), so the client can confirm the response came from the same issuer it expected.

Because identity flows from the DID, and the DID document is served by the PDS, the chain
handle -> DID -> PDS -> RS metadata -> AS metadata -> `issuer` closes on itself. There is no central
registry to trust.

![The DID-anchored trust chain: a handle resolves to a DID, the DID document advertises the #atproto_pds endpoint, the PDS protected-resource metadata names the authorization server, and the AS metadata issuer must equal the PDS origin. The chain closes on itself, so a login can only succeed against the server your identity points at.](img/oauth-trust-chain.svg)

## What the PDS serves

### Metadata documents

- `GET /.well-known/oauth-protected-resource` returns
  `{ "authorization_servers": ["<PDS origin>"], "scopes_supported": [...] }`. This is the RS half.
- `GET /.well-known/oauth-authorization-server` returns the AS descriptor:
  - `issuer` (the PDS origin), `authorization_endpoint`, `token_endpoint`,
    `pushed_authorization_request_endpoint`
  - `response_types_supported: ["code"]`,
    `grant_types_supported: ["authorization_code", "refresh_token"]`
  - `code_challenge_methods_supported: ["S256"]`
  - `token_endpoint_auth_methods_supported: ["none", "private_key_jwt"]`,
    `token_endpoint_auth_signing_alg_values_supported: ["ES256"]`
  - `scopes_supported: ["atproto", "transition:generic", ...]`
  - `dpop_signing_alg_values_supported: ["ES256"]`
  - `authorization_response_iss_parameter_supported: true`
  - `require_pushed_authorization_requests: true`
  - `client_id_metadata_document_supported: true`

Both documents (and the PAR and token endpoints) send permissive CORS headers, because browser clients
read them cross-origin.

### Endpoints

- `POST /oauth/par` accepts the pushed request, validates it, and returns a `request_uri`.
- `GET /oauth/authorize` and `POST /oauth/authorize` render the login and consent interface and issue
  the authorization code.
- `POST /oauth/token` exchanges the code (and later, refresh tokens) for DPoP-bound tokens.
- The existing XRPC write endpoints (`com.atproto.repo.*`, `uploadBlob`) additionally accept a
  DPoP-bound OAuth access token, alongside the existing session-JWT path.

### Tokens

- **Access token:** a short-lived signed JWT (15 minutes or less) carrying `sub` (your DID), `scope`,
  and `cnf.jkt` (the DPoP key thumbprint). Because the PDS is its own RS, it validates this locally.
- **Refresh token:** an opaque, server-side, single-use value. Redeeming it rotates it (issues a new
  one and invalidates the old), and it is bound to the same DPoP key and client. Public-client sessions
  expire within two weeks.

Both token types are opaque to the client, which never needs to read them. That is exactly what the
spec expects.

### Scopes

The PDS supports `atproto` (always required), the explicit compatibility scope
`transition:generic`, and granular repository permissions. A granular grant is either
`repo:<collection>` (all repository actions), `repo:<collection>?action=create&action=update` (selected
actions), or `repo:*?action=create` (a full collection wildcard with selected actions). Partial
collection wildcards are rejected.

OAuth PAR resolves `include:<permission-set-nsid>` declarations through the configured permission-set
resolver. The resulting grants are validated once, expanded into the access-token scope, and persisted
with the PAR, authorization code, and OAuth session. Expired or stale permission sets and unknown
permission declarations fail the authorization request; a refresh reuses the persisted snapshot rather
than resolving the set again. Hosts can replace the generic resolver seam when the shared atproto-dotnet
permission library is available.

## The threat model and the security bar

Every item below is something an attacker could try, paired with the defense. Each defense gets a
negative test, so the suite proves the attack fails, not just that the happy path works.

### Fetching client metadata (SSRF)

Because `client_id` is a URL we fetch, a malicious client could point it at an internal address.

- Only `https` URLs are fetched (the `http://localhost` dev client is never fetched; it is synthesized).
- Resolved IP addresses in private, loopback, link-local, or otherwise reserved ranges are rejected.
- The response size and total time are capped, and redirects to disallowed hosts are refused.
- The same hardening applies to fetching a confidential client's `jwks_uri`.

### Authorization request and code

- The `request_uri` from PAR is single-use, short-lived (about 60 seconds), and bound to the client and
  the DPoP key thumbprint from the PAR call.
- The authorization code is single-use and short-lived. Redeeming it twice revokes the session and its
  tokens.
- The `redirect_uri` must exactly match one listed in the client's metadata.
- The code carries the PKCE challenge, the DPoP thumbprint, the granted scope, and your DID, so none of
  those can be swapped at the token step.
- The resolved permission snapshot is carried through PAR, authorization code, session, refresh, and
  access-token issuance, so a later permission-set change cannot widen an existing session.

### PKCE

- Only `S256` is accepted; `plain` is refused.
- A missing or too-short verifier is refused, and a `code_challenge` cannot be reused across requests.

### DPoP

- The proof's `ES256` signature is verified against the embedded public key.
- `htm` (HTTP method) and `htu` (HTTP URI) must match the actual request.
- `iat` must be fresh (small clock skew allowed).
- The server nonce must be current (a small window of recently-rotated nonces is accepted); a
  missing or stale nonce returns `400 use_dpop_nonce` with a fresh `DPoP-Nonce` header, and the client
  retries.
- The `jti` must not have been seen before (replay protection, scoped to the nonce window).
- On resource requests, `ath` must equal the hash of the presented access token, and the token's
  `cnf.jkt` must match the proof key.
- Nonces rotate on a short interval (five minutes or less) and every response carries a fresh one.

A subtle but important detail: JOSE `ES256` signatures are **not** required to use low-S encoding, while
this stack's commit-signature verifier deliberately rejects high-S. The OAuth library therefore uses its
own JOSE verifier that accepts both, rather than reusing the commit-signature path, so that valid client
proofs are never rejected.

### Tokens and sessions

- Access tokens live 15 minutes or less; refresh tokens are single-use with rotation and revocation.
- A refresh token is bound to its DPoP key and client; a cross-key or cross-client refresh is refused.
- Repository writes enforce the snapshot's collection and action grants before any mutation.

### Interface and transport

- `state` is echoed back to the client, and no session data is smuggled inside it.
- The consent screen does **not** render an unverified `client_name`, `logo_uri`, or `client_uri` for
  unknown clients (an impersonation defense). It shows the full `client_id` URL so you can see exactly
  who is asking.
- Login and consent forms carry CSRF protection; the session cookie is `HttpOnly`, `Secure`, and
  `SameSite`.
- Secret and code comparisons are constant-time. CORS is scoped to the metadata, PAR, and token
  endpoints.

## What ships first (the MVP boundary)

**Shipped:**

- Public clients (`token_endpoint_auth_method = none`) and the `http://localhost` dev client.
- Confidential clients (`private_key_jwt` client assertions with `jwks` / `jwks_uri`), including the
  key-continuity binding that requires the client-assertion key to differ from the DPoP key. Confidential
  sessions get a longer lifetime than public ones.
- The `atproto` and `transition:generic` scopes.
- Collection/action permission enforcement for `createRecord`, `putRecord`, and `deleteRecord`, including
  permission-set expansion and durable OAuth snapshots.
- Runtime Lexicon resolution and validation through an optional file-backed catalog or host-provided
  resolver; the default non-authoritative resolver preserves existing unconfigured collections.
- The full PAR, PKCE, DPoP-with-nonces, and DID-anchored discovery machinery above.
- RS enforcement on the write endpoints, additive to the existing session-JWT path.
- An in-memory store by default, and a durable SQLite store under the production profile.

**Not included in this generic slice:** Mycelium-specific permission-set schemas, governance,
deployment, UI, or scenario code. Hosts may add protocol-defined permission-set resolvers through the
service registration seam.

Keeping the boundary explicit means the first cut is small enough to verify thoroughly, and the deferred
items have a clear home.

## Trying it with a real client

Self-verification is a first-class deliverable, because OAuth is hard to eyeball. Three layers prove it:

1. **In-process integration tests** drive the whole flow (PAR, authorize, token, DPoP-authorized write)
   with a purpose-built test client, plus a negative matrix (replay, wrong key, PKCE mismatch, reused
   code, reused refresh, SSRF-blocked metadata, missing-nonce retry, and confidential-client assertion
   misuse).
2. **A real reference client.** The primary path is the pure-.NET `CarpaNet.OAuth` client library, so
   the whole interop check stays in the .NET toolchain. The official TypeScript
   `@atproto/oauth-client-node` is the secondary cross-check. Both drive a login and an authorized write
   against this AS.
3. **Known-answer vectors** for the underlying RFCs: RFC 7638 (`jkt` thumbprint), RFC 7636 (PKCE),
   RFC 9449 (DPoP).

Real external clients fetch your `client-metadata.json` and follow redirects, so true end-to-end interop
needs the PDS reachable over HTTPS (a TLS tunnel in local development is enough). The
[OAuth interop guide](oauth-interop.md) records the exact procedure for all three layers.

## Code pointers

- Protocol primitives (DPoP, PKCE, JWK / `jkt`, client-metadata fetch, token models): the
  `AtProto.OAuth` core library (packable, so others can build their own AS or client).
- Endpoints, the authorization interface, and RS enforcement: `src/services/AtProto.Pds`.
- The storage seam: `IOAuthStore` (in-memory default, SQLite under the production profile), mirroring
  `IPdsPersistence`.
- The diagrams: `docs/diagrams/generate.mjs` renders the OAuth sequence into `docs/img/`.

## References

- [atproto OAuth specification](https://atproto.com/specs/oauth)
- [OAuth 2.1 draft](https://datatracker.ietf.org/doc/draft-ietf-oauth-v2-1/)
- [RFC 9126: Pushed Authorization Requests](https://www.rfc-editor.org/rfc/rfc9126)
- [RFC 7636: PKCE](https://www.rfc-editor.org/rfc/rfc7636)
- [RFC 9449: DPoP](https://www.rfc-editor.org/rfc/rfc9449)
- [RFC 7638: JWK Thumbprint](https://www.rfc-editor.org/rfc/rfc7638)
- [draft-parecki-oauth-client-id-metadata-document](https://datatracker.ietf.org/doc/draft-parecki-oauth-client-id-metadata-document/)
