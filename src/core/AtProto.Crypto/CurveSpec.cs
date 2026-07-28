using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace AtProto.Crypto;

/// <summary>
/// Elliptic-curve domain parameters and helpers for the two atproto signing curves. Everything
/// is built on <see cref="System.Security.Cryptography.ECDsa"/> and
/// <see cref="System.Numerics.BigInteger"/> — no third-party crypto dependency. secp256k1 is
/// supplied as explicit parameters (portable across the OpenSSL/CNG backends); P-256 uses the
/// built-in named curve.
/// </summary>
internal sealed class CurveSpec
{
    public required EcKeyType KeyType { get; init; }
    public required ECCurve Curve { get; init; }

    /// <summary>Field prime.</summary>
    public required BigInteger P { get; init; }

    /// <summary>Curve coefficient a (mod p).</summary>
    public required BigInteger A { get; init; }

    /// <summary>Curve coefficient b.</summary>
    public required BigInteger B { get; init; }

    /// <summary>Group order n (used for low-S normalization/validation).</summary>
    public required BigInteger N { get; init; }

    /// <summary>Unsigned-varint multicodec prefix for the compressed public key in did:key/multibase.</summary>
    public required byte[] Multicodec { get; init; }

    public BigInteger HalfN => N >> 1;

    private static BigInteger Hex(string h) =>
        BigInteger.Parse("0" + h, NumberStyles.HexNumber);

    private static byte[] HexBytes(string h) => Convert.FromHexString(h);

    public static readonly CurveSpec Secp256k1 = new()
    {
        KeyType = EcKeyType.Secp256k1,
        Multicodec = [0xE7, 0x01],
        P = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F"),
        A = BigInteger.Zero,
        B = new BigInteger(7),
        N = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"),
        Curve = new ECCurve
        {
            CurveType = ECCurve.ECCurveType.PrimeShortWeierstrass,
            Prime = HexBytes("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F"),
            A = HexBytes("0000000000000000000000000000000000000000000000000000000000000000"),
            B = HexBytes("0000000000000000000000000000000000000000000000000000000000000007"),
            Order = HexBytes("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"),
            Cofactor = [0x01],
            G = new ECPoint
            {
                X = HexBytes("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"),
                Y = HexBytes("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"),
            },
        },
    };

    public static readonly CurveSpec P256 = new()
    {
        KeyType = EcKeyType.P256,
        Multicodec = [0x80, 0x24],
        P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF"),
        A = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC"), // p - 3
        B = Hex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B"),
        N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        Curve = ECCurve.NamedCurves.nistP256,
    };

    public static CurveSpec For(EcKeyType type) => type switch
    {
        EcKeyType.Secp256k1 => Secp256k1,
        EcKeyType.P256 => P256,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static CurveSpec ForMulticodec(ReadOnlySpan<byte> code)
    {
        if (code.SequenceEqual(Secp256k1.Multicodec)) return Secp256k1;
        if (code.SequenceEqual(P256.Multicodec)) return P256;
        throw new FormatException($"unsupported key multicodec 0x{Convert.ToHexString(code)}");
    }

    /// <summary>
    /// Recover the Y coordinate of a compressed point. Both curves have p ≡ 3 (mod 4), so the
    /// modular square root is <c>v^((p+1)/4) mod p</c>.
    /// </summary>
    public BigInteger DecompressY(BigInteger x, bool yIsOdd)
    {
        BigInteger v = (BigInteger.ModPow(x, 3, P) + A * x + B) % P;
        if (v < 0) v += P;
        BigInteger y = BigInteger.ModPow(v, (P + 1) / 4, P);
        if ((y * y % P) != v)
            throw new FormatException("compressed point is not on the curve");
        if (y.IsEven == yIsOdd)
            y = P - y;
        return y;
    }

    /// <summary>Serialize a non-negative integer to a fixed 32-byte big-endian buffer.</summary>
    public static byte[] To32(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32) return bytes;
        if (bytes.Length > 32) throw new FormatException("integer exceeds 32 bytes");
        var padded = new byte[32];
        bytes.CopyTo(padded, 32 - bytes.Length);
        return padded;
    }
}
