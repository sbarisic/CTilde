using System.Text;
using CTilde;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
    {
        PrintUsage();
        return args.Length == 0 ? 1 : 0;
    }

    var inputs = new List<string>();
    string? output = null;
    var checkOnly = false;
    var trace = false;
    for (var index = 0; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "-o":
                if (++index >= args.Length)
                    return UsageError("-o requires an output path.");
                output = args[index];
                break;
            case "--check":
                checkOnly = true;
                break;
            case "--trace":
                trace = true;
                break;
            default:
                if (args[index].StartsWith("-", StringComparison.Ordinal))
                    return UsageError($"Unknown option '{args[index]}'.");
                inputs.Add(args[index]);
                break;
        }
    }

    if (inputs.Count == 0)
        return UsageError("At least one .ct input file is required.");
    if (!checkOnly && string.IsNullOrWhiteSpace(output))
        return UsageError("-o is required unless --check is used.");

    try
    {
        if (trace)
            Console.Error.WriteLine($"trace: reading {inputs.Count} source file(s)");
        var trees = inputs.Select(path => SyntaxTree.Parse(SourceText.FromFile(path))).ToArray();
        if (trace)
            Console.Error.WriteLine("trace: parsing complete; declaring and binding symbols");
        var compilation = Compilation.Create(trees);
        using var generated = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var result = compilation.EmitC(generated);
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic);
        if (!result.Success)
            return 1;
        if (trace)
            Console.Error.WriteLine("trace: semantic analysis and C11 lowering complete");
        if (!checkOnly)
        {
            File.WriteAllText(Path.GetFullPath(output!), generated.ToString(), new UTF8Encoding(false));
            if (trace)
                Console.Error.WriteLine($"trace: wrote {Path.GetFullPath(output!)}");
        }
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
    {
        Console.Error.WriteLine($"ctilde: {exception.Message}");
        return 1;
    }
}

static int UsageError(string message)
{
    Console.Error.WriteLine($"ctilde: {message}");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: ctilde <input.ct>... -o <program.c> [--check] [--trace]");
}
