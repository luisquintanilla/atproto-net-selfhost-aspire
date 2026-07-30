using System.Buffers.Text;

namespace AtProto.OAuth;

/// <summary>
/// The three base64url segments of a compact JWS / JWT (<c>header.payload.signature</c>). A helper
/// for splitting and decoding a token without pulling in a full JWT library, used for both DPoP
/// proofs and client assertions.
/// </summary>
public readonly record struct JwsParts(string Header, string Payload, string Signature)
{
    /// <summary>The signing input: <c>header.payload</c> (what the signature covers).</summary>
    public string SigningInput => $"{Header}.{Payload}";

    /// <summary>The decoded JOSE header bytes (UTF-8 JSON).</summary>
    public byte[] HeaderJson() => Base64Url.DecodeFromChars(Header);

    /// <summary>The decoded payload bytes (UTF-8 JSON).</summary>
    public byte[] PayloadJson() => Base64Url.DecodeFromChars(Payload);

    /// <summary>The decoded signature bytes.</summary>
    public byte[] SignatureBytes() => Base64Url.DecodeFromChars(Signature);

    /// <summary>
    /// Parse a compact JWS. Requires exactly three non-empty, dot-separated segments. Does not
    /// validate the signature or decode the segments (call the accessors for that).
    /// </summary>
    public static bool TryParse(string? token, out JwsParts parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(token))
            return false;

        int firstDot = token.IndexOf('.');
        if (firstDot <= 0)
            return false;
        int secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1)
            return false;
        if (token.IndexOf('.', secondDot + 1) >= 0)
            return false; // more than two dots: not a compact JWS
        if (secondDot == token.Length - 1)
            return false; // empty signature

        parts = new JwsParts(token[..firstDot], token[(firstDot + 1)..secondDot], token[(secondDot + 1)..]);
        return true;
    }
}
