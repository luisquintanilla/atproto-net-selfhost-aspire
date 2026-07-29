using AtProto.Identity;

namespace AtProto.AppView;

/// <summary>The essentials the AppView needs to read a repo: which PDS hosts it, plus the (optional) DID document.</summary>
public sealed record ResolvedIdentity(string Did, string? Handle, string PdsBase, DidDocument? Document);

/// <summary>
/// Resolves a DID to the PDS that hosts its repository, the atproto identity layer in miniature.
/// It fetches the account's DID document (<c>did:web</c> here) and reads the
/// <c>#atproto_pds</c> <c>serviceEndpoint</c> from it, exactly as a real AppView would. To stay
/// robust on an all-HTTP localhost stack it prefers the configured PDS base
/// (<c>AppView:PdsUrl</c>, injected by the Aspire integration) for the document's host and scheme,
/// and falls back to it if the document can't be fetched. This keeps inspection working for a
/// single-PDS demo while still demonstrating real resolution (the document, its handle, and its
/// advertised endpoint are surfaced to the UI).
/// </summary>
public sealed class PdsResolver
{
    private readonly HttpClient _http;
    private readonly ILogger<PdsResolver> _logger;
    private readonly string? _configuredPds;

    public PdsResolver(IHttpClientFactory factory, IConfiguration config, ILogger<PdsResolver> logger)
    {
        _http = factory.CreateClient();
        _logger = logger;
        _configuredPds = config["AppView:PdsUrl"]?.TrimEnd('/');
    }

    /// <summary>Whether inspection is available (we know of a PDS to read from).</summary>
    public bool Enabled => _configuredPds is not null;

    /// <summary>Resolve a DID to its hosting PDS (+ DID document when reachable).</summary>
    public async Task<ResolvedIdentity> ResolveAsync(string did, CancellationToken cancellationToken = default)
    {
        DidDocument? doc = await TryGetDocumentAsync(did, cancellationToken).ConfigureAwait(false);
        string? pds = doc?.PdsEndpoint?.TrimEnd('/') ?? _configuredPds;
        if (string.IsNullOrEmpty(pds))
            throw new InvalidOperationException(
                $"Cannot resolve a PDS for {did}: no DID document and no configured AppView:PdsUrl.");
        return new ResolvedIdentity(did, doc?.Handle, pds, doc);
    }

    /// <summary>Best-effort fetch + parse of the DID document (null on any failure).</summary>
    public async Task<DidDocument?> TryGetDocumentAsync(string did, CancellationToken cancellationToken = default)
    {
        Uri? url = DidDocumentUrl(did);
        if (url is null)
            return null;
        try
        {
            string json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return DidDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DID document fetch failed for {Did} at {Url}: {Message}", did, url, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Compute the <c>did.json</c> URL for a <c>did:web</c>. A path-based DID
    /// (<c>did:web:host:pds:id</c>) serves its document at <c>/pds/id/did.json</c>; a bare
    /// authority DID uses <c>/.well-known/did.json</c>. When a PDS base is configured we borrow its
    /// scheme + host (so <c>http</c>/localhost resolves), otherwise we fall back to <c>https</c> per
    /// the did:web spec.
    /// </summary>
    internal Uri? DidDocumentUrl(string did)
    {
        const string prefix = "did:web:";
        if (!did.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string[] parts = did[prefix.Length..].Split(':');
        string path = parts.Length > 1
            ? string.Join('/', parts[1..]) + "/did.json"
            : ".well-known/did.json";

        if (_configuredPds is not null)
            return new Uri(new Uri(_configuredPds), "/" + path);

        string authority = Uri.UnescapeDataString(parts[0]);
        return new Uri($"https://{authority}/{path}");
    }
}
