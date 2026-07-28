using AtProto.Car;

namespace AtProto.Firehose.Tests;

// Offline, deterministic validation of the frame decoder against real firehose frames
// captured from the public relay (frozen under tests/fixtures/firehose).
public sealed class FrameDecoderTests
{
    private static readonly string FramesDir = Path.Combine(FixtureRoot(), "firehose");

    private static IEnumerable<byte[]> Frames() =>
        Directory.EnumerateFiles(FramesDir, "frame-*.bin")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllBytes);

    [Fact]
    public void Every_captured_frame_decodes_to_an_event_with_a_positive_seq()
    {
        int decoded = 0;
        foreach (byte[] frame in Frames())
        {
            RepoEvent? ev = FrameDecoder.Decode(frame);
            Assert.NotNull(ev);
            Assert.True(ev!.Seq > 0, "event seq should be positive");
            decoded++;
        }
        Assert.True(decoded >= 8, $"expected >= 8 frames, decoded {decoded}");
    }

    [Fact]
    public void Commit_frames_carry_a_readable_car_slice_and_well_formed_ops()
    {
        int commits = 0;
        foreach (byte[] frame in Frames())
        {
            if (FrameDecoder.Decode(frame) is not RepoCommitEvent commit)
                continue;

            commits++;
            Assert.False(string.IsNullOrEmpty(commit.Did));
            Assert.StartsWith("did:", commit.Did);
            Assert.False(string.IsNullOrEmpty(commit.Rev));

            // The blocks field is a CARv1 slice; it must parse and its integrity must hold.
            CarArchive car = CarReader.Read(commit.Blocks);
            car.VerifyIntegrity();

            foreach (RepoOp op in commit.Ops)
            {
                Assert.NotEqual(RepoOpAction.Unknown, op.Action);
                Assert.Contains('/', op.Path);
                Assert.False(string.IsNullOrEmpty(op.Collection));
                Assert.False(string.IsNullOrEmpty(op.Rkey));
                // create/update carry the new record CID inside the slice
                if (op.Action is RepoOpAction.Create or RepoOpAction.Update)
                {
                    Assert.NotNull(op.Cid);
                    Assert.True(car.TryGetBlock(op.Cid!.Value, out _),
                        $"record block {op.Cid} for {op.Path} missing from CAR slice");
                }
            }
        }
        Assert.True(commits >= 1, "expected at least one #commit frame in the capture");
    }

    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "atproto-net-selfhost-aspire.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root");
    }
}
