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

    /// <summary>
    /// Encode a plain .NET object graph (the same shape <see cref="Decode"/> produces) into
    /// canonical DAG-CBOR bytes. Canonical rules are enforced via
    /// <see cref="CborConformanceMode.Canonical"/>: map keys sorted length-first-then-bytewise,
    /// definite lengths, shortest-form integers. CID links are written as tag 42 over a byte
    /// string carrying the <c>0x00</c> multibase-identity prefix followed by the binary CID.
    /// </summary>
    /// <remarks>
    /// The accepted value types mirror the decode model: <c>null</c>, <see cref="bool"/>,
    /// <see cref="long"/>, <see cref="ulong"/>, <see cref="byte"/>[], <see cref="string"/>,
    /// <see cref="double"/>, <see cref="Cid"/>, <see cref="IReadOnlyList{T}"/> of object?, and
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> with string keys. Note DAG-CBOR requires
    /// 64-bit floats; the Canonical writer shortens floats, so this encoder is byte-exact only
    /// for float-free graphs (which is every MST node and commit object).
    /// </remarks>
    public static byte[] Encode(object? value)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical, convertIndefiniteLengthEncodings: true);
        WriteValue(writer, value);
        return writer.Encode();
    }

    private static void WriteValue(CborWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull();
                break;
            case bool b:
                writer.WriteBoolean(b);
                break;
            case Cid cid:
                writer.WriteTag((CborTag)CidTag);
                writer.WriteByteString(CidLinkBytes(cid));
                break;
            case string s:
                writer.WriteTextString(s);
                break;
            case byte[] bytes:
                writer.WriteByteString(bytes);
                break;
            case long l:
                writer.WriteInt64(l);
                break;
            case ulong u:
                writer.WriteUInt64(u);
                break;
            case int i:
                writer.WriteInt64(i);
                break;
            case double d:
                writer.WriteDouble(d);
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartMap(map.Count);
                foreach (KeyValuePair<string, object?> kv in map)
                {
                    writer.WriteTextString(kv.Key);
                    WriteValue(writer, kv.Value);
                }
                writer.WriteEndMap();
                break;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray(list.Count);
                foreach (object? item in list)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new FormatException($"cannot dag-cbor encode value of type {value.GetType()}");
        }
    }

    /// <summary>The tag-42 byte-string payload for a CID: the 0x00 identity prefix then the binary CID.</summary>
    private static byte[] CidLinkBytes(Cid cid)
    {
        ReadOnlySpan<byte> binary = cid.Binary;
        var link = new byte[binary.Length + 1];
        link[0] = 0x00;
        binary.CopyTo(link.AsSpan(1));
        return link;
    }

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
