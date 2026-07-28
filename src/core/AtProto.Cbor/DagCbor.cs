using System.Formats.Cbor;

namespace AtProto.Cbor;

/// <summary>
/// A DAG-CBOR reader built on <see cref="CborReader"/>. Decodes a block into a plain
/// .NET object graph:
/// <list type="bullet">
///   <item>map -&gt; <see cref="Dictionary{TKey,TValue}"/> (string keys)</item>
///   <item>array -&gt; <see cref="List{T}"/> of object?</item>
///   <item>text -&gt; string, bytes -&gt; byte[]</item>
///   <item>integer -&gt; long (or ulong when it overflows long)</item>
///   <item>bool -&gt; bool, null -&gt; null, float -&gt; double</item>
///   <item>tag 42 (CID link) -&gt; <see cref="Cid"/></item>
/// </list>
/// Use the accessor extensions in <see cref="DagCborExtensions"/> for typed navigation.
/// </summary>
public static class DagCbor
{
    /// <summary>The IPLD "CID link" CBOR tag.</summary>
    public const int CidTag = 42;

    /// <summary>Decode a single DAG-CBOR value; throws if trailing bytes remain.</summary>
    public static object? Decode(ReadOnlySpan<byte> data)
    {
        var reader = new CborReader(data.ToArray(), CborConformanceMode.Strict);
        object? value = ReadValue(reader);
        if (reader.BytesRemaining != 0)
            throw new FormatException($"trailing bytes after dag-cbor value ({reader.BytesRemaining} remaining)");
        return value;
    }

    /// <summary>
    /// Decode the first DAG-CBOR value from a buffer that may contain more after it, reporting how
    /// many bytes it consumed. Used for firehose frames (header CBOR followed by payload CBOR).
    /// </summary>
    public static object? DecodeFirst(ReadOnlyMemory<byte> data, out int bytesConsumed)
    {
        var reader = new CborReader(data, CborConformanceMode.Strict);
        object? value = ReadValue(reader);
        bytesConsumed = data.Length - reader.BytesRemaining;
        return value;
    }

    private static object? ReadValue(CborReader reader)
    {
        CborReaderState state = reader.PeekState();
        switch (state)
        {
            case CborReaderState.Null:
                reader.ReadNull();
                return null;

            case CborReaderState.Boolean:
                return reader.ReadBoolean();

            case CborReaderState.UnsignedInteger:
                ulong u = reader.ReadUInt64();
                return u <= long.MaxValue ? (long)u : u;

            case CborReaderState.NegativeInteger:
                return reader.ReadInt64();

            case CborReaderState.ByteString:
                return reader.ReadByteString();

            case CborReaderState.TextString:
                return reader.ReadTextString();

            case CborReaderState.StartArray:
                return ReadArray(reader);

            case CborReaderState.StartMap:
                return ReadMap(reader);

            case CborReaderState.Tag:
                return ReadTagged(reader);

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                return reader.ReadDouble();

            default:
                throw new FormatException($"unexpected dag-cbor state: {state}");
        }
    }

    private static List<object?> ReadArray(CborReader reader)
    {
        int? count = reader.ReadStartArray();
        var list = new List<object?>(count ?? 4);
        while (reader.PeekState() != CborReaderState.EndArray)
            list.Add(ReadValue(reader));
        reader.ReadEndArray();
        return list;
    }

    private static Dictionary<string, object?> ReadMap(CborReader reader)
    {
        int? count = reader.ReadStartMap();
        var map = new Dictionary<string, object?>(count ?? 4, StringComparer.Ordinal);
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            if (reader.PeekState() != CborReaderState.TextString)
                throw new FormatException("dag-cbor map keys must be text strings");
            string key = reader.ReadTextString();
            map[key] = ReadValue(reader);
        }
        reader.ReadEndMap();
        return map;
    }

    private static object? ReadTagged(CborReader reader)
    {
        CborTag tag = reader.ReadTag();
        if ((int)tag != CidTag)
            throw new FormatException($"unsupported dag-cbor tag {(int)tag} (only {CidTag} CID-link is allowed)");

        byte[] link = reader.ReadByteString();
        if (link.Length == 0 || link[0] != 0x00)
            throw new FormatException("dag-cbor CID link must begin with the 0x00 multibase identity prefix");
        return Cid.FromBytes(link.AsSpan(1));
    }
}
