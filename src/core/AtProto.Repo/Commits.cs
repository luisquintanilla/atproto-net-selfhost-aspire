using AtProto.Cbor;
using AtProto.Crypto;

namespace AtProto.Repo;

/// <summary>
/// Builds, signs, and verifies AT Protocol repository commits. A commit is signed by serializing
/// the <b>unsigned</b> object (all fields except <c>sig</c>) as canonical dag-cbor and signing
/// <c>sha256</c> of those bytes; the signature is stored raw in the <c>sig</c> field. The commit's
/// own CID is the dag-cbor CID of the <b>signed</b> object.
/// </summary>
public static class Commits
{
    /// <summary>The repository format version this builder emits (repo format v3).</summary>
    public const long CurrentVersion = 3;

    /// <summary>Canonical dag-cbor of the unsigned commit (the exact bytes that get signed).</summary>
    public static byte[] EncodeUnsigned(string did, Cid data, string rev, Cid? prev, long version = CurrentVersion) =>
        DagCbor.Encode(BuildMap(did, version, data, rev, prev, sig: null));

    /// <summary>Canonical dag-cbor of a signed commit.</summary>
    public static byte[] EncodeSigned(Commit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return DagCbor.Encode(BuildMap(commit.Did, commit.Version, commit.Data, commit.Rev, commit.Prev, commit.Sig));
    }

    /// <summary>The CID that addresses a signed commit (dag-cbor, sha-256).</summary>
    public static Cid ComputeCid(Commit commit) =>
        Cid.ComputeV1(Cid.CodecDagCbor, EncodeSigned(commit));

    /// <summary>Build a commit over <paramref name="data"/> (the MST root) and sign it with <paramref name="key"/>.</summary>
    public static Commit CreateSigned(EcKeypair key, string did, Cid data, string rev, Cid? prev = null, long version = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] unsigned = EncodeUnsigned(did, data, rev, prev, version);
        byte[] sig = key.Sign(unsigned);
        return new Commit(did, version, data, rev, prev, sig);
    }

    /// <summary>Verify a commit's signature against a public key (low-S enforced by the verifier).</summary>
    public static bool Verify(EcPublicKey key, Commit commit)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(commit);
        byte[] unsigned = EncodeUnsigned(commit.Did, commit.Data, commit.Rev, commit.Prev, commit.Version);
        return key.Verify(unsigned, commit.Sig);
    }

    private static Dictionary<string, object?> BuildMap(string did, long version, Cid data, string rev, Cid? prev, byte[]? sig)
    {
        // Keys are written in any order; the canonical dag-cbor encoder sorts them.
        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["did"] = did,
            ["version"] = version,
            ["data"] = data,
            ["rev"] = rev,
            ["prev"] = prev is Cid p ? p : null,
        };
        if (sig is not null)
            map["sig"] = sig;
        return map;
    }
}
