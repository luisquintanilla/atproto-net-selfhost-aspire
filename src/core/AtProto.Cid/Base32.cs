namespace AtProto;

/// <summary>
/// RFC 4648 base32 (lowercase, no padding) - the multibase 'b' encoding used for
/// AT Protocol / IPLD CIDv1 string forms.
/// </summary>
public static class Base32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        // 5 bits per output char
        int outLen = (data.Length * 8 + 4) / 5;
        var sb = new System.Text.StringBuilder(outLen);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    public static byte[] Decode(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
            return [];

        var outBytes = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in s)
        {
            int val = Value(c);
            if (val < 0)
                throw new FormatException($"invalid base32 character '{c}'");
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                outBytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return [.. outBytes];
    }

    private static int Value(char c) => c switch
    {
        >= 'a' and <= 'z' => c - 'a',
        >= '2' and <= '7' => c - '2' + 26,
        >= 'A' and <= 'Z' => c - 'A', // tolerate uppercase on decode
        _ => -1,
    };
}
