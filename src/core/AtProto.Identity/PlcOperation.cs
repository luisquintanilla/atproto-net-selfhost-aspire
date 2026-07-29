using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AtProto.Identity;

/// <summary>A did:plc service entry inside a PLC operation.</summary>
public sealed record PlcService(string Type, string Endpoint);

/// <summary>An unsigned did:plc operation. Genesis operations have <see cref="Prev"/> set to null.</summary>
public sealed record PlcUnsignedOperation(
    IReadOnlyList<string> RotationKeys,
    IReadOnlyDictionary<string, string> VerificationMethods,
    IReadOnlyList<string> AlsoKnownAs,
    IReadOnlyDictionary<string, PlcService> Services,
    string? Prev = null)
{
    public string Type => "plc_operation";
}

/// <summary>A signed did:plc operation.</summary>
public sealed record PlcSignedOperation(
    IReadOnlyList<string> RotationKeys,
    IReadOnlyDictionary<string, string> VerificationMethods,
    IReadOnlyList<string> AlsoKnownAs,
    IReadOnlyDictionary<string, PlcService> Services,
    string Sig,
    string? Prev = null)
{
    public string Type => "plc_operation";

    public PlcUnsignedOperation Unsigned => new(RotationKeys, VerificationMethods, AlsoKnownAs, Services, Prev);
}

/// <summary>Offline did:plc genesis operation creation, signing, DID derivation, and verification.</summary>
public static class PlcOperations
{
    private const string PlcOperationType = "plc_operation";
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly int[] Base58Map = BuildBase58Map();

    private static readonly BigInteger Secp256k1P = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F");
    private static readonly BigInteger Secp256k1N = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

    private static readonly ECCurve Secp256k1Curve = new()
    {
        CurveType = ECCurve.ECCurveType.PrimeShortWeierstrass,
        Prime = HexBytes("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F"),
        A = HexBytes("0000000000000000000000000000000000000000000000000000000000000000"),
        B = HexBytes("0000000000000000000000000000000000000000000000000000000000000007"),
        Order = HexBytes("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"),
        Cofactor = new byte[] { 0x01 },
        G = new ECPoint
        {
            X = HexBytes("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"),
            Y = HexBytes("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"),
        },
    };

