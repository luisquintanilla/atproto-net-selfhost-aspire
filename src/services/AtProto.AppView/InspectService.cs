using System.Text;
using System.Text.Json;

namespace AtProto.AppView;

/// <summary>
/// Read-through inspection: given a DID, the AppView resolves its PDS and fetches the *real*
/// repository data so the browser can see what's behind a board row, the actual record JSON and
/// CID, the repo's records and latest signed commit, the DID document, and the raw CAR. This is the
/// AppView acting as a hydrating reader over the network, single-origin so the browser needs no CORS
/// and no credentials. Every call proxies a standard <c>com.atproto.*</c> XRPC method on the PDS.
/// </summary>
public sealed class InspectService
{
    private readonly HttpClient _http;
    private readonly PdsResolver _resolver;
    private readonly string _collection;

    public InspectService(IHttpClientFactory factory, PdsResolver resolver, IConfiguration config)
    {
        _http = factory.CreateClient();
        _resolver = resolver;
        _collection = config["AppView:Collection"] ?? "place.selfhost.status";
    }

    public bool Enabled => _resolver.Enabled;

    /// <summary>One record's full value + CID + AT-URI (proxies <c>com.atproto.repo.getRecord</c>).</summary>
    public async Task<IResult> GetRecordAsync(string did, string? collection, string? rkey, CancellationToken ct)
    {
        ResolvedIdentity id = await _resolver.ResolveAsync(did, ct);
        string col = string.IsNullOrEmpty(collection) ? _collection : collection!;
        string rk = string.IsNullOrEmpty(rkey) ? "self" : rkey!;
        string url = $"{id.PdsBase}/xrpc/com.atproto.repo.getRecord" +
                     $"?repo={Uri.EscapeDataString(did)}&collection={Uri.EscapeDataString(col)}&rkey={Uri.EscapeDataString(rk)}";
        return await ProxyJsonAsync(url, ct);
    }

    /// <summary>A repo overview: identity/DID document, latest commit, and the account's records.</summary>
    public async Task<IResult> GetRepoAsync(string did, CancellationToken ct)
    {
        ResolvedIdentity id = await _resolver.ResolveAsync(did, ct);

        JsonElement? list = await TryGetJsonAsync(
            $"{id.PdsBase}/xrpc/com.atproto.repo.listRecords?repo={Uri.EscapeDataString(did)}&collection={Uri.EscapeDataString(_collection)}&limit=100",
            ct);
        JsonElement? commit = await TryGetJsonAsync(
            $"{id.PdsBase}/xrpc/com.atproto.sync.getLatestCommit?did={Uri.EscapeDataString(did)}",
            ct);

        object[] records = list is { } l && l.TryGetProperty("records", out JsonElement recs) && recs.ValueKind == JsonValueKind.Array
            ? recs.EnumerateArray().Select(r => (object)new
            {
                uri = r.TryGetProperty("uri", out JsonElement u) ? u.GetString() : null,
                cid = r.TryGetProperty("cid", out JsonElement c) ? c.GetString() : null,
                value = r.TryGetProperty("value", out JsonElement v) ? (object?)v.Clone() : null,
            }).ToArray()
            : [];

        return Results.Ok(new
        {
            did = id.Did,
            handle = id.Handle,
            pds = id.PdsBase,
            didDoc = id.Document is null ? null : new
            {
                id = id.Document.Did,
                handle = id.Document.Handle,
                pds = id.Document.PdsEndpoint,
                signingKey = id.Document.SigningKeyMultibase,
            },
            commit = commit is { } cm ? new
            {
                cid = cm.TryGetProperty("cid", out JsonElement cc) ? cc.GetString() : null,
                rev = cm.TryGetProperty("rev", out JsonElement rv) ? rv.GetString() : null,
            } : null,
            collection = _collection,
            records,
        });
    }

    /// <summary>The whole repository as a CARv1 download (proxies <c>com.atproto.sync.getRepo</c>).</summary>
    public async Task<IResult> GetCarAsync(string did, CancellationToken ct)
    {
        ResolvedIdentity id = await _resolver.ResolveAsync(did, ct);
        string url = $"{id.PdsBase}/xrpc/com.atproto.sync.getRepo?did={Uri.EscapeDataString(did)}";
        HttpResponseMessage resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return Results.StatusCode((int)resp.StatusCode);
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Results.File(bytes, "application/vnd.ipld.car", fileDownloadName: $"{Sanitize(did)}.car");
    }

    private async Task<IResult> ProxyJsonAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp = await _http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        return Results.Content(body, "application/json", Encoding.UTF8, (int)resp.StatusCode);
    }

    private async Task<JsonElement?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string did) =>
        new(did.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
