using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using Microsoft.AspNetCore.WebUtilities;

namespace AtProto.Pds.Tests;

// The authorization interface (atproto profile): sign-in plus consent. These tests drive the full
// PAR -> authorize flow with a real DPoP client and a real account, and confirm the approve path
// redirects back with code+state+iss, denial redirects with access_denied, a wrong password
// re-renders instead of issuing a code, and the CSRF and request_uri gates reject bad input.
public sealed class OAuthAuthorizeTests : IClassFixture<PdsServerFixture>
{
    private const string Redirect = "http://127.0.0.1/callback";
    private readonly PdsServerFixture _fixture;

    public OAuthAuthorizeTests(PdsServerFixture fixture) => _fixture = fixture;

    private static string ClientId =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope=atproto";

    private static HttpClient NewClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
    });

    private async Task CreateAccountAsync(HttpClient http, string handle, string password)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"{_fixture.BaseUrl}/xrpc/com.atproto.server.createAccount", new { handle, password });
        response.EnsureSuccessStatusCode();
    }

    // Complete a pushed authorization request (nonce challenge then issue) and return its request_uri.
    private async Task<string> CompleteParAsync(HttpClient http, OAuthDpopClient dpop)
    {
        string parUrl = $"{_fixture.BaseUrl}/oauth/par";
        Dictionary<string, string> Form() => new()
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = Redirect,
            ["code_challenge"] = Pkce.ComputeChallenge(Pkce.GenerateVerifier()),
            ["code_challenge_method"] = "S256",
            ["scope"] = "atproto",
            ["state"] = "test-state",
        };

        var challenge = new HttpRequestMessage(HttpMethod.Post, parUrl) { Content = new FormUrlEncodedContent(Form()) };
        challenge.Headers.Add("DPoP", dpop.Proof("POST", parUrl));
        HttpResponseMessage challenged = await http.SendAsync(challenge);
        string nonce = challenged.Headers.GetValues("DPoP-Nonce").Single();

        var issue = new HttpRequestMessage(HttpMethod.Post, parUrl) { Content = new FormUrlEncodedContent(Form()) };
        issue.Headers.Add("DPoP", dpop.Proof("POST", parUrl, nonce));
        HttpResponseMessage issued = await http.SendAsync(issue);
        using JsonDocument doc = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("request_uri").GetString()!;
    }

    private string AuthorizeUrl(string requestUri) =>
        $"{_fixture.BaseUrl}/oauth/authorize?client_id={Uri.EscapeDataString(ClientId)}&request_uri={Uri.EscapeDataString(requestUri)}";

    private async Task<string> GetCsrfAsync(HttpClient http, string requestUri)
    {
        HttpResponseMessage page = await http.GetAsync(AuthorizeUrl(requestUri));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Match match = Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "CSRF token not found in the authorize page");
        return match.Groups[1].Value;
    }

    private FormUrlEncodedContent AuthorizeForm(string requestUri, string csrf, string identifier, string password, string action) =>
        new(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["request_uri"] = requestUri,
            ["csrf"] = csrf,
            ["identifier"] = identifier,
            ["password"] = password,
            ["action"] = action,
        });

    [Fact]
    public async Task Authorize_Approve_RedirectsWithCode()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "auth-approve.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string requestUri = await CompleteParAsync(http, dpop);
        string csrf = await GetCsrfAsync(http, requestUri);

        HttpResponseMessage response = await http.PostAsync(
            $"{_fixture.BaseUrl}/oauth/authorize",
            AuthorizeForm(requestUri, csrf, handle, "hunter2", "approve"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Uri location = response.Headers.Location!;
        Assert.Equal("http://127.0.0.1/callback", location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.False(string.IsNullOrEmpty(query["code"]));
        Assert.Equal("test-state", query["state"]);
        Assert.Equal(_fixture.BaseUrl, query["iss"]);
    }

    [Fact]
    public async Task Authorize_Deny_RedirectsWithAccessDenied()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "auth-deny.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string requestUri = await CompleteParAsync(http, dpop);
        string csrf = await GetCsrfAsync(http, requestUri);

        HttpResponseMessage response = await http.PostAsync(
            $"{_fixture.BaseUrl}/oauth/authorize",
            AuthorizeForm(requestUri, csrf, handle, "hunter2", "deny"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("test-state", query["state"]);
        Assert.False(query.ContainsKey("code"));
    }

    [Fact]
    public async Task Authorize_WrongPassword_ReRendersWithoutCode()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "auth-badpass.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string requestUri = await CompleteParAsync(http, dpop);
        string csrf = await GetCsrfAsync(http, requestUri);

        HttpResponseMessage response = await http.PostAsync(
            $"{_fixture.BaseUrl}/oauth/authorize",
            AuthorizeForm(requestUri, csrf, handle, "wrong-password", "approve"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Invalid identifier or password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_RejectsMissingCsrf()
    {
        using HttpClient http = NewClient();
        using var dpop = new OAuthDpopClient();
        const string handle = "auth-csrf.pds.localhost";
        await CreateAccountAsync(http, handle, "hunter2");
        string requestUri = await CompleteParAsync(http, dpop);
        await GetCsrfAsync(http, requestUri);

        HttpResponseMessage response = await http.PostAsync(
            $"{_fixture.BaseUrl}/oauth/authorize",
            AuthorizeForm(requestUri, "forged-token", handle, "hunter2", "approve"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_RejectsUnknownRequestUri()
    {
        using HttpClient http = NewClient();

        HttpResponseMessage response = await http.GetAsync(
            AuthorizeUrl("urn:ietf:params:oauth:request_uri:req-does-not-exist"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
