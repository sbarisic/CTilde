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
            if (Compile([input], output, checkOnly: false, trace, removeStaleGeneratedOutput: true) != 0)
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

static int Compile(IReadOnlyCollection<string> inputs, string? output, bool checkOnly, bool trace, bool removeStaleGeneratedOutput = false)
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
        var diagnostics = checkOnly ? compilation.GetDiagnostics() : compilation.EmitC(generated).Diagnostics;
        foreach (var diagnostic in diagnostics)
            Console.Error.WriteLine(diagnostic);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            if (removeStaleGeneratedOutput && output is not null)
                RemoveStaleGeneratedOutput(output);
            return 1;
        }
        if (trace)
            Console.Error.WriteLine(checkOnly ? "trace: semantic analysis complete" : "trace: semantic analysis and GNU C23 lowering complete");
        if (!checkOnly)
        {
            var fullOutputPath = Path.GetFullPath(output!);
            WriteAtomically(fullOutputPath, generated.ToString());
            if (trace)
                Console.Error.WriteLine($"trace: wrote {fullOutputPath}");
        }
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
    {
        if (removeStaleGeneratedOutput && output is not null)
            RemoveStaleGeneratedOutput(output);
        Console.Error.WriteLine($"ctilde: {exception.Message}");
        return 1;
    }
}

static void WriteAtomically(string outputPath, string contents)
{
    var directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);
    var temporaryPath = Path.Combine(directory ?? Directory.GetCurrentDirectory(), $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        File.Move(temporaryPath, outputPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

static void RemoveStaleGeneratedOutput(string outputPath)
{
    var fullPath = Path.GetFullPath(outputPath);
    if (!File.Exists(fullPath))
        return;
    using var reader = new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    var firstLine = reader.ReadLine();
    reader.Close();
    if (firstLine?.StartsWith("/* Generated by C~", StringComparison.Ordinal) == true)
        File.Delete(fullPath);
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
