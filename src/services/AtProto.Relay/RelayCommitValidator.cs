using AtProto.Car;
using AtProto.Cbor;
using AtProto.Firehose;
using AtProto.Repo;

namespace AtProto.Relay;

/// <summary>Strict cryptographic validation for incoming <c>#commit</c> firehose frames.</summary>
public static class RelayCommitValidator
{
    public static bool TryValidate(RepoCommitEvent ev, out string error)
    {
        ArgumentNullException.ThrowIfNull(ev);
        try
        {
            Validate(ev);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Validate(RepoCommitEvent ev)
    {
        if (ev.Blocks.Length == 0)
            throw new InvalidDataException("commit carries no CAR blocks");

        CarArchive car = CarReader.Read(ev.Blocks);
        VerifyBlockIntegrity(car.Blocks);

        if (car.Roots.Count == 0)
            throw new InvalidDataException("commit CAR has no roots");
        if (car.Roots[0] != ev.Commit)
            throw new InvalidDataException($"commit CAR root {car.Roots[0].Encode()} does not match frame commit {ev.Commit.Encode()}");
        if (!car.TryGetBlock(ev.Commit, out byte[] commitBytes))
            throw new InvalidDataException($"commit block {ev.Commit.Encode()} is missing");

        Commit signed = DecodeCommit(commitBytes);
        if (signed.Did != ev.Did)
            throw new InvalidDataException($"signed commit DID {signed.Did} does not match frame DID {ev.Did}");
        if (signed.Rev != ev.Rev)
            throw new InvalidDataException($"signed commit rev {signed.Rev} does not match frame rev {ev.Rev}");
        if (Commits.ComputeCid(signed) != ev.Commit)
            throw new InvalidDataException("signed commit CID does not match frame commit CID");

        var blockMap = new Dictionary<Cid, byte[]>(car.Blocks.Count);
        foreach (CarBlock block in car.Blocks)
            blockMap[block.Cid] = block.Data;

        var leaves = new List<KeyValuePair<string, Cid>>();
        foreach (RepoRecord record in MstReader.Walk(blockMap, signed.Data))
            leaves.Add(new KeyValuePair<string, Cid>(record.Key, record.Cid));

        Mst.MstResult rebuilt = Mst.BuildFromPaths(leaves);
        if (rebuilt.Root != signed.Data)
            throw new InvalidDataException($"MST root mismatch: rebuilt {rebuilt.Root.Encode()}, commit.data {signed.Data.Encode()}");

        ValidateOps(ev, blockMap, leaves);
    }

    public static void VerifyBlockIntegrity(IEnumerable<CarBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        foreach (CarBlock block in blocks)
        {
            Cid recomputed = Cid.ComputeV1(block.Cid.Codec, block.Data);
            if (recomputed != block.Cid)
                throw new InvalidDataException($"CAR block CID mismatch: stored {block.Cid.Encode()}, recomputed {recomputed.Encode()}");
        }
    }

    private static Commit DecodeCommit(byte[] bytes)
    {
        object? obj = DagCbor.Decode(bytes);
        return new Commit(
            Did: obj.RequiredField("did").AsString(),
            Version: obj.RequiredField("version").AsInt64(),
            Data: obj.RequiredField("data").AsCid(),
            Rev: obj.RequiredField("rev").AsString(),
            Prev: obj.Field("prev") is Cid prev ? prev : null,
            Sig: obj.RequiredField("sig").AsBytes());
    }

    private static void ValidateOps(
        RepoCommitEvent ev,
        IReadOnlyDictionary<Cid, byte[]> blocks,
        IReadOnlyList<KeyValuePair<string, Cid>> leaves)
    {
        var tree = new Dictionary<string, Cid>(leaves.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, Cid> leaf in leaves)
            tree[leaf.Key] = leaf.Value;

        foreach (AtProto.Firehose.RepoOp op in ev.Ops)
        {
            if (string.IsNullOrEmpty(op.Path))
                throw new InvalidDataException("commit op has an empty path");

            switch (op.Action)
            {
                case RepoOpAction.Create:
                case RepoOpAction.Update:
                    if (op.Cid is not Cid cid)
                        throw new InvalidDataException($"commit op {op.Action} for {op.Path} has no CID");
                    if (!tree.TryGetValue(op.Path, out Cid actual))
                        throw new InvalidDataException($"commit op {op.Action} for {op.Path} is absent from the new MST");
                    if (actual != cid)
                        throw new InvalidDataException($"commit op {op.Action} for {op.Path} advertises {cid.Encode()} but MST has {actual.Encode()}");
                    if (!blocks.ContainsKey(cid))
                        throw new InvalidDataException($"record block {cid.Encode()} for {op.Path} is missing");
                    break;

                case RepoOpAction.Delete:
                    if (tree.ContainsKey(op.Path))
                        throw new InvalidDataException($"delete op for {op.Path} is still present in the new MST");
                    break;

                default:
                    throw new InvalidDataException($"unknown commit op action for {op.Path}");
            }
        }
    }
}
