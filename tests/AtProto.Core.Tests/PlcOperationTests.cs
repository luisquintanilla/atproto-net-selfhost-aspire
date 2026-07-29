using AtProto.Crypto;
using AtProto.Identity;

namespace AtProto.Core.Tests;

public sealed class PlcOperationTests
{
    [Fact]
    public void Build_sign_derive_and_verify_genesis_round_trips_offline()
    {
        using EcKeypair rotationKey = EcKeypair.Generate(EcKeyType.Secp256k1);
        using EcKeypair signingKey = EcKeypair.Generate(EcKeyType.Secp256k1);
        PlcUnsignedOperation unsigned = PlcOperations.CreateGenesis(
            rotationKey.DidKey,
            signingKey.DidKey,
            "alice.example.com",
            "https://pds.example.com");

        PlcSignedOperation signed = PlcOperations.Sign(unsigned, bytes => rotationKey.Sign(bytes));
        string did = PlcOperations.DeriveDid(signed);

        Assert.Matches("^did:plc:[a-z2-7]{24}$", did);
        Assert.True(PlcOperations.VerifySignature(signed, rotationKey.DidKey));
        Assert.Equal(did, PlcOperations.DeriveDid(signed));
    }

    [Fact]
    public void Signature_verification_fails_for_other_key_or_tampered_operation()
    {
        using EcKeypair rotationKey = EcKeypair.Generate(EcKeyType.Secp256k1);
        using EcKeypair otherKey = EcKeypair.Generate(EcKeyType.Secp256k1);
        PlcUnsignedOperation unsigned = PlcOperations.CreateGenesis(
            rotationKey.DidKey,
            rotationKey.DidKey,
            "alice.example.com",
            "https://pds.example.com");
        PlcSignedOperation signed = PlcOperations.Sign(unsigned, bytes => rotationKey.Sign(bytes));

        PlcSignedOperation withOtherRotationKey = signed with { RotationKeys = new[] { otherKey.DidKey } };
        PlcSignedOperation tampered = signed with { AlsoKnownAs = new[] { "at://mallory.example.com" } };

        Assert.False(PlcOperations.VerifySignature(signed, otherKey.DidKey));
        Assert.False(PlcOperations.VerifySignature(withOtherRotationKey, otherKey.DidKey));
        Assert.False(PlcOperations.VerifySignature(tampered, rotationKey.DidKey));
    }

    [Fact]
    public void Published_bsky_app_genesis_vector_derives_expected_did_and_verifies()
    {
        const string expectedDid = "did:plc:z72i7hdynmk6r22z27h6tvur";
        const string rotationKey = "did:key:zQ3shhCGUqDKjStzuDxPkTxN6ujddP4RkEKJJouJGRRkaLGbg";
        var signed = new PlcSignedOperation(
            new[]
            {
                rotationKey,
                "did:key:zQ3shpKnbdPx3g3CmPf5cRVTPe1HtSwVn5ish3wSnDPQCbLJK",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["atproto"] = "did:key:zQ3shXjHeiBuRCKmM36cuYnm7YEMzhGnCmCyW92sRJ9pribSF",
            },
            new[] { "at://bluesky-team.bsky.social" },
            new Dictionary<string, PlcService>(StringComparer.Ordinal)
            {
                ["atproto_pds"] = new("AtprotoPersonalDataServer", "https://bsky.social"),
            },
            "9NuYV7AqwHVTc0YuWzNV3CJafsSZWH7qCxHRUIP2xWlB-YexXC1OaYAnUayiCXLVzRQ8WBXIqF-SvZdNalwcjA");

        Assert.Equal(expectedDid, PlcOperations.DeriveDid(signed));
        Assert.Contains(signed.RotationKeys, key => PlcOperations.VerifySignature(signed, key));
    }
}
