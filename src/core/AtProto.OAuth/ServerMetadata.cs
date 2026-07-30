using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtProto.OAuth;

/// <summary>
/// The atproto OAuth Authorization Server metadata document, served at
/// <c>/.well-known/oauth-authorization-server</c> (RFC 8414 plus the atproto profile fields). Clients
/// fetch this to discover the AS endpoints and to confirm the <c>issuer</c> is the authoritative server
/// for an account's DID.
/// </summary>
public sealed record AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public required string Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public required string AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; init; }

    [JsonPropertyName("pushed_authorization_request_endpoint")]
    public required string PushedAuthorizationRequestEndpoint { get; init; }

    [JsonPropertyName("response_types_supported")]
    public IReadOnlyList<string> ResponseTypesSupported { get; init; } = ["code"];

    [JsonPropertyName("grant_types_supported")]
    public IReadOnlyList<string> GrantTypesSupported { get; init; } = ["authorization_code", "refresh_token"];

    [JsonPropertyName("code_challenge_methods_supported")]
    public IReadOnlyList<string> CodeChallengeMethodsSupported { get; init; } = ["S256"];

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public IReadOnlyList<string> TokenEndpointAuthMethodsSupported { get; init; } = ["none", "private_key_jwt"];

    [JsonPropertyName("token_endpoint_auth_signing_alg_values_supported")]
    public IReadOnlyList<string> TokenEndpointAuthSigningAlgValuesSupported { get; init; } = ["ES256"];

    [JsonPropertyName("scopes_supported")]
    public required IReadOnlyList<string> ScopesSupported { get; init; }

    [JsonPropertyName("dpop_signing_alg_values_supported")]
    public IReadOnlyList<string> DpopSigningAlgValuesSupported { get; init; } = ["ES256"];

    [JsonPropertyName("authorization_response_iss_parameter_supported")]
    public bool AuthorizationResponseIssParameterSupported { get; init; } = true;

    [JsonPropertyName("require_pushed_authorization_requests")]
    public bool RequirePushedAuthorizationRequests { get; init; } = true;

    [JsonPropertyName("client_id_metadata_document_supported")]
    public bool ClientIdMetadataDocumentSupported { get; init; } = true;

    /// <summary>The scopes this MVP advertises: account identity plus the transitional write level.</summary>
    public static readonly IReadOnlyList<string> DefaultScopes = ["atproto", "transition:generic"];

    /// <summary>
    /// Build the metadata for an issuer origin. The <c>issuer</c> is the PDS origin (scheme + host, no
    /// trailing slash), and all endpoints are derived from it. This is the value a client must confirm
    /// by resolving the account DID to its PDS.
    /// </summary>
    public static AuthorizationServerMetadata ForIssuer(Uri issuer, IReadOnlyList<string>? scopes = null)
    {
        string origin = issuer.GetLeftPart(UriPartial.Authority);
        return new AuthorizationServerMetadata
        {
            Issuer = origin,
            AuthorizationEndpoint = origin + "/oauth/authorize",
            TokenEndpoint = origin + "/oauth/token",
            PushedAuthorizationRequestEndpoint = origin + "/oauth/par",
            ScopesSupported = scopes ?? DefaultScopes,
        };
    }

    /// <summary>Serialize to the JSON body served at the well-known endpoint.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ServerMetadataJson.Options);
}

/// <summary>
/// The Protected Resource metadata document (RFC 9728), served at
/// <c>/.well-known/oauth-protected-resource</c>. For a self-hosted PDS the resource server and
/// authorization server are the same origin, so this simply points clients at the AS.
/// </summary>
public sealed record ProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("scopes_supported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    /// <summary>Build the resource metadata for a PDS origin (which is also its own authorization server).</summary>
    public static ProtectedResourceMetadata ForIssuer(Uri issuer, IReadOnlyList<string>? scopes = null)
    {
        string origin = issuer.GetLeftPart(UriPartial.Authority);
        return new ProtectedResourceMetadata
        {
            Resource = origin,
            AuthorizationServers = [origin],
            ScopesSupported = scopes,
        };
    }

    /// <summary>Serialize to the JSON body served at the well-known endpoint.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ServerMetadataJson.Options);
}

internal static class ServerMetadataJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
