using AtProto.Lexicon.CodeGen;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args.Any(static arg => arg is "-h" or "--help"))
    {
        Console.Error.WriteLine("Usage: AtProto.Lexicon.CodeGen <input-lexicon.json> [output.cs] [--namespace <namespace>]");
        return args.Length == 0 ? 1 : 0;
    }

    var inputPath = args[0];
    string? outputPath = null;
    string? namespaceOverride = null;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] is "-n" or "--namespace")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("Missing namespace value.");
                return 1;
            }

            namespaceOverride = args[i];
            continue;
        }

        if (outputPath is null)
        {
            outputPath = args[i];
            continue;
        }

        Console.Error.WriteLine($"Unexpected argument: {args[i]}");
        return 1;
    }

    var lexiconJson = File.ReadAllText(inputPath);
    var source = LexiconCodeGenerator.Generate(lexiconJson, new LexiconCodeGeneratorOptions { Namespace = namespaceOverride });

    if (outputPath is null)
    {
        Console.Write(source);
    }
    else
    {
        File.WriteAllText(outputPath, source);
    }

    return 0;
}
