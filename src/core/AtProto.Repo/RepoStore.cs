using AtProto.Car;
using AtProto.Cbor;
using AtProto.Crypto;

namespace AtProto.Repo;

/// <summary>The kind of a single repository write.</summary>
public enum WriteAction
{
    /// <summary>Create a new record (fails if the key already exists).</summary>
    Create,
    /// <summary>Overwrite an existing record (or create it).</summary>
    Update,
    /// <summary>Remove a record.</summary>
    Delete,
}

/// <summary>A requested write against a repository. <paramref name="Rkey"/> may be empty on
/// <see cref="WriteAction.Create"/> to have the store mint a fresh TID rkey.</summary>
public sealed record RepoWrite(WriteAction Action, string Collection, string Rkey, object? Record)
{
    /// <summary>Create a record with a caller-supplied rkey.</summary>
    public static RepoWrite Create(string collection, string rkey, object record) =>
        new(WriteAction.Create, collection, rkey, record);

    /// <summary>Create a record, letting the store mint a TID rkey.</summary>
    public static RepoWrite Create(string collection, object record) =>
        new(WriteAction.Create, collection, string.Empty, record);

    /// <summary>Overwrite (or create) a record at a known rkey.</summary>
    public static RepoWrite Update(string collection, string rkey, object record) =>
        new(WriteAction.Update, collection, rkey, record);

    /// <summary>Delete the record at a known rkey.</summary>
    public static RepoWrite Delete(string collection, string rkey) =>
        new(WriteAction.Delete, collection, rkey, null);
}

/// <summary>A single applied operation, as it appears in a firehose <c>#commit</c>.</summary>
public sealed record RepoOp(WriteAction Action, string Path, Cid? Cid, Cid? Prev);

/// <summary>The result of committing a batch of writes.</summary>
public sealed record CommitResult(
    Cid Commit,
    Cid Root,
    string Rev,
    Cid? Prev,
    IReadOnlyList<RepoOp> Ops,
    IReadOnlyDictionary<Cid, byte[]> NewBlocks);

/// <summary>
/// A mutable, in-memory AT Protocol repository: an MST over <c>collection/rkey → record-CID</c>, a
/// block store, and a signed commit chain. Each <see cref="ApplyWrites"/> encodes the touched
/// records, rebuilds the MST, mints a new TID revision, and produces a fresh signed commit plus the
/// set of newly referenced blocks (the firehose commit slice). Export the current state as CARv1 via
/// <see cref="ExportCar"/> — exactly what <c>com.atproto.sync.getRepo</c> serves.
/// </summary>
public sealed class RepoStore
{
    private readonly EcKeypair _key;
    private readonly TidClock _clock;
    private readonly Dictionary<string, Cid> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<Cid, byte[]> _blocks = new();
    private IReadOnlyDictionary<Cid, byte[]> _mstBlocks = new Dictionary<Cid, byte[]>();

    private RepoStore(EcKeypair key, string did, TidClock clock)
    {
        _key = key;
        Did = did;
        _clock = clock;
    }

    /// <summary>The repository's DID.</summary>
    public string Did { get; }

    /// <summary>The signing key's <c>did:key</c> (goes in the DID document's <c>#atproto</c> verification method).</summary>
    public string SigningKeyDid => _key.DidKey;

    /// <summary>The current commit CID (repository head).</summary>
    public Cid Head { get; private set; }

    /// <summary>The current signed commit.</summary>
    public Commit Commit { get; private set; } = null!;

    /// <summary>The current MST root CID (<c>commit.data</c>).</summary>
    public Cid Root => Commit.Data;

    /// <summary>The live <c>collection/rkey → record-CID</c> map.</summary>
    public IReadOnlyDictionary<string, Cid> Records => _records;

    /// <summary>Create an empty repository with a genesis commit over an empty MST.</summary>
    public static RepoStore CreateEmpty(EcKeypair key, string did, TidClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(did);
        var store = new RepoStore(key, did, clock ?? new TidClock());
        store.Rebuild(prev: null, Array.Empty<KeyValuePair<Cid, byte[]>>(), out _);
        return store;
    }

