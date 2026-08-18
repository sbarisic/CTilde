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
    string? inputDirectory = null;
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
            case "--compile-directory":
                if (++index >= args.Length)
                    return UsageError("--compile-directory requires a directory path.");
                inputDirectory = args[index];
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

    if (inputDirectory is not null)
    {
        if (inputs.Count != 0 || output is not null || checkOnly)
            return UsageError("--compile-directory cannot be combined with input files, -o, or --check.");

        return CompileDirectory(inputDirectory, trace);
    }

    if (inputs.Count == 0)
        return UsageError("At least one .ct input file is required.");
    if (!checkOnly && string.IsNullOrWhiteSpace(output))
        return UsageError("-o is required unless --check is used.");

    return Compile(inputs, output, checkOnly, trace);
}

static int CompileDirectory(string inputDirectory, bool trace)
{
    try
    {
        var directory = ResolveDirectory(inputDirectory);
        var inputs = Directory.GetFiles(directory, "*.ct", SearchOption.TopDirectoryOnly);
        Array.Sort(inputs, StringComparer.Ordinal);

        if (inputs.Length == 0)
        {
            Console.Error.WriteLine($"ctilde: No .ct files found in '{directory}'.");
            return 1;
        }

        if (trace)
            Console.Error.WriteLine($"trace: compiling {inputs.Length} program(s) from {directory}");

        var exitCode = 0;
        foreach (var input in inputs)
        {
            var output = Path.ChangeExtension(input, ".c");
            if (Compile([input], output, checkOnly: false, trace) != 0)
                exitCode = 1;
        }

        return exitCode;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"ctilde: {exception.Message}");
        return 1;
    }
}

static string ResolveDirectory(string path)
{
    if (Path.IsPathRooted(path))
        return Path.GetFullPath(path);

    var workingDirectoryPath = Path.GetFullPath(path);
    if (Directory.Exists(workingDirectoryPath))
        return workingDirectoryPath;

    string? resolvedPath = null;
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, path);
        if (Directory.Exists(candidate))
        {
            resolvedPath ??= candidate;
            if (File.Exists(Path.Combine(directory.FullName, "CTilde.Cli.csproj")))
                return candidate;
        }
    }

    return resolvedPath ?? workingDirectoryPath;
}

static int Compile(IReadOnlyCollection<string> inputs, string? output, bool checkOnly, bool trace)
{
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
            Console.Error.WriteLine("trace: semantic analysis and GNU C23 lowering complete");
        if (!checkOnly)
        {
            var fullOutputPath = Path.GetFullPath(output!);
            File.WriteAllText(fullOutputPath, generated.ToString(), new UTF8Encoding(false));
            if (trace)
                Console.Error.WriteLine($"trace: wrote {fullOutputPath}");
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
    Console.Error.WriteLine("       ctilde --compile-directory <directory> [--trace]");
}
