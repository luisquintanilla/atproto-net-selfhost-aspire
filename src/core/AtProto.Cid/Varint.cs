namespace AtProto;

/// <summary>Unsigned LEB128 varint codec (as used by multiformats / CARv1).</summary>
public static class Varint
{
    public static ulong ReadUnsigned(ReadOnlySpan<byte> data, out int bytesRead)
    {
        ulong result = 0;
        int shift = 0;
        bytesRead = 0;
        while (bytesRead < data.Length)
        {
            byte b = data[bytesRead++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift >= 64)
                break;
        }
        throw new FormatException("invalid or truncated unsigned varint");
    }

    public static int WriteUnsigned(ulong value, Span<byte> dest)
    {
        int i = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            dest[i++] = b;
        } while (value != 0);
        return i;
    }

    public static byte[] EncodeUnsigned(ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        int n = WriteUnsigned(value, tmp);
        return tmp[..n].ToArray();
    }
}
