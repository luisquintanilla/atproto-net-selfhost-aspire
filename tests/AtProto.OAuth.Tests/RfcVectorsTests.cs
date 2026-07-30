using System.Buffers.Text;
using System.Text;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

/// <summary>
/// Known-answer tests against the published RFC vectors. These pin our primitives to the specs, not
/// just to round-tripping against themselves.
/// </summary>
public class RfcVectorsTests
{
    // RFC 9449 Figure 4: the public key from the example DPoP proof.
    private const string Rfc9449JwkX = "l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs";
    private const string Rfc9449JwkY = "9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA";

    // RFC 9449 Figure 9: the jkt (RFC 7638 thumbprint) of that key.
    private const string Rfc9449Jkt = "0ZcOCORZNYy-DWpqq30jZyJGHTN0d2HglBV3uiguA4I";

    // RFC 9449 Figure 2/4: the example proof's payload and signature segments (base64url).
    private const string Rfc9449ProofHeaderJson =
        "{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"EC\"," +
        "\"x\":\"l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs\"," +
        "\"y\":\"9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA\",\"crv\":\"P-256\"}}";
    private const string Rfc9449ProofPayload =
        "eyJqdGkiOiItQndDM0VTYzZhY2MybFRjIiwiaHRtIjoiUE9TVCIsImh0dSI6Imh0dHBzOi8v" +
        "c2VydmVyLmV4YW1wbGUuY29tL3Rva2VuIiwiaWF0IjoxNTYyMjYyNjE2fQ";
    private const string Rfc9449ProofSignature =
        "2-GxA6T8lP4vfrg8v-FdWP0A0zdrj8igiMLvqRMUvwnQg4PtFLbdLXiOSsX0x7NVY-FNyJK70nfbV37xRZT3Lg";

    // RFC 7636 Appendix B: PKCE S256 known-answer.
    private const string Rfc7636Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Rfc7636Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static string Rfc9449ProofHeaderSegment =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Rfc9449ProofHeaderJson));

    private static string Rfc9449ProofToken =>
        $"{Rfc9449ProofHeaderSegment}.{Rfc9449ProofPayload}.{Rfc9449ProofSignature}";

    [Fact]
    public void JwkThumbprint_MatchesRfc9449Example()
    {
        var jwk = new EcPublicJwk { Crv = "P-256", X = Rfc9449JwkX, Y = Rfc9449JwkY };
        Assert.Equal(Rfc9449Jkt, jwk.Thumbprint());
    }

    [Fact]
    public void Rfc9449ExampleProof_Verifies()
    {
        Assert.True(JwsParts.TryParse(Rfc9449ProofToken, out JwsParts jws));

        using var doc = System.Text.Json.JsonDocument.Parse(jws.HeaderJson());
        EcPublicJwk jwk = EcPublicJwk.Parse(doc.RootElement.GetProperty("jwk"));

        Assert.True(JoseEs256.Verify(jws, jwk));
        // The embedded key's thumbprint is the published jkt.
        Assert.Equal(Rfc9449Jkt, jwk.Thumbprint());
    }

    [Fact]
    public void Rfc9449ExampleProof_TamperedPayload_FailsVerification()
    {
        // Flip the last character of the payload segment.
        string payload = Rfc9449ProofPayload[..^1] + (Rfc9449ProofPayload[^1] == 'Q' ? 'R' : 'Q');
        string tampered = $"{Rfc9449ProofHeaderSegment}.{payload}.{Rfc9449ProofSignature}";

        Assert.True(JwsParts.TryParse(tampered, out JwsParts jws));
        var jwk = new EcPublicJwk { Crv = "P-256", X = Rfc9449JwkX, Y = Rfc9449JwkY };
        Assert.False(JoseEs256.Verify(jws, jwk));
    }

    [Fact]
    public void Pkce_MatchesRfc7636Example()
    {
        Assert.Equal(Rfc7636Challenge, Pkce.ComputeChallenge(Rfc7636Verifier));
        Assert.True(Pkce.Verify(Rfc7636Verifier, Rfc7636Challenge));
    }
}
