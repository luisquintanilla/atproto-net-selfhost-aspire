using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>Thrown when a <c>client_id</c> or its client metadata document is malformed or disallowed.</summary>
public sealed class ClientMetadataException : Exception
{
    /// <summary>The OAuth error code to surface to the client (default <c>invalid_client_metadata</c>).</summary>
    public string ErrorCode { get; }

    public ClientMetadataException(string message, string errorCode = OAuthErrors.InvalidClientMetadata)
        : base(message) => ErrorCode = errorCode;
}

/// <summary>
/// A parsed and validated atproto OAuth <c>client-metadata.json</c> document
/// (<c>draft-parecki-oauth-client-id-metadata-document</c>). This models both real documents fetched
/// from a client's HTTPS <c>client_id</c> URL and the virtual document synthesized for the
/// <c>http://localhost</c> development client. See the atproto OAuth spec, "Clients" section.
/// </summary>
public sealed record ClientMetadata
{
    /// <summary>The <c>client_id</c>: the exact URL the document was fetched from (or the localhost URL).</summary>
    public required string ClientId { get; init; }

    /// <summary>Either <c>web</c> (default) or <c>native</c>. Governs the redirect-URI best practices applied.</summary>
    public string ApplicationType { get; init; } = "web";

    /// <summary>The declared grant types. Always contains <c>authorization_code</c>.</summary>
    public required IReadOnlyList<string> GrantTypes { get; init; }

    /// <summary>The declared response types. Always contains <c>code</c>.</summary>
    public required IReadOnlyList<string> ResponseTypes { get; init; }

    /// <summary>The raw space-separated <c>scope</c> string. Always contains <c>atproto</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>The declared redirect URIs. Never empty.</summary>
    public required IReadOnlyList<string> RedirectUris { get; init; }

    /// <summary>
    /// The client authentication method: <c>none</c> for public clients, <c>private_key_jwt</c> for
    /// confidential clients.
    /// </summary>
    public string TokenEndpointAuthMethod { get; init; } = "none";

    /// <summary>The client-assertion signing algorithm for confidential clients (<c>ES256</c>).</summary>
    public string? TokenEndpointAuthSigningAlg { get; init; }

    /// <summary>DPoP is mandatory for every atproto client, so this is always <c>true</c>.</summary>
    public required bool DpopBoundAccessTokens { get; init; }

    /// <summary>The raw JSON text of an inline <c>jwks</c> object (confidential clients), if present.</summary>
    public string? JwksJson { get; init; }

    /// <summary>An HTTPS URL to a JWKS document (confidential clients), if present.</summary>
    public string? JwksUri { get; init; }

    /// <summary>Human-readable client name. Only shown for trusted clients (impersonation defense).</summary>
    public string? ClientName { get; init; }

    /// <summary>Client homepage URL (same hostname as <see cref="ClientId"/>). Only shown for trusted clients.</summary>
    public string? ClientUri { get; init; }

    /// <summary>HTTPS URL to the client logo. Only shown for trusted clients.</summary>
    public string? LogoUri { get; init; }

    /// <summary>HTTPS URL to the client terms of service.</summary>
    public string? TosUri { get; init; }

    /// <summary>HTTPS URL to the client privacy policy.</summary>
    public string? PolicyUri { get; init; }

    /// <summary>True when this is the synthesized <c>http://localhost</c> development client.</summary>
    public bool IsLocalhostClient { get; init; }

    /// <summary>True for confidential clients (<c>token_endpoint_auth_method=private_key_jwt</c>).</summary>
    public bool IsConfidential => TokenEndpointAuthMethod == "private_key_jwt";

    /// <summary>The declared scopes as a set.</summary>
    public IReadOnlySet<string> ScopeValues =>
        new HashSet<string>(Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>Whether every requested scope is one the client declared (and includes <c>atproto</c>).</summary>
    public bool AllowsScopes(IEnumerable<string> requested)
    {
        IReadOnlySet<string> declared = ScopeValues;
        bool any = false;
        bool hasAtproto = false;
        foreach (string s in requested)
        {
            any = true;
            if (s == "atproto") hasAtproto = true;
            if (!declared.Contains(s)) return false;
        }
        return any && hasAtproto;
    }

    /// <summary>
    /// Whether the given redirect URI is allowed by this client. For most clients this is an exact
    /// string match against a declared URI. For the localhost development client the scheme, host and
    /// path must match a declared loopback URI while the port is ignored (per the spec's localhost rule).
    /// </summary>
    public bool AllowsRedirectUri(string redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri)) return false;

