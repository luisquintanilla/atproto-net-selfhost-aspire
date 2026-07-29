using System.Text;
using AtProto.Car;
using AtProto.Cbor;

namespace AtProto.Repo;

/// <summary>Read-side Merkle Search Tree walker over an in-memory CID to block map.</summary>
public static class MstReader
{
    /// <summary>Walk an MST rooted at <paramref name="root"/> and yield leaf records in key order.</summary>
    public static IEnumerable<RepoRecord> Walk(IReadOnlyDictionary<Cid, byte[]> blocks, Cid root)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var seen = new HashSet<Cid>();
        foreach (RepoRecord record in WalkNode(blocks, root, seen))
            yield return record;
    }

    /// <summary>Walk an MST rooted at <paramref name="root"/> and yield leaf records in key order.</summary>
    public static IEnumerable<RepoRecord> Walk(IEnumerable<CarBlock> blocks, Cid root)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var map = new Dictionary<Cid, byte[]>();
        foreach (CarBlock block in blocks)
            map[block.Cid] = block.Data;
        return Walk(map, root);
    }

    private static IEnumerable<RepoRecord> WalkNode(IReadOnlyDictionary<Cid, byte[]> blocks, Cid nodeCid, HashSet<Cid> seen)
    {
        if (!seen.Add(nodeCid))
            throw new FormatException($"MST contains a repeated or cyclic node link: {nodeCid.Encode()}");

        MstNode node = DecodeNode(blocks, nodeCid);

        if (node.Left is Cid left)
        {
            foreach (RepoRecord record in WalkNode(blocks, left, seen))
                yield return record;
        }

        foreach (MstEntry entry in node.Entries)
        {
            yield return new RepoRecord(Encoding.UTF8.GetString(entry.Key), entry.Value);
            if (entry.Right is Cid right)
            {
                foreach (RepoRecord record in WalkNode(blocks, right, seen))
                    yield return record;
            }
        }
    }

    private static MstNode DecodeNode(IReadOnlyDictionary<Cid, byte[]> blocks, Cid cid)
    {
        if (!blocks.TryGetValue(cid, out byte[]? bytes))
            throw new KeyNotFoundException($"MST node block {cid.Encode()} is missing");

        object? obj = DagCbor.Decode(bytes);
        Cid? left = obj.Field("l") is Cid l ? l : null;

        var entries = new List<MstEntry>();
        byte[] previousKey = Array.Empty<byte>();
        foreach (object? entryObj in obj.RequiredField("e").AsArray())
        {
            long rawPrefixLen = entryObj.RequiredField("p").AsInt64();
            if (rawPrefixLen < 0 || rawPrefixLen > int.MaxValue)
                throw new FormatException($"invalid MST prefix length {rawPrefixLen}");

            int prefixLen = (int)rawPrefixLen;
            byte[] suffix = entryObj.RequiredField("k").AsBytes();
            Cid value = entryObj.RequiredField("v").AsCid();
            Cid? right = entryObj.Field("t") is Cid t ? t : null;

            if (prefixLen > previousKey.Length)
                throw new FormatException($"MST prefix length {prefixLen} exceeds previous key length {previousKey.Length}");

            var key = new byte[prefixLen + suffix.Length];
            previousKey.AsSpan(0, prefixLen).CopyTo(key);
            suffix.CopyTo(key.AsSpan(prefixLen));
            previousKey = key;

            entries.Add(new MstEntry(key, value, right));
        }

        return new MstNode(left, entries);
    }

    private readonly record struct MstEntry(byte[] Key, Cid Value, Cid? Right);

    private sealed record MstNode(Cid? Left, IReadOnlyList<MstEntry> Entries);
}
