using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

/// <summary>A settable clock for deterministic time-based tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Builds DPoP proof JWTs the way a conformant client would, for driving the validator.</summary>
internal static class TestDpop
{
    private static readonly JsonSerializerOptions Json =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Create a client keypair.</summary>
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string Jkt(ECDsa key) =>
        EcPublicJwk.FromP256(key.ExportParameters(includePrivateParameters: false)).Thumbprint();

    /// <summary>Build a signed DPoP proof with the given claims.</summary>
    public static string Proof(
        ECDsa key,
        string htm,
        string htu,
        long iat,
        string? nonce = null,
        string? ath = null,
        string? jti = null)
    {
        EcPublicJwk jwk = EcPublicJwk.FromP256(key.ExportParameters(includePrivateParameters: false));
        var header = new Dictionary<string, object?>
        {
            ["typ"] = "dpop+jwt",
            ["alg"] = "ES256",
            ["jwk"] = new Dictionary<string, object?>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = jwk.X,
                ["y"] = jwk.Y,
            },
        };
        var payload = new Dictionary<string, object?>
        {
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["htm"] = htm,
            ["htu"] = htu,
            ["iat"] = iat,
            ["nonce"] = nonce,
            ["ath"] = ath,
        };
        return JoseEs256.CreateJws(
            JsonSerializer.Serialize(header, Json),
            JsonSerializer.Serialize(payload, Json),
            key);
    }
}
