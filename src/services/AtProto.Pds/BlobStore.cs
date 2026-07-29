using System.Collections.Concurrent;

namespace AtProto.Pds;

/// <summary>In-memory AT Protocol blob storage keyed by CIDv1/raw/sha2-256.</summary>
public sealed class BlobStore
{
    private readonly ConcurrentDictionary<Cid, StoredBlob> _blobs = new();

    public StoredBlob Put(ReadOnlySpan<byte> bytes, string contentType)
    {
        Cid cid = Cid.ComputeV1(Cid.CodecRaw, bytes);
        var blob = new StoredBlob(cid, bytes.ToArray(), contentType);
        return _blobs.GetOrAdd(cid, blob);
    }

    public bool TryGet(Cid cid, out StoredBlob blob) => _blobs.TryGetValue(cid, out blob!);
}

public sealed record StoredBlob(Cid Cid, byte[] Bytes, string ContentType)
{
    public int Size => Bytes.Length;
}
