using System.Linq;
using AtProto;
using AtProto.Car;
using AtProto.Repo;

namespace AtProto.Core.Tests;

// The strongest self-check in M1: decode a real 703KB Bluesky repo export and prove the
// entire Cid + Cbor + Car + Repo(MST) stack agrees with the frozen golden vectors, with no
// network and no human in the loop.
public sealed class RepoInteropTests
{
    private static CarArchive LoadRepoCar() => CarReader.Read(Fixtures.Bytes("car/repo.car"));

    [Fact]
    public void Every_block_cid_recomputes_from_its_bytes()
    {
        // Recompute sha-256 CID of every block and assert == stored CID. This validates
        // Cid + Cbor(header) + Car framing together against real data.
        LoadRepoCar().VerifyIntegrity();
    }

    [Fact]
    public void Root_cid_and_commit_match_the_manifest_head()
    {
        CarArchive car = LoadRepoCar();
        Repository repo = Repository.FromCar(car);

        string expectedCommit = Fixtures.Head.GetProperty("commitCid").GetString()!;
        string expectedRev = Fixtures.Head.GetProperty("rev").GetString()!;

        Assert.Equal(expectedCommit, car.Roots[0].Encode());
        Assert.Equal(expectedCommit, repo.Root.Encode());
        Assert.Equal(3, repo.Commit.Version);
        Assert.Equal(expectedRev, repo.Commit.Rev);
        Assert.Equal("did:plc:ewvi7nxzyoun6zhxrhs64oiz", repo.Commit.Did);
        Assert.NotEmpty(repo.Commit.Sig);
    }

    [Fact]
    public void Mst_walk_yields_sorted_keys_and_finds_the_sample_record()
    {
        Repository repo = Repository.FromCar(LoadRepoCar());
        List<RepoRecord> records = repo.Records().ToList();

        Assert.NotEmpty(records);

        // MST in-order traversal must yield keys in ascending byte order.
        for (int i = 1; i < records.Count; i++)
            Assert.True(string.CompareOrdinal(records[i - 1].Key, records[i].Key) < 0,
                $"MST keys out of order at {i}: '{records[i - 1].Key}' then '{records[i].Key}'");

        var sample = Fixtures.Manifest.RootElement.GetProperty("sampleRecords")[0];
        string uri = sample.GetProperty("uri").GetString()!;
        // uri = at://<did>/<collection>/<rkey>; the MST key is <collection>/<rkey>
        string key = string.Join('/', uri.Split('/')[^2..]);
        string expectedCid = sample.GetProperty("cid").GetString()!;

        RepoRecord found = Assert.Single(records, r => r.Key == key);
        Assert.Equal(expectedCid, found.Cid.Encode());
        Assert.Equal("actor.rpg.stats", found.Collection);
        Assert.Equal("self", found.Rkey);
    }

    [Fact]
    public void All_manifest_collections_are_present_in_the_repo()
    {
        Repository repo = Repository.FromCar(LoadRepoCar());
        HashSet<string> collections = repo.Records().Select(r => r.Collection).ToHashSet();

        foreach (var c in Fixtures.Manifest.RootElement.GetProperty("collections").EnumerateArray())
            Assert.Contains(c.GetString()!, collections);
    }
}