        if (!IsLocalhostClient)
        {
            foreach (string declared in RedirectUris)
                if (string.Equals(declared, redirectUri, StringComparison.Ordinal))
                    return true;
            return false;
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? requested))
            return false;
        foreach (string declared in RedirectUris)
        {
            if (!Uri.TryCreate(declared, UriKind.Absolute, out Uri? d)) continue;
            if (string.Equals(d.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.AbsolutePath, requested.AbsolutePath, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parse and validate a client metadata document. The document's <c>client_id</c> must exactly
    /// match <paramref name="expectedClientId"/> (the URL it was fetched from). Throws
    /// <see cref="ClientMetadataException"/> on any violation.
    /// </summary>
    public static ClientMetadata Parse(JsonElement doc, string expectedClientId, ClientMetadataResolverOptions options)
    {
        if (doc.ValueKind != JsonValueKind.Object)
            throw new ClientMetadataException("client metadata must be a JSON object");

        string clientId = RequireString(doc, "client_id");
        if (!string.Equals(clientId, expectedClientId, StringComparison.Ordinal))
            throw new ClientMetadataException("client_id in the document does not match the fetched URL");

        string applicationType = OptionalString(doc, "application_type") ?? "web";
        if (applicationType is not ("web" or "native"))
            throw new ClientMetadataException($"unsupported application_type '{applicationType}'");

        IReadOnlyList<string> grantTypes = RequireStringArray(doc, "grant_types");
        if (!grantTypes.Contains("authorization_code"))
            throw new ClientMetadataException("grant_types must include 'authorization_code'");

        IReadOnlyList<string> responseTypes = RequireStringArray(doc, "response_types");
        if (!responseTypes.Contains("code"))
            throw new ClientMetadataException("response_types must include 'code'");

        string scope = RequireString(doc, "scope");
        var scopeSet = new HashSet<string>(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (!scopeSet.Contains("atproto"))
            throw new ClientMetadataException("scope must include 'atproto'");

        IReadOnlyList<string> redirectUris = RequireStringArray(doc, "redirect_uris");
        if (redirectUris.Count == 0)
            throw new ClientMetadataException("at least one redirect_uri is required");
        foreach (string uri in redirectUris)
            ValidateRedirectUri(uri, applicationType);

        if (!doc.TryGetProperty("dpop_bound_access_tokens", out JsonElement dpopEl)
            || dpopEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !dpopEl.GetBoolean())
            throw new ClientMetadataException("dpop_bound_access_tokens must be present and true");

        string authMethod = OptionalString(doc, "token_endpoint_auth_method") ?? "none";
        string? authAlg = OptionalString(doc, "token_endpoint_auth_signing_alg");
        string? jwksJson = doc.TryGetProperty("jwks", out JsonElement jwksEl) && jwksEl.ValueKind == JsonValueKind.Object
            ? jwksEl.GetRawText()
            : null;
        string? jwksUri = OptionalString(doc, "jwks_uri");

        if (authMethod == "none")
        {
            if (authAlg is not null)
                throw new ClientMetadataException("public clients must not set token_endpoint_auth_signing_alg");
            if (jwksJson is not null || jwksUri is not null)
                throw new ClientMetadataException("public clients must not declare client authentication keys");
        }
        else if (authMethod == "private_key_jwt")
        {
            if (!options.AllowConfidentialClients)
                throw new ClientMetadataException("confidential clients are not supported yet", OAuthErrors.UnauthorizedClient);
            if (authAlg is not null && authAlg != "ES256")
                throw new ClientMetadataException($"unsupported token_endpoint_auth_signing_alg '{authAlg}'");
            bool hasInline = jwksJson is not null;
            bool hasUri = jwksUri is not null;
            if (hasInline == hasUri)
                throw new ClientMetadataException("confidential clients must supply exactly one of jwks or jwks_uri");
            if (hasUri && !IsHttpsUrl(jwksUri!))
                throw new ClientMetadataException("jwks_uri must be an https URL");
        }
        else
        {
            throw new ClientMetadataException($"unsupported token_endpoint_auth_method '{authMethod}'");
        }

        string? clientUri = OptionalString(doc, "client_uri");
        if (clientUri is not null
            && Uri.TryCreate(clientId, UriKind.Absolute, out Uri? cid)
            && Uri.TryCreate(clientUri, UriKind.Absolute, out Uri? cu)
            && !string.Equals(cid.Host, cu.Host, StringComparison.OrdinalIgnoreCase))
            throw new ClientMetadataException("client_uri must share the client_id hostname");

        string? logoUri = RequireHttpsIfPresent(doc, "logo_uri");
        string? tosUri = RequireHttpsIfPresent(doc, "tos_uri");
        string? policyUri = RequireHttpsIfPresent(doc, "policy_uri");

        return new ClientMetadata
        {
            ClientId = clientId,
            ApplicationType = applicationType,
            GrantTypes = grantTypes,
            ResponseTypes = responseTypes,
            Scope = scope,
            RedirectUris = redirectUris,
            TokenEndpointAuthMethod = authMethod,
            TokenEndpointAuthSigningAlg = authAlg,
            DpopBoundAccessTokens = true,
            JwksJson = jwksJson,
            JwksUri = jwksUri,
            ClientName = OptionalString(doc, "client_name"),
            ClientUri = clientUri,
            LogoUri = logoUri,
            TosUri = tosUri,
            PolicyUri = policyUri,
            IsLocalhostClient = false,
        };
    }

    private static void ValidateRedirectUri(string value, string applicationType)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new ClientMetadataException($"redirect_uri '{value}' is not an absolute URI");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new ClientMetadataException("redirect_uri must not contain a fragment");

        if (uri.Scheme == Uri.UriSchemeHttps)
            return;
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!IsLoopbackHost(uri.Host))
                throw new ClientMetadataException("http redirect_uri is only allowed for loopback (127.0.0.1 or [::1])");
            return;
        }
        // A non-http(s) custom scheme is only valid for native clients (platform callback schemes).
        if (applicationType != "native")
            throw new ClientMetadataException($"redirect_uri scheme '{uri.Scheme}' requires application_type 'native'");
    }

    internal static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "[::1]" or "::1";

    internal static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps;

    private static string RequireString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String)
            throw new ClientMetadataException($"client metadata is missing required string '{name}'");
        string? s = v.GetString();
        if (string.IsNullOrEmpty(s))
            throw new ClientMetadataException($"client metadata '{name}' must be non-empty");
        return s;
    }

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? RequireHttpsIfPresent(JsonElement obj, string name)
    {
        string? value = OptionalString(obj, name);
        if (value is not null && !IsHttpsUrl(value))
            throw new ClientMetadataException($"{name} must be an https URL");
        return value;
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
            throw new ClientMetadataException($"client metadata is missing required array '{name}'");
        var list = new List<string>();
        foreach (JsonElement item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ClientMetadataException($"client metadata '{name}' must contain only strings");
            string? s = item.GetString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
    }
}
