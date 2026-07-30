using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtProto.OAuth;

/// <summary>
/// An elliptic-curve <b>public</b> key in JSON Web Key form, restricted to the curve the atproto
/// OAuth profile uses for DPoP and client assertions: NIST P-256 (<c>ES256</c>). This type never
/// carries private key material; parsing a JWK that contains a private <c>d</c> member is rejected,
/// per RFC 9449 (the DPoP <c>jwk</c> header "MUST NOT contain a private key").
/// </summary>
public sealed record EcPublicJwk
{
    /// <summary>The curve name. Always <c>P-256</c> for this profile.</summary>
    public required string Crv { get; init; }

    /// <summary>The base64url (unpadded) big-endian X coordinate.</summary>
    public required string X { get; init; }

    /// <summary>The base64url (unpadded) big-endian Y coordinate.</summary>
    public required string Y { get; init; }

    /// <summary>The key type. Always <c>EC</c>.</summary>
    public string Kty => "EC";

    /// <summary>Build a JWK from a P-256 public key's parameters.</summary>
    public static EcPublicJwk FromP256(ECParameters parameters)
    {
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            throw new ArgumentException("expected a P-256 public point with 32-byte coordinates", nameof(parameters));
        return new EcPublicJwk
        {
            Crv = "P-256",
            X = Base64Url.EncodeToString(x),
            Y = Base64Url.EncodeToString(y),
        };
    }

    /// <summary>
    /// Parse and validate a public EC JWK from JSON. Enforces <c>kty=EC</c>, <c>crv=P-256</c>,
    /// 32-byte coordinates, and the absence of any private <c>d</c> member.
    /// </summary>
    public static EcPublicJwk Parse(JsonElement jwk)
    {
        if (jwk.ValueKind != JsonValueKind.Object)
            throw new FormatException("jwk must be a JSON object");
        if (jwk.TryGetProperty("d", out _))
            throw new FormatException("jwk must not contain a private key ('d')");

        string kty = GetString(jwk, "kty");
        if (kty != "EC")
            throw new FormatException($"unsupported jwk kty '{kty}', expected 'EC'");
        string crv = GetString(jwk, "crv");
        if (crv != "P-256")
            throw new FormatException($"unsupported jwk crv '{crv}', expected 'P-256'");

        string x = GetString(jwk, "x");
        string y = GetString(jwk, "y");
        // Validate the coordinates decode to a P-256 field element (32 bytes).
        if (Decode32(x) is null || Decode32(y) is null)
            throw new FormatException("jwk x/y must each be a 32-byte base64url value");

        return new EcPublicJwk { Crv = crv, X = x, Y = y };
    }

    /// <summary>Convert to <see cref="ECParameters"/> suitable for <see cref="ECDsa.Create(ECParameters)"/>.</summary>
    public ECParameters ToEcParameters()
    {
        byte[] x = Decode32(X) ?? throw new FormatException("invalid x coordinate");
        byte[] y = Decode32(Y) ?? throw new FormatException("invalid y coordinate");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        };
    }

    /// <summary>
    /// The RFC 7638 JWK SHA-256 thumbprint, base64url encoded. This is the <c>jkt</c> value used to
    /// sender-constrain DPoP tokens (RFC 9449 section 6.1). The canonical JSON contains only the
    /// required EC members in lexicographic order with no whitespace: <c>crv, kty, x, y</c>.
    /// </summary>
    public string Thumbprint()
    {
        string canonical = $"{{\"crv\":\"{Crv}\",\"kty\":\"EC\",\"x\":\"{X}\",\"y\":\"{Y}\"}}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Base64Url.EncodeToString(hash);
    }

    private static string GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"jwk is missing required string member '{name}'");
        string? s = value.GetString();
        if (string.IsNullOrEmpty(s))
            throw new FormatException($"jwk member '{name}' must be non-empty");
        return s;
    }

    private static byte[]? Decode32(string base64url)
    {
        try
        {
            byte[] bytes = Base64Url.DecodeFromChars(base64url);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
