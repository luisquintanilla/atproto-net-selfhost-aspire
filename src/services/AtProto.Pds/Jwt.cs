using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtProto.Pds;

/// <summary>
/// A minimal, dependency-free HS256 JSON Web Token for PDS session tokens. Access/refresh tokens
/// are opaque to other services (only this PDS validates them), so a symmetric HMAC secret is
/// sufficient — no external JWT package. (Inter-service auth would use ES256K service JWTs; not
/// needed for a single self-hosted PDS.)
/// </summary>
public static class Jwt
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    /// <summary>Issue a signed token for <paramref name="did"/> with the given scope and lifetime.</summary>
    public static string Issue(string did, string scope, TimeSpan lifetime, byte[] secret)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new Dictionary<string, object?> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = did,
            ["scope"] = scope,
            ["iat"] = now,
            ["exp"] = now + (long)lifetime.TotalSeconds,
        };

        string head = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, Json));
        string body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        string signingInput = $"{head}.{body}";
        string sig = Base64Url(Sign(signingInput, secret));
        return $"{signingInput}.{sig}";
    }

    /// <summary>Validate a token's signature and expiry and extract its subject DID.</summary>
    public static bool TryValidate(string? token, byte[] secret, out string did)
    {
        did = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;

        string[] parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        byte[] expected = Sign($"{parts[0]}.{parts[1]}", secret);
        if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2])))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(FromBase64Url(parts[1]));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("exp", out JsonElement exp)
                && exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return false;
            did = root.GetProperty("sub").GetString() ?? string.Empty;
            return did.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Sign(string input, byte[] secret) =>
        HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(input));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }
}
