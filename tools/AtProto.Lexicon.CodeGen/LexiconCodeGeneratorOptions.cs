namespace AtProto.Lexicon.CodeGen;

/// <summary>Options for generating C# source from an AT Protocol lexicon.</summary>
public sealed record LexiconCodeGeneratorOptions
{
    /// <summary>
    /// Optional namespace override. When omitted, all NSID segments except the final segment become
    /// PascalCase namespace segments, and the final segment becomes the record type name.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>Optional generated record type name override.</summary>
    public string? TypeName { get; init; }
}
