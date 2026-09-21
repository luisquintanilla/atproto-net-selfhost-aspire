using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using AtProto.Pds;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace AtProto.Pds.Tests;

public sealed class PermissionRuntimeTests : IAsyncLifetime
{
    private const string Redirect = "http://127.0.0.1/callback";
    private const string SetNsid = "com.example.permissions.write";
    private const string Collection = "com.example.permissions.post";
    private const string OtherCollection = "com.example.permissions.other";

    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _baseUrl = string.Empty;

    public async Task InitializeAsync()
    {
        _baseUrl = $"http://127.0.0.1:{FreePort()}";
        _app = PdsHost.Build(Array.Empty<string>(), options =>
        {
            options.PublicUrl = _baseUrl;
            options.HandleDomain = "pds.localhost";
        }, services => services.AddSingleton<IPermissionSetResolver>(_ =>
            new InMemoryPermissionSetResolver(
            [
                new PermissionSetResolution(
                    SetNsid,
                    [PermissionDeclaration.Repo(Collection, PermissionAction.Create, PermissionAction.Update)]),
                new PermissionSetResolution(
                    "com.example.permissions.expired",
                    [PermissionDeclaration.Repo(Collection)],
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                new PermissionSetResolution(
                    "com.example.permissions.stale",
                    [PermissionDeclaration.Repo(Collection)],
                    StaleAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                new PermissionSetResolution(
                    "com.example.permissions.unknown",
                    [new PermissionDeclaration("unknown", new Dictionary<string, IReadOnlyList<string>>())]),
            ])));
        _app.Urls.Clear();
        _app.Urls.Add(_baseUrl);
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Granular_scope_rejects_wrong_collection_and_action()
    {
        using var dpop = new OAuthDpopClient();
        string handle = "granular-" + Guid.NewGuid().ToString("N")[..8] + ".pds.localhost";
        string did = await CreateAccountAsync(handle);
        Login login = await LoginAsync(dpop, handle, $"atproto repo:{Collection}?action=create");

        HttpResponseMessage create = await SendWriteAsync(
            dpop, login, "com.atproto.repo.createRecord", new
            {
                repo = did,
                collection = Collection,
                rkey = "one",
                record = Record(Collection),
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        HttpResponseMessage wrongCollection = await SendWriteAsync(
            dpop, login, "com.atproto.repo.createRecord", new
            {
                repo = did,
                collection = OtherCollection,
                rkey = "one",
                record = Record(OtherCollection),
            });
        Assert.Equal(HttpStatusCode.Forbidden, wrongCollection.StatusCode);

        HttpResponseMessage wrongAction = await SendWriteAsync(
            dpop, login, "com.atproto.repo.putRecord", new
            {
                repo = did,
                collection = Collection,
                rkey = "one",
                record = Record(Collection),
            });
        Assert.Equal(HttpStatusCode.Forbidden, wrongAction.StatusCode);
    }

    [Fact]
    public async Task Wildcard_scope_can_be_restricted_to_create()
    {
        using var dpop = new OAuthDpopClient();
        string handle = "wildcard-" + Guid.NewGuid().ToString("N")[..8] + ".pds.localhost";
        string did = await CreateAccountAsync(handle);
        Login login = await LoginAsync(dpop, handle, "atproto repo:*?action=create");

        HttpResponseMessage create = await SendWriteAsync(
            dpop, login, "com.atproto.repo.createRecord", new
            {
                repo = did,
                collection = OtherCollection,
                rkey = "one",
                record = Record(OtherCollection),
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        HttpResponseMessage update = await SendWriteAsync(
            dpop, login, "com.atproto.repo.putRecord", new
            {
                repo = did,
                collection = OtherCollection,
                rkey = "one",
                record = Record(OtherCollection),
            });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task Included_permission_set_is_snapshotted_and_enforces_actions()
    {
        using var dpop = new OAuthDpopClient();
        string handle = "include-" + Guid.NewGuid().ToString("N")[..8] + ".pds.localhost";
        string did = await CreateAccountAsync(handle);
        Login login = await LoginAsync(dpop, handle, $"atproto include:{SetNsid}");

        HttpResponseMessage create = await SendWriteAsync(
            dpop, login, "com.atproto.repo.createRecord", new
            {
                repo = did,
                collection = Collection,
                rkey = "one",
                record = Record(Collection),
            });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        HttpResponseMessage update = await SendWriteAsync(
            dpop, login, "com.atproto.repo.putRecord", new
            {
                repo = did,
                collection = Collection,
                rkey = "one",
                record = Record(Collection),
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        HttpResponseMessage delete = await SendWriteAsync(
            dpop, login, "com.atproto.repo.deleteRecord", new
            {
                repo = did,
                collection = Collection,
                rkey = "one",
            });
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Invalid_permission_set_declarations_are_rejected_at_par()
    {
        using var dpop = new OAuthDpopClient();

        HttpResponseMessage unknown = await RequestParAsync(
            dpop, "atproto include:com.example.permissions.unknown");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid_scope", await ErrorCodeAsync(unknown));

        HttpResponseMessage expired = await RequestParAsync(
            dpop, "atproto include:com.example.permissions.expired");
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal("invalid_scope", await ErrorCodeAsync(expired));

        HttpResponseMessage stale = await RequestParAsync(
            dpop, "atproto include:com.example.permissions.stale");
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("invalid_scope", await ErrorCodeAsync(stale));
    }

    [Fact]
    public async Task Partial_collection_wildcards_are_rejected_at_par()
    {
        using var dpop = new OAuthDpopClient();

        HttpResponseMessage response = await RequestParAsync(
            dpop, "atproto repo:com.example.*");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", await ErrorCodeAsync(response));
    }

    private async Task<Login> LoginAsync(OAuthDpopClient dpop, string handle, string scope)
    {
        string password = "hunter2";
        string clientId = ClientId(scope);
        string verifier = Pkce.GenerateVerifier();
        HttpResponseMessage par = await RequestParAsync(
            dpop, scope, clientId, Pkce.ComputeChallenge(verifier));
        par.EnsureSuccessStatusCode();
        using JsonDocument parDoc = JsonDocument.Parse(await par.Content.ReadAsStringAsync());
        string requestUri = parDoc.RootElement.GetProperty("request_uri").GetString()!;

        string authorizeUrl = $"{_baseUrl}/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
        string html = await (await _http.GetAsync(authorizeUrl)).Content.ReadAsStringAsync();
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
        HttpResponseMessage redirect = await _http.PostAsync($"{_baseUrl}/oauth/authorize", approve);
        string code = QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["code"]!;

        HttpResponseMessage token = await PostFormWithDpopAsync(dpop, "/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        });
        token.EnsureSuccessStatusCode();
        using JsonDocument tokenDoc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        return new Login(
            tokenDoc.RootElement.GetProperty("access_token").GetString()!,
            token.Headers.GetValues("DPoP-Nonce").Single());
    }

    private async Task<HttpResponseMessage> RequestParAsync(
        OAuthDpopClient dpop,
        string scope,
        string? clientId = null,
        string? challenge = null)
    {
        clientId ??= ClientId(scope);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = Redirect,
            ["code_challenge"] = challenge ?? Pkce.ComputeChallenge(Pkce.GenerateVerifier()),
            ["code_challenge_method"] = "S256",
            ["scope"] = scope,
            ["state"] = "test-state",
        };
        return await PostFormWithDpopAsync(dpop, "/oauth/par", form);
    }

    private async Task<HttpResponseMessage> PostFormWithDpopAsync(
        OAuthDpopClient dpop,
        string path,
        Dictionary<string, string> form)
    {
        string url = _baseUrl + path;
        var first = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        first.Headers.Add("DPoP", dpop.Proof("POST", url));
        HttpResponseMessage response = await _http.SendAsync(first);
        if (response.StatusCode != HttpStatusCode.BadRequest
            || !response.Headers.TryGetValues("DPoP-Nonce", out var values)
            || !(await response.Content.ReadAsStringAsync()).Contains("use_dpop_nonce"))
            return response;

        var retry = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        retry.Headers.Add("DPoP", dpop.Proof("POST", url, values.Single()));
        return await _http.SendAsync(retry);
    }

    private async Task<HttpResponseMessage> SendWriteAsync(
        OAuthDpopClient dpop,
        Login login,
        string operation,
        object body)
    {
        string path = $"/xrpc/{operation}";
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", login.AccessToken);
        request.Headers.Add("DPoP", dpop.Proof("POST", _baseUrl + path, login.Nonce,
            DpopValidator.AccessTokenHash(login.AccessToken)));
        return await _http.SendAsync(request);
    }

    private async Task<string> CreateAccountAsync(string handle)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/xrpc/com.atproto.server.createAccount", new { handle, password = "hunter2" });
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("did").GetString()!;
    }

    private static string ClientId(string scope) =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope={Uri.EscapeDataString(scope)}";

    private static Dictionary<string, object?> Record(string collection) => new()
    {
        ["$type"] = collection,
        ["text"] = "permission test",
        ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetString()!;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Login(string AccessToken, string Nonce);
}
