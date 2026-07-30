using System.Net;
using System.Text.Json;
using AtProto.OAuth;

namespace AtProto.Pds.Tests;

// The pushed authorization request endpoint (RFC 9126, atproto profile). These tests drive it with a
// real ES256 DPoP proof and the http://localhost development client (synthesized, no network), and
// exercise the security gates: the mandatory DPoP nonce challenge, proof replay rejection, PKCE S256
// enforcement, redirect_uri registration, and scope bounds.
public sealed class OAuthParTests : IClassFixture<PdsServerFixture>
{
    private const string Redirect = "http://127.0.0.1/callback";
    private readonly PdsServerFixture _fixture;

    public OAuthParTests(PdsServerFixture fixture) => _fixture = fixture;

    private string ParUrl => $"{_fixture.BaseUrl}/oauth/par";

    private static string ClientId =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope=atproto";

    private static FormUrlEncodedContent Form(
        string codeChallengeMethod = "S256",
        string scope = "atproto",
        string redirect = Redirect,
        string? codeChallenge = null) =>
        new(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["code_challenge"] = codeChallenge ?? Pkce.ComputeChallenge(Pkce.GenerateVerifier()),
            ["code_challenge_method"] = codeChallengeMethod,
            ["scope"] = scope,
            ["state"] = "test-state",
        });

    // A first request with no nonce always yields a use_dpop_nonce challenge carrying a fresh nonce.
    private async Task<string> ChallengeNonceAsync(OAuthDpopClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ParUrl) { Content = Form() };
        request.Headers.Add("DPoP", client.Proof("POST", ParUrl));
        HttpResponseMessage response = await _fixture.Http.SendAsync(request);
        return response.Headers.GetValues("DPoP-Nonce").Single();
    }

    private async Task<HttpResponseMessage> PostAsync(string dpopProof, FormUrlEncodedContent form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ParUrl) { Content = form };
        request.Headers.Add("DPoP", dpopProof);
        return await _fixture.Http.SendAsync(request);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Par_ChallengesForNonce_ThenIssuesRequestUri()
    {
        using var client = new OAuthDpopClient();

        var noNonce = new HttpRequestMessage(HttpMethod.Post, ParUrl) { Content = Form() };
        noNonce.Headers.Add("DPoP", client.Proof("POST", ParUrl));
        HttpResponseMessage challenge = await _fixture.Http.SendAsync(noNonce);

        Assert.Equal(HttpStatusCode.BadRequest, challenge.StatusCode);
        Assert.Equal("use_dpop_nonce", await ErrorCodeAsync(challenge));
        string nonce = challenge.Headers.GetValues("DPoP-Nonce").Single();

        HttpResponseMessage issued = await PostAsync(client.Proof("POST", ParUrl, nonce), Form());

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.True(issued.Headers.Contains("DPoP-Nonce"));
        using JsonDocument doc = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", doc.RootElement.GetProperty("request_uri").GetString());
        Assert.True(doc.RootElement.GetProperty("expires_in").GetInt32() > 0);
    }

    [Fact]
    public async Task Par_RejectsReplayedProof()
    {
        using var client = new OAuthDpopClient();
        string nonce = await ChallengeNonceAsync(client);
        string proof = client.Proof("POST", ParUrl, nonce);

        HttpResponseMessage first = await PostAsync(proof, Form());
        HttpResponseMessage replay = await PostAsync(proof, Form());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_dpop_proof", await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task Par_RejectsProofForWrongEndpoint()
    {
        using var client = new OAuthDpopClient();

        // A proof bound to a different htu must fail before any nonce challenge.
        HttpResponseMessage response = await PostAsync(
            client.Proof("POST", $"{_fixture.BaseUrl}/oauth/token"), Form());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_dpop_proof", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Par_RejectsPlainPkce()
    {
        using var client = new OAuthDpopClient();
        string nonce = await ChallengeNonceAsync(client);

        HttpResponseMessage response = await PostAsync(
            client.Proof("POST", ParUrl, nonce), Form(codeChallengeMethod: "plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Par_RejectsUnregisteredRedirectUri()
    {
        using var client = new OAuthDpopClient();
        string nonce = await ChallengeNonceAsync(client);

        HttpResponseMessage response = await PostAsync(
            client.Proof("POST", ParUrl, nonce), Form(redirect: "http://127.0.0.1/not-registered"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Par_RejectsScopeBeyondClientRegistration()
    {
        using var client = new OAuthDpopClient();
        string nonce = await ChallengeNonceAsync(client);

        HttpResponseMessage response = await PostAsync(
            client.Proof("POST", ParUrl, nonce), Form(scope: "atproto transition:generic"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", await ErrorCodeAsync(response));
    }
}
