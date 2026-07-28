using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using AtProto.Car;
using AtProto.Crypto;
using AtProto.Firehose;
using AtProto.Pds;
using AtProto.Repo;
using RepoOp = AtProto.Firehose.RepoOp;

namespace AtProto.Pds.Tests;

/// <summary>Boots one in-process PDS on a real Kestrel loopback port, shared across the test class.</summary>
public sealed class PdsServerFixture : IAsyncLifetime
{
    private WebApplication _app = null!;

    public int Port { get; private set; }
    public string BaseUrl { get; private set; } = string.Empty;
    public HttpClient Http { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Port = FreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _app = PdsHost.Build(Array.Empty<string>(), options =>
        {
            options.PublicUrl = BaseUrl;
            options.HandleDomain = "pds.localhost";
        });
        _app.Urls.Clear();
        _app.Urls.Add(BaseUrl);
        await _app.StartAsync();
        Http = new HttpClient();
    }

    public async Task DisposeAsync()
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

// End-to-end proof of the M3 write path over real HTTP + a real WebSocket firehose: create an
// account, write a record, and confirm the PDS emits a signature-verifiable #commit an independent
// consumer reads, and that getRepo serves a valid CARv1. No mocks, no test-host package.
public sealed class PdsEndToEndTests : IClassFixture<PdsServerFixture>
{
    private const string StatusNsid = "place.selfhost.status";
    private readonly PdsServerFixture _fx;

