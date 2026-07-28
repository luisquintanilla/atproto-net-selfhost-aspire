using System.Security.Cryptography;

namespace AtProto;

/// <summary>
/// An IPLD Content Identifier, version 1. AT Protocol uses CIDv1 exclusively with the
/// dag-cbor (0x71) or raw (0x55) codec and a sha2-256 (0x12) multihash. The string form
/// is multibase base32-lower (leading 'b'), e.g. <c>bafyrei...</c>.
/// </summary>
public readonly struct Cid : IEquatable<Cid>
{
    public const ulong CodecDagCbor = 0x71;
    public const ulong CodecRaw = 0x55;
    public const ulong HashSha2_256 = 0x12;

    private readonly byte[] _binary;
    private readonly int _digestOffset;
    private readonly int _digestLength;

    /// <summary>CID version (always 1 for AT Protocol).</summary>
    public int Version { get; }

    /// <summary>Content codec (0x71 dag-cbor, 0x55 raw).</summary>
    public ulong Codec { get; }

    /// <summary>Multihash function code (0x12 sha2-256).</summary>
    public ulong HashType { get; }

    /// <summary>The full binary CID: varint(version) || varint(codec) || multihash.</summary>
    public ReadOnlySpan<byte> Binary => _binary;

    /// <summary>The raw hash digest (32 bytes for sha2-256).</summary>
    public ReadOnlySpan<byte> Digest => _binary.AsSpan(_digestOffset, _digestLength);

    public Cid(byte[] binary)
    {
        ArgumentNullException.ThrowIfNull(binary);
        _binary = binary;

        int o = 0;
        ulong version = Varint.ReadUnsigned(binary.AsSpan(o), out int n);
        o += n;
        if (version != 1)
            throw new FormatException($"only CIDv1 is supported (got v{version})");
        Version = 1;

        Codec = Varint.ReadUnsigned(binary.AsSpan(o), out n);
        o += n;

        HashType = Varint.ReadUnsigned(binary.AsSpan(o), out n);
        o += n;

        ulong len = Varint.ReadUnsigned(binary.AsSpan(o), out n);
        o += n;
        if (o + (int)len != binary.Length)
            throw new FormatException("CID multihash length does not match buffer");

        _digestOffset = o;
        _digestLength = (int)len;
    }

    /// <summary>Parse a CID from its binary form (a defensive copy is taken).</summary>
    public static Cid FromBytes(ReadOnlySpan<byte> binary) => new(binary.ToArray());

    /// <summary>
    /// Parse a CID that sits at the start of a larger buffer (e.g. a CARv1 block section),
    /// returning the CID and how many bytes it occupied.
    /// </summary>
    public static Cid ReadFrom(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        int o = 0;
        ulong version = Varint.ReadUnsigned(data[o..], out int n);
        o += n;
        if (version != 1)
            throw new FormatException($"only CIDv1 is supported (got v{version})");
        _ = Varint.ReadUnsigned(data[o..], out n); // codec
        o += n;
        _ = Varint.ReadUnsigned(data[o..], out n); // hash type
        o += n;
        ulong len = Varint.ReadUnsigned(data[o..], out n); // digest length
        o += n;
        bytesConsumed = o + (int)len;
        return new Cid(data[..bytesConsumed].ToArray());
    }

    /// <summary>Parse a CID from its multibase string form (base32 'b' prefix).</summary>
    public static Cid Decode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        char prefix = value[0];
        return prefix switch
        {
            'b' => new Cid(Base32.Decode(value.AsSpan(1))),
            _ => throw new FormatException($"unsupported multibase prefix '{prefix}' (only base32 'b' supported)"),
        };
    }

    /// <summary>
    /// Compute a CIDv1 over <paramref name="content"/> using sha2-256 and the given codec.
    /// This is how AT Protocol addresses every block: cid = v1(codec, sha256(bytes)).
    /// </summary>
    public static Cid ComputeV1(ulong codec, ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(content, digest);

        Span<byte> head = stackalloc byte[24];
        int o = 0;
        o += Varint.WriteUnsigned(1, head[o..]);      // version
        o += Varint.WriteUnsigned(codec, head[o..]);  // codec
        o += Varint.WriteUnsigned(HashSha2_256, head[o..]);
        o += Varint.WriteUnsigned(32, head[o..]);     // digest length

        var binary = new byte[o + 32];
        head[..o].CopyTo(binary);
        digest.CopyTo(binary.AsSpan(o));
        return new Cid(binary);
    }

    /// <summary>Multibase base32 string form (leading 'b').</summary>
    public string Encode() => "b" + Base32.Encode(_binary);

    public override string ToString() => Encode();

    public bool Equals(Cid other) =>
        _binary is not null && other._binary is not null
            ? _binary.AsSpan().SequenceEqual(other._binary)
            : ReferenceEquals(_binary, other._binary);

    public override bool Equals(object? obj) => obj is Cid other && Equals(other);

    public override int GetHashCode()
    {
        if (_binary is null)
            return 0;
        var hash = new HashCode();
        hash.AddBytes(_binary);
        return hash.ToHashCode();
    }

    public static bool operator ==(Cid left, Cid right) => left.Equals(right);
    public static bool operator !=(Cid left, Cid right) => !left.Equals(right);
}
