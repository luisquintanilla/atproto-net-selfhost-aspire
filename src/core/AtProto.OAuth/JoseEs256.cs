using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AtProto.OAuth;

/// <summary>
/// ES256 (ECDSA on P-256 with SHA-256) signing and verification for JOSE / JWS, as used by DPoP
/// proofs and confidential-client assertions.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally separate from the stack's commit-signature verifier
/// (<c>AtProto.Crypto.EcPublicKey.Verify</c>). That verifier enforces the atproto low-S rule and
/// rejects high-S signatures, which is correct for repository commits. JOSE ES256 has no such rule:
/// a valid signature may use either low-S or high-S, so rejecting high-S here would wrongly refuse
/// conformant client proofs. We therefore verify with the raw IEEE P1363 (r||s) format and no low-S
/// constraint.
/// </para>
/// </remarks>
public static class JoseEs256
{
    /// <summary>
    /// Verify an ES256 signature over <paramref name="signingInput"/> (the ASCII
    /// <c>header.payload</c>) using the given public key. Accepts both low-S and high-S signatures,
    /// as JOSE requires.
    /// </summary>
    public static bool Verify(string signingInput, ReadOnlySpan<byte> signature, ECParameters publicKey)
    {
        if (signature.Length != 64)
            return false;

        try
        {
            using ECDsa ecdsa = ECDsa.Create(publicKey);
            return ecdsa.VerifyData(
                Encoding.ASCII.GetBytes(signingInput),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verify a parsed compact JWS with the given P-256 public key.
    /// </summary>
    public static bool Verify(in JwsParts jws, EcPublicJwk publicKey)
    {
        byte[] signature;
        try
        {
            signature = jws.SignatureBytes();
        }
        catch (FormatException)
        {
            return false;
        }
        return Verify(jws.SigningInput, signature, publicKey.ToEcParameters());
    }

    /// <summary>
    /// Create a compact JWS (<c>header.payload.signature</c>) by signing the given JOSE header and
    /// payload JSON with a P-256 key. Provided for building DPoP proofs and client assertions on the
    /// client side (used by tests and by adopters building an atproto OAuth client in .NET).
    /// </summary>
    public static string CreateJws(string headerJson, string payloadJson, ECDsa key)
    {
        string header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson));
        string payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        string signingInput = $"{header}.{payload}";
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }
}
