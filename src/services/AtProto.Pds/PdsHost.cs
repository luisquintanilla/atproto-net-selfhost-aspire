using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using AtProto.Cbor;
using AtProto.Firehose;
using AtProto.OAuth;
using AtProto.Repo;

namespace AtProto.Pds;

/// <summary>
/// Composes the PDS ASP.NET application: DI wiring plus the XRPC surface
/// (<c>com.atproto.server.*</c>, <c>com.atproto.repo.*</c>, <c>com.atproto.sync.*</c>,
/// <c>com.atproto.identity.resolveHandle</c>), the did:web documents, and the
/// <c>subscribeRepos</c> WebSocket firehose. Kept separate from <c>Program</c> so tests can build
/// and host the same app in-process.
/// </summary>
public static class PdsHost
{
    public static WebApplication Build(string[] args, Action<PdsOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        var options = new PdsOptions();
        builder.Configuration.GetSection("Pds").Bind(options);
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PdsIdentity>();
        builder.Services.AddSingleton<FirehoseBroadcaster>();
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<BlobStore>();
        RegisterPersistence(builder.Services, options);
        RegisterOAuthStore(builder.Services, options);
        builder.Services.AddSingleton<PdsService>();
        builder.Services.AddHttpClient();
        builder.Services.AddCors(cors => cors.AddPolicy(OAuthCorsPolicy, policy => policy
            .AllowAnyOrigin()
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("Authorization", "DPoP", "Content-Type")
            .WithExposedHeaders("DPoP-Nonce", "WWW-Authenticate")));
        // The DPoP nonce secret is per-process: a restart invalidates outstanding nonces, and the
        // client simply retries after a use_dpop_nonce challenge, so no durable secret is needed.
        builder.Services.AddSingleton(new DpopNonceService(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton(_ => new ClientMetadataResolver(new ClientMetadataResolverOptions
        {
            AllowConfidentialClients = false,
            AllowLocalhostClient = true,
        }));
        builder.Services.AddHostedService<PdsInstanceAdvertiser>();

        WebApplication app = builder.Build();
        RehydrateFromStorage(app.Services);
        app.UseWebSockets();
        app.UseCors();
        app.MapDefaultEndpoints();
        MapErrorHandling(app);
        MapServer(app);
        MapRepo(app);
        MapBlob(app);
        MapSync(app);
        MapIdentity(app);
        MapOAuthMetadata(app);
        MapOAuthPar(app);
        MapOAuthAuthorize(app);
        return app;
    }

    /// <summary>The CORS policy applied to the OAuth surface so browser apps can call it cross-origin.</summary>
    internal const string OAuthCorsPolicy = "oauth";

    /// <summary>The lifetime of a pushed authorization request's <c>request_uri</c>, in seconds.</summary>
    private const int ParTtlSeconds = 300;

    /// <summary>The lifetime of an issued authorization code, in seconds (exchanged immediately).</summary>
    private const int AuthorizationCodeTtlSeconds = 60;

    /// <summary>The double-submit CSRF cookie for the authorization interface forms.</summary>
    private const string OAuthCsrfCookie = "oauth_csrf";

    /// <summary>Register the storage seam: in-memory by default, SQLite when <c>Pds:Storage=sqlite</c>.</summary>
    private static void RegisterPersistence(IServiceCollection services, PdsOptions options)
    {
        if (string.Equals(options.Storage, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            string path = string.IsNullOrWhiteSpace(options.SqlitePath)
                ? Path.Combine(Environment.CurrentDirectory, "pds.db")
                : options.SqlitePath;
            services.AddSingleton<IPdsPersistence>(_ => new SqlitePdsPersistence(path));
        }
        else
        {
            services.AddSingleton<IPdsPersistence, NullPdsPersistence>();
        }
    }

    /// <summary>Register the OAuth store seam: in-memory by default, SQLite when <c>Pds:Storage=sqlite</c>.</summary>
    private static void RegisterOAuthStore(IServiceCollection services, PdsOptions options)
    {
        if (string.Equals(options.Storage, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // A sibling database to pds.db keeps the ephemeral OAuth write path from contending with
            // the repository write path on a single connection.
            string basePath = string.IsNullOrWhiteSpace(options.SqlitePath)
                ? Path.Combine(Environment.CurrentDirectory, "pds.db")
                : options.SqlitePath;
            string oauthPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? Environment.CurrentDirectory,
                "oauth.db");
            services.AddSingleton<IOAuthStore>(_ => new SqliteOAuthStore(oauthPath));
        }
        else
        {
            services.AddSingleton<IOAuthStore, InMemoryOAuthStore>();
        }
    }

    /// <summary>Replay persisted accounts and blobs into the in-memory read models before serving.</summary>
    private static void RehydrateFromStorage(IServiceProvider services)
    {
        var persistence = services.GetRequiredService<IPdsPersistence>();
        if (persistence is NullPdsPersistence)
            return;

        var accounts = services.GetRequiredService<AccountStore>();
        foreach (PersistedAccount persisted in persistence.LoadAccounts())
            accounts.Add(AccountPersistence.ToAccount(persisted));

        var blobs = services.GetRequiredService<BlobStore>();
        foreach (StoredBlob blob in persistence.LoadBlobs())
            blobs.Put(blob.Bytes, blob.ContentType);
    }

    private static void MapErrorHandling(WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (XrpcException ex)
            {
                context.Response.StatusCode = ex.Status;
                await context.Response.WriteAsJsonAsync(new { error = ex.Error, message = ex.Message });
            }
        });

    /// <summary>The atproto OAuth discovery documents: protected-resource and authorization-server metadata.</summary>
    private static void MapOAuthMetadata(WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", (PdsService pds) =>
            Results.Content(
                ProtectedResourceMetadata.ForIssuer(pds.Identity.PublicUrl, AuthorizationServerMetadata.DefaultScopes).ToJson(),
                "application/json"))
            .RequireCors(OAuthCorsPolicy);

        app.MapGet("/.well-known/oauth-authorization-server", (PdsService pds) =>
            Results.Content(
                AuthorizationServerMetadata.ForIssuer(pds.Identity.PublicUrl).ToJson(),
                "application/json"))
            .RequireCors(OAuthCorsPolicy);
    }

    /// <summary>
    /// The pushed authorization request endpoint (RFC 9126, atproto profile). The client posts every
    /// authorization parameter here over a DPoP-proofed request and receives a single-use
    /// <c>request_uri</c> that stands in for those parameters at the authorize endpoint. This is where
    /// the DPoP key thumbprint is bound to the pending authorization, the client-metadata document is
    /// resolved and validated, and PKCE/redirect_uri/scope are checked up front.
    /// </summary>
    private static void MapOAuthPar(WebApplication app) =>
        app.MapPost("/oauth/par", async (
            HttpContext context,
            PdsService pds,
            IOAuthStore store,
            ClientMetadataResolver resolver,
            DpopNonceService nonces) =>
        {
            if (!context.Request.HasFormContentType)
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest,
                    "request must be application/x-www-form-urlencoded");

            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);

            // The DPoP proof is mandatory on PAR; it binds the token-holder key (jkt) to the request.
            // htu is derived from the advertised endpoint, not the incoming request, so it stays
            // correct behind a TLS-terminating reverse proxy.
            string htu = OAuthEndpointUrl(pds, "/oauth/par");
            DpopValidationResult dpop = DpopValidator.Validate(
                context.Request.Headers["DPoP"].ToString(),
                new DpopValidationOptions { ExpectedHtm = "POST", ExpectedHtu = htu },
                nonces);
            if (dpop.Status == DpopValidationStatus.NonceRequired)
            {
                context.Response.Headers["DPoP-Nonce"] = nonces.Current();
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.UseDpopNonce,
                    "authorization server requires a DPoP nonce");
            }
            if (!dpop.IsValid)
            {
                context.Response.Headers["DPoP-Nonce"] = nonces.Current();
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidDpopProof, dpop.Error);
            }
            DpopProof proof = dpop.Proof!;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            // Reject a replayed proof. The jti is retained for the nonce acceptance window.
            if (!store.TryRegisterJti(proof.Jti, now.AddMinutes(10)))
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidDpopProof,
                    "DPoP proof has already been used");

            // RFC 9126 section 2.1: the PAR endpoint must reject a request_uri parameter.
            if (!string.IsNullOrEmpty(form["request_uri"]))
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest,
                    "request_uri is not allowed at the PAR endpoint");

            string? clientId = NullIfEmpty(form["client_id"].ToString());
            if (clientId is null)
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest, "client_id is required");

            if (form["response_type"] != "code")
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest, "response_type must be 'code'");

            string? redirectUri = NullIfEmpty(form["redirect_uri"].ToString());
            if (redirectUri is null)
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest, "redirect_uri is required");

            string? codeChallenge = NullIfEmpty(form["code_challenge"].ToString());
            if (codeChallenge is null)
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest, "code_challenge is required");
            if (form["code_challenge_method"] != Pkce.MethodS256)
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest, "code_challenge_method must be 'S256'");

            string scope = form["scope"].ToString();
            if (string.IsNullOrWhiteSpace(scope))
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidScope, "scope is required");
            string? state = NullIfEmpty(form["state"].ToString());
            string? loginHint = NullIfEmpty(form["login_hint"].ToString());

            // Resolve and validate the client's metadata document (SSRF-hardened; localhost synthesized).
            ClientMetadata client;
            try
            {
                client = await resolver.ResolveAsync(clientId, context.RequestAborted);
            }
            catch (ClientMetadataException ex)
            {
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, ex.ErrorCode, ex.Message);
            }

            if (!client.AllowsRedirectUri(redirectUri))
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidRequest,
                    "redirect_uri is not registered for this client");

            string[] requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!client.AllowsScopes(requestedScopes))
                return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidScope,
                    "requested scope exceeds the client's registered scope");
            foreach (string s in requestedScopes)
                if (!AuthorizationServerMetadata.DefaultScopes.Contains(s))
                    return OAuthJsonError(context, StatusCodes.Status400BadRequest, OAuthErrors.InvalidScope,
                        $"unsupported scope '{s}'");

            string requestUri = OAuthIds.NewRequestUri();
            store.SaveParRequest(new ParRequest(
                RequestUri: requestUri,
                ClientId: clientId,
                ResponseType: "code",
                RedirectUri: redirectUri,
                Scope: scope,
                CodeChallenge: codeChallenge,
                CodeChallengeMethod: Pkce.MethodS256,
                State: state,
                DpopJkt: proof.Jkt,
                LoginHint: loginHint,
                CreatedAt: now,
                ExpiresAt: now.AddSeconds(ParTtlSeconds)));

            context.Response.Headers["DPoP-Nonce"] = nonces.Current();
            context.Response.Headers.CacheControl = "no-store";
            string body = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request_uri"] = requestUri,
                ["expires_in"] = ParTtlSeconds,
            });
            return Results.Content(body, "application/json", null, StatusCodes.Status201Created);
        }).RequireCors(OAuthCorsPolicy);

    /// <summary>The absolute URL of an OAuth endpoint, derived from the advertised issuer origin.</summary>
    private static string OAuthEndpointUrl(PdsService pds, string path) =>
        pds.Identity.PublicUrl.GetLeftPart(UriPartial.Authority) + path;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Write a standard OAuth JSON error body (<c>error</c> + optional <c>error_description</c>).</summary>
    private static IResult OAuthJsonError(HttpContext context, int status, string error, string? description)
    {
        context.Response.Headers.CacheControl = "no-store";
        var payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = error };
        if (!string.IsNullOrEmpty(description))
            payload["error_description"] = description;
        return Results.Content(JsonSerializer.Serialize(payload), "application/json", null, status);
    }

    /// <summary>
    /// The authorization interface (atproto profile). A <c>GET</c> renders a combined sign-in and
    /// consent page for the pending pushed request; the <c>POST</c> authenticates the account with its
    /// password, and on approval mints a single-use authorization code and redirects back to the client
    /// with <c>code</c>, the echoed <c>state</c>, and the <c>iss</c> identifier. Only the verifiable
    /// <c>client_id</c> URL is shown (never an unverified client name or logo), which is the profile's
    /// impersonation defense.
    /// </summary>
    private static void MapOAuthAuthorize(WebApplication app)
    {
        app.MapGet("/oauth/authorize", (HttpContext context, PdsService pds, IOAuthStore store) =>
        {
            string? clientId = NullIfEmpty(context.Request.Query["client_id"].ToString());
            string? requestUri = NullIfEmpty(context.Request.Query["request_uri"].ToString());

            ParRequest? par = ValidatePendingRequest(store, clientId, requestUri);
            if (par is null)
                return AuthorizeErrorPage(context);

            string csrf = IssueCsrfCookie(context);
            return AuthorizePage(context, par, csrf, par.LoginHint, error: null);
        });

        app.MapPost("/oauth/authorize", async (HttpContext context, PdsService pds, IOAuthStore store) =>
        {
            if (!context.Request.HasFormContentType)
                return AuthorizeErrorPage(context);
            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);

            string? clientId = NullIfEmpty(form["client_id"].ToString());
            string? requestUri = NullIfEmpty(form["request_uri"].ToString());
            ParRequest? par = ValidatePendingRequest(store, clientId, requestUri);
            if (par is null)
                return AuthorizeErrorPage(context);

            // Double-submit CSRF: the cookie set on the GET must match the posted hidden field.
            if (!CsrfMatches(context, form["csrf"].ToString()))
                return AuthorizeErrorPage(context);

            string issuer = pds.Identity.PublicUrl.GetLeftPart(UriPartial.Authority);

            // Denial is a normal, spec-defined outcome: redirect with access_denied.
            if (form["action"] != "approve")
            {
                store.DeleteParRequest(par.RequestUri);
                return RedirectToClient(par.RedirectUri, new Dictionary<string, string?>
                {
                    ["error"] = OAuthErrors.AccessDenied,
                    ["state"] = par.State,
                    ["iss"] = issuer,
                });
            }

            string identifier = form["identifier"].ToString();
            Account? account = pds.Accounts.Resolve(identifier);
            if (account is null || !AccountStore.VerifyPassword(form["password"].ToString(), account.PasswordSalt, account.PasswordHash))
            {
                // Re-render with a fresh CSRF token; the pending request is untouched.
                string csrf = IssueCsrfCookie(context);
                return AuthorizePage(context, par, csrf, identifier, "Invalid identifier or password.");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string code = OAuthIds.NewCode();
            store.SaveAuthorizationCode(new AuthorizationCode(
                Code: code,
                ClientId: par.ClientId,
                Did: account.Did,
                RedirectUri: par.RedirectUri,
                Scope: par.Scope,
                CodeChallenge: par.CodeChallenge,
                CodeChallengeMethod: par.CodeChallengeMethod,
                DpopJkt: par.DpopJkt,
                CreatedAt: now,
                ExpiresAt: now.AddSeconds(AuthorizationCodeTtlSeconds)));
            store.DeleteParRequest(par.RequestUri);

            return RedirectToClient(par.RedirectUri, new Dictionary<string, string?>
            {
                ["code"] = code,
                ["state"] = par.State,
                ["iss"] = issuer,
            });
        });
    }

    /// <summary>Look up a pending PAR by <c>request_uri</c> and confirm it is unexpired and client-matched.</summary>
    private static ParRequest? ValidatePendingRequest(IOAuthStore store, string? clientId, string? requestUri)
    {
        if (clientId is null || requestUri is null)
            return null;
        ParRequest? par = store.GetParRequest(requestUri);
        if (par is null || par.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        return string.Equals(par.ClientId, clientId, StringComparison.Ordinal) ? par : null;
    }

    private static string IssueCsrfCookie(HttpContext context)
    {
        string token = RandomNumberGenerator.GetHexString(64, lowercase: true);
        context.Response.Cookies.Append(OAuthCsrfCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/oauth",
        });
        return token;
    }

    private static bool CsrfMatches(HttpContext context, string posted)
    {
        string? cookie = context.Request.Cookies[OAuthCsrfCookie];
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(posted))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(cookie),
            System.Text.Encoding.UTF8.GetBytes(posted));
    }

    private static IResult RedirectToClient(string redirectUri, IDictionary<string, string?> parameters) =>
        Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUri, parameters));

    private static IResult AuthorizePage(HttpContext context, ParRequest par, string csrf, string? identifier, string? error)
    {
        context.Response.Headers.CacheControl = "no-store";
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        string errorHtml = error is null
            ? string.Empty
            : $"<p class=\"error\">{Enc(error)}</p>";
        string scopeItems = string.Join("", par.Scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => $"<li><code>{Enc(s)}</code></li>"));

        string html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in</title>
              <style>
                body { font-family: system-ui, sans-serif; max-width: 26rem; margin: 3rem auto; padding: 0 1rem; color: #1a1a1a; }
                h1 { font-size: 1.25rem; }
                .client { background: #f4f4f5; border-radius: 8px; padding: .75rem 1rem; font-size: .9rem; word-break: break-all; }
                label { display: block; margin: 1rem 0 .25rem; font-weight: 600; font-size: .9rem; }
                input { width: 100%; padding: .55rem; border: 1px solid #c8c8cc; border-radius: 6px; font-size: 1rem; box-sizing: border-box; }
                .actions { display: flex; gap: .75rem; margin-top: 1.5rem; }
                button { flex: 1; padding: .6rem; border-radius: 6px; border: 0; font-size: 1rem; cursor: pointer; }
                .approve { background: #2f6f4f; color: #fff; }
                .deny { background: #e5e5e7; color: #1a1a1a; }
                .error { background: #fde8e8; color: #9b1c1c; padding: .6rem .8rem; border-radius: 6px; font-size: .9rem; }
                ul { margin: .25rem 0; }
              </style>
            </head>
            <body>
              <h1>Authorize access</h1>
              <p>The application at this URL is requesting access to your account:</p>
              <p class="client">{{Enc(par.ClientId)}}</p>
              <p>Requested scopes:</p>
              <ul>{{scopeItems}}</ul>
              {{errorHtml}}
              <form method="post" action="/oauth/authorize" autocomplete="off">
                <input type="hidden" name="client_id" value="{{Enc(par.ClientId)}}">
                <input type="hidden" name="request_uri" value="{{Enc(par.RequestUri)}}">
                <input type="hidden" name="csrf" value="{{Enc(csrf)}}">
                <label for="identifier">Handle or DID</label>
                <input id="identifier" name="identifier" value="{{Enc(identifier)}}" autocapitalize="none" autocorrect="off">
                <label for="password">Password</label>
                <input id="password" name="password" type="password">
                <div class="actions">
                  <button class="deny" type="submit" name="action" value="deny">Deny</button>
                  <button class="approve" type="submit" name="action" value="approve">Approve</button>
                </div>
              </form>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult AuthorizeErrorPage(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        const string html = """
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Request invalid</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 26rem; margin: 3rem auto;">
              <h1>This request is invalid</h1>
              <p>The authorization request has expired or could not be found. Please start again from the application.</p>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", null, StatusCodes.Status400BadRequest);
    }

    private static void MapServer(WebApplication app)
    {
        app.MapGet("/xrpc/com.atproto.server.describeServer", (PdsService pds) => Results.Ok(new
        {
            did = pds.Identity.ServiceDid,
            availableUserDomains = new[] { "." + pds.Identity.HandleDomain },
            inviteCodeRequired = false,
        }));

        app.MapPost("/xrpc/com.atproto.server.createAccount", (CreateAccountRequest req, PdsService pds) =>
        {
            Account account = pds.CreateAccount(req.Handle, req.Password ?? string.Empty);
            (string access, string refresh) = pds.IssueSession(account);
            return Results.Ok(new
            {
                did = account.Did,
                handle = account.Handle,
                accessJwt = access,
                refreshJwt = refresh,
            });
        });

        app.MapPost("/xrpc/com.atproto.server.createSession", (CreateSessionRequest req, PdsService pds) =>
        {
            Account? account = pds.Accounts.Resolve(req.Identifier);
            if (account is null || !AccountStore.VerifyPassword(req.Password, account.PasswordSalt, account.PasswordHash))
                throw new XrpcException(401, "AuthenticationRequired", "Invalid identifier or password.");
            (string access, string refresh) = pds.IssueSession(account);
            return Results.Ok(new
            {
                did = account.Did,
                handle = account.Handle,
                accessJwt = access,
                refreshJwt = refresh,
            });
        });

        app.MapGet("/xrpc/com.atproto.server.getSession", (HttpContext ctx, PdsService pds) =>
        {
            Account account = pds.Authenticate(ctx.Request.Headers.Authorization);
            return Results.Ok(new { did = account.Did, handle = account.Handle });
        });
    }

    private static void MapRepo(WebApplication app)
    {
        app.MapPost("/xrpc/com.atproto.repo.createRecord", (CreateRecordRequest req, HttpContext ctx, PdsService pds) =>
        {
            Account account = pds.Authenticate(ctx.Request.Headers.Authorization);
            RequireRepo(pds, account, req.Repo);
            object record = DataModel.FromJson(req.Record)
                ?? throw new XrpcException(400, "InvalidRequest", "Record must be an object.");
            RepoWrite write = string.IsNullOrEmpty(req.Rkey)
                ? RepoWrite.Create(req.Collection, record)
                : RepoWrite.Create(req.Collection, req.Rkey!, record);
            CommitResult result = pds.Commit(account, new[] { write });
            AtProto.Repo.RepoOp op = result.Ops[0];
            return Results.Ok(new
            {
                uri = $"at://{account.Did}/{op.Path}",
                cid = op.Cid!.Value.ToString(),
                commit = new { cid = result.Commit.ToString(), rev = result.Rev },
            });
        });

        app.MapPost("/xrpc/com.atproto.repo.putRecord", (PutRecordRequest req, HttpContext ctx, PdsService pds) =>
        {
            Account account = pds.Authenticate(ctx.Request.Headers.Authorization);
            RequireRepo(pds, account, req.Repo);
            object record = DataModel.FromJson(req.Record)
                ?? throw new XrpcException(400, "InvalidRequest", "Record must be an object.");
            CommitResult result = pds.Commit(account, new[] { RepoWrite.Update(req.Collection, req.Rkey, record) });
            AtProto.Repo.RepoOp op = result.Ops[0];
            return Results.Ok(new
            {
                uri = $"at://{account.Did}/{op.Path}",
                cid = op.Cid!.Value.ToString(),
                commit = new { cid = result.Commit.ToString(), rev = result.Rev },
            });
        });

        app.MapPost("/xrpc/com.atproto.repo.deleteRecord", (DeleteRecordRequest req, HttpContext ctx, PdsService pds) =>
        {
            Account account = pds.Authenticate(ctx.Request.Headers.Authorization);
            RequireRepo(pds, account, req.Repo);
            CommitResult result = pds.Commit(account, new[] { RepoWrite.Delete(req.Collection, req.Rkey) });
            return Results.Ok(new { commit = new { cid = result.Commit.ToString(), rev = result.Rev } });
        });

        app.MapGet("/xrpc/com.atproto.repo.getRecord", (string repo, string collection, string rkey, PdsService pds) =>
        {
            Account account = ResolveRepo(pds, repo);
            if (!account.Repo.TryGetRecord(collection, rkey, out Cid cid, out byte[] bytes))
                throw new XrpcException(404, "RecordNotFound", $"Record not found: {collection}/{rkey}");
            return Results.Ok(new
            {
                uri = $"at://{account.Did}/{collection}/{rkey}",
                cid = cid.ToString(),
                value = DataModel.ToJson(DagCbor.Decode(bytes)),
            });
        });

        app.MapGet("/xrpc/com.atproto.repo.listRecords", (string repo, string collection, int? limit, PdsService pds) =>
        {
            Account account = ResolveRepo(pds, repo);
            int take = Math.Clamp(limit ?? 50, 1, 100);
            var records = account.Repo.ListCollection(collection).Take(take).Select(r =>
            {
                account.Repo.TryGetRecord(collection, r.Rkey, out _, out byte[] bytes);
                return new
                {
                    uri = $"at://{account.Did}/{r.Key}",
                    cid = r.Cid.ToString(),
                    value = DataModel.ToJson(DagCbor.Decode(bytes)),
                };
            });
            return Results.Ok(new { records });
        });
    }

    private static void MapBlob(WebApplication app)
    {
        app.MapPost("/xrpc/com.atproto.repo.uploadBlob", async (HttpContext ctx, PdsService pds, BlobStore blobs, IPdsPersistence persistence) =>
        {
            _ = pds.Authenticate(ctx.Request.Headers.Authorization);

            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            string contentType = ctx.Request.ContentType ?? "application/octet-stream";
            StoredBlob blob = blobs.Put(body.ToArray(), contentType);
            persistence.SaveBlob(blob);

            return Results.Ok(new
            {
                blob = BlobRef(blob),
            });
        });

        app.MapGet("/xrpc/com.atproto.sync.getBlob", (string did, string cid, BlobStore blobs) =>
        {
            Cid blobCid;
            try
            {
                blobCid = Cid.Decode(cid);
            }
            catch (FormatException ex)
            {
                throw new XrpcException(400, "InvalidRequest", ex.Message);
            }

            if (!blobs.TryGet(blobCid, out StoredBlob? blob))
                throw new XrpcException(404, "BlobNotFound", $"Blob not found: {cid}");

            return Results.Bytes(blob.Bytes, blob.ContentType);
        });
    }

    private static void MapSync(WebApplication app)
    {
        app.MapGet("/xrpc/com.atproto.sync.getRepo", (string did, PdsService pds) =>
        {
            Account account = pds.Accounts.ByDid(did)
                ?? throw new XrpcException(404, "RepoNotFound", $"Unknown repo: {did}");
            return Results.Bytes(account.Repo.ExportCar(), "application/vnd.ipld.car");
        });

        app.MapGet("/xrpc/com.atproto.sync.getLatestCommit", (string did, PdsService pds) =>
        {
            Account account = pds.Accounts.ByDid(did)
                ?? throw new XrpcException(404, "RepoNotFound", $"Unknown repo: {did}");
            return Results.Ok(new { cid = account.Repo.Head.ToString(), rev = account.Repo.Commit.Rev });
        });

        app.Map("/xrpc/com.atproto.sync.subscribeRepos", async (HttpContext ctx, PdsService pds) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            long? cursor = long.TryParse(ctx.Request.Query["cursor"], out long c) ? c : null;
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                await foreach (SequencedFrame frame in pds.Firehose.Subscribe(cursor, ctx.RequestAborted))
                    await socket.SendAsync(frame.Frame, WebSocketMessageType.Binary, endOfMessage: true, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
        });
    }

    private static void MapIdentity(WebApplication app)
    {
        app.MapGet("/xrpc/com.atproto.identity.resolveHandle", (string handle, PdsService pds) =>
        {
            Account account = pds.Accounts.ByHandle(handle)
                ?? throw new XrpcException(404, "HandleNotFound", $"Unknown handle: {handle}");
            return Results.Ok(new { did = account.Did });
        });

        app.MapGet("/pds/{accountId}/did.json", (string accountId, PdsService pds) =>
        {
            Account account = pds.Accounts.ByAccountId(accountId)
                ?? throw new XrpcException(404, "NotFound", $"Unknown account: {accountId}");
            return Results.Json(DidDocument(account, pds.Identity));
        });

        app.MapGet("/.well-known/did.json", (PdsService pds) => Results.Json(new
        {
            @context = new[] { "https://www.w3.org/ns/did/v1" },
            id = pds.Identity.ServiceDid,
            service = new object[]
            {
                new
                {
                    id = "#atproto_pds",
                    type = "AtprotoPersonalDataServer",
                    serviceEndpoint = pds.Identity.PublicUrl.ToString().TrimEnd('/'),
                },
            },
        }));
    }

    private static object DidDocument(Account account, PdsIdentity identity) => new
    {
        @context = new[]
        {
            "https://www.w3.org/ns/did/v1",
            "https://w3id.org/security/multikey/v1",
            "https://w3id.org/security/suites/secp256k1-2019/v1",
        },
        id = account.Did,
        alsoKnownAs = new[] { $"at://{account.Handle}" },
        verificationMethod = new object[]
        {
            new
            {
                id = $"{account.Did}#atproto",
                type = "Multikey",
                controller = account.Did,
                publicKeyMultibase = account.SigningKey.PublicKey.Multibase,
            },
        },
        service = new object[]
        {
            new
            {
                id = "#atproto_pds",
                type = "AtprotoPersonalDataServer",
                serviceEndpoint = identity.PublicUrl.ToString().TrimEnd('/'),
            },
        },
    };

    private static Dictionary<string, object?> BlobRef(StoredBlob blob) => new(StringComparer.Ordinal)
    {
        ["$type"] = "blob",
        ["ref"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$link"] = blob.Cid.ToString(),
        },
        ["mimeType"] = blob.ContentType,
        ["size"] = blob.Size,
    };

    private static Account ResolveRepo(PdsService pds, string repo)
    {
        Account? account = repo.StartsWith("did:", StringComparison.Ordinal)
            ? pds.Accounts.ByDid(repo)
            : pds.Accounts.ByHandle(repo);
        return account ?? throw new XrpcException(404, "RepoNotFound", $"Unknown repo: {repo}");
    }

    private static void RequireRepo(PdsService pds, Account account, string repo)
    {
        if (string.IsNullOrEmpty(repo))
            return;
        if (!string.Equals(repo, account.Did, StringComparison.Ordinal)
            && !string.Equals(repo, account.Handle, StringComparison.OrdinalIgnoreCase))
            throw new XrpcException(403, "InvalidRequest", "Authenticated account does not own this repo.");
    }
}

internal sealed record CreateAccountRequest(string Handle, string? Password, string? Email);

internal sealed record CreateSessionRequest(string Identifier, string Password);

internal sealed record CreateRecordRequest(string Repo, string Collection, string? Rkey, JsonElement Record, bool? Validate);

internal sealed record PutRecordRequest(string Repo, string Collection, string Rkey, JsonElement Record);

internal sealed record DeleteRecordRequest(string Repo, string Collection, string Rkey);
