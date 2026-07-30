using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtProto.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;

namespace AtProto.Pds.Tests;

// Proves the durable "production" OAuth profile: with Pds:Storage=sqlite the OAuth store (PAR
// requests, authorization codes, sessions, refresh tokens) is SqliteOAuthStore, so an issued OAuth
// session survives a full process restart. Phase 1 completes the authorization-code flow and does a
// DPoP-authorized write; phase 2, a brand-new host over the same SQLite files, rotates the phase-1
// refresh token (proving the session persisted) and writes again with the re-issued access token.
// This also exercises the realistic proxy split where the DPoP htu is the advertised public origin,
// not the loopback address the server actually listens on.
public sealed class OAuthPersistenceTests : IDisposable
{
    private const string PublicUrl = "http://pds.example:5100";
    private const string Redirect = "http://127.0.0.1/callback";
    private const string Scope = "atproto transition:generic";
    private const string Collection = "app.bsky.feed.post";
    private const string Handle = "durable-oauth.pds.localhost";
    private const string Password = "hunter2";

    private static string ClientId =>
        $"http://localhost/?redirect_uri={Uri.EscapeDataString(Redirect)}&scope={Uri.EscapeDataString(Scope)}";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "atproto-oauth-persist-" + Guid.NewGuid().ToString("N"), "pds.db");

    [Fact]
    public async Task OAuth_session_survives_a_restart_under_sqlite()
    {
        string did;
        string refresh1;
        using var dpop = new OAuthDpopClient();

        // Phase 1: complete the flow, then do a DPoP-authorized write, and shut down.
        await using (Phase phase = await Phase.StartAsync(_dbPath))
        {
            did = await phase.CreateAccountAsync(Handle, Password);
            (string access1, refresh1) = await phase.LoginAsync(dpop, Handle, Password);
            HttpResponseMessage write = await phase.WriteAsync(dpop, access1, did);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        // The OAuth database file was created alongside the PDS database.
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(_dbPath)!, "oauth.db")),
            "the SqliteOAuthStore database should have been created under the production profile");

        // Phase 2: a brand-new host over the same SQLite files rehydrates the OAuth session.
        await using (Phase phase = await Phase.StartAsync(_dbPath))
        {
            // The refresh token from the previous process still rotates: the session persisted.
            (string access2, string refresh2) = await phase.RefreshAsync(dpop, refresh1);
            Assert.NotEqual(refresh1, refresh2);
            // And the re-issued access token authorizes a fresh write in the new process.
            HttpResponseMessage write = await phase.WriteAsync(dpop, access2, did);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }
    }

    public void Dispose()
    {
        string? dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    // One booted PDS instance on a loopback port, backed by shared SQLite files, advertising a fixed
    // public origin so DIDs, the issuer, and DPoP htu stay stable across restarts.
    private sealed class Phase : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _requestBase;
        private readonly HttpClient _http;

        private Phase(WebApplication app, string requestBase)
        {
            _app = app;
            _requestBase = requestBase;
            _http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            });
        }

        public static async Task<Phase> StartAsync(string dbPath)
        {
            string requestBase = $"http://127.0.0.1:{FreePort()}";
            WebApplication app = PdsHost.Build(Array.Empty<string>(), options =>
            {
                options.PublicUrl = PublicUrl;
                options.HandleDomain = "pds.localhost";
                options.Storage = "sqlite";
                options.SqlitePath = dbPath;
            });
            app.Urls.Clear();
            app.Urls.Add(requestBase);
            await app.StartAsync();
            return new Phase(app, requestBase);
        }

        // DPoP htu is derived from the advertised public origin, not the loopback listen address.
        private static string Htu(string path) => PublicUrl + path;

        public async Task<string> CreateAccountAsync(string handle, string password)
        {
            HttpResponseMessage resp = await _http.PostAsJsonAsync(
                $"{_requestBase}/xrpc/com.atproto.server.createAccount", new { handle, password });
            resp.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("did").GetString()!;
        }

        public async Task<(string Access, string Refresh)> LoginAsync(OAuthDpopClient dpop, string handle, string password)
        {
            string verifier = Pkce.GenerateVerifier();
            string code = await AuthorizeToCodeAsync(dpop, handle, password, Pkce.ComputeChallenge(verifier));
            var grant = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Redirect,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
            };
            HttpResponseMessage token = await PostFormWithDpopAsync(dpop, "/oauth/token", grant);
            token.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("access_token").GetString()!, doc.RootElement.GetProperty("refresh_token").GetString()!);
        }

        public async Task<(string Access, string Refresh)> RefreshAsync(OAuthDpopClient dpop, string refreshToken)
        {
            var grant = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            };
            HttpResponseMessage token = await PostFormWithDpopAsync(dpop, "/oauth/token", grant);
            token.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("access_token").GetString()!, doc.RootElement.GetProperty("refresh_token").GetString()!);
        }

        private async Task<string> AuthorizeToCodeAsync(OAuthDpopClient dpop, string handle, string password, string challenge)
        {
            var parForm = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = Redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["scope"] = Scope,
                ["state"] = "test-state",
            };
            HttpResponseMessage par = await PostFormWithDpopAsync(dpop, "/oauth/par", parForm);
            par.EnsureSuccessStatusCode();
            using JsonDocument parDoc = JsonDocument.Parse(await par.Content.ReadAsStringAsync());
            string requestUri = parDoc.RootElement.GetProperty("request_uri").GetString()!;

            string authorizeUrl = $"{_requestBase}/oauth/authorize?client_id={Uri.EscapeDataString(ClientId)}&request_uri={Uri.EscapeDataString(requestUri)}";
            string html = await (await _http.GetAsync(authorizeUrl)).Content.ReadAsStringAsync();
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
            HttpResponseMessage redirect = await _http.PostAsync($"{_requestBase}/oauth/authorize", approve);
            return QueryHelpers.ParseQuery(redirect.Headers.Location!.Query)["code"]!;
        }

        // Post a form with the standard DPoP-nonce challenge/retry. A restart rotates the per-process
        // nonce secret, so phase 2 naturally takes the challenge path on its first request.
        private async Task<HttpResponseMessage> PostFormWithDpopAsync(OAuthDpopClient dpop, string path, Dictionary<string, string> form)
        {
            string url = _requestBase + path;
            string htu = Htu(path);
            var first = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            first.Headers.Add("DPoP", dpop.Proof("POST", htu));
            HttpResponseMessage response = await _http.SendAsync(first);
            if (response.StatusCode != HttpStatusCode.BadRequest || !response.Headers.TryGetValues("DPoP-Nonce", out var values))
                return response;
            if (!(await response.Content.ReadAsStringAsync()).Contains("use_dpop_nonce"))
                return response;
            var retry = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            retry.Headers.Add("DPoP", dpop.Proof("POST", htu, values.Single()));
            return await _http.SendAsync(retry);
        }

        public async Task<HttpResponseMessage> WriteAsync(OAuthDpopClient dpop, string accessToken, string did)
        {
            const string path = "/xrpc/com.atproto.repo.createRecord";
            string url = _requestBase + path;
            string htu = Htu(path);
            string ath = DpopValidator.AccessTokenHash(accessToken);

            HttpRequestMessage Build(string? nonce)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
                req.Headers.Add("DPoP", dpop.Proof("POST", htu, nonce, ath));
                req.Content = JsonContent.Create(new Dictionary<string, object?>
                {
                    ["repo"] = did,
                    ["collection"] = Collection,
                    ["record"] = new Dictionary<string, object?>
                    {
                        ["$type"] = Collection,
                        ["text"] = "durable oauth write",
                        ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    },
                });
                return req;
            }

            HttpResponseMessage first = await _http.SendAsync(Build(null));
            if (first.StatusCode == HttpStatusCode.Unauthorized && first.Headers.TryGetValues("DPoP-Nonce", out var v))
                return await _http.SendAsync(Build(v.Single()));
            return first;
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.DisposeAsync();
        }
    }
}
