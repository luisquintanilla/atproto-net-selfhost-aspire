using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>
/// Validates DPoP proof JWTs per RFC 9449 section 4.3, adapted to the atproto profile (ES256 / P-256
/// only, server nonces required). Replay detection of the <c>jti</c> is left to the caller (the store,
/// scoped to the nonce window), which also issues and rotates the nonce; this validator reports
/// whether a nonce is required so the caller can respond with <c>use_dpop_nonce</c>.
/// </summary>
public static class DpopValidator
{
    /// <summary>The base64url SHA-256 hash of an access token, as used for the DPoP <c>ath</c> claim.</summary>
    public static string AccessTokenHash(string accessToken)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64Url.EncodeToString(hash);
    }

    /// <summary>
    /// Validate a DPoP proof against the current request. Returns <see cref="DpopValidationStatus.Valid"/>
    /// with the parsed proof, <see cref="DpopValidationStatus.NonceRequired"/> when the client must
    /// retry with a nonce, or <see cref="DpopValidationStatus.Invalid"/> otherwise.
    /// </summary>
    public static DpopValidationResult Validate(
        string? proofJwt,
        DpopValidationOptions options,
        DpopNonceService nonceService,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nonceService);
        clock ??= TimeProvider.System;

        if (!JwsParts.TryParse(proofJwt, out JwsParts jws))
            return DpopValidationResult.Invalid("malformed DPoP proof");

        // Header: typ, alg, and the embedded public key.
        EcPublicJwk jwk;
        try
        {
            using JsonDocument header = JsonDocument.Parse(jws.HeaderJson());
            JsonElement root = header.RootElement;
            if (GetString(root, "typ") != "dpop+jwt")
                return DpopValidationResult.Invalid("DPoP proof typ must be dpop+jwt");
            if (GetString(root, "alg") != "ES256")
                return DpopValidationResult.Invalid("DPoP proof alg must be ES256");
            if (!root.TryGetProperty("jwk", out JsonElement jwkElement))
                return DpopValidationResult.Invalid("DPoP proof header missing jwk");
            jwk = EcPublicJwk.Parse(jwkElement);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return DpopValidationResult.Invalid("invalid DPoP proof header");
        }

        // Signature must verify with the embedded key.
        if (!JoseEs256.Verify(jws, jwk))
            return DpopValidationResult.Invalid("DPoP proof signature verification failed");

        // Payload claims.
        string jti, htm, htu;
        long iat;
        string? nonce, ath;
        try
        {
            using JsonDocument payload = JsonDocument.Parse(jws.PayloadJson());
            JsonElement root = payload.RootElement;
            jti = GetString(root, "jti");
            htm = GetString(root, "htm");
            htu = GetString(root, "htu");
            if (!root.TryGetProperty("iat", out JsonElement iatElement)
                || iatElement.ValueKind != JsonValueKind.Number
                || !iatElement.TryGetInt64(out iat))
            {
                return DpopValidationResult.Invalid("DPoP proof iat missing or invalid");
            }
            nonce = OptionalString(root, "nonce");
            ath = OptionalString(root, "ath");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return DpopValidationResult.Invalid("invalid DPoP proof payload");
        }

        if (jti.Length == 0)
            return DpopValidationResult.Invalid("DPoP proof jti is empty");

        // htm: HTTP methods are case-insensitive per RFC 9110.
        if (!string.Equals(htm, options.ExpectedHtm, StringComparison.OrdinalIgnoreCase))
            return DpopValidationResult.Invalid("DPoP proof htm mismatch");

        // htu: compare after scheme/host normalization, ignoring query and fragment.
        string? htuNorm = NormalizeHtu(htu);
        string? expectedHtuNorm = NormalizeHtu(options.ExpectedHtu);
        if (htuNorm is null || expectedHtuNorm is null || !string.Equals(htuNorm, expectedHtuNorm, StringComparison.Ordinal))
            return DpopValidationResult.Invalid("DPoP proof htu mismatch");

        // iat freshness.
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (iat > now + options.MaxIatSkewSeconds)
            return DpopValidationResult.Invalid("DPoP proof iat is in the future");
        if (iat < now - options.MaxIatAgeSeconds)
            return DpopValidationResult.Invalid("DPoP proof iat is too old");

        // Nonce (required by the atproto profile). A missing or stale nonce is recoverable: the caller
        // returns use_dpop_nonce with a fresh nonce and the client retries.
        if (options.RequireNonce && !nonceService.IsAccepted(nonce))
            return DpopValidationResult.NonceRequired();

        // ath: on resource-server requests, bind the proof to the presented access token.
        if (options.AccessToken is not null)
        {
            string expectedAth = AccessTokenHash(options.AccessToken);
            if (ath is null || !FixedTimeStringEquals(ath, expectedAth))
                return DpopValidationResult.Invalid("DPoP proof ath mismatch");
        }

        return DpopValidationResult.Ok(new DpopProof
        {
            Jwk = jwk,
            Jkt = jwk.Thumbprint(),
            Jti = jti,
            Htm = htm,
            Htu = htu,
            IssuedAt = iat,
            Nonce = nonce,
            AccessTokenHash = ath,
        });
    }

    private static string GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"missing string claim '{name}'");
        return value.GetString() ?? throw new FormatException($"null claim '{name}'");
    }

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool FixedTimeStringEquals(string a, string b)
    {
        byte[] ab = Encoding.ASCII.GetBytes(a);
        byte[] bb = Encoding.ASCII.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>
    /// Normalize an <c>htu</c> for comparison (RFC 9449 recommends syntax- and scheme-based
    /// normalization): lower-case scheme and host, drop the default port, drop query and fragment.
    /// </summary>
    internal static string? NormalizeHtu(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return null;
        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host.ToLowerInvariant();
        string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string path = uri.AbsolutePath;
        return $"{scheme}://{host}{port}{path}";
    }
}
