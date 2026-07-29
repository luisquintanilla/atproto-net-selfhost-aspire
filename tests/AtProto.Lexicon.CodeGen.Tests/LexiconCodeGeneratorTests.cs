using System.Reflection;
using System.Runtime.Loader;
using AtProto.Lexicon.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AtProto.Lexicon.CodeGen.Tests;

public sealed class LexiconCodeGeneratorTests
{
    [Fact]
    public void Generate_ForStatusLexicon_EmitsExpectedMembers()
    {
        var source = GenerateStatusSource();

        Assert.Contains("public const string Nsid = \"place.selfhost.status\";", source, StringComparison.Ordinal);
        Assert.Contains("namespace Place.Selfhost;", source, StringComparison.Ordinal);
        Assert.Contains("public sealed record class StatusRecord", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"status\")]", source, StringComparison.Ordinal);
        Assert.Contains("public required string Status { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"createdAt\")]", source, StringComparison.Ordinal);
        Assert.Contains("public required DateTimeOffset CreatedAt { get; init; }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ForStatusLexicon_CompilesWithRoslyn()
    {
        var source = GenerateStatusSource();
        var diagnostics = CompileGeneratedSource(source).Diagnostics;

        var errors = diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void GeneratedStatusRecord_CanRoundTripThroughSystemTextJson()
    {
        var source = GenerateStatusSource();
        var assembly = CompileGeneratedSource(source).Assembly;
        var statusType = assembly.GetType("Place.Selfhost.StatusRecord", throwOnError: true)!;
        var instance = Activator.CreateInstance(statusType)!;

        statusType.GetProperty("Status")!.SetValue(instance, "🙂");
        statusType.GetProperty("CreatedAt")!.SetValue(instance, DateTimeOffset.Parse("2026-07-29T13:59:35.806-04:00"));

        var json = System.Text.Json.JsonSerializer.Serialize(instance, statusType);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("place.selfhost.status", root.GetProperty("$type").GetString());
        Assert.Equal("🙂", root.GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-07-29T13:59:35.806-04:00"), root.GetProperty("createdAt").GetDateTimeOffset());
    }

    private static string GenerateStatusSource()
    {
        var lexiconPath = FindRepoRoot().GetFile("lexicons/place.selfhost.status.json");
        return LexiconCodeGenerator.Generate(File.ReadAllText(lexiconPath));
    }

    private static CompilationResult CompileGeneratedSource(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            assemblyName: $"GeneratedLexicons_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        var diagnostics = emitResult.Diagnostics;

        if (!emitResult.Success)
        {
            return new CompilationResult(diagnostics, null);
        }

        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        return new CompilationResult(diagnostics, assembly);
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "lexicons", "place.selfhost.status.json")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record CompilationResult(IReadOnlyList<Diagnostic> Diagnostics, Assembly? MaybeAssembly)
    {
        public Assembly Assembly => MaybeAssembly ?? throw new InvalidOperationException(
            string.Join(Environment.NewLine, Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}

file static class DirectoryInfoExtensions
{
    public static string GetFile(this DirectoryInfo directory, string relativePath) => Path.Combine(directory.FullName, relativePath);
}
