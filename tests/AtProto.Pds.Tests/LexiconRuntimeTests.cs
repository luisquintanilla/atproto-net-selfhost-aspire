using System.Text.Json;
using AtProto.Pds;

namespace AtProto.Pds.Tests;

public sealed class LexiconRuntimeTests
{
    private const string Collection = "com.example.lexicon.post";

    [Fact]
    public async Task Valid_record_satisfies_required_fields_and_formats()
    {
        RuntimeLexiconValidator validator = Validator(authoritative: true);

        await validator.ValidateAsync(Collection, "self", ValidRecord());
    }

    [Fact]
    public async Task Wrong_type_missing_field_and_bad_format_are_rejected()
    {
        RuntimeLexiconValidator validator = Validator(authoritative: true);

        XrpcException wrongType = await Assert.ThrowsAsync<XrpcException>(() =>
            validator.ValidateAsync(Collection, "self", Record("com.example.lexicon.other", "hello", "2026-07-28T12:00:00.000Z")).AsTask());
        Assert.Equal("InvalidRecord", wrongType.Error);

        XrpcException missingField = await Assert.ThrowsAsync<XrpcException>(() =>
            validator.ValidateAsync(Collection, "self", Record(Collection, null, "2026-07-28T12:00:00.000Z")).AsTask());
        Assert.Contains("text", missingField.Message);

        XrpcException badFormat = await Assert.ThrowsAsync<XrpcException>(() =>
            validator.ValidateAsync(Collection, "self", Record(Collection, "hello", "not-a-date")).AsTask());
        Assert.Contains("datetime", badFormat.Message);
    }

    [Fact]
    public async Task Literal_record_key_and_unknown_collection_are_rejected()
    {
        RuntimeLexiconValidator validator = Validator(authoritative: true);
        JsonElement valid = ValidRecord();

        XrpcException wrongKey = await Assert.ThrowsAsync<XrpcException>(() =>
            validator.ValidateAsync(Collection, "other", valid).AsTask());
        Assert.Equal("InvalidRecordKey", wrongKey.Error);

        XrpcException unknown = await Assert.ThrowsAsync<XrpcException>(() =>
            validator.ValidateAsync("com.example.lexicon.unknown", "self", valid).AsTask());
        Assert.Contains("No Lexicon", unknown.Message);
    }

    [Fact]
    public async Task Non_authoritative_resolver_preserves_unconfigured_collections()
    {
        RuntimeLexiconValidator validator = Validator(authoritative: false);

        await validator.ValidateAsync(
            "app.bsky.feed.post",
            "self",
            JsonSerializer.SerializeToElement(new { text = "legacy record" }));
    }

    private static JsonElement ValidRecord() =>
        Record(Collection, "hello", "2026-07-28T12:00:00.000Z");

    private static JsonElement Record(string type, string? text, string createdAt)
    {
        var record = new Dictionary<string, object?>
        {
            ["$type"] = type,
            ["createdAt"] = createdAt,
        };
        if (text is not null)
            record["text"] = text;
        return JsonSerializer.SerializeToElement(record);
    }

    private static RuntimeLexiconValidator Validator(bool authoritative)
    {
        string document = $$"""
        {
          "id": "{{Collection}}",
          "defs": {
            "main": {
              "type": "record",
              "key": "literal:self",
              "record": {
                "type": "object",
                "required": ["text", "createdAt"],
                "properties": {
                  "$type": { "type": "string", "const": "{{Collection}}" },
                  "text": { "type": "string", "minLength": 1 },
                  "createdAt": { "type": "string", "format": "datetime" }
                }
              }
            }
          }
        }
        """;
        var definitions = new Dictionary<string, LexiconRecordDefinition>(StringComparer.Ordinal)
        {
            [Collection] = LexiconRecordDefinition.FromDocument(document),
        };
        return new RuntimeLexiconValidator(new StaticLexiconResolver(definitions, authoritative));
    }

    private sealed class StaticLexiconResolver(
        IReadOnlyDictionary<string, LexiconRecordDefinition> definitions,
        bool isAuthoritative) : ILexiconResolver
    {
        public bool IsAuthoritative => isAuthoritative;

        public ValueTask<LexiconRecordDefinition?> ResolveAsync(
            string collection,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(definitions.GetValueOrDefault(collection));
    }
}
