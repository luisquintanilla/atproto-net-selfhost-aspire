using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace AtProto.Pds.Tests;

// Proves the durable "production" storage profile: with Pds:Storage=sqlite, accounts, repositories,
// and blobs survive a full process restart (a fresh WebApplication over the same SQLite file), the
// repository head/rev are preserved, a rehydrated repo keeps writing with monotonic revisions, and
// the password hash still authenticates. The default in-memory profile is exercised everywhere else.
public sealed class PersistenceTests : IDisposable
{
    private const string StatusNsid = "place.selfhost.status";
    private const string Handle = "durable.pds.localhost";
    private const string Password = "hunter2";

    // A fixed public URL keeps the did:web authority (and therefore every persisted DID) stable
    // across restarts, independent of the ephemeral loopback port each phase binds to.
    private const string PublicUrl = "http://pds.example:5100";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "atproto-pds-persist-" + Guid.NewGuid().ToString("N"), "pds.db");

    [Fact]
    public async Task Accounts_repositories_and_blobs_survive_a_restart()
    {
        string did;
        string firstRev;
        string firstCommit;
        byte[] blobBytes = [0x89, 0x50, 0x4e, 0x47, 0x01, 0x02, 0x03, 0x04];
        string blobCid;

        // Phase 1: write through a SQLite-backed PDS, then shut it down.
        await using (PdsPhase phase = await PdsPhase.StartAsync(_dbPath))
        {
            (did, string jwt) = await phase.CreateAccount(Handle, Password);
            await phase.CreateRecord(jwt, did, StatusNsid, "s1", new Dictionary<string, object?>
            {
                ["$type"] = StatusNsid,
                ["status"] = "first",
                ["createdAt"] = "2026-07-29T14:00:00.000Z",
            });
            blobCid = await phase.UploadBlob(jwt, blobBytes, "image/png");

            JsonElement latest = await phase.Get($"com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}");
            firstCommit = latest.GetProperty("cid").GetString()!;
            firstRev = latest.GetProperty("rev").GetString()!;
        }

        Assert.True(File.Exists(_dbPath), "SQLite database file should have been created.");

        // Phase 2: a brand-new host over the same file rehydrates everything.
        await using (PdsPhase phase = await PdsPhase.StartAsync(_dbPath))
        {
            // The repository head and revision were persisted.
            JsonElement latest = await phase.Get($"com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}");
            Assert.Equal(firstCommit, latest.GetProperty("cid").GetString());
            Assert.Equal(firstRev, latest.GetProperty("rev").GetString());

            // The record survived and reads back with its value intact.
            JsonElement record = await phase.Get(
                $"com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection={StatusNsid}&rkey=s1");
            Assert.Equal("first", record.GetProperty("value").GetProperty("status").GetString());

            // The blob survived byte-for-byte.
            HttpResponseMessage blob = await phase.Http.GetAsync(
                $"{phase.BaseUrl}/xrpc/com.atproto.sync.getBlob?did={Uri.EscapeDataString(did)}&cid={Uri.EscapeDataString(blobCid)}");
            blob.EnsureSuccessStatusCode();
            Assert.Equal(blobBytes, await blob.Content.ReadAsByteArrayAsync());

            // The password hash survived: the same credentials still authenticate to the same DID.
            HttpResponseMessage session = await phase.Http.PostAsJsonAsync(
                $"{phase.BaseUrl}/xrpc/com.atproto.server.createSession",
                new { identifier = Handle, password = Password });
            session.EnsureSuccessStatusCode();
            JsonElement sessionBody = await session.Content.ReadFromJsonAsync<JsonElement>();
            string sessionJwt = sessionBody.GetProperty("accessJwt").GetString()!;
            Assert.Equal(did, sessionBody.GetProperty("did").GetString());

            // The rehydrated repository keeps writing, and the new revision is strictly greater than
            // the persisted one (the TID clock was advanced past the restored head).
            await phase.CreateRecord(sessionJwt, did, StatusNsid, "s2", new Dictionary<string, object?>
            {
                ["$type"] = StatusNsid,
                ["status"] = "second",
                ["createdAt"] = "2026-07-29T15:00:00.000Z",
            });
            JsonElement afterWrite = await phase.Get($"com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}");
            string secondRev = afterWrite.GetProperty("rev").GetString()!;
            Assert.True(
                string.CompareOrdinal(secondRev, firstRev) > 0,
                $"revision after restart ({secondRev}) should sort after the persisted revision ({firstRev}).");
        }
    }

    public void Dispose()
    {
        string? dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>One booted PDS instance on a real loopback port, backed by a shared SQLite file.</summary>
    private sealed class PdsPhase : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public string BaseUrl { get; }
        public HttpClient Http { get; }

        private PdsPhase(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
            Http = new HttpClient();
        }

        public static async Task<PdsPhase> StartAsync(string dbPath)
        {
            string baseUrl = $"http://127.0.0.1:{FreePort()}";
            WebApplication app = PdsHost.Build(Array.Empty<string>(), options =>
            {
                options.PublicUrl = PublicUrl;
                options.HandleDomain = "pds.localhost";
                options.Storage = "sqlite";
                options.SqlitePath = dbPath;
            });
            app.Urls.Clear();
            app.Urls.Add(baseUrl);
            await app.StartAsync();
            return new PdsPhase(app, baseUrl);
        }

        public async Task<(string Did, string AccessJwt)> CreateAccount(string handle, string password)
        {
            HttpResponseMessage resp = await Http.PostAsJsonAsync(
                $"{BaseUrl}/xrpc/com.atproto.server.createAccount", new { handle, password });
            resp.EnsureSuccessStatusCode();
            JsonElement account = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return (account.GetProperty("did").GetString()!, account.GetProperty("accessJwt").GetString()!);
        }

        public async Task CreateRecord(string jwt, string did, string collection, string rkey, object record)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/xrpc/com.atproto.repo.createRecord");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            req.Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["repo"] = did,
                ["collection"] = collection,
                ["rkey"] = rkey,
                ["record"] = record,
            });
            (await Http.SendAsync(req)).EnsureSuccessStatusCode();
        }

        public async Task<string> UploadBlob(string jwt, byte[] bytes, string contentType)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/xrpc/com.atproto.repo.uploadBlob");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            HttpResponseMessage resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("blob").GetProperty("ref").GetProperty("$link").GetString()!;
        }

        public async Task<JsonElement> Get(string xrpc) =>
            await Http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/xrpc/{xrpc}");

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