    /// <summary>Create the common AT Protocol did:plc genesis operation shape.</summary>
    public static PlcUnsignedOperation CreateGenesis(
        string rotationKeyDidKey,
        string atprotoVerificationDidKey,
        string handle,
        string pdsEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(rotationKeyDidKey);
        ArgumentException.ThrowIfNullOrEmpty(atprotoVerificationDidKey);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentException.ThrowIfNullOrEmpty(pdsEndpoint);

        string alias = handle.StartsWith("at://", StringComparison.Ordinal) ? handle : "at://" + handle;
        return new PlcUnsignedOperation(
            new[] { rotationKeyDidKey },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["atproto"] = atprotoVerificationDidKey },
            new[] { alias },
            new Dictionary<string, PlcService>(StringComparer.Ordinal)
            {
                ["atproto_pds"] = new("AtprotoPersonalDataServer", pdsEndpoint),
            });
    }

    /// <summary>Sign an unsigned operation. The signer receives canonical DAG-CBOR bytes.</summary>
    public static PlcSignedOperation Sign(PlcUnsignedOperation operation, Func<byte[], byte[]> signer)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(signer);

        byte[] unsignedBytes = EncodeUnsignedDagCbor(operation);
        byte[] signature = signer(unsignedBytes) ?? throw new InvalidOperationException("signer returned null");
        if (signature.Length != 64)
            throw new InvalidOperationException("did:plc signatures must be 64-byte compact ECDSA signatures");

        return new PlcSignedOperation(
            operation.RotationKeys,
            operation.VerificationMethods,
            operation.AlsoKnownAs,
            operation.Services,
            Base64UrlNoPadEncode(signature),
            operation.Prev);
    }

    /// <summary>Derive <c>did:plc:&lt;24 chars&gt;</c> from the signed operation's canonical DAG-CBOR hash.</summary>
    public static string DeriveDid(PlcSignedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        byte[] encoded = EncodeSignedDagCbor(operation);
        byte[] digest = SHA256.HashData(encoded);
        return "did:plc:" + Base32NoPadEncode(digest).Substring(0, 24);
    }

    /// <summary>Verify the operation signature against a listed secp256k1 rotation <c>did:key</c>.</summary>
    public static bool VerifySignature(PlcSignedOperation operation, string rotationKeyDidKey)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrEmpty(rotationKeyDidKey);

        if (!operation.RotationKeys.Contains(rotationKeyDidKey, StringComparer.Ordinal))
            return false;

        byte[] signature;
        try
        {
            signature = Base64UrlNoPadDecode(operation.Sig);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signature.Length != 64)
            return false;

        BigInteger r = new(signature.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
        BigInteger s = new(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (r < BigInteger.One || r >= Secp256k1N || s < BigInteger.One || s >= Secp256k1N)
            return false;

        ECPoint publicPoint;
        try
        {
            publicPoint = ParseSecp256k1DidKey(rotationKeyDidKey);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] digest = SHA256.HashData(EncodeUnsignedDagCbor(operation.Unsigned));
        using ECDsa ecdsa = ECDsa.Create(new ECParameters { Curve = Secp256k1Curve, Q = publicPoint });
        return ecdsa.VerifyHash(digest, signature);
    }

    public static byte[] EncodeUnsignedDagCbor(PlcUnsignedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return DagCborEncode(ToMap(operation));
    }

    public static byte[] EncodeSignedDagCbor(PlcSignedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Dictionary<string, object?> map = ToMap(operation.Unsigned);
        map["sig"] = operation.Sig;
        return DagCborEncode(map);
    }

    private static Dictionary<string, object?> ToMap(PlcUnsignedOperation operation)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = PlcOperationType,
            ["rotationKeys"] = ToObjectList(operation.RotationKeys),
            ["verificationMethods"] = ToObjectMap(operation.VerificationMethods),
            ["alsoKnownAs"] = ToObjectList(operation.AlsoKnownAs),
            ["services"] = ToServiceMap(operation.Services),
            ["prev"] = operation.Prev,
        };
    }

    private static List<object?> ToObjectList(IEnumerable<string> values)
    {
        var list = new List<object?>();
        foreach (string value in values)
            list.Add(value);
        return list;
    }

    private static Dictionary<string, object?> ToObjectMap(IReadOnlyDictionary<string, string> values)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in values)
            map[kv.Key] = kv.Value;
        return map;
    }

    private static Dictionary<string, object?> ToServiceMap(IReadOnlyDictionary<string, PlcService> services)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, PlcService> kv in services)
        {
            map[kv.Key] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = kv.Value.Type,
                ["endpoint"] = kv.Value.Endpoint,
            };
        }
        return map;
    }

    private static byte[] DagCborEncode(object? value)
    {
        using var output = new MemoryStream();
        WriteCbor(output, value);
        return output.ToArray();
    }

    private static void WriteCbor(Stream output, object? value)
    {
        switch (value)
        {
            case null:
                output.WriteByte(0xF6);
                return;
            case string text:
                WriteText(output, text);
                return;
            case IReadOnlyList<object?> list:
                WriteTypeAndLength(output, 4, (ulong)list.Count);
                foreach (object? item in list)
                    WriteCbor(output, item);
                return;
            case IReadOnlyDictionary<string, object?> map:
                WriteMap(output, map);
                return;
            default:
                throw new FormatException($"cannot dag-cbor encode value of type {value.GetType()}");
        }
    }

    private static void WriteMap(Stream output, IReadOnlyDictionary<string, object?> map)
    {
        List<(string Key, byte[] EncodedKey)> keys = new(map.Count);
        foreach (string key in map.Keys)
            keys.Add((key, Encoding.UTF8.GetBytes(key)));

        keys.Sort((left, right) =>
        {
            int byLength = left.EncodedKey.Length.CompareTo(right.EncodedKey.Length);
            return byLength != 0 ? byLength : CompareBytes(left.EncodedKey, right.EncodedKey);
        });

        WriteTypeAndLength(output, 5, (ulong)map.Count);
        foreach ((string key, byte[] encodedKey) in keys)
        {
            WriteTypeAndLength(output, 3, (ulong)encodedKey.Length);
            output.Write(encodedKey, 0, encodedKey.Length);
            WriteCbor(output, map[key]);
        }
    }

    private static void WriteText(Stream output, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteTypeAndLength(output, 3, (ulong)bytes.Length);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void WriteTypeAndLength(Stream output, int majorType, ulong length)
    {
        int prefix = majorType << 5;
        if (length < 24)
        {
            output.WriteByte((byte)(prefix | (int)length));
        }
        else if (length <= byte.MaxValue)
        {
            output.WriteByte((byte)(prefix | 24));
            output.WriteByte((byte)length);
        }
        else if (length <= ushort.MaxValue)
        {
            output.WriteByte((byte)(prefix | 25));
            WriteUInt16(output, (ushort)length);
        }
        else if (length <= uint.MaxValue)
        {
            output.WriteByte((byte)(prefix | 26));
            WriteUInt32(output, (uint)length);
        }
        else
        {
            output.WriteByte((byte)(prefix | 27));
            WriteUInt64(output, length);
        }
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        output.WriteByte((byte)(value >> 24));
        output.WriteByte((byte)(value >> 16));
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    private static void WriteUInt64(Stream output, ulong value)
    {
        for (int shift = 56; shift >= 0; shift -= 8)
            output.WriteByte((byte)(value >> shift));
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            int cmp = left[i].CompareTo(right[i]);
            if (cmp != 0)
                return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static string Base64UrlNoPadEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlNoPadDecode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder == 1)
            throw new FormatException("invalid base64url length");
        if (remainder != 0)
            padded = padded.PadRight(padded.Length + 4 - remainder, '=');
        return Convert.FromBase64String(padded);
    }

    private static string Base32NoPadEncode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    private static ECPoint ParseSecp256k1DidKey(string didKey)
    {
        const string prefix = "did:key:z";
        if (!didKey.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException("expected a did:key:z secp256k1 key");

        byte[] raw = Base58Decode(didKey.Substring(prefix.Length));
        if (raw.Length != 35 || raw[0] != 0xE7 || raw[1] != 0x01)
            throw new FormatException("expected a secp256k1 did:key multicodec prefix");

        byte[] compressed = new byte[33];
        Array.Copy(raw, 2, compressed, 0, compressed.Length);
        if (compressed[0] != 0x02 && compressed[0] != 0x03)
            throw new FormatException("expected a compressed secp256k1 public key");

        BigInteger x = new(compressed.AsSpan(1), isUnsigned: true, isBigEndian: true);
        BigInteger y = DecompressSecp256k1Y(x, compressed[0] == 0x03);
        return new ECPoint { X = To32(x), Y = To32(y) };
    }

    private static BigInteger DecompressSecp256k1Y(BigInteger x, bool yIsOdd)
    {
        BigInteger v = (BigInteger.ModPow(x, 3, Secp256k1P) + 7) % Secp256k1P;
        if (v < 0)
            v += Secp256k1P;

        BigInteger y = BigInteger.ModPow(v, (Secp256k1P + 1) / 4, Secp256k1P);
        if ((y * y % Secp256k1P) != v)
            throw new FormatException("compressed point is not on the curve");
        if (y.IsEven == yIsOdd)
            y = Secp256k1P - y;
        return y;
    }

    private static byte[] Base58Decode(string text)
    {
        BigInteger value = BigInteger.Zero;
        foreach (char c in text)
        {
            int digit = c < 128 ? Base58Map[c] : -1;
            if (digit < 0)
                throw new FormatException($"invalid base58 character '{c}'");
            value = value * 58 + digit;
        }

        int leadingOnes = 0;
        while (leadingOnes < text.Length && text[leadingOnes] == '1')
            leadingOnes++;

        byte[] magnitude = value.IsZero ? Array.Empty<byte>() : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] result = new byte[leadingOnes + magnitude.Length];
        Array.Copy(magnitude, 0, result, leadingOnes, magnitude.Length);
        return result;
    }

    private static int[] BuildBase58Map()
    {
        var map = new int[128];
        Array.Fill(map, -1);
        for (int i = 0; i < Base58Alphabet.Length; i++)
            map[Base58Alphabet[i]] = i;
        return map;
    }

    private static BigInteger Hex(string h) => BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);

    private static byte[] HexBytes(string h) => Convert.FromHexString(h);

    private static byte[] To32(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32)
            return bytes;
        if (bytes.Length > 32)
            throw new FormatException("integer exceeds 32 bytes");
        byte[] padded = new byte[32];
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
        return padded;
    }
}
