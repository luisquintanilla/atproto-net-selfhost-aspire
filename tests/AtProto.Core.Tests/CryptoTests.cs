using AtProto;
using AtProto.Car;
using AtProto.Crypto;
using AtProto.Repo;
using Xunit.Abstractions;

namespace AtProto.Core.Tests;

// Validates the BCL-only (no third-party) signing stack: secp256k1 + P-256 ECDSA, low-S, compact
// 64-byte signatures, and did:key/multibase encoding. The strongest check verifies the REAL
// signature on the frozen Bluesky commit against the account's real published signing key.
public sealed class CryptoTests
{
    private readonly ITestOutputHelper _out;

    public CryptoTests(ITestOutputHelper output) => _out = output;

    // The real atproto.com signing key from did:plc:ewvi7nxzyoun6zhxrhs64oiz (#atproto),
    // fetched from plc.directory. secp256k1 ("zQ3sh" multibase prefix).
    private const string AtprotoComSigningKey = "did:key:zQ3shunBKsXixLxKtC5qeSG9E4J5RkGN57im31pcTzbNQnm5w";

    [Theory]
    [InlineData(EcKeyType.Secp256k1)]
    [InlineData(EcKeyType.P256)]
    public void Sign_then_verify_round_trips(EcKeyType type)
    {
        using EcKeypair key = EcKeypair.Generate(type);
        byte[] message = System.Text.Encoding.UTF8.GetBytes("the quick brown fox");

        byte[] sig = key.Sign(message);

        Assert.Equal(64, sig.Length);
        Assert.True(key.PublicKey.Verify(message, sig));
        Assert.False(key.PublicKey.Verify(System.Text.Encoding.UTF8.GetBytes("tampered"), sig));
    }

    [Theory]
    [InlineData(EcKeyType.Secp256k1)]
    [InlineData(EcKeyType.P256)]
    public void DidKey_round_trips_through_parse(EcKeyType type)
    {
        using EcKeypair key = EcKeypair.Generate(type);
        string didKey = key.DidKey;

        EcPublicKey parsed = EcPublicKey.Parse(didKey);

        Assert.Equal(type, parsed.KeyType);
        Assert.Equal(didKey, parsed.DidKey);
        Assert.Equal(key.PublicKey.CompressedPoint, parsed.CompressedPoint);

        // A signature made by the private key verifies through the re-parsed public key.
        byte[] message = System.Text.Encoding.UTF8.GetBytes("verify via did:key");
        Assert.True(parsed.Verify(message, key.Sign(message)));
    }

    [Theory]
    [InlineData(EcKeyType.Secp256k1)]
    [InlineData(EcKeyType.P256)]
    public void Private_key_export_import_round_trips(EcKeyType type)
    {
        using EcKeypair original = EcKeypair.Generate(type);
        byte[] der = original.ExportPrivateKey();

        using EcKeypair restored = EcKeypair.ImportPrivateKey(type, der);

        Assert.Equal(original.DidKey, restored.DidKey);
        byte[] message = System.Text.Encoding.UTF8.GetBytes("persisted key still signs");
        Assert.True(original.PublicKey.Verify(message, restored.Sign(message)));
    }

    [Fact]
    public void REAL_commit_signature_verifies_against_published_key()
    {
        // Reconstruct the unsigned commit from a real repo and verify its real signature against
        // atproto.com's published signing key. This exercises the whole chain end to end: canonical
        // dag-cbor of the unsigned commit + sha256 + secp256k1 low-S verify + multibase key parse.
        Repository repo = Repository.FromCar(CarReader.Read(Fixtures.Bytes("car/repo.car")));
        EcPublicKey signingKey = EcPublicKey.Parse(AtprotoComSigningKey);

        bool verified = Commits.Verify(signingKey, repo.Commit);

        // The recomputed commit CID must also equal the CAR root (proves our signed-commit encoding).
        Cid recomputedCommitCid = Commits.ComputeCid(repo.Commit);

        _out.WriteLine("=============== REAL COMMIT SIGNATURE ===============");
        _out.WriteLine($"signing key (did:key)  : {signingKey.DidKey}");
        _out.WriteLine($"key curve              : {signingKey.KeyType}");
        _out.WriteLine($"commit CID (CAR root)  : {repo.Root.Encode()}");
        _out.WriteLine($"recomputed commit CID  : {recomputedCommitCid.Encode()}");
        _out.WriteLine($"signature verifies     : {verified}");
        _out.WriteLine("====================================================");

        Assert.Equal(EcKeyType.Secp256k1, signingKey.KeyType);
        Assert.Equal(repo.Root.Encode(), recomputedCommitCid.Encode());
        Assert.True(verified, "real atproto.com commit signature failed to verify against its published key");
    }

    [Fact]
    public void We_can_sign_a_commit_over_a_rebuilt_mst_root()
    {
        // Forward direction: build an MST from a real repo's contents, sign a commit over that root
        // with our own generated key, and verify it round-trips (commit CID stable, sig verifies).
        Repository repo = Repository.FromCar(CarReader.Read(Fixtures.Bytes("car/repo.car")));
        Mst.MstResult mst = Mst.BuildFromPaths(
            repo.Records().Select(r => new KeyValuePair<string, Cid>(r.Key, r.Cid)));

        using EcKeypair key = EcKeypair.Generate(EcKeyType.Secp256k1);
        Commit commit = Commits.CreateSigned(key, did: "did:web:localhost", data: mst.Root, rev: "3luxntgof3v2a");

        Assert.True(Commits.Verify(key.PublicKey, commit));
        Assert.Equal(mst.Root, commit.Data);

        // A different key must not verify.
        using EcKeypair other = EcKeypair.Generate(EcKeyType.Secp256k1);
        Assert.False(Commits.Verify(other.PublicKey, commit));
    }
}
