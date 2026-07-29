using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AtProto.AppView.Tests;

/// <summary>
/// Read-through inspection: the AppView resolves a DID and proxies standard <c>com.atproto.*</c>
/// reads so the browser can see the real record, repo overview, and raw CAR, single-origin. These
/// pin the passthrough of record JSON (body + status preserved), the composed repo overview shape,
/// and the CAR download bytes/content-type.
/// </summary>
public class InspectServiceTests
{
    private const string Pds = "http://localhost:5271";
    private const string Did = "did:web:localhost%3A5271:pds:demo1";

    private static readonly string DidJson = $$"""
        {
          "id": "{{Did}}",
          "alsoKnownAs": ["at://demo1.pds.localhost"],
          "service": [
            { "id": "#atproto_pds", "type": "AtprotoPersonalDataServer", "serviceEndpoint": "{{Pds}}" }
          ]
        }
        """;

    private static (InspectService svc, StubHttpMessageHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/did.json", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(DidJson);
            return route(req);
        });
        var factory = new StubHttpClientFactory(handler);
        IConfiguration config = TestConfig.InMemory(("AppView:PdsUrl", Pds));
        var resolver = new PdsResolver(factory, config, NullLogger<PdsResolver>.Instance);
        return (new InspectService(factory, resolver, config), handler);
    }

    private static bool Xrpc(HttpRequestMessage req, string method) =>
        req.RequestUri!.AbsolutePath.EndsWith(method, StringComparison.Ordinal);

    [Fact]
    public async Task GetRecord_passes_body_and_status_through_and_defaults_collection_rkey()
    {
        const string record = """{"uri":"at://did/place.selfhost.status/self","cid":"bafyrec","value":{"$type":"place.selfhost.status","status":"x"}}""";
        var (svc, handler) = Build(req =>
            Xrpc(req, "com.atproto.repo.getRecord")
                ? StubHttpMessageHandler.Json(record)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetRecordAsync(Did, null, null, default));

        Assert.Equal(200, res.Status);
        Assert.Contains("application/json", res.ContentType);
        Assert.Equal(record, res.Text);

        HttpRequestMessage getRecord = handler.Requests.Single(r => Xrpc(r, "com.atproto.repo.getRecord"));
        string query = getRecord.RequestUri!.Query;
        Assert.Contains("collection=place.selfhost.status", query);
        Assert.Contains("rkey=self", query);
        Assert.Contains($"repo={Uri.EscapeDataString(Did)}", query);
    }

    [Fact]
    public async Task GetRecord_propagates_a_not_found_from_the_pds()
    {
        const string error = """{"error":"RecordNotFound"}""";
        var (svc, _) = Build(req =>
            Xrpc(req, "com.atproto.repo.getRecord")
                ? StubHttpMessageHandler.Json(error, HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetRecordAsync(Did, "place.selfhost.status", "self", default));

        Assert.Equal(404, res.Status);
        Assert.Equal(error, res.Text);
    }

    [Fact]
    public async Task GetRecord_honours_an_explicit_collection_and_rkey()
    {
        var (svc, handler) = Build(_ => StubHttpMessageHandler.Json("""{"ok":true}"""));

        await svc.GetRecordAsync(Did, "app.bsky.feed.post", "abc123", default);

        string query = handler.Requests.Single(r => Xrpc(r, "com.atproto.repo.getRecord")).RequestUri!.Query;
        Assert.Contains("collection=app.bsky.feed.post", query);
        Assert.Contains("rkey=abc123", query);
    }

    [Fact]
    public async Task GetRepo_composes_identity_commit_and_records()
    {
        const string list = """{"records":[{"uri":"at://did/place.selfhost.status/self","cid":"bafyrec","value":{"$type":"place.selfhost.status","status":"🌤"}}]}""";
        const string commit = """{"cid":"bafycommit","rev":"3krev01"}""";
        var (svc, _) = Build(req =>
        {
            if (Xrpc(req, "com.atproto.repo.listRecords")) return StubHttpMessageHandler.Json(list);
            if (Xrpc(req, "com.atproto.sync.getLatestCommit")) return StubHttpMessageHandler.Json(commit);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetRepoAsync(Did, default));

        Assert.Equal(200, res.Status);
        using JsonDocument doc = JsonDocument.Parse(res.Text);
        JsonElement root = doc.RootElement;
        Assert.Equal(Did, root.GetProperty("did").GetString());
        Assert.Equal("demo1.pds.localhost", root.GetProperty("handle").GetString());
        Assert.Equal(Pds, root.GetProperty("pds").GetString());
        Assert.Equal(Did, root.GetProperty("didDoc").GetProperty("id").GetString());
        Assert.Equal("bafycommit", root.GetProperty("commit").GetProperty("cid").GetString());
        Assert.Equal("3krev01", root.GetProperty("commit").GetProperty("rev").GetString());
        Assert.Equal("place.selfhost.status", root.GetProperty("collection").GetString());

        JsonElement records = root.GetProperty("records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal("bafyrec", records[0].GetProperty("cid").GetString());
        Assert.Equal("🌤", records[0].GetProperty("value").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetRepo_tolerates_a_missing_commit_and_empty_records()
    {
        var (svc, _) = Build(req =>
            Xrpc(req, "com.atproto.repo.listRecords")
                ? StubHttpMessageHandler.Json("""{"records":[]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)); // getLatestCommit fails

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetRepoAsync(Did, default));

        using JsonDocument doc = JsonDocument.Parse(res.Text);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("commit").ValueKind);
        Assert.Equal(0, root.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task GetCar_returns_bytes_with_the_car_content_type()
    {
        byte[] car = [0x0a, 0x01, 0x02, 0x03];
        var (svc, _) = Build(req =>
            Xrpc(req, "com.atproto.sync.getRepo")
                ? StubHttpMessageHandler.Bytes(car, "application/vnd.ipld.car")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetCarAsync(Did, default));

        Assert.Equal(200, res.Status);
        Assert.Equal("application/vnd.ipld.car", res.ContentType);
        Assert.Equal(car, res.Body);
    }

    [Fact]
    public async Task GetCar_propagates_a_pds_failure_status()
    {
        var (svc, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        ExecutedResult res = await ResultRunner.RunAsync(await svc.GetCarAsync(Did, default));

        Assert.Equal(404, res.Status);
    }

    [Fact]
    public void Enabled_follows_the_resolver()
    {
        var (svc, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(svc.Enabled);
    }
}
