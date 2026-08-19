using System.Text;
using CTilde;

namespace CTilde.Cli;

internal static class CTildeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!CommandLineOptions.TryParse(args, out var options, out var parseError, out var showHelp))
            return UsageError(parseError!);
        if (args.Length == 0 || showHelp)
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (options!.InputDirectory is not null)
                return CompileDirectory(options);

            BuildRequest request;
            try
            {
                request = BuildRequestResolver.Resolve(options);
            }
            catch (Exception exception) when (exception is CommandLineException or CTildeProjectException)
            {
                Console.Error.WriteLine($"ctilde: {exception.Message}");
                return exception is CommandLineException ? 2 : 1;
            }

            await using var buildLock = request.BuildNative ? BuildLock.Acquire(request.LockDirectory) : null;
            var result = Compile(request);
            if (result != 0 || !request.BuildNative)
                return result;
            return await NativeBuildDriver.BuildAsync(request, cancellation.Token);
        }
        catch (BuildLockException exception)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
        catch (NativeBuildException exception)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ctilde: Build canceled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int Compile(BuildRequest request)
    {
        try
        {
            if (request.Trace)
            {
                Console.Error.WriteLine($"trace: target {(request.Target == CompilationTarget.EspIdf ? "esp-idf" : "hosted")}");
                Console.Error.WriteLine($"trace: reading {request.Inputs.Count} source file(s)");
                if (request.ManifestPath is not null)
                    Console.Error.WriteLine($"trace: loaded project {request.ManifestPath}");
            }
            var trees = request.Inputs.Select(path => SyntaxTree.Parse(SourceText.FromFile(path))).ToArray();
            var compilation = Compilation.Create(trees, new CompilationOptions(request.Target));
            using var generated = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using var generatedHeader = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var diagnostics = request.CheckOnly ? compilation.GetDiagnostics() : compilation.EmitC(generated).Diagnostics;
            if (!request.CheckOnly && request.GeneratedHeaderPath is not null && !HasErrors(diagnostics))
                diagnostics = compilation.EmitCHeader(generatedHeader).Diagnostics;
            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic);
            if (HasErrors(diagnostics))
            {
                if (request.BuildNative)
                    RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath);
                return 1;
            }

            if (request.CheckOnly)
            {
                if (request.Trace)
                    Console.Error.WriteLine("trace: semantic analysis complete");
                return 0;
            }

            WriteAtomically(request.GeneratedCPath!, generated.ToString());
            if (request.GeneratedHeaderPath is not null)
                WriteAtomically(request.GeneratedHeaderPath, generatedHeader.ToString());
            if (request.Trace)
            {
                Console.Error.WriteLine("trace: semantic analysis and GNU C23 lowering complete");
                Console.Error.WriteLine($"trace: wrote {request.GeneratedCPath}");
                if (request.GeneratedHeaderPath is not null)
                    Console.Error.WriteLine($"trace: wrote {request.GeneratedHeaderPath}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (request.BuildNative)
                RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath);
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
    }

    private static int CompileDirectory(CommandLineOptions options)
    {
        if (options.Inputs.Count != 0 || options.Output is not null || options.HeaderOutput is not null ||
            options.CheckOnly || options.ProjectManifest is not null || options.Build || options.Configuration is not null ||
            options.Compiler is not null || options.NativeOutput is not null || options.EspIdfProject is not null || options.EspIdfPath is not null)
            return UsageError("--compile-directory cannot be combined with inputs, project, output, check, build, or native-build options.");
        try
        {
            var directory = ResolveDirectory(options.InputDirectory!);
            var inputs = Directory.GetFiles(directory, "*.ct", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
            if (inputs.Length == 0)
            {
                Console.Error.WriteLine($"ctilde: No .ct files found in '{directory}'.");
                return 1;
            }
            var exitCode = 0;
            foreach (var input in inputs)
            {
                var request = new BuildRequest([input], options.Target, null, directory, Path.ChangeExtension(input, ".c"),
                    null, false, options.Trace, false, CTildeNativeBuildConfiguration.Debug, "auto", null, null, null);
                if (Compile(request) != 0)
                {
                    RemoveStaleGeneratedOutput(request.GeneratedCPath);
                    exitCode = 1;
                }
            }
            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
    }

    private static string ResolveDirectory(string path)
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
            if (!Directory.Exists(candidate))
                continue;
            resolvedPath ??= candidate;
            if (File.Exists(Path.Combine(directory.FullName, "CTilde.Cli.csproj")))
                return candidate;
        }
        return resolvedPath ?? workingDirectoryPath;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static void WriteAtomically(string outputPath, string contents)
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

    private static void RemoveStaleGeneratedOutput(params string?[] paths)
    {
        foreach (var path in paths.Where(path => path is not null).Cast<string>())
        {
            if (!File.Exists(path))
                continue;
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var firstLine = reader.ReadLine();
            reader.Close();
            if (firstLine?.StartsWith("/* Generated by C~", StringComparison.Ordinal) == true ||
                firstLine?.StartsWith("#ifndef CTILDE_", StringComparison.Ordinal) == true)
                File.Delete(path);
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"ctilde: {message}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ctilde <input.ct>... -o <program.c> [--header <exports.h>] [--target hosted|esp-idf] [--check] [--trace]");
        Console.Error.WriteLine("       ctilde <input.ct>... --build [--target hosted|esp-idf] [native build options] [--trace]");
        Console.Error.WriteLine("       ctilde --project <ctilde.json> [--build] [native build options] [--check] [--trace]");
        Console.Error.WriteLine("       ctilde --compile-directory <directory> [--target hosted|esp-idf] [--trace]");
        Console.Error.WriteLine("Native build options: --configuration debug|release --compiler <name|path> --native-output <path>");
        Console.Error.WriteLine("                          --idf-project <directory> --idf-path <directory>");
    }
}
