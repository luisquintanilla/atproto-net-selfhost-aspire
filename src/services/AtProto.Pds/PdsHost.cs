using System.Net.WebSockets;
using System.Text.Json;
using AtProto.Cbor;
using AtProto.Firehose;
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
        builder.Services.AddSingleton<PdsService>();
        builder.Services.AddHttpClient();
        builder.Services.AddHostedService<PdsInstanceAdvertiser>();

        WebApplication app = builder.Build();
        RehydrateFromStorage(app.Services);
        app.UseWebSockets();
        app.MapDefaultEndpoints();
        MapErrorHandling(app);
        MapServer(app);
        MapRepo(app);
        MapBlob(app);
        MapSync(app);
        MapIdentity(app);
        return app;
    }

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