    public PdsEndToEndTests(PdsServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task DescribeServer_reports_the_service_did()
    {
        JsonElement doc = await _fx.Http.GetFromJsonAsync<JsonElement>(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.describeServer");
        Assert.StartsWith("did:web:", doc.GetProperty("did").GetString());
        Assert.Contains(".pds.localhost", doc.GetProperty("availableUserDomains")[0].GetString());
    }

    [Fact]
    public async Task CreateSession_authenticates_a_registered_account()
    {
        await CreateAccount("bob.pds.localhost", "sw0rdfish");

        HttpResponseMessage ok = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createSession",
            new { identifier = "bob.pds.localhost", password = "sw0rdfish" });
        ok.EnsureSuccessStatusCode();
        JsonElement session = await ok.Content.ReadFromJsonAsync<JsonElement>();
        string accessJwt = session.GetProperty("accessJwt").GetString()!;

        var req = new HttpRequestMessage(HttpMethod.Get, $"{_fx.BaseUrl}/xrpc/com.atproto.server.getSession");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);
        JsonElement whoami = await (await _fx.Http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bob.pds.localhost", whoami.GetProperty("handle").GetString());

        HttpResponseMessage bad = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createSession",
            new { identifier = "bob.pds.localhost", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Write_path_emits_a_verifiable_commit_and_serves_a_valid_getRepo_car()
    {
        (string did, string accessJwt) = await CreateAccount("alice.pds.localhost", "hunter2");

        // Write a status record.
        JsonElement written = await CreateRecord(accessJwt, did, StatusNsid, "self", Status("👍"));
        string recordCid = written.GetProperty("cid").GetString()!;
        string commitCid = written.GetProperty("commit").GetProperty("cid").GetString()!;
        Assert.Equal($"at://{did}/{StatusNsid}/self", written.GetProperty("uri").GetString());

        // An independent firehose consumer reads the #commit (cursor=0 replays backfill deterministically).
        RepoCommitEvent commit = await ReadCommit(did);
        Assert.Equal(commitCid, commit.Commit.ToString());
        RepoOp op = Assert.Single(commit.Ops);
        Assert.Equal(RepoOpAction.Create, op.Action);
        Assert.Equal($"{StatusNsid}/self", op.Path);
        Assert.Equal(recordCid, op.Cid!.Value.ToString());

        // The commit's CAR slice is self-consistent and carries the new record.
        CarArchive slice = CarReader.Read(commit.Blocks);
        slice.VerifyIntegrity();
        Assert.Contains(Repository.FromCar(slice).Records(), r => r.Key == $"{StatusNsid}/self");

        // The signature verifies against the account's published signing key (from its DID document).
        string accountId = did.Split(':').Last();
        JsonElement didDoc = await _fx.Http.GetFromJsonAsync<JsonElement>($"{_fx.BaseUrl}/pds/{accountId}/did.json");
        string multibase = didDoc.GetProperty("verificationMethod")[0].GetProperty("publicKeyMultibase").GetString()!;
        EcPublicKey signingKey = EcPublicKey.Parse(multibase);

        // getRepo returns a valid CARv1 our reader validates; its commit matches and verifies.
        byte[] carBytes = await _fx.Http.GetByteArrayAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString(did)}");
        Repository repo = Repository.FromCar(CarReader.Read(carBytes));
        Assert.Equal(commitCid, Commits.ComputeCid(repo.Commit).ToString());
        Assert.True(Commits.Verify(signingKey, repo.Commit), "commit signature must verify");
        Assert.Contains(repo.Records(), r => r.Key == $"{StatusNsid}/self" && r.Cid.ToString() == recordCid);

        // getRecord round-trips the value; getLatestCommit agrees.
        JsonElement got = await _fx.Http.GetFromJsonAsync<JsonElement>(
            $"{_fx.BaseUrl}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection={StatusNsid}&rkey=self");
        Assert.Equal("👍", got.GetProperty("value").GetProperty("status").GetString());
        Assert.Equal(recordCid, got.GetProperty("cid").GetString());

        JsonElement latest = await _fx.Http.GetFromJsonAsync<JsonElement>(
            $"{_fx.BaseUrl}/xrpc/com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}");
        Assert.Equal(commitCid, latest.GetProperty("cid").GetString());
    }

    [Fact]
    public async Task Update_then_delete_change_the_repo_and_stay_verifiable()
    {
        (string did, string accessJwt) = await CreateAccount("carol.pds.localhost", "passw0rd");

        JsonElement created = await CreateRecord(accessJwt, did, StatusNsid, "self", Status("🌤"));
        string firstCid = created.GetProperty("cid").GetString()!;

        // putRecord overwrites; new value CID differs.
        JsonElement put = await PutRecord(accessJwt, did, StatusNsid, "self", Status("🌧"));
        Assert.NotEqual(firstCid, put.GetProperty("cid").GetString());

        JsonElement afterPut = await _fx.Http.GetFromJsonAsync<JsonElement>(
            $"{_fx.BaseUrl}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection={StatusNsid}&rkey=self");
        Assert.Equal("🌧", afterPut.GetProperty("value").GetProperty("status").GetString());

        // deleteRecord removes it.
        var del = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.deleteRecord");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);
        del.Content = JsonContent.Create(new { repo = did, collection = StatusNsid, rkey = "self" });
        (await _fx.Http.SendAsync(del)).EnsureSuccessStatusCode();

        HttpResponseMessage gone = await _fx.Http.GetAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection={StatusNsid}&rkey=self");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Repo still verifies after the mutations.
        byte[] carBytes = await _fx.Http.GetByteArrayAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString(did)}");
        Repository repo = Repository.FromCar(CarReader.Read(carBytes));
        string accountId = did.Split(':').Last();
        JsonElement didDoc = await _fx.Http.GetFromJsonAsync<JsonElement>($"{_fx.BaseUrl}/pds/{accountId}/did.json");
        var key = EcPublicKey.Parse(didDoc.GetProperty("verificationMethod")[0].GetProperty("publicKeyMultibase").GetString()!);
        Assert.True(Commits.Verify(key, repo.Commit));
        Assert.Empty(repo.Records());
    }

    private async Task<(string Did, string AccessJwt)> CreateAccount(string handle, string password)
    {
        HttpResponseMessage resp = await _fx.Http.PostAsJsonAsync(
            $"{_fx.BaseUrl}/xrpc/com.atproto.server.createAccount",
            new { handle, password });
        resp.EnsureSuccessStatusCode();
        JsonElement account = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (account.GetProperty("did").GetString()!, account.GetProperty("accessJwt").GetString()!);
    }

    private async Task<JsonElement> CreateRecord(string jwt, string did, string collection, string rkey, object record)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.createRecord");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = collection,
            ["rkey"] = rkey,
            ["record"] = record,
        });
        HttpResponseMessage resp = await _fx.Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> PutRecord(string jwt, string did, string collection, string rkey, object record)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_fx.BaseUrl}/xrpc/com.atproto.repo.putRecord");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = collection,
            ["rkey"] = rkey,
            ["record"] = record,
        });
        HttpResponseMessage resp = await _fx.Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<RepoCommitEvent> ReadCommit(string did)
    {
        var client = new FirehoseClient(new Uri($"ws://127.0.0.1:{_fx.Port}/xrpc/com.atproto.sync.subscribeRepos"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (RepoEvent ev in client.StreamOnceAsync(0, cts.Token))
        {
            if (ev is RepoCommitEvent commit && commit.Did == did && commit.Ops.Count > 0)
                return commit;
        }
        throw new Xunit.Sdk.XunitException("no #commit event received from the firehose");
    }

    private static Dictionary<string, object?> Status(string emoji) => new()
    {
        ["$type"] = StatusNsid,
        ["status"] = emoji,
        ["createdAt"] = "2026-07-28T12:00:00.000Z",
    };
}
