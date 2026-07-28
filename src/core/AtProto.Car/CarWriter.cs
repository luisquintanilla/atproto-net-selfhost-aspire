using AtProto.Cbor;

namespace AtProto.Car;

/// <summary>
/// Writes CARv1 archives. Layout mirrors <see cref="CarReader"/>:
/// <c>uvarint(headerLen) || header(dag-cbor {version:1, roots}) || (uvarint(len) || cid || data)*</c>.
/// The header is canonical dag-cbor; blocks are written in the given order (atproto readers are
/// tolerant of block order, so callers may choose a streamable order).
/// </summary>
public static class CarWriter
{
    private const long Version = 1;

    /// <summary>Serialize an archive to a byte array.</summary>
    public static byte[] Write(IReadOnlyList<Cid> roots, IEnumerable<CarBlock> blocks)
    {
        using var buffer = new MemoryStream();
        WriteTo(buffer, roots, blocks);
        return buffer.ToArray();
    }

    /// <summary>Serialize an archive to a stream.</summary>
    public static void WriteTo(Stream output, IReadOnlyList<Cid> roots, IEnumerable<CarBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(blocks);

        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = Version,
            ["roots"] = roots.Select(r => (object?)r).ToList(),
        };
        byte[] headerBytes = DagCbor.Encode(header);
        WriteVarint(output, (ulong)headerBytes.Length);
        output.Write(headerBytes);

        foreach (CarBlock block in blocks)
        {
            ReadOnlySpan<byte> cid = block.Cid.Binary;
            WriteVarint(output, (ulong)(cid.Length + block.Data.Length));
            output.Write(cid);
            output.Write(block.Data);
        }
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        int n = Varint.WriteUnsigned(value, buffer);
        output.Write(buffer[..n]);
    }
}
