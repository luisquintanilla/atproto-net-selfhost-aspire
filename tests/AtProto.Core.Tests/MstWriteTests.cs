using System.Linq;
using System.Text;
using AtProto;
using AtProto.Car;
using AtProto.Cbor;
using AtProto.Repo;
using Xunit.Abstractions;

namespace AtProto.Core.Tests;

// M3 write-side proof. The MST root CID is a pure function of the (key -> value CID) content, so
// rebuilding the tree of the frozen 703KB Bluesky repo and reproducing its commit `data` CID over
// hundreds of nodes is an extremely strong oracle: every rule (depth, empty-node pruning, prefix
// compression, canonical dag-cbor, CID compute) must be exactly right for the root to match.
public sealed class MstWriteTests
{
    private readonly ITestOutputHelper _out;

    public MstWriteTests(ITestOutputHelper output) => _out = output;

    private static CarArchive LoadRepoCar() => CarReader.Read(Fixtures.Bytes("car/repo.car"));

    [Theory]
    [InlineData("2653ae71", 0)]
    [InlineData("blue", 1)]
    [InlineData("app.bsky.feed.post/454397e440ec", 4)]
    [InlineData("app.bsky.feed.post/9adeb165882c", 8)]
    public void LeadingZerosOnHash_matches_spec_examples(string key, int expectedDepth)
    {
        // Worked examples straight from atproto.com/specs/repository (fanout-4, 2-bit chunks).
        Assert.Equal(expectedDepth, Mst.LeadingZerosOnHash(Encoding.UTF8.GetBytes(key)));
    }

    [Fact]
    public void Canonical_encoder_reproduces_every_real_repo_block()
    {
        // Encode(Decode(block)) == block, byte-for-byte, for every canonical dag-cbor block in a
        // real repo export. This hard-validates the canonical encoder (key order, prefix ints,
        // CID tag framing) against ~1000+ blocks produced by the reference TS implementation.
        CarArchive car = LoadRepoCar();

        int checkedBlocks = 0;
        var mismatches = new List<string>();
        foreach (CarBlock block in car.Blocks)
        {
            if (block.Cid.Codec != Cid.CodecDagCbor)
                continue; // raw blocks (e.g. blobs) are not dag-cbor

            byte[] reencoded = DagCbor.Encode(DagCbor.Decode(block.Data));
            checkedBlocks++;
            if (!reencoded.AsSpan().SequenceEqual(block.Data))
                mismatches.Add(block.Cid.Encode());
        }

        _out.WriteLine($"re-encoded {checkedBlocks} dag-cbor blocks; {mismatches.Count} mismatch(es)");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count}/{checkedBlocks} blocks did not round-trip; first: {mismatches.FirstOrDefault()}");
    }

    [Fact]
    public void Rebuilt_mst_reproduces_every_read_side_node_block()
    {
        // Beyond the root: every node block we emit must be byte-identical to the one in the real
        // repo. This localizes any construction bug to a specific node instead of only the root.
        CarArchive car = LoadRepoCar();
        Repository repo = Repository.FromCar(car);

        Mst.MstResult built = Mst.BuildFromPaths(
            repo.Records().Select(r => new KeyValuePair<string, Cid>(r.Key, r.Cid)));

        int matched = 0;
        foreach (KeyValuePair<Cid, byte[]> node in built.Blocks)
        {
            Assert.True(car.TryGetBlock(node.Key, out byte[] expected),
                $"emitted MST node {node.Key.Encode()} is absent from the real repo");
            Assert.True(node.Value.AsSpan().SequenceEqual(expected),
                $"emitted MST node {node.Key.Encode()} bytes differ from the real repo");
            matched++;
        }

        _out.WriteLine($"rebuilt and byte-matched {matched} MST node blocks");
        Assert.True(matched > 1, "expected a multi-node MST for a 703KB repo");
    }

    [Fact]
    public void ROOT_CID_GATE_rebuilt_mst_root_matches_reference_commit_data()
    {
        // ===================== THE M3 HUMAN GATE =====================
        // Rebuild the MST purely from (key -> value CID) pairs and prove our root CID equals the
        // reference commit.data. Match == our write-side MST is correct; safe to build PDS writes.
        CarArchive car = LoadRepoCar();
        Repository repo = Repository.FromCar(car);

        var pairs = repo.Records()
            .Select(r => new KeyValuePair<string, Cid>(r.Key, r.Cid))
            .ToList();

        Mst.MstResult built = Mst.BuildFromPaths(pairs);

        Cid reference = repo.Commit.Data;
        _out.WriteLine("================ MST ROOT-CID GATE ================");
        _out.WriteLine($"records in repo        : {pairs.Count}");
        _out.WriteLine($"MST node blocks built  : {built.Blocks.Count}");
        _out.WriteLine($"reference commit.data  : {reference.Encode()}");
        _out.WriteLine($"our rebuilt MST root   : {built.Root.Encode()}");
        _out.WriteLine($"MATCH                  : {built.Root == reference}");
        _out.WriteLine("===================================================");

        Assert.Equal(reference.Encode(), built.Root.Encode());
    }
}
