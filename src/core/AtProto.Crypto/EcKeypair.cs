using System.Numerics;
using System.Security.Cryptography;

namespace AtProto.Crypto;

/// <summary>
/// An AT Protocol signing keypair over secp256k1 or P-256, built on the BCL
/// <see cref="System.Security.Cryptography.ECDsa"/>. Produces compact 64-byte (r‖s) signatures
/// over <c>sha256(message)</c>, normalized to low-S — the format atproto commits use.
/// </summary>
public sealed class EcKeypair : IDisposable
{
    private readonly CurveSpec _spec;
    private readonly ECDsa _ecdsa;

    private EcKeypair(CurveSpec spec, ECDsa ecdsa)
    {
        _spec = spec;
        _ecdsa = ecdsa;
    }

    /// <summary>The curve of this keypair.</summary>
    public EcKeyType KeyType => _spec.KeyType;

    /// <summary>The public half of this keypair.</summary>
    public EcPublicKey PublicKey
    {
        get
        {
            ECParameters p = _ecdsa.ExportParameters(includePrivateParameters: false);
            var x = new BigInteger(p.Q.X!, isUnsigned: true, isBigEndian: true);
            var y = new BigInteger(p.Q.Y!, isUnsigned: true, isBigEndian: true);
            return new EcPublicKey(_spec, x, y);
        }
    }

    /// <summary>The public key's <c>did:key</c> form (used as the atproto signing key identifier).</summary>
    public string DidKey => PublicKey.DidKey;

    /// <summary>Generate a fresh random keypair on the given curve.</summary>
    public static EcKeypair Generate(EcKeyType type)
    {
        CurveSpec spec = CurveSpec.For(type);
        return new EcKeypair(spec, ECDsa.Create(spec.Curve));
    }

    /// <summary>Restore a keypair from SEC1 EC private-key DER produced by <see cref="ExportPrivateKey"/>.</summary>
    public static EcKeypair ImportPrivateKey(EcKeyType type, ReadOnlySpan<byte> sec1Der)
    {
        CurveSpec spec = CurveSpec.For(type);
        var ecdsa = ECDsa.Create(spec.Curve);
        ecdsa.ImportECPrivateKey(sec1Der, out _);
        return new EcKeypair(spec, ecdsa);
    }

    /// <summary>Export the private key as SEC1 EC private-key DER (for at-rest persistence).</summary>
    public byte[] ExportPrivateKey() => _ecdsa.ExportECPrivateKey();

    /// <summary>
    /// Sign a message: compute <c>sha256(message)</c>, ECDSA-sign it, and return the compact
    /// 64-byte (r‖s) signature normalized to low-S.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(message, digest);

        byte[] signature = _ecdsa.SignHash(digest); // IEEE P1363 (r‖s), 64 bytes
        NormalizeLowS(signature);
        return signature;
    }

    private void NormalizeLowS(byte[] signature)
    {
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (s > _spec.HalfN)
        {
            s = _spec.N - s;
            CurveSpec.To32(s).CopyTo(signature.AsSpan(32, 32));
        }
    }

    public void Dispose() => _ecdsa.Dispose();
}
