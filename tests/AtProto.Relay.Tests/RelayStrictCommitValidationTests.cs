using System.Reflection;
using AtProto.Car;
using AtProto.Firehose;
using AtProto.Repo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AtProto.Relay.Tests;

public sealed class RelayStrictCommitValidationTests
{
    private readonly ITestOutputHelper _out;

    public RelayStrictCommitValidationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void MstReader_walks_real_repo_and_rebuilds_commit_data()
    {
        CarArchive car = LoadRepoCar();
        Repository repo = Repository.FromCar(car);

        List<RepoRecord> records = MstReader.Walk(car.Blocks, repo.Commit.Data).ToList();
        Mst.MstResult rebuilt = Mst.BuildFromPaths(records.Select(r => new KeyValuePair<string, Cid>(r.Key, r.Cid)));

        _out.WriteLine($"records               : {records.Count}");
        _out.WriteLine($"rebuilt MST node blocks: {rebuilt.Blocks.Count}");
        _out.WriteLine($"commit.data           : {repo.Commit.Data.Encode()}");
        _out.WriteLine($"rebuilt root          : {rebuilt.Root.Encode()}");

        Assert.Equal(462, rebuilt.Blocks.Count);
        Assert.Equal(repo.Commit.Data, rebuilt.Root);
    }

    [Fact]
    public void Block_cid_integrity_passes_real_blocks_and_fails_tampered_block()
    {
        CarArchive car = LoadRepoCar();
        RelayCommitValidator.VerifyBlockIntegrity(car.Blocks);

        var tampered = car.Blocks.ToArray();
        byte[] data = tampered[0].Data.ToArray();
        data[^1] ^= 0x01;
        tampered[0] = new CarBlock(tampered[0].Cid, data);

        Assert.Throws<InvalidDataException>(() => RelayCommitValidator.VerifyBlockIntegrity(tampered));
    }

    [Fact]
    public void Strict_validation_accepts_real_repo_commit_and_rejects_missing_mst_root()
    {
        RepoCommitEvent valid = RealRepoCommit();

        Assert.True(RelayCommitValidator.TryValidate(valid, out string validError), validError);

        CarArchive car = CarReader.Read(valid.Blocks);
        Repository repo = Repository.FromCar(car);
        byte[] missingRootCar = CarWriter.Write(
            new[] { valid.Commit },
            car.Blocks.Where(b => b.Cid != repo.Commit.Data));

        RepoCommitEvent invalid = valid with { Blocks = missingRootCar };
        Assert.False(RelayCommitValidator.TryValidate(invalid, out string invalidError));
        Assert.Contains("missing", invalidError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Relay_process_accepts_valid_commit_and_reemits_it()
    {
        string cursorDir = Path.Combine(Fixtures.ProjectDir, ".test-cursors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cursorDir);
        try
        {
            var service = new RelayService(
                new RelayOptions { CursorDir = cursorDir, StrictCommitValidation = true },
                new HostRegistry(),
                NullLogger<RelayService>.Instance);

            RepoCommitEvent valid = RealRepoCommit();
            await Process(service, "https://fixture.example", valid);

            Assert.Equal(1, service.Firehose.CurrentSeq);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (SequencedFrame frame in service.Firehose.Subscribe(0, cts.Token))
            {
                Assert.Equal(1, frame.Seq);
                RepoCommitEvent emitted = Assert.IsType<RepoCommitEvent>(FrameDecoder.Decode(frame.Frame));
                Assert.Equal(valid.Did, emitted.Did);
                Assert.Equal(valid.Commit, emitted.Commit);
                Assert.Equal(1, emitted.Seq);
                break;
            }
        }
        finally
        {
            if (Directory.Exists(cursorDir))
                Directory.Delete(cursorDir, recursive: true);
        }
    }

    [Fact]
    public async Task Relay_process_rejects_invalid_commit_without_reemitting()
    {
        string cursorDir = Path.Combine(Fixtures.ProjectDir, ".test-cursors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cursorDir);
        try
        {
            var service = new RelayService(
                new RelayOptions { CursorDir = cursorDir, StrictCommitValidation = true },
                new HostRegistry(),
                NullLogger<RelayService>.Instance);

            RepoCommitEvent valid = RealRepoCommit();
            byte[] tampered = valid.Blocks.ToArray();
            tampered[^1] ^= 0x01;

            await Process(service, "https://fixture.example", valid with { Blocks = tampered });

            Assert.Equal(0, service.Firehose.CurrentSeq);
        }
        finally
        {
            if (Directory.Exists(cursorDir))
                Directory.Delete(cursorDir, recursive: true);
        }
    }

    private static CarArchive LoadRepoCar() => CarReader.Read(Fixtures.Bytes("car/repo.car"));

    private static RepoCommitEvent RealRepoCommit()
    {
        byte[] blocks = Fixtures.Bytes("car/repo.car");
        CarArchive car = CarReader.Read(blocks);
        Repository repo = Repository.FromCar(car);
        RepoRecord first = MstReader.Walk(car.Blocks, repo.Commit.Data).First();

        return new RepoCommitEvent(
            Seq: 42,
            Did: repo.Commit.Did,
            Rev: repo.Commit.Rev,
            Commit: repo.Root,
            Since: repo.Commit.Prev?.Encode(),
            PrevData: repo.Commit.Prev,
            Blocks: blocks,
            Ops: new[] { new AtProto.Firehose.RepoOp(RepoOpAction.Create, first.Key, first.Cid, null) },
            Time: DateTimeOffset.UtcNow);
    }

    private static async Task Process(RelayService service, string url, RepoCommitEvent ev)
    {
        MethodInfo method = typeof(RelayService).GetMethod("ProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(RelayService), "ProcessAsync");
        var host = new HostState(url);
        var task = (Task?)method.Invoke(service, new object[] { url, host, ev, CancellationToken.None })
            ?? throw new InvalidOperationException("ProcessAsync did not return a task");
        await task;
    }
}
