using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using Microsoft.AspNetCore.WebUtilities;

namespace AtProto.Pds.Tests;

// Resource-server (RS) enforcement: the write endpoints accept a DPoP-bound OAuth access token, in
// addition to the app-password session Bearer JWT. These tests drive the full PAR -> authorize ->
// token flow to obtain a real sender-constrained access token, then exercise an authorized write and
// the negative matrix: the DPoP-nonce challenge, a missing ath binding, a wrong DPoP key (cnf.jkt
// mismatch), a replayed proof, an under-scoped token, and the still-working app-password path.
public sealed class OAuthResourceTests : IClassFixture<PdsServerFixture>
{
    private const string Redirect = "http://127.0.0.1/callback";
    private const string Collection = "app.bsky.feed.post";
    private readonly PdsServerFixture _fixture;

    public OAuthResourceTests(PdsServerFixture fixture) => _fixture = fixture;

    private string Base => _fixture.BaseUrl;

    private static string ClientId(string scope) =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope={Uri.EscapeDataString(scope)}";

    private static HttpClient NewClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
    });

    // ----- Full authorization-code flow, returning a DPoP-bound access token and a usable nonce -----

    private sealed record Login(string AccessToken, string Did, string Nonce);

    private async Task<Login> LoginAsync(HttpClient http, OAuthDpopClient dpop, string handle, string password, string scope)
    {
        string clientId = ClientId(scope);
        string did = await CreateAccountAsync(http, handle, password);
        string verifier = Pkce.GenerateVerifier();
        string code = await AuthorizeToCodeAsync(http, dpop, clientId, handle, password, scope, Pkce.ComputeChallenge(verifier));

        var grant = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };
        HttpResponseMessage token = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/token", grant);
        token.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        // The AS and RS share one nonce service, so a token-response nonce is immediately usable at the RS.
        return new Login(
            doc.RootElement.GetProperty("access_token").GetString()!,
            did,
            token.Headers.GetValues("DPoP-Nonce").Single());
    }

    private async Task<string> CreateAccountAsync(HttpClient http, string handle, string password)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"{Base}/xrpc/com.atproto.server.createAccount", new { handle, password });
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("did").GetString()!;
    }

    private async Task<string> AuthorizeToCodeAsync(
        HttpClient http, OAuthDpopClient dpop, string clientId, string handle, string password, string scope, string challenge)
    {
        var parForm = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = Redirect,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["scope"] = scope,
            ["state"] = "test-state",
        };
        HttpResponseMessage par = await PostFormWithDpopAsync(http, dpop, $"{Base}/oauth/par", parForm);
        par.EnsureSuccessStatusCode();
        using JsonDocument parDoc = JsonDocument.Parse(await par.Content.ReadAsStringAsync());
        string requestUri = parDoc.RootElement.GetProperty("request_uri").GetString()!;

        string authorizeUrl = $"{Base}/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
        string html = await (await http.GetAsync(authorizeUrl)).Content.ReadAsStringAsync();
        string csrf = Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"").Groups[1].Value;

        var approve = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["request_uri"] = requestUri,
            ["csrf"] = csrf,
            ["identifier"] = handle,
            ["password"] = password,
            ["action"] = "approve",
        });
        HttpResponseMessage redirect = await http.PostAsync($"{Base}/oauth/authorize", approve);
        return QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["code"]!;
    }

    // POST a form with the standard DPoP-nonce challenge/retry (for PAR and token).
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

    // ----- Resource request (createRecord) with an explicit DPoP proof -----

    private HttpRequestMessage BuildWrite(string accessToken, string did, string proof)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/xrpc/com.atproto.repo.createRecord");
        req.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        req.Headers.Add("DPoP", proof);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = Collection,
            ["record"] = new Dictionary<string, object?>
            {
                ["$type"] = Collection,
                ["text"] = "hello from an oauth client",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        });
        return req;
    }

    private string WriteUrl => $"{Base}/xrpc/com.atproto.repo.createRecord";

    private static string ChallengeError(HttpResponseMessage response) =>
        response.Headers.TryGetValues("WWW-Authenticate", out var values) ? values.Single() : string.Empty;

    [Fact]
    public async Task Write_WithDpopBoundAccessToken_Succeeds()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        Login login = await LoginAsync(http, dpop, "rs-happy.pds.localhost", "hunter2", "atproto transition:generic");

        string ath = DpopValidator.AccessTokenHash(login.AccessToken);
        HttpRequestMessage req = BuildWrite(login.AccessToken, login.Did, dpop.Proof("POST", WriteUrl, login.Nonce, ath));
        HttpResponseMessage response = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("DPoP-Nonce"));
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith($"at://{login.Did}/{Collection}/", doc.RootElement.GetProperty("uri").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("cid").GetString()));
    }

    [Fact]
    public async Task Write_MissingNonce_ChallengesThenSucceeds()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        Login login = await LoginAsync(http, dpop, "rs-nonce.pds.localhost", "hunter2", "atproto transition:generic");
        string ath = DpopValidator.AccessTokenHash(login.AccessToken);

        // A proof with no nonce is a recoverable challenge, not an outright rejection.
        HttpResponseMessage challenge = await http.SendAsync(
            BuildWrite(login.AccessToken, login.Did, dpop.Proof("POST", WriteUrl, nonce: null, ath: ath)));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains("use_dpop_nonce", ChallengeError(challenge));
        string nonce = challenge.Headers.GetValues("DPoP-Nonce").Single();

        // Retrying with the issued nonce succeeds.
        HttpResponseMessage ok = await http.SendAsync(
            BuildWrite(login.AccessToken, login.Did, dpop.Proof("POST", WriteUrl, nonce, ath)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Write_WithoutAth_IsRejected()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        Login login = await LoginAsync(http, dpop, "rs-ath.pds.localhost", "hunter2", "atproto transition:generic");

        // A resource request must bind the proof to the token via ath; omitting it fails.
        HttpResponseMessage response = await http.SendAsync(
            BuildWrite(login.AccessToken, login.Did, dpop.Proof("POST", WriteUrl, login.Nonce, ath: null)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_dpop_proof", ChallengeError(response));
    }

    [Fact]
    public async Task Write_WithWrongDpopKey_IsRejected()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        using var otherKey = new OAuthDpopClient();
        Login login = await LoginAsync(http, dpop, "rs-jkt.pds.localhost", "hunter2", "atproto transition:generic");

        // The token is bound to dpop's key; a proof from a different key must not be honored.
        string ath = DpopValidator.AccessTokenHash(login.AccessToken);
        HttpResponseMessage response = await http.SendAsync(
            BuildWrite(login.AccessToken, login.Did, otherKey.Proof("POST", WriteUrl, login.Nonce, ath)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", ChallengeError(response));
    }

    [Fact]
    public async Task Write_ReplayedProof_IsRejected()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        Login login = await LoginAsync(http, dpop, "rs-replay.pds.localhost", "hunter2", "atproto transition:generic");

        string ath = DpopValidator.AccessTokenHash(login.AccessToken);
        string proof = dpop.Proof("POST", WriteUrl, login.Nonce, ath);

        HttpResponseMessage first = await http.SendAsync(BuildWrite(login.AccessToken, login.Did, proof));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The same proof (same jti) cannot be used twice.
        HttpResponseMessage replay = await http.SendAsync(BuildWrite(login.AccessToken, login.Did, proof));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("invalid_dpop_proof", ChallengeError(replay));
    }

    [Fact]
    public async Task Write_WithoutWriteScope_IsForbidden()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        // A token scoped to atproto only does not grant the transitional write level.
        Login login = await LoginAsync(http, dpop, "rs-scope.pds.localhost", "hunter2", "atproto");

        string ath = DpopValidator.AccessTokenHash(login.AccessToken);
        HttpResponseMessage response = await http.SendAsync(
            BuildWrite(login.AccessToken, login.Did, dpop.Proof("POST", WriteUrl, login.Nonce, ath)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient_scope", ChallengeError(response));
    }

    [Fact]
    public async Task Write_WithAppPasswordSession_StillWorks()
    {
        using HttpClient http = NewClient();
        // The legacy Bearer session path is unaffected by the additive DPoP path.
        HttpResponseMessage create = await http.PostAsJsonAsync(
            $"{Base}/xrpc/com.atproto.server.createAccount", new { handle = "rs-legacy.pds.localhost", password = "hunter2" });
        create.EnsureSuccessStatusCode();
        using JsonDocument account = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        string did = account.RootElement.GetProperty("did").GetString()!;
        string accessJwt = account.RootElement.GetProperty("accessJwt").GetString()!;

        var req = new HttpRequestMessage(HttpMethod.Post, WriteUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["repo"] = did,
            ["collection"] = Collection,
            ["record"] = new Dictionary<string, object?>
            {
                ["$type"] = Collection,
                ["text"] = "written with an app password",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        });
        HttpResponseMessage response = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
