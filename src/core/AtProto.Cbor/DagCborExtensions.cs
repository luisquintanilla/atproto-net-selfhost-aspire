namespace AtProto.Cbor;

/// <summary>Typed accessors over the object graph produced by <see cref="DagCbor.Decode"/>.</summary>
public static class DagCborExtensions
{
    public static IReadOnlyDictionary<string, object?> AsMap(this object? value) =>
        value as Dictionary<string, object?>
            ?? throw new InvalidCastException($"expected dag-cbor map, got {Describe(value)}");

    public static IReadOnlyList<object?> AsArray(this object? value) =>
        value as List<object?>
            ?? throw new InvalidCastException($"expected dag-cbor array, got {Describe(value)}");

    public static string AsString(this object? value) =>
        value as string
            ?? throw new InvalidCastException($"expected dag-cbor text, got {Describe(value)}");

    public static byte[] AsBytes(this object? value) =>
        value as byte[]
            ?? throw new InvalidCastException($"expected dag-cbor bytes, got {Describe(value)}");

    public static Cid AsCid(this object? value) =>
        value is Cid cid
            ? cid
            : throw new InvalidCastException($"expected dag-cbor CID link, got {Describe(value)}");

    public static long AsInt64(this object? value) => value switch
    {
        long l => l,
        ulong u => checked((long)u),
        int i => i,
        _ => throw new InvalidCastException($"expected dag-cbor integer, got {Describe(value)}"),
    };

    public static bool AsBool(this object? value) =>
        value is bool b
            ? b
            : throw new InvalidCastException($"expected dag-cbor bool, got {Describe(value)}");

    /// <summary>Get a map field, or null if the map lacks the key.</summary>
    public static object? Field(this object? value, string key) =>
        value.AsMap().TryGetValue(key, out object? field) ? field : null;

    /// <summary>Get a required map field; throws if the key is missing.</summary>
    public static object? RequiredField(this object? value, string key) =>
        value.AsMap().TryGetValue(key, out object? field)
            ? field
            : throw new FormatException($"missing required dag-cbor field '{key}'");

    private static string Describe(object? value) => value switch
    {
        null => "null",
        _ => value.GetType().Name,
    };
}
