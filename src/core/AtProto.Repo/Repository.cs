using System.Text;
using AtProto.Car;
using AtProto.Cbor;

namespace AtProto.Repo;

/// <summary>
/// Read-only view over an AT Protocol repository decoded from a CAR archive. Decodes the
/// signed commit and walks the Merkle Search Tree (MST) in key order to enumerate records.
/// </summary>
/// <remarks>
/// MST node shape (dag-cbor): <c>{ l: CID|null, e: [ { p:int, k:bytes, v:CID, t:CID|null } ] }</c>.
/// Keys are prefix-compressed against the previous entry in the node: the full key is
/// <c>previousKey[..p] + k</c> (byte-wise). In-order traversal yields records sorted by key.
/// </remarks>
public sealed class Repository
{
    private readonly CarArchive _car;

    private Repository(CarArchive car, Cid root, Commit commit)
    {
        _car = car;
        Root = root;
        Commit = commit;
    }

    /// <summary>The commit CID (root[0] of the CAR export).</summary>
    public Cid Root { get; }

    /// <summary>The decoded signed commit.</summary>
    public Commit Commit { get; }

    /// <summary>Decode a repository from a CAR archive (its first root is the signed commit).</summary>
    public static Repository FromCar(CarArchive car)
    {
        ArgumentNullException.ThrowIfNull(car);
        if (car.Roots.Count == 0)
            throw new FormatException("CAR has no roots; expected the commit CID at index 0");

        Cid root = car.Roots[0];
        object? commitObj = DagCbor.Decode(car.GetBlock(root));

        var commit = new Commit(
            Did: commitObj.RequiredField("did").AsString(),
            Version: commitObj.RequiredField("version").AsInt64(),
            Data: commitObj.RequiredField("data").AsCid(),
            Rev: commitObj.RequiredField("rev").AsString(),
            Prev: commitObj.Field("prev") is Cid prev ? prev : null,
            Sig: commitObj.RequiredField("sig").AsBytes());

        return new Repository(car, root, commit);
    }

    /// <summary>Enumerate every record in the repository, in MST key order.</summary>
    public IEnumerable<RepoRecord> Records() => Walk(Commit.Data);

    /// <summary>Get the raw dag-cbor bytes of a record (or any block) by CID.</summary>
    public byte[] GetBlock(Cid cid) => _car.GetBlock(cid);

    /// <summary>Decode a record's dag-cbor into the object graph.</summary>
    public object? GetRecord(Cid cid) => DagCbor.Decode(_car.GetBlock(cid));

    private IEnumerable<RepoRecord> Walk(Cid nodeCid)
    {
        MstNode node = DecodeNode(nodeCid);

        if (node.Left is Cid left)
        {
            foreach (RepoRecord record in Walk(left))
                yield return record;
        }

        foreach (MstEntry entry in node.Entries)
        {
            yield return new RepoRecord(Encoding.UTF8.GetString(entry.Key), entry.Value);
            if (entry.Right is Cid right)
            {
                foreach (RepoRecord record in Walk(right))
                    yield return record;
            }
        }
    }

    private MstNode DecodeNode(Cid cid)
    {
        object? obj = DagCbor.Decode(_car.GetBlock(cid));
        Cid? left = obj.Field("l") is Cid l ? l : null;

        var entries = new List<MstEntry>();
        byte[] previousKey = [];
        foreach (object? entryObj in obj.RequiredField("e").AsArray())
        {
            int prefixLen = (int)entryObj.RequiredField("p").AsInt64();
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
