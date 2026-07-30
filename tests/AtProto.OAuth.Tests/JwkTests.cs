using System.Security.Cryptography;
using System.Text.Json;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class JwkTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void FromP256_RoundTripsThroughParameters()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters original = key.ExportParameters(includePrivateParameters: false);

        EcPublicJwk jwk = EcPublicJwk.FromP256(original);
        ECParameters restored = jwk.ToEcParameters();

        Assert.Equal(original.Q.X, restored.Q.X);
        Assert.Equal(original.Q.Y, restored.Q.Y);
    }

    [Fact]
    public void Parse_RejectsPrivateKey()
    {
        // A JWK carrying 'd' is a private key; DPoP headers must never contain one.
        string withPrivate =
            "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs\"," +
            "\"y\":\"9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA\",\"d\":\"AA\"}";
        Assert.Throws<FormatException>(() => EcPublicJwk.Parse(Json(withPrivate)));
    }

    [Theory]
    [InlineData("{\"kty\":\"RSA\",\"crv\":\"P-256\",\"x\":\"AA\",\"y\":\"AA\"}")] // wrong kty
    [InlineData("{\"kty\":\"EC\",\"crv\":\"P-384\",\"x\":\"AA\",\"y\":\"AA\"}")]  // wrong crv
    [InlineData("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"AA\"}")]              // missing y
    [InlineData("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"AA\",\"y\":\"AA\"}")] // short coordinates
    public void Parse_RejectsMalformed(string jwkJson)
    {
        Assert.Throws<FormatException>(() => EcPublicJwk.Parse(Json(jwkJson)));
    }

    [Fact]
    public void Parse_AcceptsValidP256()
    {
        string valid =
            "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs\"," +
            "\"y\":\"9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA\"}";
        EcPublicJwk jwk = EcPublicJwk.Parse(Json(valid));
        Assert.Equal("P-256", jwk.Crv);
        Assert.Equal("EC", jwk.Kty);
    }
}
