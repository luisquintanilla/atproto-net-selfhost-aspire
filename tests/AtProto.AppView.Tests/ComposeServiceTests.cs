using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AtProto.AppView.Tests;

/// <summary>
/// Compose closes the loop: "you" pick an emoji and the AppView writes it to the PDS on a cached
/// demo session, then it flows back over the firehose. These pin the write shape (repo/collection/
/// rkey/record + bearer auth), the session caching (provision once, reuse), the createAccount →
/// createSession fallback when the identity already exists, and the disabled path.
/// </summary>
public class ComposeServiceTests
{
    private const string Pds = "http://localhost:5271";
    private const string Did = "did:web:localhost%3A5271:pds:you";

    private static ComposeService Build(StubHttpMessageHandler handler, string? pds = Pds) =>
        new(new StubHttpClientFactory(handler),
            TestConfig.InMemory(("AppView:PdsUrl", pds)),
            NullLogger<ComposeService>.Instance);

    private static bool Xrpc(HttpRequestMessage req, string method) =>
        req.RequestUri!.AbsolutePath.EndsWith(method, StringComparison.Ordinal);

    private static HttpResponseMessage Account() =>
        StubHttpMessageHandler.Json($$"""{"did":"{{Did}}","accessJwt":"test-jwt","handle":"you.pds.localhost"}""");

    private static HttpResponseMessage PutOk() =>
        StubHttpMessageHandler.Json("""{"uri":"at://did:web:localhost%3A5271:pds:you/place.selfhost.status/self","cid":"bafyoucid"}""");

    [Fact]
    public async Task SetStatus_provisions_writes_the_record_and_returns_the_uri()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (Xrpc(req, "com.atproto.server.createAccount")) return Account();
            if (Xrpc(req, "com.atproto.repo.putRecord")) return PutOk();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        ComposeService compose = Build(handler);

        ComposeResult result = await compose.SetStatusAsync("🌤", default);

        Assert.Equal("at://did:web:localhost%3A5271:pds:you/place.selfhost.status/self", result.Uri);
        Assert.Equal("bafyoucid", result.Cid);
        Assert.Equal(Did, result.Did);
        Assert.Equal("you.pds.localhost", result.Handle);
        Assert.Equal("🌤", result.Status);

        HttpRequestMessage put = handler.Requests.Single(r => Xrpc(r, "com.atproto.repo.putRecord"));
        Assert.Equal("Bearer", put.Headers.Authorization!.Scheme);
        Assert.Equal("test-jwt", put.Headers.Authorization.Parameter);

        using JsonDocument body = JsonDocument.Parse(await put.Content!.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(Did, root.GetProperty("repo").GetString());
        Assert.Equal("place.selfhost.status", root.GetProperty("collection").GetString());
        Assert.Equal("self", root.GetProperty("rkey").GetString());
        JsonElement record = root.GetProperty("record");
        Assert.Equal("place.selfhost.status", record.GetProperty("$type").GetString());
        Assert.Equal("🌤", record.GetProperty("status").GetString());
        Assert.True(record.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Session_is_provisioned_once_and_reused()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (Xrpc(req, "com.atproto.server.createAccount")) return Account();
            if (Xrpc(req, "com.atproto.repo.putRecord")) return PutOk();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        ComposeService compose = Build(handler);

        await compose.SetStatusAsync("🌤", default);
        await compose.SetStatusAsync("🌙", default);

        Assert.Single(handler.Requests, r => Xrpc(r, "com.atproto.server.createAccount"));
        Assert.Equal(2, handler.Requests.Count(r => Xrpc(r, "com.atproto.repo.putRecord")));
    }

    [Fact]
    public async Task Falls_back_to_createSession_when_the_account_already_exists()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (Xrpc(req, "com.atproto.server.createAccount"))
                return StubHttpMessageHandler.Json("""{"error":"AlreadyExists"}""", HttpStatusCode.BadRequest);
            if (Xrpc(req, "com.atproto.server.createSession")) return Account();
            if (Xrpc(req, "com.atproto.repo.putRecord")) return PutOk();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        ComposeService compose = Build(handler);

        ComposeResult result = await compose.SetStatusAsync("🌤", default);

        Assert.Equal(Did, result.Did);
        Assert.Single(handler.Requests, r => Xrpc(r, "com.atproto.server.createSession"));
    }

    [Fact]
    public async Task Throws_when_no_pds_is_configured()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        ComposeService compose = Build(handler, pds: null);

        Assert.False(compose.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => compose.SetStatusAsync("🌤", default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Surfaces_a_putRecord_failure()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (Xrpc(req, "com.atproto.server.createAccount")) return Account();
            return new HttpResponseMessage(HttpStatusCode.BadRequest); // putRecord rejected
        });
        ComposeService compose = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => compose.SetStatusAsync("🌤", default));
    }
}
