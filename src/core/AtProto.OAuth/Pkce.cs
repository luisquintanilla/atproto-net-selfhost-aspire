using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AtProto.OAuth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636), restricted to the <c>S256</c> method the atproto OAuth
/// profile mandates. The <c>plain</c> method is never supported.
/// </summary>
public static class Pkce
{
    /// <summary>The only challenge method this profile allows.</summary>
    public const string MethodS256 = "S256";

    /// <summary>
    /// Compute the S256 <c>code_challenge</c> for a verifier: <c>base64url(sha256(ascii(verifier)))</c>.
    /// </summary>
    public static string ComputeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url.EncodeToString(hash);
    }

    /// <summary>
    /// Verify a <c>code_verifier</c> against a stored <c>code_challenge</c> in constant time. Returns
    /// false for a malformed verifier or a mismatch.
    /// </summary>
    public static bool Verify(string codeVerifier, string codeChallenge)
    {
        if (!IsValidVerifier(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
            return false;
        byte[] computed = Encoding.ASCII.GetBytes(ComputeChallenge(codeVerifier));
        byte[] expected = Encoding.ASCII.GetBytes(codeChallenge);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    /// <summary>
    /// A well-formed verifier per RFC 7636: 43 to 128 characters from the unreserved set
    /// <c>[A-Za-z0-9-._~]</c>.
    /// </summary>
    public static bool IsValidVerifier(string? codeVerifier)
    {
        if (codeVerifier is null || codeVerifier.Length is < 43 or > 128)
            return false;
        foreach (char c in codeVerifier)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~';
            if (!ok)
                return false;
        }
        return true;
    }

    /// <summary>Generate a random 43-character verifier (256 bits of entropy). Client-side helper.</summary>
    public static string GenerateVerifier()
    {
        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        return Base64Url.EncodeToString(entropy);
    }
}
