using System.Numerics;

namespace AtProto.Crypto;

/// <summary>
/// Base58 encoding using the Bitcoin alphabet, as used by <c>did:key</c> and the
/// <c>publicKeyMultibase</c> ("z" multibase prefix) form of atproto signing keys.
/// </summary>
public static class Base58Btc
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly int[] Map = BuildMap();

    private static int[] BuildMap()
    {
        var map = new int[128];
        Array.Fill(map, -1);
        for (int i = 0; i < Alphabet.Length; i++)
            map[Alphabet[i]] = i;
        return map;
    }

    /// <summary>Encode bytes to a base58btc string.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        int leadingZeros = 0;
        while (leadingZeros < data.Length && data[leadingZeros] == 0)
            leadingZeros++;

        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        Span<char> buffer = stackalloc char[data.Length * 2 + 1];
        int pos = buffer.Length;
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out BigInteger rem);
            buffer[--pos] = Alphabet[(int)rem];
        }
        for (int i = 0; i < leadingZeros; i++)
            buffer[--pos] = '1';
        return new string(buffer[pos..]);
    }

    /// <summary>Decode a base58btc string to bytes.</summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        BigInteger value = BigInteger.Zero;
        foreach (char c in text)
        {
            int digit = c < 128 ? Map[c] : -1;
            if (digit < 0)
                throw new FormatException($"invalid base58 character '{c}'");
            value = value * 58 + digit;
        }

        int leadingOnes = 0;
        while (leadingOnes < text.Length && text[leadingOnes] == '1')
            leadingOnes++;

        byte[] magnitude = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[leadingOnes + magnitude.Length];
        magnitude.CopyTo(result, leadingOnes);
        return result;
    }
}
