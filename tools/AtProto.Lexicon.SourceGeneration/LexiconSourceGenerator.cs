using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AtProto.Lexicon.SourceGeneration;

[Generator]
public sealed class LexiconSourceGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MalformedLexicon = new(
        id: "LEXICON001",
        title: "Malformed Lexicon definition",
        messageFormat: "{0}",
        category: "AtProto.Lexicon",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidLexicon = new(
        id: "LEXICON002",
        title: "Invalid Lexicon definition",
        messageFormat: "{0}",
        category: "AtProto.Lexicon",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedLexicon = new(
        id: "LEXICON003",
        title: "Unsupported Lexicon definition",
        messageFormat: "{0}",
        category: "AtProto.Lexicon",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var lexicons = context.AdditionalTextsProvider
            .Where(static file => string.Equals(Path.GetExtension(file.Path), ".json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => Parse(file, cancellationToken));

        context.RegisterSourceOutput(lexicons, static (productionContext, result) => result.Emit(productionContext));
    }

    private static GenerationResult Parse(AdditionalText file, CancellationToken cancellationToken)
    {
        var sourceText = file.GetText(cancellationToken);
        if (sourceText is null)
        {
            return GenerationResult.Failure(
                file.Path,
                CreateDiagnostic(MalformedLexicon, file.Path, "The AdditionalFile could not be read."));
        }

        try
        {
            using var document = JsonDocument.Parse(sourceText.ToString());
            return Generate(file.Path, document.RootElement);
        }
        catch (JsonException exception)
        {
            return GenerationResult.Failure(
                file.Path,
                CreateDiagnostic(MalformedLexicon, file.Path, $"The JSON is not valid: {exception.Message}"));
        }
    }

    private static GenerationResult Generate(string path, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(InvalidLexicon, path, "The Lexicon root must be a JSON object.");
        }

        if (!TryGetString(root, "id", out var nsid))
        {
            return Failure(InvalidLexicon, path, "The Lexicon must define a string 'id'.");
        }

        if (!IsValidNsid(nsid))
        {
            return Failure(InvalidLexicon, path, $"The Lexicon id '{nsid}' is not a valid NSID.");
        }

        if (!root.TryGetProperty("defs", out var defs) || defs.ValueKind != JsonValueKind.Object)
        {
            return Failure(InvalidLexicon, path, "The Lexicon must define an object 'defs'.");
        }

        if (!defs.TryGetProperty("main", out var main) || main.ValueKind != JsonValueKind.Object)
        {
            return Failure(InvalidLexicon, path, "The Lexicon must define an object at 'defs.main'.");
        }

        if (!TryGetString(main, "type", out var mainType))
        {
            return Failure(InvalidLexicon, path, "The 'defs.main' definition must define a string 'type'.");
        }

        if (!string.Equals(mainType, "record", StringComparison.Ordinal))
        {
            return Failure(UnsupportedLexicon, path, $"The Lexicon definition type '{mainType}' is not supported.");
        }

        if (!main.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return Failure(InvalidLexicon, path, "A record definition must contain an object 'record'.");
        }

        if (!TryGetString(record, "type", out var recordType))
        {
            return Failure(InvalidLexicon, path, "A record definition must define a string 'record.type'.");
        }

        if (!string.Equals(recordType, "object", StringComparison.Ordinal))
        {
            return Failure(UnsupportedLexicon, path, $"The record schema type '{recordType}' is not supported.");
        }

        if (!record.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return Failure(InvalidLexicon, path, "An object record must define an object 'properties'.");
        }

        if (!TryReadRequiredProperties(record, properties, path, out var required, out var requiredFailure))
        {
            return requiredFailure!;
        }

        var mappedProperties = new List<MappedProperty>();
        var generatedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            var propertyName = ToPascalCase(property.Name);
            if (string.Equals(propertyName, "Type", StringComparison.Ordinal))
            {
                return Failure(InvalidLexicon, path, "The property name 'type' conflicts with the generated $type property.");
            }

            if (!generatedNames.Add(propertyName))
            {
                return Failure(InvalidLexicon, path, $"Properties generate the duplicate C# member name '{propertyName}'.");
            }

            if (property.Value.ValueKind != JsonValueKind.Object || !TryGetString(property.Value, "type", out _))
            {
                return Failure(InvalidLexicon, path, $"Property '{property.Name}' must define an object with a string 'type'.");
            }

            if (!TryMapType(property.Value, required.Contains(property.Name), out var csharpType, out var failure))
            {
                return Failure(failure!.Descriptor, path, $"Property '{property.Name}': {failure.Message}");
            }

            mappedProperties.Add(new MappedProperty(property.Name, propertyName, csharpType, required.Contains(property.Name)));
        }

        var @namespace = DeriveNamespace(nsid);
        var typeName = DeriveTypeName(nsid);
        while (string.Equals(typeName, "Type", StringComparison.Ordinal)
            || string.Equals(typeName, "Nsid", StringComparison.Ordinal)
            || mappedProperties.Any(property => string.Equals(property.CSharpName, typeName, StringComparison.Ordinal)))
        {
            typeName += "Record";
        }

        if (!IsValidIdentifier(typeName))
        {
            return Failure(InvalidLexicon, path, $"The NSID '{nsid}' does not produce a valid C# type name.");
        }

        var source = RenderSource(nsid, @namespace, typeName, mappedProperties, main);
        return GenerationResult.Success(path, GetHintName(nsid), source);
    }

    private static bool TryReadRequiredProperties(
        JsonElement record,
        JsonElement properties,
        string path,
        out HashSet<string> required,
        out GenerationResult? failure)
    {
        required = new HashSet<string>(StringComparer.Ordinal);
        failure = null;

        if (!record.TryGetProperty("required", out var requiredElement))
        {
            return true;
        }

        if (requiredElement.ValueKind != JsonValueKind.Array)
        {
            failure = Failure(InvalidLexicon, path, "The 'required' property must be an array of strings.");
            return false;
        }

        foreach (var item in requiredElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                failure = Failure(InvalidLexicon, path, "The 'required' property must contain only non-empty strings.");
                return false;
            }

            var propertyName = item.GetString()!;
            if (!properties.TryGetProperty(propertyName, out _))
            {
                failure = Failure(InvalidLexicon, path, $"The required property '{propertyName}' is not declared in 'properties'.");
                return false;
            }

            if (!required.Add(propertyName))
            {
                failure = Failure(InvalidLexicon, path, $"The required property '{propertyName}' is listed more than once.");
                return false;
            }
        }

        return true;
    }

    private static bool TryMapType(
        JsonElement schema,
        bool isRequired,
        out string csharpType,
        out TypeFailure? failure)
    {
        csharpType = string.Empty;
        failure = null;

        if (!TryGetString(schema, "type", out var lexiconType))
        {
            failure = new TypeFailure(MalformedLexicon, "The schema must define a string 'type'.");
            return false;
        }

        string mappedType;
        switch (lexiconType)
        {
            case "string":
                if (schema.TryGetProperty("format", out var format) && format.ValueKind != JsonValueKind.String)
                {
                    failure = new TypeFailure(InvalidLexicon, "A string schema's 'format' must be a string.");
                    return false;
                }

                mappedType = IsDatetime(schema) ? "DateTimeOffset" : "string";
                break;
            case "integer":
                if (!TryMapIntegerType(schema, out mappedType, out var integerFailure))
                {
                    failure = integerFailure;
                    return false;
                }

                break;
            case "number":
                mappedType = "double";
                break;
            case "boolean":
                mappedType = "bool";
                break;
            case "array":
                if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
                {
                    failure = new TypeFailure(InvalidLexicon, "An array schema must define an object 'items'.");
                    return false;
                }

                if (!TryMapType(items, isRequired: true, out var itemType, out failure))
                {
                    return false;
                }

                mappedType = $"IReadOnlyList<{itemType}>";
                break;
            case "blob":
            case "bytes":
            case "cid-link":
            case "ref":
            case "union":
            case "object":
                failure = new TypeFailure(UnsupportedLexicon, $"The schema type '{lexiconType}' is not supported.");
                return false;
            default:
                failure = new TypeFailure(UnsupportedLexicon, $"The schema type '{lexiconType}' is not supported.");
                return false;
        }

        csharpType = isRequired || mappedType.EndsWith("?", StringComparison.Ordinal)
            ? mappedType
            : mappedType + "?";
        return true;
    }

    private static bool TryMapIntegerType(JsonElement schema, out string csharpType, out TypeFailure? failure)
    {
        csharpType = "int";
        failure = null;

        var minimum = long.MinValue;
        var maximum = long.MaxValue;
        var hasMinimum = false;
        var hasMaximum = false;
        if (schema.TryGetProperty("minimum", out var minimumElement))
        {
            if (minimumElement.ValueKind != JsonValueKind.Number || !minimumElement.TryGetInt64(out minimum))
            {
                failure = new TypeFailure(InvalidLexicon, "The integer 'minimum' must be an integer.");
                return false;
            }

            hasMinimum = true;
        }

        if (schema.TryGetProperty("maximum", out var maximumElement))
        {
            if (maximumElement.ValueKind != JsonValueKind.Number || !maximumElement.TryGetInt64(out maximum))
            {
                failure = new TypeFailure(InvalidLexicon, "The integer 'maximum' must be an integer.");
                return false;
            }

            hasMaximum = true;
        }

        if (minimum > maximum)
        {
            failure = new TypeFailure(InvalidLexicon, "An integer schema cannot have a minimum greater than its maximum.");
            return false;
        }

        csharpType = (!hasMinimum || minimum >= int.MinValue) && (!hasMaximum || maximum <= int.MaxValue)
            ? "int"
            : "long";
        return true;
    }

    private static bool IsDatetime(JsonElement schema)
    {
        return schema.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.String
            && string.Equals(format.GetString(), "datetime", StringComparison.Ordinal);
    }

    private static string RenderSource(
        string nsid,
        string @namespace,
        string typeName,
        IReadOnlyList<MappedProperty> properties,
        JsonElement main)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Text.Json.Serialization;");
        builder.AppendLine();
        builder.AppendLine($"namespace {@namespace};");
        builder.AppendLine();

        if (TryGetString(main, "description", out var description) && !string.IsNullOrWhiteSpace(description))
        {
            AppendXmlSummary(builder, description!);
        }

        builder.AppendLine($"public sealed record class {typeName}");
        builder.AppendLine("{");
        builder.AppendLine($"    public const string Nsid = \"{EscapeStringLiteral(nsid)}\";");
        builder.AppendLine();
        builder.AppendLine("    [JsonPropertyName(\"$type\")]");
        builder.AppendLine("    public string Type { get; init; } = Nsid;");

        foreach (var property in properties)
        {
            builder.AppendLine();
            builder.AppendLine($"    [JsonPropertyName(\"{EscapeStringLiteral(property.JsonName)}\")]");
            var requiredKeyword = property.IsRequired && !property.CSharpType.EndsWith("?", StringComparison.Ordinal)
                ? "required "
                : string.Empty;
            builder.AppendLine($"    public {requiredKeyword}{property.CSharpType} {property.CSharpName} {{ get; init; }}");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string DeriveNamespace(string nsid)
    {
        var segments = nsid.Split('.');
        return string.Join(".", segments.Take(segments.Length - 1).Select(ToPascalCase));
    }

    private static string DeriveTypeName(string nsid)
    {
        var segments = nsid.Split('.');
        return ToPascalCase(segments[segments.Length - 1]);
    }

    private static string ToPascalCase(string value)
    {
        var builder = new StringBuilder();
        var capitalizeNext = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                capitalizeNext = true;
                continue;
            }

            if (builder.Length == 0 && char.IsDigit(character))
            {
                builder.Append('_');
            }

            builder.Append(capitalizeNext ? char.ToUpper(character, CultureInfo.InvariantCulture) : character);
            capitalizeNext = false;
        }

        var identifier = builder.Length == 0 ? "Value" : builder.ToString();
        return IsKeyword(identifier) ? identifier + "Value" : identifier;
    }

    private static bool IsValidNsid(string value)
    {
        var segments = value.Split('.');
        if (value.Length > 317
            || segments.Length < 3
            || segments.Any(static segment => segment.Length == 0 || segment.Length > 63))
        {
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var isName = index == segments.Length - 1;
            if (!IsAsciiLetter(segment[0])
                || segment.Any(character => isName
                    ? !IsAsciiLetterOrDigit(character)
                    : !IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }

            if (!isName && (segment[0] == '-' || segment[segment.Length - 1] == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsAsciiLetterOrDigit(char value) =>
        IsAsciiLetter(value) || (value >= '0' && value <= '9');

    private static bool IsValidIdentifier(string value)
    {
        return value.Length > 0
            && (char.IsLetter(value[0]) || value[0] == '_')
            && value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static bool IsKeyword(string value)
    {
        switch (value)
        {
            case "Abstract":
            case "As":
            case "Base":
            case "Bool":
            case "Break":
            case "Byte":
            case "Case":
            case "Catch":
            case "Char":
            case "Checked":
            case "Class":
            case "Const":
            case "Continue":
            case "Decimal":
            case "Default":
            case "Delegate":
            case "Do":
            case "Double":
            case "Else":
            case "Enum":
            case "Event":
            case "Explicit":
            case "Extern":
            case "False":
            case "Finally":
            case "Fixed":
            case "Float":
            case "For":
            case "Foreach":
            case "Goto":
            case "If":
            case "Implicit":
            case "In":
            case "Int":
            case "Interface":
            case "Internal":
            case "Is":
            case "Lock":
            case "Long":
            case "Namespace":
            case "New":
            case "Null":
            case "Object":
            case "Operator":
            case "Out":
            case "Override":
            case "Params":
            case "Private":
            case "Protected":
            case "Public":
            case "Readonly":
            case "Ref":
            case "Return":
            case "Sbyte":
            case "Sealed":
            case "Short":
            case "Sizeof":
            case "Stackalloc":
            case "Static":
            case "String":
            case "Struct":
            case "Switch":
            case "This":
            case "Throw":
            case "True":
            case "Try":
            case "Typeof":
            case "Uint":
            case "Ulong":
            case "Unchecked":
            case "Unsafe":
            case "Ushort":
            case "Using":
            case "Virtual":
            case "Void":
            case "Volatile":
            case "While":
            case "Add":
            case "Alias":
            case "And":
            case "Ascending":
            case "Args":
            case "Async":
            case "Await":
            case "By":
            case "Descending":
            case "Dynamic":
            case "Equals":
            case "From":
            case "Get":
            case "Global":
            case "Group":
            case "Init":
            case "Into":
            case "Join":
            case "Let":
            case "Managed":
            case "nameof":
            case "Not":
            case "On":
            case "Or":
            case "Orderby":
            case "Partial":
            case "Remove":
            case "Required":
            case "Select":
            case "Set":
            case "Unmanaged":
            case "Value":
            case "Var":
            case "When":
            case "Where":
            case "With":
            case "Yield":
                return true;
            default:
                return false;
        }
    }

    private static string GetHintName(string nsid)
    {
        return $"Lexicon.{string.Join(".", nsid.Split('.').Select(SanitizeHintSegment))}.g.cs";
    }

    private static string SanitizeHintSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static Diagnostic CreateDiagnostic(DiagnosticDescriptor descriptor, string path, string message)
    {
        return Diagnostic.Create(descriptor, Location.Create(
            path,
            new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0))), message);
    }

    private static GenerationResult Failure(DiagnosticDescriptor descriptor, string path, string message)
    {
        return GenerationResult.Failure(path, CreateDiagnostic(descriptor, path, message));
    }

    private static void AppendXmlSummary(StringBuilder builder, string summary)
    {
        builder.AppendLine("/// <summary>");
        builder.AppendLine($"/// {EscapeXml(summary)}");
        builder.AppendLine("/// </summary>");
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static string EscapeStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private sealed class GenerationResult
    {
        private GenerationResult(string path, string? hintName, string? source, Diagnostic? diagnostic)
        {
            Path = path;
            HintName = hintName;
            Source = source;
            Diagnostic = diagnostic;
        }

        public string Path { get; }

        public string? HintName { get; }

        public string? Source { get; }

        public Diagnostic? Diagnostic { get; }

        public static GenerationResult Success(string path, string hintName, string source)
        {
            return new GenerationResult(path, hintName, source, null);
        }

        public static GenerationResult Failure(string path, Diagnostic diagnostic)
        {
            return new GenerationResult(path, null, null, diagnostic);
        }

        public void Emit(SourceProductionContext context)
        {
            if (Diagnostic is not null)
            {
                context.ReportDiagnostic(Diagnostic);
            }

            if (HintName is not null && Source is not null)
            {
                context.AddSource(HintName, Source);
            }
        }
    }

    private sealed class TypeFailure
    {
        public TypeFailure(DiagnosticDescriptor descriptor, string message)
        {
            Descriptor = descriptor;
            Message = message;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public string Message { get; }
    }

    private sealed class MappedProperty
    {
        public MappedProperty(string jsonName, string cSharpName, string cSharpType, bool isRequired)
        {
            JsonName = jsonName;
            CSharpName = cSharpName;
            CSharpType = cSharpType;
            IsRequired = isRequired;
        }

        public string JsonName { get; }

        public string CSharpName { get; }

        public string CSharpType { get; }

        public bool IsRequired { get; }
    }
}
