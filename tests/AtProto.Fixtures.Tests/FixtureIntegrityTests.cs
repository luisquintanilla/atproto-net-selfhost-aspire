using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtProto.Fixtures.Tests;

// T0 acceptance: the golden-vectors harness produces a frozen, internally consistent
// fixture set. These tests verify the fixtures are present and well-formed with ZERO
// dependency on the codec libraries (which M1 builds and which will consume these files).
public sealed class FixtureIntegrityTests
{
    private static readonly string FixturesDir = LocateFixtures();
    private static readonly JsonDocument Manifest = LoadManifest();

    [Fact]
    public void Manifest_lists_files_and_every_sha256_matches()
    {
        var files = Manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.NotEmpty(files);

        foreach (var f in files)
        {
            var rel = f.GetProperty("file").GetString()!;
            var expectedHash = f.GetProperty("sha256").GetString()!;
            var expectedBytes = f.GetProperty("bytes").GetInt64();

            var path = Path.Combine(FixturesDir, rel);
            Assert.True(File.Exists(path), $"missing fixture file: {rel}");

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(expectedBytes, bytes.Length);
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void Head_commit_is_a_cidv1_dagcbor_sha256()
    {
        var head = Manifest.RootElement.GetProperty("head");
        var cid = head.GetProperty("commitCid").GetString()!;
        var rev = head.GetProperty("rev").GetString()!;

        // base32 multibase CIDv1 / dag-cbor / sha-256 always renders with this prefix
        Assert.StartsWith("bafyrei", cid);
        Assert.False(string.IsNullOrWhiteSpace(rev));
    }

    [Fact]
    public void Repo_car_has_a_valid_carv1_header()
    {
        AssertLooksLikeCar(Path.Combine(FixturesDir, "car", "repo.car"));
    }

    [Fact]
    public void Record_proof_car_has_a_valid_carv1_header()
    {
        var rel = Manifest.RootElement.GetProperty("recordProofCar").GetString();
        Assert.False(string.IsNullOrEmpty(rel), "manifest has no recordProofCar");
        AssertLooksLikeCar(Path.Combine(FixturesDir, rel!));
    }

    [Fact]
    public void Firehose_frames_captured_and_start_with_a_cbor_map_header()
    {
        var frames = Manifest.RootElement.GetProperty("firehoseFrames").EnumerateArray().ToArray();
        Assert.True(frames.Length >= 8, $"expected >= 8 firehose frames, got {frames.Length}");

        foreach (var fr in frames)
        {
            var path = Path.Combine(FixturesDir, fr.GetProperty("file").GetString()!);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 0, "empty firehose frame");
            // each frame is header-CBOR || payload-CBOR; the header {op,t} is a CBOR map (major type 5)
            Assert.True(IsCborMajorType(bytes[0], 5), $"frame does not start with a CBOR map: 0x{bytes[0]:X2}");
        }
    }

    // ---- helpers (no codec dependency) ----

    private static void AssertLooksLikeCar(string path)
    {
        Assert.True(File.Exists(path), $"missing CAR: {path}");
        var bytes = File.ReadAllBytes(path);

        // CARv1 = uvarint(headerLen) || headerCbor(map { version:1, roots:[...] }) || blocks...
        int headerLen = ReadUvarint(bytes, 0, out int consumed);
        Assert.InRange(headerLen, 1, 1 << 20);
        Assert.True(consumed + headerLen <= bytes.Length, "declared CAR header length exceeds file");

        var header = bytes.AsSpan(consumed, headerLen);
        Assert.True(IsCborMajorType(header[0], 5), $"CAR header is not a CBOR map: 0x{header[0]:X2}");
        Assert.True(Contains(header, "version"u8), "CAR header missing 'version' key");
        Assert.True(Contains(header, "roots"u8), "CAR header missing 'roots' key");
    }

    private static bool IsCborMajorType(byte b, int majorType) => (b >> 5) == majorType;

    private static int ReadUvarint(byte[] data, int offset, out int consumed)
    {
        int result = 0, shift = 0;
        consumed = 0;
        while (offset + consumed < data.Length)
        {
            byte b = data[offset + consumed++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) break;
        }
        throw new FormatException("invalid uvarint");
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle) >= 0;

    private static string LocateFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "atproto-net-selfhost-aspire.slnx")))
                return Path.Combine(dir.FullName, "tests", "fixtures");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (atproto-net-selfhost-aspire.slnx)");
    }

    private static JsonDocument LoadManifest()
    {
        var path = Path.Combine(FixturesDir, "manifest.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
