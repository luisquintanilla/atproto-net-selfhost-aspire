using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>The claims carried by a DPoP-bound access token.</summary>
public sealed record AccessTokenClaims
{
    /// <summary>The account DID the token authorizes (<c>sub</c>).</summary>
    public required string Subject { get; init; }

    /// <summary>The granted scope, space-delimited (<c>scope</c>). Always includes <c>atproto</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>The issuer (the PDS origin / AS <c>issuer</c>).</summary>
    public required string Issuer { get; init; }

    /// <summary>The DPoP key thumbprint the token is bound to (<c>cnf.jkt</c>).</summary>
    public required string ConfirmationJkt { get; init; }

    /// <summary>Issued-at, Unix seconds (<c>iat</c>).</summary>
    public required long IssuedAt { get; init; }

    /// <summary>Expiry, Unix seconds (<c>exp</c>).</summary>
    public required long ExpiresAt { get; init; }

    /// <summary>Unique token id (<c>jti</c>).</summary>
    public required string TokenId { get; init; }
}

/// <summary>
/// Issues and validates the PDS access token: an HS256 JWT (type <c>at+jwt</c>, RFC 9068) carrying the
/// subject DID, scope, and the DPoP <c>cnf.jkt</c> binding. Because this PDS is its own resource
/// server, a symmetric secret is sufficient and the token is opaque to clients. This mirrors the
/// existing session <c>Jwt</c> helper but adds the DPoP confirmation and richer claims, and lives in
/// the packable core so a client or another server can validate it too.
/// </summary>
public static class AccessToken
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    /// <summary>Issue a signed access token for the given claims.</summary>
    public static string Issue(AccessTokenClaims claims, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(secret);

        var header = new Dictionary<string, object?> { ["alg"] = "HS256", ["typ"] = "at+jwt" };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = claims.Subject,
            ["scope"] = claims.Scope,
            ["iss"] = claims.Issuer,
            ["iat"] = claims.IssuedAt,
            ["exp"] = claims.ExpiresAt,
            ["jti"] = claims.TokenId,
            ["cnf"] = new Dictionary<string, object?> { ["jkt"] = claims.ConfirmationJkt },
        };

        string head = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header, Json));
        string body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        string signingInput = $"{head}.{body}";
        string signature = Base64Url.EncodeToString(Sign(signingInput, secret));
        return $"{signingInput}.{signature}";
    }

    /// <summary>
    /// Validate an access token's signature, issuer, and expiry, and extract its claims. Returns false
    /// for any failure.
    /// </summary>
    public static bool TryValidate(
        string? token,
        byte[] secret,
        string expectedIssuer,
        out AccessTokenClaims claims,
        TimeProvider? clock = null)
    {
        claims = null!;
        clock ??= TimeProvider.System;

        if (!JwsParts.TryParse(token, out JwsParts jws))
            return false;

        byte[] expected = Sign(jws.SigningInput, secret);
        byte[] actual;
        try
        {
            actual = jws.SignatureBytes();
        }
        catch (FormatException)
        {
            return false;
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jws.PayloadJson());
            JsonElement root = doc.RootElement;

            long now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (!root.TryGetProperty("exp", out JsonElement exp) || !exp.TryGetInt64(out long expValue) || expValue <= now)
                return false;
            if (!root.TryGetProperty("iat", out JsonElement iat) || !iat.TryGetInt64(out long iatValue))
                return false;

            string? issuer = root.TryGetProperty("iss", out JsonElement iss) ? iss.GetString() : null;
            if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
                return false;

            string? sub = root.TryGetProperty("sub", out JsonElement subEl) ? subEl.GetString() : null;
            string? scope = root.TryGetProperty("scope", out JsonElement scopeEl) ? scopeEl.GetString() : null;
            string? jti = root.TryGetProperty("jti", out JsonElement jtiEl) ? jtiEl.GetString() : null;
            string? jkt = root.TryGetProperty("cnf", out JsonElement cnf)
                && cnf.ValueKind == JsonValueKind.Object
                && cnf.TryGetProperty("jkt", out JsonElement jktEl)
                    ? jktEl.GetString()
                    : null;

            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(jkt) || string.IsNullOrEmpty(jti))
                return false;

            claims = new AccessTokenClaims
            {
                Subject = sub,
                Scope = scope,
                Issuer = issuer!,
                ConfirmationJkt = jkt,
                IssuedAt = iatValue,
                ExpiresAt = expValue,
                TokenId = jti,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Sign(string input, byte[] secret) =>
        HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(input));
}