    /// <summary>Apply a batch of writes as a single commit and return the commit slice.</summary>
    public CommitResult ApplyWrites(IEnumerable<RepoWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var ops = new List<RepoOp>();
        var newRecordBlocks = new List<KeyValuePair<Cid, byte[]>>();

        foreach (RepoWrite write in writes)
        {
            string rkey = write.Rkey;
            string path;
            switch (write.Action)
            {
                case WriteAction.Create:
                case WriteAction.Update:
                    if (write.Record is null)
                        throw new ArgumentException($"{write.Action} of {write.Collection} requires a record.");
                    if (string.IsNullOrEmpty(rkey))
                        rkey = write.Action == WriteAction.Create
                            ? _clock.Next()
                            : throw new ArgumentException("Update requires an explicit rkey.");
                    path = $"{write.Collection}/{rkey}";
                    if (write.Action == WriteAction.Create && _records.ContainsKey(path))
                        throw new InvalidOperationException($"Record already exists: {path}");

                    byte[] bytes = DagCbor.Encode(write.Record);
                    Cid cid = Cid.ComputeV1(Cid.CodecDagCbor, bytes);
                    _records.TryGetValue(path, out Cid prevCid);
                    _records[path] = cid;
                    if (_blocks.TryAdd(cid, bytes))
                        newRecordBlocks.Add(new(cid, bytes));
                    ops.Add(new RepoOp(write.Action, path, cid, prevCid == default ? null : prevCid));
                    break;

                case WriteAction.Delete:
                    path = $"{write.Collection}/{rkey}";
                    if (!_records.TryGetValue(path, out Cid existing))
                        throw new InvalidOperationException($"Record not found: {path}");
                    _records.Remove(path);
                    ops.Add(new RepoOp(WriteAction.Delete, path, null, existing));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(writes));
            }
        }

        CommitResult result = Rebuild(prev: Head, newRecordBlocks, out IReadOnlyDictionary<Cid, byte[]> slice);
        return result with { Ops = ops, NewBlocks = slice };
    }

    /// <summary>Serialize the current repository (commit + MST nodes + records) as a CARv1 archive,
    /// with the commit as the sole root — the exact bytes <c>getRepo</c> returns.</summary>
    public byte[] ExportCar()
    {
        var blocks = new List<CarBlock>(_mstBlocks.Count + _records.Count + 1)
        {
            new(Head, _blocks[Head]),
        };
        foreach (KeyValuePair<Cid, byte[]> node in _mstBlocks)
            blocks.Add(new CarBlock(node.Key, node.Value));
        foreach (Cid recordCid in _records.Values.Distinct())
            blocks.Add(new CarBlock(recordCid, _blocks[recordCid]));
        return CarWriter.Write(new[] { Head }, blocks);
    }

    private CommitResult Rebuild(
        Cid? prev,
        IReadOnlyCollection<KeyValuePair<Cid, byte[]>> newRecordBlocks,
        out IReadOnlyDictionary<Cid, byte[]> slice)
    {
        Mst.MstResult mst = Mst.BuildFromPaths(_records);

        var newMstNodes = new Dictionary<Cid, byte[]>();
        foreach (KeyValuePair<Cid, byte[]> node in mst.Blocks)
        {
            if (_blocks.TryAdd(node.Key, node.Value))
                newMstNodes[node.Key] = node.Value;
        }
        _mstBlocks = mst.Blocks;

        string rev = _clock.Next();
        Commit commit = Commits.CreateSigned(_key, Did, mst.Root, rev, prev);
        Cid commitCid = Commits.ComputeCid(commit);
        byte[] commitBytes = Commits.EncodeSigned(commit);
        _blocks[commitCid] = commitBytes;
        Head = commitCid;
        Commit = commit;

        var sliceBlocks = new Dictionary<Cid, byte[]>(newMstNodes) { [commitCid] = commitBytes };
        foreach (KeyValuePair<Cid, byte[]> record in newRecordBlocks)
            sliceBlocks[record.Key] = record.Value;
        slice = sliceBlocks;

        return new CommitResult(commitCid, mst.Root, rev, prev, Array.Empty<RepoOp>(), sliceBlocks);
    }
}
