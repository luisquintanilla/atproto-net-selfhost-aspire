using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using Microsoft.AspNetCore.WebUtilities;

namespace AtProto.Pds.Tests;

// The token endpoint (atproto profile): the authorization_code exchange and refresh_token rotation.
// These tests drive the whole PAR -> authorize -> token flow with a real DPoP client and a real
// account, then assert the tokens are DPoP-bound (cnf.jkt), and that PKCE mismatch, a wrong DPoP
// key, code reuse, and refresh reuse are all rejected (the last two revoking the session).
public sealed class OAuthTokenTests : IClassFixture<PdsServerFixture>
{
    private const string Redirect = "http://127.0.0.1/callback";
    private readonly PdsServerFixture _fixture;

    public OAuthTokenTests(PdsServerFixture fixture) => _fixture = fixture;

    private string Base => _fixture.BaseUrl;
    private static string ClientId =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope=atproto";

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

    // Post a form with an automatic DPoP-nonce challenge/retry, so callers get the real response.
    private async Task<HttpResponseMessage> PostWithDpopAsync(HttpClient http, OAuthDpopClient dpop, string url, Dictionary<string, string> form)
    {
        var first = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        first.Headers.Add("DPoP", dpop.Proof("POST", url));
        HttpResponseMessage response = await http.SendAsync(first);
        if (response.StatusCode != HttpStatusCode.BadRequest || !response.Headers.TryGetValues("DPoP-Nonce", out var values))
            return response;
        if (!(await response.Content.ReadAsStringAsync()).Contains("use_dpop_nonce"))
            return response;

        string nonce = values.Single();
        var retry = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        retry.Headers.Add("DPoP", dpop.Proof("POST", url, nonce));
        return await http.SendAsync(retry);
    }

    private Dictionary<string, string> ParForm(string challenge) => new()
    {
        ["client_id"] = ClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = Redirect,
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
        ["scope"] = "atproto",
        ["state"] = "test-state",
    };

    // Run PAR + authorize with a fixed PKCE challenge and return the issued authorization code.
    private async Task<string> AuthorizeToCodeAsync(HttpClient http, OAuthDpopClient dpop, string handle, string password, string challenge)
    {
        HttpResponseMessage par = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/par", ParForm(challenge));
        using JsonDocument parDoc = JsonDocument.Parse(await par.Content.ReadAsStringAsync());
        string requestUri = parDoc.RootElement.GetProperty("request_uri").GetString()!;

        string authorizeUrl = $"{Base}/oauth/authorize?client_id={Uri.EscapeDataString(ClientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
        string html = await (await http.GetAsync(authorizeUrl)).Content.ReadAsStringAsync();
        string csrf = Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"").Groups[1].Value;

        var approve = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["request_uri"] = requestUri,
            ["csrf"] = csrf,
            ["identifier"] = handle,
            ["password"] = password,
            ["action"] = "approve",
        });
        HttpResponseMessage redirect = await http.PostAsync($"{Base}/oauth/authorize", approve);
        return QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["code"]!;
    }

    private Dictionary<string, string> CodeGrant(string code, string verifier) => new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = Redirect,
        ["client_id"] = ClientId,
        ["code_verifier"] = verifier,
    };

    private Dictionary<string, string> RefreshGrant(string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["client_id"] = ClientId,
    };

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        string segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        segment = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(segment)).RootElement.Clone();
    }

    [Fact]
    public async Task Token_AuthorizationCode_IssuesDpopBoundTokens()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "tok-happy.pds.localhost";
        string did = await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier));

        HttpResponseMessage response = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("DPoP-Nonce"));
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("DPoP", root.GetProperty("token_type").GetString());
        Assert.Equal(did, root.GetProperty("sub").GetString());
        Assert.Equal("atproto", root.GetProperty("scope").GetString());
        Assert.True(root.GetProperty("expires_in").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("refresh_token").GetString()));

        // The access token is sender-constrained to the client's DPoP key.
        JsonElement claims = DecodeJwtPayload(root.GetProperty("access_token").GetString()!);
        Assert.Equal(did, claims.GetProperty("sub").GetString());
        Assert.Equal(dpop.Jkt, claims.GetProperty("cnf").GetProperty("jkt").GetString());
    }

    [Fact]
    public async Task Token_RejectsPkceMismatch()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "tok-pkce.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(Pkce.GenerateVerifier()));

        HttpResponseMessage response = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token",
            CodeGrant(code, Pkce.GenerateVerifier()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Token_RejectsWrongDpopKey()
    {
        using HttpClient http = NewClient();
        using var parKey = new OAuthDpopClient();
        using var otherKey = new OAuthDpopClient();
        const string handle = "tok-jkt.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, parKey, handle, "hunter2", Pkce.ComputeChallenge(verifier));

        // Exchange with a different key than the one bound at PAR.
        HttpResponseMessage response = await PostWithDpopAsync(http, otherKey, $"{Base}/oauth/token", CodeGrant(code, verifier));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Token_CodeReuse_RevokesSession()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "tok-reuse.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier));

        HttpResponseMessage first = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string refreshToken = firstDoc.RootElement.GetProperty("refresh_token").GetString()!;

        // Replaying the code is rejected and revokes the session it produced.
        HttpResponseMessage replay = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(replay));

        // The refresh token from the first exchange no longer works.
        HttpResponseMessage refresh = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", RefreshGrant(refreshToken));
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(refresh));
    }

    [Fact]
    public async Task Token_RefreshRotation_Works()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "tok-refresh.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, handle, "hunter2", Pkce.ComputeChallenge(verifier));

        HttpResponseMessage exchange = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", CodeGrant(code, verifier));
        string refresh1 = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync()).RootElement.GetProperty("refresh_token").GetString()!;

        HttpResponseMessage rotated = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", RefreshGrant(refresh1));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using JsonDocument rotatedDoc = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
        string refresh2 = rotatedDoc.RootElement.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh1, refresh2);
        Assert.False(string.IsNullOrEmpty(rotatedDoc.RootElement.GetProperty("access_token").GetString()));

        // The rotated-away token is single-use; presenting it again is rejected.
        HttpResponseMessage reused = await PostWithDpopAsync(http, dpop, $"{Base}/oauth/token", RefreshGrant(refresh1));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("invalid_grant", await ErrorCodeAsync(reused));
    }
}
