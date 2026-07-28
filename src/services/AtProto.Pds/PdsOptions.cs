using System.Text;

namespace AtProto.Pds;

/// <summary>Configuration for a self-hosted PDS instance.</summary>
public sealed class PdsOptions
{
    /// <summary>The externally reachable base URL (drives the did:web authority + service endpoint).</summary>
    public string PublicUrl { get; set; } = "http://localhost:5100";

    /// <summary>The domain new handles default into (e.g. <c>alice.pds.localhost</c>).</summary>
    public string HandleDomain { get; set; } = "pds.localhost";

    /// <summary>HMAC secret for session JWTs. Override in production.</summary>
    public string JwtSecret { get; set; } = "dev-only-pds-secret-change-me";
}

/// <summary>
/// Derives AT Protocol identity strings (did:web DIDs, DID-document paths, service endpoint) from
/// <see cref="PdsOptions.PublicUrl"/>. A did:web authority percent-encodes the port colon, and
/// per-account DIDs are path-based (<c>did:web:&lt;authority&gt;:pds:&lt;id&gt;</c> →
/// <c>/pds/&lt;id&gt;/did.json</c>) so one host can serve many repositories.
/// </summary>
public sealed class PdsIdentity
{
    public PdsIdentity(PdsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PublicUrl = new Uri(options.PublicUrl, UriKind.Absolute);
        HandleDomain = options.HandleDomain;
        JwtSecret = Encoding.UTF8.GetBytes(options.JwtSecret);
        AuthorityEncoded = PublicUrl.IsDefaultPort
            ? PublicUrl.Host
            : $"{PublicUrl.Host}%3A{PublicUrl.Port}";
    }

    /// <summary>The externally reachable base URL.</summary>
    public Uri PublicUrl { get; }

    /// <summary>The default handle domain.</summary>
    public string HandleDomain { get; }

    /// <summary>The did:web authority (host with a percent-encoded port).</summary>
    public string AuthorityEncoded { get; }

    /// <summary>The signing secret bytes for session JWTs.</summary>
    public byte[] JwtSecret { get; }

    /// <summary>The PDS's own service DID.</summary>
    public string ServiceDid => $"did:web:{AuthorityEncoded}";

    /// <summary>The DID for a hosted account.</summary>
    public string DidFor(string accountId) => $"did:web:{AuthorityEncoded}:pds:{accountId}";

    /// <summary>The HTTP path serving an account's DID document.</summary>
    public string DidDocPathFor(string accountId) => $"/pds/{accountId}/did.json";

    /// <summary>Recover an account id from one of its DIDs (or null if it is not ours).</summary>
    public string? AccountIdFromDid(string did)
    {
        string prefix = $"did:web:{AuthorityEncoded}:pds:";
        return did.StartsWith(prefix, StringComparison.Ordinal) ? did[prefix.Length..] : null;
    }
}
