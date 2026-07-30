namespace AtProto.OAuth;

/// <summary>
/// Well-known OAuth 2.0 / atproto error codes used in error responses (the <c>error</c> field of a
/// JSON error body, or the <c>error</c> parameter of a redirect).
/// </summary>
public static class OAuthErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidClient = "invalid_client";
    public const string InvalidGrant = "invalid_grant";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string InvalidScope = "invalid_scope";
    public const string AccessDenied = "access_denied";
    public const string ServerError = "server_error";
    public const string TemporarilyUnavailable = "temporarily_unavailable";

    /// <summary>The DPoP proof was malformed or failed validation (RFC 9449).</summary>
    public const string InvalidDpopProof = "invalid_dpop_proof";

    /// <summary>The client must retry with a server-provided DPoP nonce (RFC 9449).</summary>
    public const string UseDpopNonce = "use_dpop_nonce";

    /// <summary>The access token presented to a resource was invalid or expired (RFC 6750).</summary>
    public const string InvalidToken = "invalid_token";

    /// <summary>The access token's scope does not grant the requested action (RFC 6750).</summary>
    public const string InsufficientScope = "insufficient_scope";

    /// <summary>The client-metadata document was missing, unreachable, or invalid.</summary>
    public const string InvalidClientMetadata = "invalid_client_metadata";
}

/// <summary>A standard OAuth error body: <c>{ "error": ..., "error_description": ... }</c>.</summary>
public sealed record OAuthErrorResponse(string Error, string? ErrorDescription = null);
