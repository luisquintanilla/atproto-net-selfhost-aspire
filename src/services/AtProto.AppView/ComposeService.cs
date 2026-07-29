using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AtProto.AppView;

/// <summary>
/// Lets the browser participate in the loop: "you" pick an emoji and it is written as a real record
/// to the PDS, then flows back through firehose → relay → AppView and lights up the board. This is
/// the app-server-writes-to-the-PDS pattern: the AppView provisions one well-known demo identity
/// (<c>you.pds.localhost</c>), caches its session, and <c>putRecord</c>s on its behalf. Demo-only
/// auth (a fixed account/password); real per-user auth (OAuth) is a later milestone.
/// </summary>
public sealed class ComposeService
{
    private const string Handle = "you.pds.localhost";
    private const string Password = "compose-demo-password";

    private readonly HttpClient _http;
    private readonly ILogger<ComposeService> _logger;
    private readonly string _collection;
    private readonly string? _pds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public ComposeService(IHttpClientFactory factory, IConfiguration config, ILogger<ComposeService> logger)
    {
        _http = factory.CreateClient();
        _logger = logger;
        _collection = config["AppView:Collection"] ?? "place.selfhost.status";
        _pds = config["AppView:PdsUrl"]?.TrimEnd('/');
    }

    /// <summary>Whether compose is available (we know of a PDS to write to).</summary>
    public bool Enabled => !string.IsNullOrEmpty(_pds);

    /// <summary>Write "your" status emoji to the PDS and return the resulting AT-URI + CID.</summary>
    public async Task<ComposeResult> SetStatusAsync(string status, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_pds))
            throw new InvalidOperationException("Compose is unavailable: no AppView:PdsUrl configured.");

        Session session = await EnsureSessionAsync(ct);

        var body = new Dictionary<string, object?>
        {
            ["repo"] = session.Did,
            ["collection"] = _collection,
            ["rkey"] = "self",
            ["record"] = new Dictionary<string, object?>
            {
                ["$type"] = _collection,
                ["status"] = status,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_pds}/xrpc/com.atproto.repo.putRecord")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);

        HttpResponseMessage resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        JsonElement result = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new ComposeResult(
            result.GetProperty("uri").GetString()!,
            result.GetProperty("cid").GetString()!,
            session.Did,
            Handle,
            status);
    }

    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return _session;
        await _gate.WaitAsync(ct);
        try
        {
            return _session ??= await CreateOrLoginAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Session> CreateOrLoginAsync(CancellationToken ct)
    {
        HttpResponseMessage created = await _http.PostAsJsonAsync(
            $"{_pds}/xrpc/com.atproto.server.createAccount", new { handle = Handle, password = Password }, ct);
        if (created.IsSuccessStatusCode)
            return await ToSession(created, ct);

        // Already provisioned (e.g. a previous compose): log in instead.
        HttpResponseMessage session = await _http.PostAsJsonAsync(
            $"{_pds}/xrpc/com.atproto.server.createSession", new { identifier = Handle, password = Password }, ct);
        session.EnsureSuccessStatusCode();
        _logger.LogInformation("Compose identity ready: {Handle}", Handle);
        return await ToSession(session, ct);
    }

    private static async Task<Session> ToSession(HttpResponseMessage response, CancellationToken ct)
    {
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Session(body.GetProperty("did").GetString()!, body.GetProperty("accessJwt").GetString()!);
    }

    private sealed record Session(string Did, string AccessJwt);
}

/// <summary>The outcome of a compose write: where the record landed.</summary>
public sealed record ComposeResult(string Uri, string Cid, string Did, string Handle, string Status);
