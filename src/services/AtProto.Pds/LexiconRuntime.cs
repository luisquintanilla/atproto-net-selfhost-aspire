using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AtProto.Pds;

/// <summary>A resolved Lexicon record definition used by the PDS write path.</summary>
public sealed record LexiconRecordDefinition(
    string Collection,
    string Key,
    JsonElement RecordSchema)
{
    public static LexiconRecordDefinition FromDocument(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string collection = root.GetProperty("id").GetString()
            ?? throw new FormatException("Lexicon id is required");
        JsonElement main = root.GetProperty("defs").GetProperty("main");
        if (!string.Equals(main.GetProperty("type").GetString(), "record", StringComparison.Ordinal))
            throw new FormatException($"Lexicon '{collection}' main definition is not a record");
        string key = main.GetProperty("key").GetString()
            ?? throw new FormatException($"Lexicon '{collection}' record key is required");
        JsonElement schema = main.GetProperty("record").Clone();
        return new LexiconRecordDefinition(collection, key, schema);
    }
}

/// <summary>
/// Resolves record Lexicons for the runtime write validator. The default implementation is
/// intentionally non-authoritative so existing hosts can adopt validation incrementally; registering
/// an authoritative resolver makes an unresolved collection a rejected write.
/// </summary>
public interface ILexiconResolver
{
    bool IsAuthoritative => true;

    ValueTask<LexiconRecordDefinition?> ResolveAsync(
        string collection,
        CancellationToken cancellationToken = default);
}

/// <summary>Default resolver for hosts that have not configured a Lexicon catalog yet.</summary>
public sealed class UnavailableLexiconResolver : ILexiconResolver
{
    public bool IsAuthoritative => false;

    public ValueTask<LexiconRecordDefinition?> ResolveAsync(
        string collection,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<LexiconRecordDefinition?>(null);
}

/// <summary>
/// Resolves one JSON Lexicon file per collection from a configured directory. This is a generic
/// adapter for self-hosted deployments and a simple reference implementation for other resolvers.
/// </summary>
public sealed class FileSystemLexiconResolver : ILexiconResolver
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, LexiconRecordDefinition> _cache = new(StringComparer.Ordinal);

