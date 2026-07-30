using System.Net;
using System.Text;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class ClientMetadataResolverTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body, string contentType = "application/json")
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static ClientMetadataResolver ResolverFor(HttpResponseMessage response, ClientMetadataResolverOptions? options = null) =>
        new(options, new StubHandler(response));

    // --- localhost development client (no network) ---

    [Fact]
    public async Task Localhost_NoQuery_UsesDefaults()
    {
        using var resolver = new ClientMetadataResolver();
        ClientMetadata meta = await resolver.ResolveAsync("http://localhost");

        Assert.True(meta.IsLocalhostClient);
        Assert.Equal("native", meta.ApplicationType);
        Assert.Equal("none", meta.TokenEndpointAuthMethod);
        Assert.True(meta.DpopBoundAccessTokens);
        Assert.Equal("atproto", meta.Scope);
        Assert.Equal(new[] { "http://127.0.0.1/", "http://[::1]/" }, meta.RedirectUris);
        Assert.Contains("authorization_code", meta.GrantTypes);
        Assert.Contains("refresh_token", meta.GrantTypes);
    }

    [Fact]
    public async Task Localhost_WithRedirectAndScope_HonorsQuery()
    {
        using var resolver = new ClientMetadataResolver();
        string clientId = "http://localhost?redirect_uri=" +
            Uri.EscapeDataString("http://127.0.0.1:8080/callback") +
            "&scope=" + Uri.EscapeDataString("atproto transition:generic");

        ClientMetadata meta = await resolver.ResolveAsync(clientId);

        Assert.Equal(new[] { "http://127.0.0.1:8080/callback" }, meta.RedirectUris);
        Assert.Equal("atproto transition:generic", meta.Scope);
        // Port is ignored when matching localhost redirects.
        Assert.True(meta.AllowsRedirectUri("http://127.0.0.1:9999/callback"));
        Assert.False(meta.AllowsRedirectUri("http://127.0.0.1:9999/other"));
    }

    [Fact]
    public async Task Localhost_WithPort_Throws()
    {
        using var resolver = new ClientMetadataResolver();
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync("http://localhost:8080"));
    }

    [Fact]
    public async Task Localhost_WithPath_Throws()
    {
        using var resolver = new ClientMetadataResolver();
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync("http://localhost/app"));
    }

    [Fact]
    public async Task Localhost_NonLoopbackRedirect_Throws()
    {
        using var resolver = new ClientMetadataResolver();
        string clientId = "http://localhost?redirect_uri=" + Uri.EscapeDataString("https://evil.example.com/cb");
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync(clientId));
    }

    [Fact]
    public async Task Localhost_ScopeWithoutAtproto_Throws()
    {
        using var resolver = new ClientMetadataResolver();
        string clientId = "http://localhost?scope=transition:generic";
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync(clientId));
    }

    [Fact]
    public async Task Localhost_Disabled_Throws()
    {
        using var resolver = new ClientMetadataResolver(new ClientMetadataResolverOptions { AllowLocalhostClient = false });
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync("http://localhost"));
    }

    // --- client_id shape validation ---

    [Theory]
    [InlineData("http://app.example.com/client-metadata.json")]   // http, not localhost
    [InlineData("https://app.example.com:8443/client-metadata.json")] // explicit port
    [InlineData("https://app.example.com/client-metadata.json#x")]    // fragment
    [InlineData("not-a-url")]
    public async Task InvalidClientId_Throws(string clientId)
    {
        using var resolver = new ClientMetadataResolver();
        await Assert.ThrowsAsync<ClientMetadataException>(() => resolver.ResolveAsync(clientId));
    }

    // --- SSRF-hardened fetch (stubbed transport) ---

    [Fact]
    public async Task Fetch_ValidDocument_Succeeds()
    {
        const string clientId = "https://app.example.com/client-metadata.json";
        string body = $$"""
        {
          "client_id": "{{clientId}}",
          "grant_types": ["authorization_code", "refresh_token"],
          "response_types": ["code"],
          "scope": "atproto transition:generic",
          "redirect_uris": ["https://app.example.com/callback"],
          "token_endpoint_auth_method": "none",
          "dpop_bound_access_tokens": true
        }
        """;
        using var resolver = ResolverFor(JsonResponse(HttpStatusCode.OK, body));

        ClientMetadata meta = await resolver.ResolveAsync(clientId);

        Assert.Equal(clientId, meta.ClientId);
        Assert.False(meta.IsLocalhostClient);
    }

    [Fact]
    public async Task Fetch_NonOkStatus_Throws()
    {
        using var resolver = ResolverFor(JsonResponse(HttpStatusCode.NotFound, "{}"));
        await Assert.ThrowsAsync<ClientMetadataException>(() =>
            resolver.ResolveAsync("https://app.example.com/client-metadata.json"));
    }

    [Fact]
    public async Task Fetch_Redirect_Throws()
    {
        // AllowAutoRedirect is off; a 3xx is surfaced as a non-200 status and rejected.
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://elsewhere.example.net/");
        using var resolver = ResolverFor(response);
        await Assert.ThrowsAsync<ClientMetadataException>(() =>
            resolver.ResolveAsync("https://app.example.com/client-metadata.json"));
    }

    [Fact]
    public async Task Fetch_WrongContentType_Throws()
    {
        using var resolver = ResolverFor(JsonResponse(HttpStatusCode.OK, "{}", contentType: "text/html"));
        await Assert.ThrowsAsync<ClientMetadataException>(() =>
            resolver.ResolveAsync("https://app.example.com/client-metadata.json"));
    }

    [Fact]
    public async Task Fetch_OversizeBody_Throws()
    {
        string big = "{\"padding\":\"" + new string('a', 500) + "\"}";
        using var resolver = ResolverFor(
            JsonResponse(HttpStatusCode.OK, big),
            new ClientMetadataResolverOptions { MaxResponseBytes = 100 });
        await Assert.ThrowsAsync<ClientMetadataException>(() =>
            resolver.ResolveAsync("https://app.example.com/client-metadata.json"));
    }

    // --- SSRF address filtering (no network for literals) ---

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    public async Task ResolveAllowedAddresses_BlocksPrivateLiterals(string host)
    {
        await Assert.ThrowsAsync<ClientMetadataException>(() =>
            ClientMetadataResolver.ResolveAllowedAddressesAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllowedAddresses_AllowsPublicLiteral()
    {
        IReadOnlyList<IPAddress> allowed =
            await ClientMetadataResolver.ResolveAllowedAddressesAsync("1.1.1.1", CancellationToken.None);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(allowed));
    }

    [Theory]
    [InlineData("https://app.example.com/x", false)]
    [InlineData("https://app.example.com:443/x", true)]
    [InlineData("http://localhost", false)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://user@app.example.com/x", false)]
    [InlineData("https://[2001:db8::1]:8443/x", true)]
    public void HasExplicitPort_DetectsPort(string url, bool expected)
    {
        Assert.Equal(expected, ClientMetadataResolver.HasExplicitPort(url));
    }
}
