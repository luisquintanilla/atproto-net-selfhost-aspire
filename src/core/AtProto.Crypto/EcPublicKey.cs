using System.Numerics;
using System.Security.Cryptography;

namespace AtProto.Crypto;

/// <summary>
/// An AT Protocol public signing key. Serializes to the <c>did:key</c> / <c>publicKeyMultibase</c>
/// form used in DID documents, and verifies compact 64-byte (r‖s) ECDSA signatures over
/// <c>sha256(message)</c>, enforcing the low-S rule atproto requires.
/// </summary>
public sealed class EcPublicKey
{
    private readonly CurveSpec _spec;
    private readonly BigInteger _x;
    private readonly BigInteger _y;

    internal EcPublicKey(CurveSpec spec, BigInteger x, BigInteger y)
    {
        _spec = spec;
        _x = x;
        _y = y;
    }

    /// <summary>The curve this key is on.</summary>
    public EcKeyType KeyType => _spec.KeyType;

    /// <summary>The 33-byte SEC1 compressed public point (0x02/0x03 ‖ X).</summary>
    public byte[] CompressedPoint
    {
        get
        {
            var point = new byte[33];
            point[0] = (byte)(_y.IsEven ? 0x02 : 0x03);
            CurveSpec.To32(_x).CopyTo(point, 1);
            return point;
        }
    }

    /// <summary>The multibase ("z" base58btc) form: <c>z</c> + base58(multicodec ‖ compressed point).</summary>
    public string Multibase
    {
        get
        {
            byte[] payload = [.. _spec.Multicodec, .. CompressedPoint];
            return "z" + Base58Btc.Encode(payload);
        }
    }

    /// <summary>The <c>did:key</c> form (<c>did:key:z...</c>).</summary>
    public string DidKey => "did:key:" + Multibase;

    /// <summary>Parse a public key from a <c>did:key:z...</c> or bare <c>z...</c> multibase string.</summary>
    public static EcPublicKey Parse(string didKeyOrMultibase)
    {
        ArgumentException.ThrowIfNullOrEmpty(didKeyOrMultibase);
        string multibase = didKeyOrMultibase.StartsWith("did:key:", StringComparison.Ordinal)
            ? didKeyOrMultibase["did:key:".Length..]
            : didKeyOrMultibase;
        if (multibase.Length == 0 || multibase[0] != 'z')
            throw new FormatException("expected a 'z' (base58btc) multibase key");

        byte[] raw = Base58Btc.Decode(multibase[1..]);
        int code0 = ReadVarint(raw, out int consumed);
        CurveSpec spec = CurveSpec.ForMulticodec(raw.AsSpan(0, consumed));
        ReadOnlySpan<byte> point = raw.AsSpan(consumed);
        _ = code0;

        return FromCompressed(spec, point);
    }

    internal static EcPublicKey FromCompressed(CurveSpec spec, ReadOnlySpan<byte> point)
    {
        if (point.Length != 33 || (point[0] != 0x02 && point[0] != 0x03))
            throw new FormatException("expected a 33-byte SEC1 compressed point");
        var x = new BigInteger(point[1..], isUnsigned: true, isBigEndian: true);
        BigInteger y = spec.DecompressY(x, yIsOdd: point[0] == 0x03);
        return new EcPublicKey(spec, x, y);
    }

    /// <summary>
    /// Verify a compact 64-byte (r‖s) signature over <c>sha256(message)</c>. High-S signatures are
    /// rejected (atproto verifies with <c>lowS: true</c>).
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
            return false;

        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: true);
        var r = new BigInteger(signature[..32], isUnsigned: true, isBigEndian: true);
        if (r < 1 || r >= _spec.N || s < 1 || s >= _spec.N)
            return false;
        if (s > _spec.HalfN) // enforce low-S
            return false;

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(message, digest);

        using ECDsa ecdsa = CreateEcdsa();
        return ecdsa.VerifyHash(digest, signature); // IEEE P1363 (r‖s)
    }

    internal ECDsa CreateEcdsa()
    {
        var parameters = new ECParameters
        {
            Curve = _spec.Curve,
            Q = new ECPoint { X = CurveSpec.To32(_x), Y = CurveSpec.To32(_y) },
        };
        return ECDsa.Create(parameters);
    }

    private static int ReadVarint(ReadOnlySpan<byte> data, out int consumed)
    {
        int result = 0, shift = 0, i = 0;
        while (true)
        {
            if (i >= data.Length)
                throw new FormatException("truncated multicodec varint");
            byte b = data[i++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        consumed = i;
        return result;
    }
}
