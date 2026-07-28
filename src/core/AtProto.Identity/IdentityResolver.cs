using System.Text.Json;

namespace AtProto.Identity;

/// <summary>
/// Resolves AT Protocol identities. DID documents come from plc.directory (did:plc) or the
/// account's own <c>/.well-known/did.json</c> (did:web). Handle-&gt;DID uses the account's
/// <c>/.well-known/atproto-did</c> (HTTPS). DNS-based handle resolution is deferred (M3+).
/// </summary>
public sealed class IdentityResolver(HttpClient http)
{
    private const string PlcDirectory = "https://plc.directory/";

    /// <summary>Resolve a DID to its document (did:plc and did:web supported).</summary>
    public async Task<DidDocument> ResolveDidAsync(string did, CancellationToken cancellationToken = default)
    {
        Uri url = DidToDocumentUrl(did);
        string json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        return DidDocument.Parse(doc.RootElement);
    }

    /// <summary>Resolve a handle to its DID via HTTPS <c>/.well-known/atproto-did</c> (best-effort).</summary>
    public async Task<string?> ResolveHandleAsync(string handle, CancellationToken cancellationToken = default)
    {
        var url = new Uri($"https://{handle}/.well-known/atproto-did");
        try
        {
            string did = (await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();
            return did.StartsWith("did:", StringComparison.Ordinal) ? did : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static Uri DidToDocumentUrl(string did)
    {
        if (did.StartsWith("did:plc:", StringComparison.Ordinal))
            return new Uri(PlcDirectory + did);

        if (did.StartsWith("did:web:", StringComparison.Ordinal))
        {
            string host = did["did:web:".Length..];
            // did:web may encode a path with ':' separators; ':' -> '/'.
            host = host.Replace(':', '/');
            return new Uri($"https://{host}/.well-known/did.json");
        }

        throw new NotSupportedException($"unsupported DID method: {did}");
    }
}
