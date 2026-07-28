using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AtProto.Cbor;

namespace AtProto.Repo;

/// <summary>
/// Deterministic <b>write</b> side of the AT Protocol Merkle Search Tree (MST). Rebuilds the
/// canonical tree structure from a full set of <c>(key → value CID)</c> pairs and computes the
/// root CID (the <c>data</c> field of a commit). The MST is history-independent: the shape and
/// therefore the root CID are a pure function of the current key/value contents, regardless of
/// insertion/deletion order.
/// </summary>
/// <remarks>
/// <para>
/// Depth (aka layer) of a key is <c>leadingZeroBits(sha256(key)) / 2</c> — a fanout-4 tree. The top
/// node holds every key at the highest depth; sub-tree links descend exactly one layer at a time,
/// so empty intermediate nodes are emitted where a run of keys skips a level (per the repository
/// spec: empty nodes are pruned at the top and bottom but kept in the middle).
/// </para>
/// <para>
/// Node shape (dag-cbor): <c>{ l: CID|null, e: [ { p:int, k:bytes, v:CID, t:CID|null } ] }</c>, with
/// keys prefix-compressed against the previous entry in the node. This mirrors the read path in
/// <see cref="Repository"/>, so a tree built here round-trips through that reader.
/// </para>
/// </remarks>
public static class Mst
{
    /// <summary>The result of building an MST: the root CID plus every node block it produced.</summary>
    /// <param name="Root">The MST root CID (the commit's <c>data</c> field).</param>
    /// <param name="Blocks">CID → canonical dag-cbor bytes for each MST node created.</param>
    public readonly record struct MstResult(Cid Root, IReadOnlyDictionary<Cid, byte[]> Blocks);

    /// <summary>
    /// Compute the depth ("layer") of an MST key: count the leading zero bits of
    /// <c>sha256(key)</c> and divide by two (rounding down), giving a fanout of 4.
    /// </summary>
    public static int LeadingZerosOnHash(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(key, hash);

        int zeroBits = 0;
        foreach (byte b in hash)
        {
            if (b == 0)
            {
                zeroBits += 8;
                continue;
            }
            zeroBits += BitOperations.LeadingZeroCount(b) - 24; // b widens to uint (24 baseline zeros)
            break;
        }
        return zeroBits / 2;
    }

    /// <summary>Build the MST from records keyed by their UTF-8 <c>collection/rkey</c> path string.</summary>
    public static MstResult BuildFromPaths(IEnumerable<KeyValuePair<string, Cid>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Build(entries.Select(e =>
            new KeyValuePair<byte[], Cid>(Encoding.UTF8.GetBytes(e.Key), e.Value)));
    }

    /// <summary>
    /// Build the MST from a full set of <c>(key bytes → value CID)</c> pairs and return the root
    /// CID together with every emitted node block. Keys must be unique and non-empty.
    /// </summary>
    public static MstResult Build(IEnumerable<KeyValuePair<byte[], Cid>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<Leaf> leaves = entries
            .Select(e => new Leaf(e.Key, e.Value))
            .OrderBy(l => l.Key, ByteArrayComparer.Instance)
            .ToList();

        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Key.Length == 0)
                throw new ArgumentException("MST keys must be non-empty.", nameof(entries));
            if (i > 0 && ByteArrayComparer.Instance.Compare(leaves[i - 1].Key, leaves[i].Key) == 0)
                throw new ArgumentException($"duplicate MST key: {Encoding.UTF8.GetString(leaves[i].Key)}", nameof(entries));
        }

        var blocks = new Dictionary<Cid, byte[]>();

        if (leaves.Count == 0)
        {
            Cid empty = EmitNode(null, [], blocks);
            return new MstResult(empty, blocks);
        }

        int topLayer = 0;
        foreach (Leaf leaf in leaves)
            topLayer = Math.Max(topLayer, LeadingZerosOnHash(leaf.Key));

        Cid root = BuildNode(leaves, topLayer, blocks);
        return new MstResult(root, blocks);
    }

    /// <summary>
    /// Emit one node at <paramref name="layer"/> covering <paramref name="leaves"/> (all of depth
    /// ≤ <paramref name="layer"/>). Keys at exactly this layer become entries; consecutive runs of
    /// lower-depth keys become sub-trees one layer down (which may recurse through empty
    /// intermediate nodes). Returns the emitted node's CID.
    /// </summary>
    private static Cid BuildNode(IReadOnlyList<Leaf> leaves, int layer, Dictionary<Cid, byte[]> blocks)
    {
        Cid? leftLink = null;
        var entries = new List<Entry>();
        var pending = new List<Leaf>();

        foreach (Leaf leaf in leaves)
        {
            if (LeadingZerosOnHash(leaf.Key) == layer)
            {
                Cid? sub = pending.Count > 0 ? BuildNode(pending, layer - 1, blocks) : null;
                AttachSubtree(ref leftLink, entries, sub);
                pending.Clear();
                entries.Add(new Entry(leaf.Key, leaf.Value, null));
            }
            else
            {
                pending.Add(leaf);
            }
        }

        if (pending.Count > 0)
        {
            Cid sub = BuildNode(pending, layer - 1, blocks);
            AttachSubtree(ref leftLink, entries, sub);
        }

        return EmitNode(leftLink, entries, blocks);
    }

    /// <summary>Hang a sub-tree link left of the next entry: on the node's <c>l</c> if no entry
    /// has been placed yet, otherwise on the most recent entry's right (<c>t</c>) pointer.</summary>
    private static void AttachSubtree(ref Cid? leftLink, List<Entry> entries, Cid? sub)
    {
        if (sub is null)
            return;
        if (entries.Count == 0)
            leftLink = sub;
        else
            entries[^1] = entries[^1] with { Right = sub };
    }

    /// <summary>Serialize a node to canonical dag-cbor, compute its CID, store the block, return the CID.</summary>
    private static Cid EmitNode(Cid? left, IReadOnlyList<Entry> entries, Dictionary<Cid, byte[]> blocks)
    {
        var eArray = new List<object?>(entries.Count);
        byte[] previousKey = [];
        foreach (Entry entry in entries)
        {
            int prefixLen = CommonPrefixLength(previousKey, entry.Key);
            byte[] suffix = entry.Key[prefixLen..];
            eArray.Add(new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["p"] = (long)prefixLen,
                ["k"] = suffix,
                ["v"] = entry.Value,
                ["t"] = entry.Right is Cid t ? t : null,
            });
            previousKey = entry.Key;
        }

        var node = new Dictionary<string, object?>(2, StringComparer.Ordinal)
        {
            ["l"] = left is Cid l ? l : null,
            ["e"] = eArray,
        };

        byte[] bytes = DagCbor.Encode(node);
        Cid cid = Cid.ComputeV1(Cid.CodecDagCbor, bytes);
        blocks[cid] = bytes;
        return cid;
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int max = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < max && a[i] == b[i])
            i++;
        return i;
    }

    private readonly record struct Leaf(byte[] Key, Cid Value);

    private readonly record struct Entry(byte[] Key, Cid Value, Cid? Right);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => (x ?? []).AsSpan().SequenceCompareTo(y ?? []);
    }
}
