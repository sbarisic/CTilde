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
    string? projectManifest = null;
    string? headerOutput = null;
    var checkOnly = false;
    var trace = false;
    var target = CompilationTarget.Hosted;
    var targetSpecified = false;
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
            case "--header":
                if (++index >= args.Length)
                    return UsageError("--header requires an output path.");
                headerOutput = args[index];
                break;
            case "--project":
                if (++index >= args.Length)
                    return UsageError("--project requires a ctilde.json path.");
                projectManifest = args[index];
                break;
            case "--trace":
                trace = true;
                break;
            case "--target":
                targetSpecified = true;
                if (++index >= args.Length)
                    return UsageError("--target requires hosted or esp-idf.");
                target = args[index] switch
                {
                    "hosted" => CompilationTarget.Hosted,
                    "esp-idf" => CompilationTarget.EspIdf,
                    _ => (CompilationTarget)(-1),
                };
                if (!Enum.IsDefined(target))
                    return UsageError($"Unknown target '{args[index]}'; expected hosted or esp-idf.");
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
        if (inputs.Count != 0 || output is not null || headerOutput is not null || checkOnly || projectManifest is not null)
            return UsageError("--compile-directory cannot be combined with input files, --project, -o, --header, or --check.");

        return CompileDirectory(inputDirectory, trace, target);
    }

    if (projectManifest is not null)
    {
        if (inputs.Count != 0 || inputDirectory is not null || targetSpecified)
            return UsageError("--project cannot be combined with input files, --compile-directory, or --target.");
        if (!checkOnly && string.IsNullOrWhiteSpace(output))
            return UsageError("-o is required unless --check is used.");
        if (checkOnly && headerOutput is not null)
            return UsageError("--header cannot be combined with --check.");
        if (headerOutput is not null && Path.GetFullPath(headerOutput).Equals(Path.GetFullPath(output!), StringComparison.OrdinalIgnoreCase))
            return UsageError("--header and -o must name different files.");
        return CompileProject(projectManifest, output, headerOutput, checkOnly, trace);
    }

    if (inputs.Count == 0)
        return UsageError("At least one .ct input file is required.");
    if (!checkOnly && string.IsNullOrWhiteSpace(output))
        return UsageError("-o is required unless --check is used.");
    if (checkOnly && headerOutput is not null)
        return UsageError("--header cannot be combined with --check.");
    if (headerOutput is not null && Path.GetFullPath(headerOutput).Equals(Path.GetFullPath(output!), StringComparison.OrdinalIgnoreCase))
        return UsageError("--header and -o must name different files.");

    return Compile(inputs, output, headerOutput, checkOnly, trace, target);
}

static int CompileProject(string manifestPath, string? output, string? headerOutput, bool checkOnly, bool trace)
{
    try
    {
        var project = CTildeProjectFile.Load(manifestPath);
        if (trace)
            Console.Error.WriteLine($"trace: loaded {project.SourceFiles.Length} source file(s) from {project.ManifestPath}");
        return Compile(project.SourceFiles, output, headerOutput, checkOnly, trace, project.Configuration.Target);
    }
    catch (CTildeProjectException exception)
    {
        Console.Error.WriteLine($"ctilde: {exception.Message}");
        return 2;
    }
}

static int CompileDirectory(string inputDirectory, bool trace, CompilationTarget target)
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
            if (Compile([input], output, null, checkOnly: false, trace, target, removeStaleGeneratedOutput: true) != 0)
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

static int Compile(IReadOnlyCollection<string> inputs, string? output, string? headerOutput, bool checkOnly, bool trace, CompilationTarget target, bool removeStaleGeneratedOutput = false)
{
    try
    {
        if (trace)
        {
            Console.Error.WriteLine($"trace: target {(target == CompilationTarget.EspIdf ? "esp-idf" : "hosted")}");
            Console.Error.WriteLine($"trace: reading {inputs.Count} source file(s)");
        }
        var trees = inputs.Select(path => SyntaxTree.Parse(SourceText.FromFile(path))).ToArray();
        if (trace)
            Console.Error.WriteLine("trace: parsing complete; declaring and binding symbols");
        var compilation = Compilation.Create(trees, new CompilationOptions(target));
        using var generated = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var generatedHeader = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var diagnostics = checkOnly ? compilation.GetDiagnostics() : compilation.EmitC(generated).Diagnostics;
        if (!checkOnly && headerOutput is not null && !diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            diagnostics = compilation.EmitCHeader(generatedHeader).Diagnostics;
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
            if (headerOutput is not null)
                WriteAtomically(Path.GetFullPath(headerOutput), generatedHeader.ToString());
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
    Console.Error.WriteLine("Usage: ctilde <input.ct>... -o <program.c> [--header <exports.h>] [--target hosted|esp-idf] [--check] [--trace]");
    Console.Error.WriteLine("       ctilde --project <ctilde.json> -o <program.c> [--header <exports.h>] [--check] [--trace]");
    Console.Error.WriteLine("       ctilde --compile-directory <directory> [--target hosted|esp-idf] [--trace]");
}
