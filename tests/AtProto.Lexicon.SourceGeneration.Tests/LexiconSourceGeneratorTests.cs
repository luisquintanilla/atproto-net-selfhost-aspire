using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AtProto.Lexicon.SourceGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AtProto.Lexicon.SourceGeneration.Tests;

public sealed class LexiconSourceGeneratorTests
{
    [Fact]
    public void ExistingLexicons_GenerateCompilableConsumerTypes()
    {
        var result = Run(
            ("place.selfhost.status.json", ReadLexicon("place.selfhost.status.json")),
            ("place.selfhost.instance.json", ReadLexicon("place.selfhost.instance.json")),
            ("Consumer.cs", """
                using System;
                using Place.Selfhost;

                public static class Consumer
                {
                    public static StatusRecord CreateStatus() => new()
                    {
                        Status = "🙂",
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    public static Instance CreateInstance() => new()
                    {
                        Name = "Example",
                        Pds = "https://pds.example",
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                }
                """));

        AssertNoErrors(result);
        var statusSource = result.Source("place.selfhost.status");
        var instanceSource = result.Source("place.selfhost.instance");

        Assert.Contains("public const string Nsid = \"place.selfhost.status\";", statusSource, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"$type\")]", statusSource, StringComparison.Ordinal);
        Assert.Contains("public required string Status { get; init; }", statusSource, StringComparison.Ordinal);
        Assert.Contains("public required DateTimeOffset CreatedAt { get; init; }", statusSource, StringComparison.Ordinal);
        Assert.Contains("public required string Name { get; init; }", instanceSource, StringComparison.Ordinal);
        Assert.Contains("public string? Description { get; init; }", instanceSource, StringComparison.Ordinal);
        Assert.Contains("public string? Relay { get; init; }", instanceSource, StringComparison.Ordinal);
        Assert.Contains("public required DateTimeOffset CreatedAt { get; init; }", instanceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicPrimitivesAndArrays_GenerateExpectedTypes()
    {
        var result = Run(("com.example.values.json", """
            {
              "lexicon": 1,
              "id": "com.example.values",
              "defs": {
                "main": {
                  "type": "record",
                  "record": {
                    "type": "object",
                    "required": ["enabled", "count", "ratio", "tags"],
                    "properties": {
                      "enabled": { "type": "boolean" },
                      "count": { "type": "integer", "minimum": 0, "maximum": 10 },
                      "ratio": { "type": "number" },
                      "tags": { "type": "array", "items": { "type": "string" } },
                      "optionalNumbers": { "type": "array", "items": { "type": "integer" } }
                    }
                  }
                }
              }
            }
            """), ("Consumer.cs", """
                using Com.Example;

                public static class Consumer
                {
                    public static Values Create() => new()
                    {
                        Enabled = true,
                        Count = 3,
                        Ratio = 0.5,
                        Tags = new[] { "one", "two" }
                    };
                }
                """));

        AssertNoErrors(result);
        var source = result.Source("com.example.values");
        Assert.Contains("public required bool Enabled { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("public required int Count { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("public required double Ratio { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("public required IReadOnlyList<string> Tags { get; init; }", source, StringComparison.Ordinal);
        Assert.True(
            source.Contains("public IReadOnlyList<int>? OptionalNumbers { get; init; }", StringComparison.Ordinal),
            source);
    }

    [Fact]
    public void GeneratedStatusRecord_RoundTripsThroughSystemTextJson()
    {
        var result = Run(("place.selfhost.status.json", ReadLexicon("place.selfhost.status.json")));
        AssertNoErrors(result);

        var assembly = Emit(result.Compilation);
        var statusType = assembly.GetType("Place.Selfhost.StatusRecord", throwOnError: true)!;
        var instance = Activator.CreateInstance(statusType)!;
        var timestamp = DateTimeOffset.Parse("2026-07-29T13:59:35.806-04:00");

        statusType.GetProperty("Status")!.SetValue(instance, "🙂");
        statusType.GetProperty("CreatedAt")!.SetValue(instance, timestamp);

        var json = JsonSerializer.Serialize(instance, statusType);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("place.selfhost.status", root.GetProperty("$type").GetString());
        Assert.Equal("🙂", root.GetProperty("status").GetString());
        Assert.Equal(timestamp, root.GetProperty("createdAt").GetDateTimeOffset());

        var restored = JsonSerializer.Deserialize(json, statusType)!;
        Assert.Equal("🙂", statusType.GetProperty("Status")!.GetValue(restored));
        Assert.Equal(timestamp, statusType.GetProperty("CreatedAt")!.GetValue(restored));
    }

    [Fact]
    public void MalformedJson_ReportsDiagnosticAndGeneratesNoSource()
    {
        var result = Run(("malformed.json", "{ \"lexicon\": 1,"));

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "LEXICON001");
        Assert.Empty(result.GeneratedSources);
        Assert.Empty(result.CompilerErrors);
    }

    [Fact]
    public void MalformedDefinition_ReportsDiagnosticAndGeneratesNoSource()
    {
        var result = Run(("com.example.malformed.json", """
            {
              "lexicon": 1,
              "id": "com.example.malformed",
              "defs": {
                "main": {
                  "type": "record",
                  "record": {
                    "type": "object"
                  }
                }
              }
            }
            """));

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "LEXICON002");
        Assert.Empty(result.GeneratedSources);
        Assert.Empty(result.CompilerErrors);
    }

    [Fact]
    public void UnsupportedSchemaType_ReportsDiagnosticAndGeneratesNoSource()
    {
        var result = Run(("com.example.unsupported.json", """
            {
              "lexicon": 1,
              "id": "com.example.unsupported",
              "defs": {
                "main": {
                  "type": "record",
                  "record": {
                    "type": "object",
                    "properties": {
                      "value": { "type": "object" }
                    }
                  }
                }
              }
            }
            """));

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "LEXICON003");
        Assert.Empty(result.GeneratedSources);
        Assert.Empty(result.CompilerErrors);
    }

    [Fact]
    public void RepeatedGeneration_IsDeterministic()
    {
        var lexicon = ReadLexicon("place.selfhost.instance.json");
        var first = Run(("place.selfhost.instance.json", lexicon));
        var second = Run(("place.selfhost.instance.json", lexicon));

        AssertNoErrors(first);
        AssertNoErrors(second);
        Assert.Equal(first.Source("place.selfhost.instance"), second.Source("place.selfhost.instance"));
    }

    private static GeneratorResult Run(params (string Path, string Content)[] files)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTrees = files
            .Where(static file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(file => CSharpSyntaxTree.ParseText(SourceText.From(file.Content), parseOptions, path: file.Path))
            .ToArray();
        if (syntaxTrees.Length == 0)
        {
            syntaxTrees = [CSharpSyntaxTree.ParseText(string.Empty, parseOptions)];
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "LexiconConsumer",
            syntaxTrees: syntaxTrees,
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additionalTexts = files
            .Where(static file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(static file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Content))
            .ToArray();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new LexiconSourceGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var driverDiagnostics);
        var runResult = driver.GetRunResult();
        var output = (CSharpCompilation)outputCompilation;

        return new GeneratorResult(
            output,
            runResult.Results.SelectMany(static result => result.GeneratedSources).ToArray(),
            runResult.Diagnostics.Concat(driverDiagnostics).ToArray(),
            output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray());
    }

    private static MetadataReference[] GetReferences()
    {
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static void AssertNoErrors(GeneratorResult result)
    {
        Assert.True(
            !result.GeneratorDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            string.Join(Environment.NewLine, result.GeneratorDiagnostics));
        Assert.True(!result.CompilerErrors.Any(), string.Join(Environment.NewLine, result.CompilerErrors));
    }

    private static string ReadLexicon(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "lexicons", fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the Lexicon fixture.");
    }

    private static Assembly Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText sourceText;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            sourceText = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return sourceText;
        }
    }

    private sealed record GeneratorResult(
        CSharpCompilation Compilation,
        IReadOnlyList<GeneratedSourceResult> GeneratedSources,
        IReadOnlyList<Diagnostic> GeneratorDiagnostics,
        IReadOnlyList<Diagnostic> CompilerErrors)
    {
        public string Source(string nsid)
        {
            return GeneratedSources.Single(source => source.HintName.Contains(nsid, StringComparison.Ordinal)).SourceText.ToString();
        }
    }
}
