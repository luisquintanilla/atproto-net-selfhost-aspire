using AtProto.Cbor;

namespace AtProto.Car;

/// <summary>A single addressed block within a CAR archive.</summary>
public readonly record struct CarBlock(Cid Cid, byte[] Data);

/// <summary>
/// A decoded CARv1 archive: the declared roots plus every content-addressed block.
/// Layout: <c>uvarint(headerLen) || header(dag-cbor {version,roots}) || (uvarint(len) || cid || data)*</c>.
/// </summary>
public sealed class CarArchive
{
    private readonly Dictionary<Cid, byte[]> _index;

    internal CarArchive(IReadOnlyList<Cid> roots, IReadOnlyList<CarBlock> blocks)
    {
        Roots = roots;
        Blocks = blocks;
        _index = new Dictionary<Cid, byte[]>(blocks.Count);
        foreach (CarBlock block in blocks)
            _index[block.Cid] = block.Data;
    }

    /// <summary>Declared root CIDs (for a repo export, index 0 is the signed commit).</summary>
    public IReadOnlyList<Cid> Roots { get; }

    /// <summary>All blocks in archive order.</summary>
    public IReadOnlyList<CarBlock> Blocks { get; }

    /// <summary>Look up a block's bytes by CID.</summary>
    public bool TryGetBlock(Cid cid, out byte[] data) => _index.TryGetValue(cid, out data!);

    /// <summary>Get a block's bytes by CID, throwing if absent.</summary>
    public byte[] GetBlock(Cid cid) =>
        _index.TryGetValue(cid, out byte[]? data)
            ? data
            : throw new KeyNotFoundException($"block {cid} not present in CAR");

    /// <summary>
    /// Recompute every block's CID from its bytes and confirm it matches the stored CID.
    /// This validates the CID + CBOR + CAR stack together against real data. Throws on mismatch.
    /// </summary>
    public void VerifyIntegrity()
    {
        foreach (CarBlock block in Blocks)
        {
            Cid recomputed = Cid.ComputeV1(block.Cid.Codec, block.Data);
            if (recomputed != block.Cid)
                throw new InvalidDataException(
                    $"CAR block CID mismatch: stored {block.Cid}, recomputed {recomputed}");
        }
    }
}

/// <summary>Reads CARv1 archives into memory.</summary>
public static class CarReader
{
    private const ulong SupportedVersion = 1;

    public static CarArchive Read(ReadOnlySpan<byte> data)
    {
        int pos = 0;

        ulong headerLen = Varint.ReadUnsigned(data[pos..], out int n);
        pos += n;
        ReadOnlySpan<byte> headerBytes = data.Slice(pos, (int)headerLen);
        pos += (int)headerLen;

        object? header = DagCbor.Decode(headerBytes);
        long version = header.RequiredField("version").AsInt64();
        if ((ulong)version != SupportedVersion)
            throw new NotSupportedException($"unsupported CAR version {version} (only v1)");

        var roots = new List<Cid>();
        foreach (object? root in header.RequiredField("roots").AsArray())
            roots.Add(root.AsCid());

        var blocks = new List<CarBlock>();
        while (pos < data.Length)
        {
            ulong sectionLen = Varint.ReadUnsigned(data[pos..], out n);
            pos += n;
            ReadOnlySpan<byte> section = data.Slice(pos, (int)sectionLen);
            pos += (int)sectionLen;

            Cid cid = Cid.ReadFrom(section, out int cidLen);
            byte[] blockData = section[cidLen..].ToArray();
            blocks.Add(new CarBlock(cid, blockData));
        }

        return new CarArchive(roots, blocks);
    }

    public static CarArchive Read(byte[] data) => Read(data.AsSpan());

    public static async Task<CarArchive> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}
