using AtProto;

namespace AtProto.Core.Tests;

public sealed class CidTests
{
    [Fact]
    public void Decode_then_encode_round_trips_the_head_commit_cid()
    {
        string expected = Fixtures.Head.GetProperty("commitCid").GetString()!;

        Cid cid = Cid.Decode(expected);

        Assert.Equal(expected, cid.Encode());
        Assert.Equal(1, cid.Version);
        Assert.Equal(Cid.CodecDagCbor, cid.Codec);
        Assert.Equal(Cid.HashSha2_256, cid.HashType);
        Assert.Equal(32, cid.Digest.Length);
    }

    [Fact]
    public void ComputeV1_reproduces_a_known_dagcbor_cid_from_its_bytes()
    {
        // The record-proof CAR contains exactly the actor.rpg.stats/self record block,
        // whose CID the manifest records. Recomputing it from bytes must match.
        var car = Car.CarReader.Read(Fixtures.Bytes("car/record-proof.actor_rpg_stats.self.car"));
        string expectedRecordCid = Fixtures.Manifest.RootElement
            .GetProperty("sampleRecords")[0].GetProperty("cid").GetString()!;

        Cid expected = Cid.Decode(expectedRecordCid);
        byte[] block = car.GetBlock(expected);

        Assert.Equal(expected, Cid.ComputeV1(Cid.CodecDagCbor, block));
    }

    [Fact]
    public void Base32_and_varint_round_trip()
    {
        byte[] data = [0x01, 0x71, 0x12, 0x20, .. Enumerable.Range(0, 32).Select(i => (byte)i)];
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));

        Span<byte> buf = stackalloc byte[10];
        int n = Varint.WriteUnsigned(0x7118A2, buf);
        Assert.Equal(0x7118A2ul, Varint.ReadUnsigned(buf[..n], out int read));
        Assert.Equal(n, read);
    }
}
