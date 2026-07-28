using System.Text.Json;

namespace AtProto.Pds;

/// <summary>
/// Converts between the AT Protocol JSON data model and the dag-cbor object model used by
/// <c>AtProto.Cbor</c>. JSON CID links are <c>{ "$link": "&lt;cid&gt;" }</c> and byte strings are
/// <c>{ "$bytes": "&lt;base64&gt;" }</c> (unpadded standard base64); everything else maps to the
/// obvious primitive, list, or string-keyed map.
/// </summary>
public static class DataModel
{
    /// <summary>Parse a JSON value into the dag-cbor object model (Cid / byte[] / string / long /
    /// double / bool / null / List / Dictionary).</summary>
    public static object? FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$link", out JsonElement link) && link.ValueKind == JsonValueKind.String
                    && element.EnumerateObject().Count() == 1)
                    return Cid.Decode(link.GetString()!);
                if (element.TryGetProperty("$bytes", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.String
                    && element.EnumerateObject().Count() == 1)
                    return DecodeBase64(bytes.GetString()!);

                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                    map[property.Name] = FromJson(property.Value);
                return map;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in element.EnumerateArray())
                    list.Add(FromJson(item));
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                return element.TryGetInt64(out long l) ? l : element.GetDouble();

            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;

            default:
                return null;
        }
    }

    /// <summary>Project the dag-cbor object model back into JSON-serializable objects, re-encoding
    /// CIDs as <c>$link</c> and byte strings as <c>$bytes</c>.</summary>
    public static object? ToJson(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case Cid cid:
                return new Dictionary<string, object?> { ["$link"] = cid.ToString() };
            case byte[] bytes:
                return new Dictionary<string, object?> { ["$bytes"] = EncodeBase64(bytes) };
            case IReadOnlyList<object?> list:
                return list.Select(ToJson).ToList();
            case IReadOnlyDictionary<string, object?> map:
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> entry in map)
                    result[entry.Key] = ToJson(entry.Value);
                return result;
            default:
                return value; // string, long, double, bool
        }
    }

    private static byte[] DecodeBase64(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }

    private static string EncodeBase64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');
}
