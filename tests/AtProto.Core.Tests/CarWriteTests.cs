using AtProto;
using AtProto.Car;
using AtProto.Repo;
using Xunit.Abstractions;

namespace AtProto.Core.Tests;

// Validates the CARv1 writer against a real 703KB Bluesky export: structural round-trip (write ->
// read -> same roots/blocks/records), and — since block order is preserved and the header is
// canonical dag-cbor — a full-file byte-for-byte round-trip.
public sealed class CarWriteTests
{
    private readonly ITestOutputHelper _out;

    public CarWriteTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Written_car_reads_back_with_same_roots_blocks_and_records()
    {
        byte[] original = Fixtures.Bytes("car/repo.car");
        CarArchive source = CarReader.Read(original);

        byte[] written = CarWriter.Write(source.Roots, source.Blocks);
        CarArchive reloaded = CarReader.Read(written);

        reloaded.VerifyIntegrity();
        Assert.Equal(source.Roots, reloaded.Roots);
        Assert.Equal(source.Blocks.Count, reloaded.Blocks.Count);

        // Records enumerated through the MST must match exactly.
        List<RepoRecord> before = Repository.FromCar(source).Records().ToList();
        List<RepoRecord> after = Repository.FromCar(reloaded).Records().ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Key, after[i].Key);
            Assert.Equal(before[i].Cid, after[i].Cid);
        }
    }

    [Fact]
    public void Full_file_byte_roundtrips_when_block_order_is_preserved()
    {
        byte[] original = Fixtures.Bytes("car/repo.car");
        CarArchive source = CarReader.Read(original);

        byte[] written = CarWriter.Write(source.Roots, source.Blocks);

        _out.WriteLine($"original {original.Length} bytes, rewritten {written.Length} bytes");
        Assert.Equal(original.Length, written.Length);
        Assert.True(original.AsSpan().SequenceEqual(written), "rewritten CAR is not byte-identical to the original");
    }
}
