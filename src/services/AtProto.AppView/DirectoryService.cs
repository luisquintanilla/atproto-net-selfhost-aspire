using System.Text.Json;

namespace AtProto.AppView;

public sealed record DirectoryInstance(
    string Name,
    string? Description,
    string Did,
    string Pds,
    string? Relay,
    string? AppView,
    DateTimeOffset? CreatedAt);

/// <summary>Builds a small federation directory by asking the relay for hosts, then reading each PDS's instance record.</summary>
public sealed class DirectoryService
{
    private const string InstanceCollection = "place.selfhost.instance";
    private readonly HttpClient _http;
    private readonly string? _relayUrl;

    public DirectoryService(IHttpClientFactory factory, IConfiguration config)
    {
        _http = factory.CreateClient();
        _relayUrl = (config["AppView:RelayUrl"] ?? config["Firehose:Url"])?.TrimEnd('/');
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_relayUrl);

    public async Task<IReadOnlyList<DirectoryInstance>> ListAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return [];

        JsonElement hostsDoc = await _http.GetFromJsonAsync<JsonElement>(
            $"{_relayUrl}/xrpc/com.atproto.sync.listHosts", ct).ConfigureAwait(false);
        if (!hostsDoc.TryGetProperty("hosts", out JsonElement hosts) || hosts.ValueKind != JsonValueKind.Array)
            return [];

        var instances = new List<DirectoryInstance>();
        foreach (JsonElement host in hosts.EnumerateArray())
        {
            string? hostname = host.TryGetProperty("hostname", out JsonElement h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(hostname))
                continue;

            DirectoryInstance? instance = await TryReadInstanceAsync(hostname, ct).ConfigureAwait(false);
            if (instance is not null)
                instances.Add(instance);
        }

        return instances
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Did, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<DirectoryInstance?> TryReadInstanceAsync(string hostname, CancellationToken ct)
    {
        string pdsBase = NormalizeBaseUrl(hostname);
        string did = ServiceDid(pdsBase);
        string url = $"{pdsBase}/xrpc/com.atproto.repo.getRecord" +
                     $"?repo={Uri.EscapeDataString(did)}&collection={InstanceCollection}&rkey=self";

        try
        {
            JsonElement doc = await _http.GetFromJsonAsync<JsonElement>(url, ct).ConfigureAwait(false);
            if (!doc.TryGetProperty("value", out JsonElement value))
                return null;

            string? name = value.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? pds = value.TryGetProperty("pds", out JsonElement p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pds))
                return null;

            DateTimeOffset? createdAt = null;
            if (value.TryGetProperty("createdAt", out JsonElement c)
                && DateTimeOffset.TryParse(c.GetString(), out DateTimeOffset parsed))
                createdAt = parsed;

            return new DirectoryInstance(
                name,
                value.TryGetProperty("description", out JsonElement d) ? d.GetString() : null,
                did,
                pds,
                value.TryGetProperty("relay", out JsonElement r) ? r.GetString() : null,
                value.TryGetProperty("appview", out JsonElement a) ? a.GetString() : null,
                createdAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
        }
    }

    public static string ServiceDid(string baseUrl)
    {
        var uri = new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
        string authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}%3A{uri.Port}";
        return $"did:web:{authority}";
    }

    private static string NormalizeBaseUrl(string hostname)
    {
        string h = hostname.Trim().TrimEnd('/');
        return h.Contains("://", StringComparison.Ordinal) ? h : "https://" + h;
    }
}