    public FileSystemLexiconResolver(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public ValueTask<LexiconRecordDefinition?> ResolveAsync(
        string collection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsNsid(collection))
            return ValueTask.FromResult<LexiconRecordDefinition?>(null);

        lock (_gate)
        {
            if (_cache.TryGetValue(collection, out LexiconRecordDefinition? cached))
                return ValueTask.FromResult<LexiconRecordDefinition?>(cached);
        }

        string path = Path.Combine(_directory, collection + ".json");
        if (!File.Exists(path))
            return ValueTask.FromResult<LexiconRecordDefinition?>(null);

        LexiconRecordDefinition definition;
        try
        {
            definition = LexiconRecordDefinition.FromDocument(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new InvalidDataException($"Lexicon '{collection}' could not be loaded from '{path}'.", ex);
        }

        if (!string.Equals(definition.Collection, collection, StringComparison.Ordinal))
            throw new InvalidDataException($"Lexicon file '{path}' declares '{definition.Collection}', not '{collection}'.");

        lock (_gate)
            _cache[collection] = definition;
        return ValueTask.FromResult<LexiconRecordDefinition?>(definition);
    }

    private static bool IsNsid(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Split('.').Length >= 3
        && value.Split('.').All(segment => segment.Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
}

/// <summary>Validates a record against a resolved Lexicon before it reaches the repository.</summary>
public sealed class RuntimeLexiconValidator(ILexiconResolver resolver)
{
    public async ValueTask ValidateAsync(
        string collection,
        string? rkey,
        JsonElement record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        LexiconRecordDefinition? definition = await ResolveAsync(collection, cancellationToken);
        if (definition is null)
            return;
        ValidateKey(definition.Key, rkey, collection);

        if (record.ValueKind != JsonValueKind.Object)
            throw new XrpcException(400, "InvalidRecord", "Record must be a JSON object.");

        if (record.TryGetProperty("$type", out JsonElement type))
        {
            if (type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), collection, StringComparison.Ordinal))
                throw new XrpcException(400, "InvalidRecord", $"Record $type must be '{collection}'.");
        }
        else
        {
            throw new XrpcException(400, "InvalidRecord", "Record must contain a matching $type.");
        }

        ValidateSchema(record, definition.RecordSchema, "$");
    }

    public async ValueTask ValidateCollectionAndKeyAsync(
        string collection,
        string? rkey,
        CancellationToken cancellationToken = default)
    {
        LexiconRecordDefinition? definition = await ResolveAsync(collection, cancellationToken);
        if (definition is not null)
            ValidateKey(definition.Key, rkey, collection);
    }

    private async ValueTask<LexiconRecordDefinition?> ResolveAsync(
        string collection,
        CancellationToken cancellationToken)
    {
        LexiconRecordDefinition? definition = await resolver.ResolveAsync(collection, cancellationToken);
        if (definition is null)
        {
            if (resolver.IsAuthoritative)
                throw new XrpcException(400, "InvalidRecord", $"No Lexicon is available for collection '{collection}'.");
            return null;
        }
        if (!string.Equals(definition.Collection, collection, StringComparison.Ordinal))
            throw new XrpcException(500, "InvalidRecord", "Lexicon resolver returned a mismatched collection.");
        return definition;
    }

    private static void ValidateKey(string key, string? rkey, string collection)
    {
        if (rkey is null)
            return;
        if (key.StartsWith("literal:", StringComparison.Ordinal)
            && !string.Equals(key["literal:".Length..], rkey, StringComparison.Ordinal))
            throw new XrpcException(400, "InvalidRecordKey", $"Collection '{collection}' requires record key '{key["literal:".Length..]}'.");
    }

    private static void ValidateSchema(JsonElement value, JsonElement schema, string path)
    {
        string type = schema.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;

        if (type is "unknown" or "ref" or "union")
            return;

        switch (type)
        {
            case "object":
                ValidateObject(value, schema, path);
                break;
            case "string":
                ValidateString(value, schema, path);
                break;
            case "integer":
                long integer = value.TryGetInt64(out long parsedInteger) ? parsedInteger : 0;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                    Invalid(path, "must be an integer");
                ValidateNumericBounds(integer, schema, path);
                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    Invalid(path, "must be a boolean");
                break;
            case "array":
                ValidateArray(value, schema, path);
                break;
            case "bytes":
                if (!IsTaggedString(value, "$bytes"))
                    Invalid(path, "must be a $bytes value");
                break;
            case "cid-link":
                if (!IsTaggedString(value, "$link"))
                    Invalid(path, "must be a $link value");
                break;
            case "blob":
                if (value.ValueKind != JsonValueKind.Object)
                    Invalid(path, "must be a blob object");
                break;
            default:
                Invalid(path, $"uses unsupported Lexicon type '{type}'");
                break;
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            Invalid(path, "must be an object");

        if (schema.TryGetProperty("required", out JsonElement required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                string name = requiredName.GetString() ?? string.Empty;
                if (!value.TryGetProperty(name, out _))
                    Invalid(path, $"is missing required field '{name}'");
            }
        }

        if (!schema.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (!value.TryGetProperty(property.Name, out JsonElement child))
                continue;
            if (child.ValueKind == JsonValueKind.Null
                && IsNullable(schema, property.Name))
                continue;
            ValidateSchema(child, property.Value, $"{path}.{property.Name}");
        }
    }

    private static bool IsNullable(JsonElement schema, string name) =>
        schema.TryGetProperty("nullable", out JsonElement nullable)
        && nullable.ValueKind == JsonValueKind.Array
        && nullable.EnumerateArray().Any(element => element.GetString() == name);

    private static void ValidateString(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
            Invalid(path, "must be a string");
        string text = value.GetString() ?? string.Empty;
        int bytes = Encoding.UTF8.GetByteCount(text);
        if (schema.TryGetProperty("minLength", out JsonElement min)
            && bytes < min.GetInt32())
            Invalid(path, "is shorter than minLength");
        if (schema.TryGetProperty("maxLength", out JsonElement max)
            && bytes > max.GetInt32())
            Invalid(path, "is longer than maxLength");
        if (schema.TryGetProperty("maxGraphemes", out JsonElement maxGraphemes)
            && new StringInfo(text).LengthInTextElements > maxGraphemes.GetInt32())
            Invalid(path, "exceeds maxGraphemes");
        if (schema.TryGetProperty("minGraphemes", out JsonElement minGraphemes)
            && new StringInfo(text).LengthInTextElements < minGraphemes.GetInt32())
            Invalid(path, "is shorter than minGraphemes");
        if (schema.TryGetProperty("enum", out JsonElement enumValues)
            && !enumValues.EnumerateArray().Any(candidate => candidate.GetString() == text))
            Invalid(path, "is not one of the allowed values");
        if (schema.TryGetProperty("const", out JsonElement constant)
            && constant.ValueKind == JsonValueKind.String
            && constant.GetString() != text)
            Invalid(path, "does not match const");

        if (schema.TryGetProperty("format", out JsonElement format))
        {
            switch (format.GetString())
            {
                case "datetime" when !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _):
                    Invalid(path, "must be a datetime");
                    break;
                case "uri" when !Uri.TryCreate(text, UriKind.Absolute, out _):
                    Invalid(path, "must be a URI");
                    break;
            }
        }
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
            Invalid(path, "must be an array");
        int count = value.GetArrayLength();
        if (schema.TryGetProperty("minLength", out JsonElement min) && count < min.GetInt32())
            Invalid(path, "contains fewer than minLength items");
        if (schema.TryGetProperty("maxLength", out JsonElement max) && count > max.GetInt32())
            Invalid(path, "contains more than maxLength items");
        if (schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
                ValidateSchema(item, items, $"{path}[{index++}]");
        }
    }

    private static void ValidateNumericBounds(long value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("minimum", out JsonElement minimum) && value < minimum.GetInt64())
            Invalid(path, "is less than minimum");
        if (schema.TryGetProperty("maximum", out JsonElement maximum) && value > maximum.GetInt64())
            Invalid(path, "is greater than maximum");
        if (schema.TryGetProperty("enum", out JsonElement values)
            && !values.EnumerateArray().Any(candidate => candidate.GetInt64() == value))
            Invalid(path, "is not one of the allowed values");
    }

    private static bool IsTaggedString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 1
        && value.TryGetProperty(property, out JsonElement tagged)
        && tagged.ValueKind == JsonValueKind.String;

    private static void Invalid(string path, string reason) =>
        throw new XrpcException(400, "InvalidRecord", $"Record field {path} {reason}.");
}
