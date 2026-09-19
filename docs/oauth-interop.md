# OAuth interop guide

OAuth is hard to eyeball. A login either works against a real client or it does not, and most of the
security lives in the parts you cannot see (a replayed proof, a swapped key, a reused code). So proving
this PDS speaks the [atproto OAuth profile](oauth.md) is a first-class deliverable, not an afterthought.

This guide records the exact procedure. There are three layers of proof, from fastest to most
convincing.

## Layer 1: in-process integration tests

These drive the whole flow (PAR, authorize, token, DPoP-authorized write) with a purpose-built test
client, and they run on every build with no network and no browser. They are deterministic, so they are
also where the full negative matrix lives: for every rule in the profile, there is a test that the
server rejects the thing it must reject.

| Test class | What it proves |
| --- | --- |
| Shared repository OAuth tests | The protocol primitives in isolation: DPoP proof validation and rolling nonces, PKCE `S256`, JWK and `jkt` thumbprints, client-metadata parsing and the SSRF guard, client assertions, and access-token issue and validate. Includes the RFC known-answer vectors (Layer 3 below). |
| `OAuthMetadataTests` | The two discovery documents advertise the right issuer, endpoints, `S256`, `ES256`, DPoP, PAR-required, `none` and `private_key_jwt` auth, and the `atproto` / `transition:generic` scopes. |
| `OAuthParTests` | PAR issues a `request_uri` only after a DPoP nonce challenge, and rejects a replayed proof, a proof bound to the wrong endpoint, `plain` PKCE, an unregistered `redirect_uri`, and a scope beyond the client's registration. |
| `OAuthAuthorizeTests` | The authorization interface issues a code on approval, returns `access_denied` on deny, re-renders (no code) on a wrong password, and rejects a missing CSRF token or an unknown `request_uri`. |
| `OAuthTokenTests` | The token endpoint issues DPoP-bound tokens for a valid code, and rejects a PKCE mismatch and a wrong DPoP key. Code reuse revokes the session, and refresh rotation works. |
| `OAuthResourceTests` | The resource server accepts a DPoP-bound access token, challenges then accepts when the nonce is missing, and rejects a proof with no `ath`, a wrong DPoP key, a replayed proof, or a request with no write scope. The existing app-password session still works (OAuth is additive). |
| `OAuthConfidentialClientTests` | A confidential client completes the full flow with a `private_key_jwt` assertion and authorizes a write, and the server rejects a missing assertion at the token endpoint, a replayed assertion `jti`, and a DPoP proof that reuses the assertion key. |

Run them:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# The in-process flow and the negative matrix.
dotnet test tests/AtProto.Pds.Tests/AtProto.Pds.Tests.csproj --filter "FullyQualifiedName~OAuth"
```

## Layer 2: reference-client interop over HTTPS

The strongest proof is a client we did not write completing a real login and an authorized write. This
is the actual conformance gate: it exercises client-metadata fetching, redirects, DPoP nonce retries,
and token use exactly as a third party would.

### Why HTTPS is required

A real external client fetches your `client-metadata.json`, follows the authorize redirect, and calls
the token and resource endpoints over the network. The atproto profile requires `https` for everything
except the `http://localhost` dev-client exception. So end-to-end interop needs the PDS reachable over
HTTPS. Two ways to get there:

- **A TLS tunnel** (for example `cloudflared` or `ngrok`) in front of the running PDS, so the PDS has a
  public `https://` origin. Set `Pds__PublicUrl` to that origin so the issuer, DID document, and
  metadata all agree. This mirrors how the firehose work was validated against the live Bluesky relay.
- **The `http://localhost` dev client**, for a purely local check. The server synthesizes a virtual
  client metadata document for a loopback `client_id`, so no hosting and no public metadata are needed.
  This proves the flow but not the public-metadata fetch, so use it alongside, not instead of, a tunnel
  run.

### Primary: the pure-.NET client (`CarpaNet.OAuth`)

[`CarpaNet`](https://github.com/drasticactions/CarpaNet) is the active .NET atproto client (the
successor to FishyFlip), and its `CarpaNet.OAuth` package implements the client side of PAR, PKCE, and
DPoP. Driving it against this AS keeps the whole interop check inside the .NET toolchain.

Procedure:

1. Start the PDS (in-memory profile is fine) and expose it over HTTPS with a tunnel, or use the
   `http://localhost` dev client for a local run. Confirm `Pds__PublicUrl` matches the reachable origin.
2. Create an account on the PDS (the existing `createAccount` path) so there is an identity to log in
   as.
3. Point a small `CarpaNet.OAuth` console app at the account's handle. The client resolves the handle
   to a DID, reads the DID document for the `#atproto_pds` endpoint, then follows
   `oauth-protected-resource` to `oauth-authorization-server` and checks the `issuer`.
4. Complete the flow: the client performs PAR, opens the authorize URL in a browser, you log in with the
   account password and consent, and the client exchanges the code at the token endpoint.
5. With the DPoP-bound access token, have the client write one `place.selfhost.status` record and read
   it back.

A green run means: discovery resolved, the issuer verified, PAR and PKCE and DPoP all round-tripped
(including at least one `use_dpop_nonce` retry), tokens came back with `sub` set to the account DID and
a scope that includes `atproto`, and the write landed in the repo.

### Cross-check: the official TypeScript client

[`@atproto/oauth-client-node`](https://www.npmjs.com/package/@atproto/oauth-client-node) is the
reference implementation from the Bluesky team. Running the same login and write through it, over the
same HTTPS tunnel, is an independent cross-check that we match the reference and not just our own
assumptions. Node.js is only needed for this optional second pass.

## Layer 3: known-answer RFC vectors

The primitives are pinned to the specs, not just to round-tripping against themselves. These live in the shared repository's
`tests/AtProto.OAuth.Tests/RfcVectorsTests.cs`:

- **RFC 7638** (JWK thumbprint): the `jkt` of the example key.
- **RFC 7636 Appendix B** (PKCE): the `S256` challenge for the published verifier.
- **RFC 9449** (DPoP): the example proof from Figures 2 and 4 verifies, and the embedded key's
  thumbprint equals the published `jkt` from Figure 9.

## What a full pass demonstrates

- **Discovery and trust.** A client that only knows a handle can find the server and prove the
  authorization server is the one the account's identity points at.
- **The mandatory machinery.** PAR, PKCE `S256`, and DPoP with server-issued nonces all work on the
  happy path and fail closed on every abuse in the matrix.
- **Real authorization.** A third-party client obtains DID-anchored, DPoP-bound tokens and uses them to
  write to a repo, with the existing app-password path still working alongside.

That combination is what lets us call this a real atproto OAuth authorization server, not a
demo-grade one.
