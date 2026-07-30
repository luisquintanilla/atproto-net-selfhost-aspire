using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class JoseEs256Tests
{
    [Fact]
    public void SignThenVerify_RoundTrips()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EcPublicJwk jwk = EcPublicJwk.FromP256(key.ExportParameters(includePrivateParameters: false));

        string token = JoseEs256.CreateJws("{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\"}", "{\"htm\":\"POST\"}", key);
        Assert.True(JwsParts.TryParse(token, out JwsParts jws));
        Assert.True(JoseEs256.Verify(jws, jwk));
    }

    [Fact]
    public void Verify_WithWrongKey_Fails()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EcPublicJwk wrong = EcPublicJwk.FromP256(other.ExportParameters(includePrivateParameters: false));

        string token = JoseEs256.CreateJws("{\"alg\":\"ES256\"}", "{\"a\":1}", signer);
        Assert.True(JwsParts.TryParse(token, out JwsParts jws));
        Assert.False(JoseEs256.Verify(jws, wrong));
    }

    // The reason AtProto.OAuth does not reuse the commit-signature verifier: JOSE ES256 must accept
    // BOTH low-S and high-S signatures. A signature (r, s) and its malleable twin (r, n - s) are both
    // valid; the atproto commit verifier rejects the high-S one on purpose, which would wrongly refuse
    // conformant DPoP proofs. This test proves our JOSE verifier accepts both.
    [Fact]
    public void Verify_AcceptsBothLowSAndHighS()
    {
        // P-256 group order.
        BigInteger n = BigInteger.Parse(
            "00FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
            NumberStyles.HexNumber);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters pub = key.ExportParameters(includePrivateParameters: false);
        const string signingInput = "aGVhZGVy.cGF5bG9hZA";

        byte[] sig = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var r = new BigInteger(sig.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(sig.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        BigInteger sTwin = n - s;

        byte[] twin = new byte[64];
        sig.AsSpan(0, 32).CopyTo(twin);           // same r
        To32(sTwin).CopyTo(twin.AsSpan(32, 32));  // flipped s

        Assert.True(JoseEs256.Verify(signingInput, sig, pub));
        Assert.True(JoseEs256.Verify(signingInput, twin, pub));
        // One of the two is low-S and the other high-S; both must verify.
        Assert.NotEqual(s > (n >> 1), sTwin > (n >> 1));
    }

    [Fact]
    public void Verify_WrongLengthSignature_ReturnsFalse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters pub = key.ExportParameters(includePrivateParameters: false);
        Assert.False(JoseEs256.Verify("a.b", new byte[10], pub));
    }

    private static byte[] To32(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32)
            return bytes;
        byte[] padded = new byte[32];
        bytes.CopyTo(padded, 32 - bytes.Length);
        return padded;
    }
}
