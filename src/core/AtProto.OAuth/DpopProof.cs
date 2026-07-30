namespace AtProto.OAuth;

/// <summary>A validated DPoP proof (RFC 9449 section 4.2): the claims plus the proof key's thumbprint.</summary>
public sealed record DpopProof
{
    /// <summary>The public key from the proof's <c>jwk</c> header.</summary>
    public required EcPublicJwk Jwk { get; init; }

    /// <summary>The RFC 7638 thumbprint of <see cref="Jwk"/> (the <c>jkt</c> a token is bound to).</summary>
    public required string Jkt { get; init; }

    /// <summary>The unique proof id (<c>jti</c>), used for replay detection.</summary>
    public required string Jti { get; init; }

    /// <summary>The HTTP method the proof covers (<c>htm</c>).</summary>
    public required string Htm { get; init; }

    /// <summary>The HTTP target URI the proof covers (<c>htu</c>), without query or fragment.</summary>
    public required string Htu { get; init; }

    /// <summary>The proof creation time (<c>iat</c>), Unix seconds.</summary>
    public required long IssuedAt { get; init; }

    /// <summary>The server nonce echoed by the client, if present.</summary>
    public string? Nonce { get; init; }

    /// <summary>The access-token hash (<c>ath</c>), present on resource-server requests.</summary>
    public string? AccessTokenHash { get; init; }
}

/// <summary>The outcome of validating a DPoP proof.</summary>
public enum DpopValidationStatus
{
    /// <summary>The proof is valid.</summary>
    Valid,

    /// <summary>The proof is malformed or failed a check (respond <c>400 invalid_dpop_proof</c>).</summary>
    Invalid,

    /// <summary>
    /// The proof is otherwise well-formed but has no acceptable nonce (respond <c>400 use_dpop_nonce</c>
    /// with a fresh <c>DPoP-Nonce</c> header; the client retries).
    /// </summary>
    NonceRequired,
}

/// <summary>The result of <see cref="DpopValidator.Validate"/>.</summary>
public sealed record DpopValidationResult(DpopValidationStatus Status, DpopProof? Proof, string? Error)
{
    /// <summary>True when the proof passed all checks.</summary>
    public bool IsValid => Status == DpopValidationStatus.Valid;

    internal static DpopValidationResult Ok(DpopProof proof) => new(DpopValidationStatus.Valid, proof, null);

    internal static DpopValidationResult Invalid(string error) =>
        new(DpopValidationStatus.Invalid, null, error);

    internal static DpopValidationResult NonceRequired(string error = "DPoP nonce required") =>
        new(DpopValidationStatus.NonceRequired, null, error);
}

/// <summary>Inputs describing the request a DPoP proof is expected to cover.</summary>
public sealed class DpopValidationOptions
{
    /// <summary>The HTTP method of the current request (for the <c>htm</c> check).</summary>
    public required string ExpectedHtm { get; init; }

    /// <summary>The full target URI of the current request (for the <c>htu</c> check).</summary>
    public required string ExpectedHtu { get; init; }

    /// <summary>
    /// The access token presented alongside the proof, if this is a resource-server request. When set,
    /// the proof's <c>ath</c> must equal the hash of this token.
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>Whether a valid server nonce is required (always true in the atproto profile).</summary>
    public bool RequireNonce { get; init; } = true;

    /// <summary>How far in the past a proof's <c>iat</c> may be, in seconds.</summary>
    public int MaxIatAgeSeconds { get; init; } = 300;

    /// <summary>How far in the future a proof's <c>iat</c> may be (clock skew), in seconds.</summary>
    public int MaxIatSkewSeconds { get; init; } = 30;
}
