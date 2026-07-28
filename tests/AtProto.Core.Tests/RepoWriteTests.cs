using AtProto;
using AtProto.Car;
using AtProto.Crypto;
using AtProto.Repo;
using Xunit.Abstractions;

namespace AtProto.Core.Tests;

// TID codec/generator + the mutable RepoStore write path: create/update/delete rebuild the MST,
// re-sign the commit, and export a getRepo-style CARv1 that our own reader validates and whose
// signature verifies with our own key. Real record keys from the frozen repo are used as a TID oracle.
public sealed class RepoWriteTests
{
    private readonly ITestOutputHelper _out;

    public RepoWriteTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Tid_zero_and_roundtrip()
    {
        Assert.Equal("2222222222222", Tid.Encode(0));
        Assert.Equal(0UL, Tid.Decode("2222222222222"));

        foreach (ulong v in new ulong[] { 1, 31, 32, 1024, 1_000_000, ulong.MaxValue >> 1 })
            Assert.Equal(v, Tid.Decode(Tid.Encode(v)));
    }

    [Fact]
    public void Tid_decodes_real_record_keys_from_the_frozen_repo()
    {
        CarArchive car = CarReader.Read(Fixtures.Bytes("car/repo.car"));
        List<string> rkeys = Repository.FromCar(car).Records()
            .Select(r => r.Rkey)
            .Where(Tid.IsValid)
            .Distinct()
            .ToList();

        Assert.NotEmpty(rkeys);
        foreach (string rkey in rkeys)
        {
            // Round-trip through the integer form is the codec oracle. (Not every syntactically
            // valid TID encodes a real timestamp — some collections use hand-picked rkeys.)
            Assert.Equal(rkey, Tid.Encode(Tid.Decode(rkey)));
        }
        _out.WriteLine($"validated {rkeys.Count} real TID rkeys");

        // A freshly minted TID decodes back to (about) now.
        DateTimeOffset minted = Tid.ToTimestamp(new TidClock().NextValue());
        Assert.InRange((DateTimeOffset.UtcNow - minted).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TidClock_is_strictly_monotonic_and_carries_its_clock_id()
    {
        var clock = new TidClock(clockId: 42);
        Assert.Equal(42, clock.ClockId);

        ulong prev = 0;
        for (int i = 0; i < 5000; i++)
        {
            ulong v = clock.NextValue();
            Assert.True(v > prev, "TID stream must be strictly increasing");
            prev = v;
        }
    }

    [Fact]
    public void Empty_repo_has_a_verifiable_genesis_commit()
    {
        using EcKeypair key = EcKeypair.Generate(EcKeyType.Secp256k1);
        var repo = RepoStore.CreateEmpty(key, "did:web:localhost");

        Assert.True(Commits.Verify(key.PublicKey, repo.Commit));
        Assert.Null(repo.Commit.Prev);
        Assert.Empty(repo.Records);

        // Genesis exports a valid CAR containing just the empty tree.
        CarArchive car = CarReader.Read(repo.ExportCar());
        car.VerifyIntegrity();
        Assert.Equal(repo.Head, car.Roots.Single());
        Assert.Empty(Repository.FromCar(car).Records());
    }

    [Fact]
    public void Create_update_delete_produce_correct_ops_and_signed_commits()
    {
        using EcKeypair key = EcKeypair.Generate(EcKeyType.Secp256k1);
        var repo = RepoStore.CreateEmpty(key, "did:web:localhost");
        Cid genesis = repo.Head;

        // CREATE
        CommitResult created = repo.ApplyWrites(new[] { RepoWrite.Create("com.example.status", "self", Status("👍")) });
        Assert.Equal(genesis, created.Prev);
        RepoOp createOp = Assert.Single(created.Ops);
        Assert.Equal(WriteAction.Create, createOp.Action);
        Assert.Equal("com.example.status/self", createOp.Path);
        Assert.NotNull(createOp.Cid);
        Assert.Null(createOp.Prev);
        Assert.True(Commits.Verify(key.PublicKey, repo.Commit));
        Assert.Contains(repo.Head, created.NewBlocks.Keys);            // commit block in the slice
        Assert.Contains(createOp.Cid!.Value, created.NewBlocks.Keys);  // record block in the slice

        // UPDATE — same key, new value CID, op.Prev == old CID
        Cid oldCid = createOp.Cid!.Value;
        CommitResult updated = repo.ApplyWrites(new[] { RepoWrite.Update("com.example.status", "self", Status("🎉")) });
        RepoOp updateOp = Assert.Single(updated.Ops);
        Assert.Equal(WriteAction.Update, updateOp.Action);
        Assert.NotEqual(oldCid, updateOp.Cid);
        Assert.Equal(oldCid, updateOp.Prev);
        Assert.Equal(updateOp.Cid, repo.Records["com.example.status/self"]);
        Assert.Equal(repo.Head, updated.Commit);
        Assert.True(updated.Rev.CompareTo(created.Rev) > 0, "rev must increase");

        // DELETE — record gone, op.Prev == current CID
        CommitResult deleted = repo.ApplyWrites(new[] { RepoWrite.Delete("com.example.status", "self") });
        RepoOp deleteOp = Assert.Single(deleted.Ops);
        Assert.Equal(WriteAction.Delete, deleteOp.Action);
        Assert.Null(deleteOp.Cid);
        Assert.Equal(updateOp.Cid, deleteOp.Prev);
        Assert.Empty(repo.Records);
        Assert.True(Commits.Verify(key.PublicKey, repo.Commit));
    }

    [Fact]
    public void Minted_rkeys_are_tids_and_getRepo_car_roundtrips_all_records()
    {
        using EcKeypair key = EcKeypair.Generate(EcKeyType.Secp256k1);
        var repo = RepoStore.CreateEmpty(key, "did:web:localhost");

        var minted = new List<string>();
        for (int i = 0; i < 25; i++)
        {
            CommitResult r = repo.ApplyWrites(new[] { RepoWrite.Create("com.example.status", Status($"n{i}")) });
            string rkey = Assert.Single(r.Ops).Path.Split('/')[1];
            Assert.True(Tid.IsValid(rkey), $"minted rkey '{rkey}' is not a valid TID");
            minted.Add(rkey);
        }
        Assert.Equal(25, minted.Distinct().Count());

        // Export as getRepo would, re-read with our reader, verify integrity + every record.
        CarArchive car = CarReader.Read(repo.ExportCar());
        car.VerifyIntegrity();
        Assert.Equal(repo.Head, car.Roots.Single());

        List<RepoRecord> records = Repository.FromCar(car).Records().ToList();
        Assert.Equal(repo.Records.Count, records.Count);
        foreach (RepoRecord record in records)
            Assert.Equal(repo.Records[record.Key], record.Cid);

        Assert.True(Commits.Verify(key.PublicKey, Repository.FromCar(car).Commit));
        _out.WriteLine($"getRepo CAR: {records.Count} records under root {car.Roots.Single()}");
    }

    private static Dictionary<string, object?> Status(string emoji) => new(StringComparer.Ordinal)
    {
        ["$type"] = "com.example.status",
        ["status"] = emoji,
        ["createdAt"] = "2026-07-28T12:00:00.000Z",
    };
}
