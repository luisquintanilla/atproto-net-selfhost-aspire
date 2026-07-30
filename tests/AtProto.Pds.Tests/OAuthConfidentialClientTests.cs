using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using AtProto.Pds;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;

namespace AtProto.Pds.Tests;

/// <summary>
/// Boots one in-process PDS whose <see cref="ClientMetadataResolver"/> is wired to a canned HTTP
/// handler that serves a confidential client's <c>client-metadata.json</c> (with an inline JWKS), so
/// the confidential-client flow can be driven offline with a real <c>private_key_jwt</c> assertion.
/// </summary>
public sealed class ConfidentialClientFixture : IAsyncLifetime
{
    private WebApplication _app = null!;
    private readonly ECDsa _assertionKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string BaseUrl { get; private set; } = string.Empty;
    public string ClientId { get; } = "https://confidential.example/client-metadata.json";
    public string RedirectUri { get; } = "https://confidential.example/callback";
    public string Kid { get; } = "assert-key-1";

    public string TokenUrl => $"{BaseUrl}/oauth/token";

    public async Task InitializeAsync()
    {
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        string metadata = BuildClientMetadata();
        var handler = new CannedJsonHandler(metadata);

        _app = PdsHost.Build(
            Array.Empty<string>(),
            options =>
            {
                options.PublicUrl = BaseUrl;
                options.HandleDomain = "pds.localhost";
            },
            services => services.AddSingleton(_ => new ClientMetadataResolver(
                new ClientMetadataResolverOptions { AllowConfidentialClients = true, AllowLocalhostClient = true },
                handler)));
        _app.Urls.Clear();
        _app.Urls.Add(BaseUrl);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _assertionKey.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A clone of the assertion signing key, so a test can bind a DPoP proof to the same key.</summary>
    public ECDsa CloneAssertionKey()
    {
        var clone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        clone.ImportParameters(_assertionKey.ExportParameters(includePrivateParameters: true));
        return clone;
    }

    /// <summary>Mint a signed <c>private_key_jwt</c> client assertion for this client.</summary>
    public string Assertion(string audience, string? jti = null, long? iat = null, long? exp = null)
    {
        long now = iat ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string header = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["typ"] = "jwt",
            ["alg"] = "ES256",
            ["kid"] = Kid,
        });
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = ClientId,
            ["sub"] = ClientId,
            ["aud"] = audience,
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["iat"] = now,
            ["exp"] = exp ?? now + 60,
        });
        return JoseEs256.CreateJws(header, payload, _assertionKey);
    }

    private string BuildClientMetadata()
    {
        EcPublicJwk jwk = EcPublicJwk.FromP256(_assertionKey.ExportParameters(includePrivateParameters: false));
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["client_id"] = ClientId,
            ["application_type"] = "web",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = "atproto transition:generic",
            ["redirect_uris"] = new[] { RedirectUri },
            ["dpop_bound_access_tokens"] = true,
            ["token_endpoint_auth_method"] = "private_key_jwt",
            ["token_endpoint_auth_signing_alg"] = "ES256",
            ["jwks"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["keys"] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["kty"] = "EC",
                        ["crv"] = "P-256",
                        ["x"] = jwk.X,
                        ["y"] = jwk.Y,
                        ["use"] = "sig",
                        ["kid"] = Kid,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
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

/// <summary>An <see cref="HttpMessageHandler"/> that returns one canned JSON body for every request.</summary>
internal sealed class CannedJsonHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

// Confidential clients (private_key_jwt): the token endpoint authenticates the client with a signed
// client assertion, verified against the client's published JWKS. These tests drive the full PAR ->
// authorize -> token flow with a real assertion, prove an authorized write works, and cover the
// negative matrix (missing assertion, replayed assertion jti, and a DPoP proof that reuses the
// assertion key).
public sealed class OAuthConfidentialClientTests : IClassFixture<ConfidentialClientFixture>
{
    private const string Collection = "app.bsky.feed.post";
    private const string Scope = "atproto transition:generic";
    private readonly ConfidentialClientFixture _fx;

    public OAuthConfidentialClientTests(ConfidentialClientFixture fixture) => _fx = fixture;

    private string Base => _fx.BaseUrl;

    private static HttpClient NewClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
    });

    private async Task<string> CreateAccountAsync(HttpClient http, string handle, string password)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"{Base}/xrpc/com.atproto.server.createAccount", new { handle, password });
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("did").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostFormWithDpopAsync(HttpClient http, OAuthDpopClient dpop, string url, Dictionary<string, string> form)
    {
        var first = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        first.Headers.Add("DPoP", dpop.Proof("POST", url));
        HttpResponseMessage response = await http.SendAsync(first);
        if (response.StatusCode != HttpStatusCode.BadRequest || !response.Headers.TryGetValues("DPoP-Nonce", out var values))
            return response;
        if (!(await response.Content.ReadAsStringAsync()).Contains("use_dpop_nonce"))
            return response;
        var retry = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        retry.Headers.Add("DPoP", dpop.Proof("POST", url, values.Single()));
        return await http.SendAsync(retry);
    }

    private Dictionary<string, string> ParForm(string challenge, string assertion) => new()
    {
        ["client_id"] = _fx.ClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = _fx.RedirectUri,
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
        ["scope"] = Scope,
        ["state"] = "test-state",
        ["client_assertion_type"] = ClientAssertionValidator.JwtBearerAssertionType,
        ["client_assertion"] = assertion,
    };

    // Run PAR (with a client assertion) + authorize, returning the issued authorization code.
    private async Task<string> AuthorizeToCodeAsync(HttpClient http, OAuthDpopClient dpop, string handle, string password, string challenge, string parAssertion)
    {
        HttpResponseMessage par = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/par", ParForm(challenge, parAssertion));
        par.EnsureSuccessStatusCode();
        using JsonDocument parDoc = JsonDocument.Parse(await par.Content.ReadAsStringAsync());
        string requestUri = parDoc.RootElement.GetProperty("request_uri").GetString()!;

        string authorizeUrl = $"{Base}/oauth/authorize?client_id={Uri.EscapeDataString(_fx.ClientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
        string html = await (await http.GetAsync(authorizeUrl)).Content.ReadAsStringAsync();
        string csrf = Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"").Groups[1].Value;

        var approve = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _fx.ClientId,
            ["request_uri"] = requestUri,
            ["csrf"] = csrf,
            ["identifier"] = handle,
            ["password"] = password,
            ["action"] = "approve",
        });
        HttpResponseMessage redirect = await http.PostAsync($"{Base}/oauth/authorize", approve);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        return QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["code"]!;
    }

    private Dictionary<string, string> CodeGrant(string code, string verifier, string? assertion)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _fx.RedirectUri,
            ["client_id"] = _fx.ClientId,
            ["code_verifier"] = verifier,
        };
        if (assertion is not null)
        {
            form["client_assertion_type"] = ClientAssertionValidator.JwtBearerAssertionType;
            form["client_assertion"] = assertion;
        }
        return form;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Confidential_FullFlow_IssuesTokensAndAuthorizesAWrite()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "conf-happy.pds.localhost";
        string did = await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier), _fx.Assertion(_fx.TokenUrl));

        HttpResponseMessage token = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier, _fx.Assertion(_fx.TokenUrl)));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        string accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        Assert.Equal(did, doc.RootElement.GetProperty("sub").GetString());
        Assert.Equal(Scope, doc.RootElement.GetProperty("scope").GetString());
        string nonce = token.Headers.GetValues("DPoP-Nonce").Single();

        // The DPoP-bound access token authorizes a real write.
        string ath = DpopValidator.AccessTokenHash(accessToken);
        string writeUrl = $"{Base}/xrpc/com.atproto.repo.createRecord";
        var write = new HttpRequestMessage(HttpMethod.Post, writeUrl);
        write.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        write.Headers.Add("DPoP", dpop.Proof("POST", writeUrl, nonce, ath));
        write.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = Collection,
            ["record"] = new Dictionary<string, object?>
            {
                ["$type"] = Collection,
                ["text"] = "hello from a confidential client",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        });
        HttpResponseMessage writeResponse = await http.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);
    }

    [Fact]
    public async Task Confidential_MissingAssertionAtToken_IsRejected()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "conf-noassert.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier), _fx.Assertion(_fx.TokenUrl));

        // The code was minted for a confidential client, so the token exchange must carry an assertion.
        HttpResponseMessage token = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier, assertion: null));

        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Equal("invalid_client", await ErrorCodeAsync(token));
    }

    [Fact]
    public async Task Confidential_ReplayedAssertionJti_IsRejected()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "conf-replay.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();

        // One assertion (one jti) is presented at PAR and then reused at the token endpoint.
        string assertion = _fx.Assertion(_fx.TokenUrl, jti: "reused-assertion-jti");
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier), assertion);

        HttpResponseMessage token = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier, assertion));

        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Equal("invalid_client", await ErrorCodeAsync(token));
    }

    [Fact]
    public async Task Confidential_DpopProofReusingTheAssertionKey_IsRejected()
    {
        using HttpClient http = NewClient();
        // The DPoP proof is signed with the very key used for the client assertion, which is banned.
        using var dpop = new OAuthDpopClient(_fx.CloneAssertionKey());
        const string handle = "conf-samekey.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();

        HttpResponseMessage par = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/par",
            ParForm(Pkce.ComputeChallenge(verifier), _fx.Assertion(_fx.TokenUrl)));

        Assert.Equal(HttpStatusCode.BadRequest, par.StatusCode);
        Assert.Equal("invalid_client", await ErrorCodeAsync(par));
    }
}
