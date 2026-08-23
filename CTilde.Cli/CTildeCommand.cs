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

            if (request.PrepareDebug == "attach")
                return DebugPreparation.ValidateAttach(request);

            await using var buildLock = request.BuildNative || request.BindingManifests is { Count: > 0 }
                ? BuildLock.Acquire(request.LockDirectory)
                : null;
            if (request.BindingManifests is { Count: > 0 })
            {
                if (!await EspIdfBindingGenerator.RefreshAsync(request, request.VerifyBindings, cancellation.Token))
                    return 1;
                if (request.GenerateBindingsOnly || request.VerifyBindings)
                    return 0;
                request = request with { Inputs = CTildeProjectFile.Load(request.ManifestPath!).SourceFiles };
            }
            var result = Compile(request);
            if (result.ExitCode != 0 || !request.BuildNative)
                return result.ExitCode;
            var nativeResult = await NativeBuildDriver.BuildAsync(request, result.UsesInlineAssembly, cancellation.Token);
            if (nativeResult.ExitCode != 0)
                return nativeResult.ExitCode;
            if (request.PrepareDebug == "launch")
            {
                if (request.Target == CompilationTarget.EspIdf)
                    await EspIdfBuildDriver.PrepareDebugLaunchAsync(request, cancellation.Token);
                DebugPreparation.WriteDescriptor(request, nativeResult);
            }
            return 0;
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

    private static CompilationOutcome Compile(BuildRequest request)
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
            var bindingDeclarations = (request.BindingManifests ?? []).Select(manifest => manifest.DeclarationsPath)
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var trees = request.Inputs.Select(path => bindingDeclarations.Contains(Path.GetFullPath(path))
                ? SyntaxTree.ParseEspIdfBinding(SourceText.FromFile(path))
                : SyntaxTree.Parse(SourceText.FromFile(path))).ToArray();
            var sourceRoot = request.DebugInformation != DebugInformationMode.None
                ? request.SourceRoot ?? (request.ManifestPath is null ? null : request.RootDirectory)
                : request.SourceRoot;
            var compilation = Compilation.Create(trees, new CompilationOptions(request.Target, sourceRoot,
                request.DebugInformation, request.DebugMemory));
            using var generated = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using var generatedHeader = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            CBundleEmitResult? bundle = null;
            var diagnostics = request.CheckOnly
                ? compilation.GetDiagnostics()
                : request.CLayout == GeneratedCLayout.Unity
                    ? compilation.EmitC(generated).Diagnostics
                    : (bundle = compilation.EmitCBundle()).Diagnostics;
            if (!request.CheckOnly && request.GeneratedHeaderPath is not null && !HasErrors(diagnostics))
                diagnostics = compilation.EmitCHeader(generatedHeader).Diagnostics;
            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic);
            if (HasErrors(diagnostics))
            {
                if (request.BuildNative)
                    RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath, request.SymbolMapPath,
                        request.DebugMapPath, request.DebugTargetPath);
                return new CompilationOutcome(1, compilation.UsesInlineAssembly);
            }

            if (request.CheckOnly)
            {
                if (request.Trace)
                    Console.Error.WriteLine("trace: semantic analysis complete");
                return new CompilationOutcome(0, compilation.UsesInlineAssembly);
            }

            if (request.CLayout == GeneratedCLayout.Unity)
                WriteAtomically(request.GeneratedCPath!, generated.ToString());
            else
                WriteBundle(request.GeneratedDirectory!, bundle!.Artifacts, request.GeneratedHeaderPath);
            if (request.GeneratedHeaderPath is not null)
                WriteAtomically(request.GeneratedHeaderPath, generatedHeader.ToString());
            if (request.SymbolMapPath is not null)
            {
                using var map = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                compilation.EmitSymbolMap(map);
                WriteAtomically(request.SymbolMapPath, map.ToString());
            }
            if (request.DebugMapPath is not null)
            {
                using var debugMap = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                compilation.EmitDebugMap(debugMap);
                WriteAtomically(request.DebugMapPath, debugMap.ToString());
            }
            if (request.Trace)
            {
                Console.Error.WriteLine("trace: semantic analysis and GNU C23 lowering complete");
                Console.Error.WriteLine($"trace: wrote {(request.CLayout == GeneratedCLayout.Unity ? request.GeneratedCPath : request.GeneratedDirectory)}");
                if (request.GeneratedHeaderPath is not null)
                    Console.Error.WriteLine($"trace: wrote {request.GeneratedHeaderPath}");
            }
            return new CompilationOutcome(0, compilation.UsesInlineAssembly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (request.BuildNative)
                RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath, request.SymbolMapPath,
                    request.DebugMapPath, request.DebugTargetPath);
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return new CompilationOutcome(1, false);
        }
    }

    private static int CompileDirectory(CommandLineOptions options)
    {
        if (options.Target == CompilationTarget.EspIdf && options.SourceRoot is not null)
            return UsageError("--source-root is valid only for hosted compilations.");
        if (options.Inputs.Count != 0 || options.Output is not null || options.HeaderOutput is not null ||
            options.CheckOnly || options.ProjectManifest is not null || options.Build || options.Configuration is not null ||
            options.Compiler is not null || options.NativeOutput is not null || options.EspIdfProject is not null || options.EspIdfPath is not null ||
            options.CLayout is not null || options.OutputDirectory is not null || options.SymbolMap is not null || options.Lto ||
            options.DebugInfo || options.DebugMemory is not null || options.DebugMap is not null || options.PrepareDebug is not null || options.DebugTarget is not null || options.SerialPort is not null ||
            options.GenerateBindings || options.VerifyBindings || options.EspClangPath is not null)
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
                var sourceRoot = options.SourceRoot is null ? null : Path.GetFullPath(options.SourceRoot, Directory.GetCurrentDirectory());
                var request = new BuildRequest([input], options.Target, null, directory, sourceRoot, Path.ChangeExtension(input, ".c"),
                    null, false, options.Trace, false, CTildeNativeBuildConfiguration.Debug, "auto", null, null, null,
                    GeneratedCLayout.Unity, null, null, false);
                if (Compile(request).ExitCode != 0)
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

    private readonly record struct CompilationOutcome(int ExitCode, bool UsesInlineAssembly);

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

    private static void WriteBundle(string outputDirectory, IEnumerable<GeneratedCArtifact> artifacts, string? additionalOutput)
    {
        Directory.CreateDirectory(outputDirectory);
        var materialized = artifacts.ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = materialized.Select(artifact => Path.GetFullPath(Path.Combine(outputDirectory, artifact.RelativePath)))
            .ToHashSet(comparer);
        if (additionalOutput is not null)
            expected.Add(Path.GetFullPath(additionalOutput));

        foreach (var artifact in materialized)
        {
            var path = Path.GetFullPath(Path.Combine(outputDirectory, artifact.RelativePath));
            var relative = Path.GetRelativePath(outputDirectory, path);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new IOException($"Generated artifact '{artifact.RelativePath}' escapes the output directory.");
            if (File.Exists(path) && !IsCompilerMarked(path) && !File.ReadAllText(path).Equals(artifact.Content, StringComparison.Ordinal))
                throw new IOException($"Refusing to overwrite handwritten file '{path}'.");
        }

        foreach (var artifact in materialized)
            WriteAtomically(Path.Combine(outputDirectory, artifact.RelativePath), artifact.Content);

        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (!expected.Contains(fullPath) && IsCompilerMarked(fullPath))
                File.Delete(fullPath);
        }
    }

    private static bool IsCompilerMarked(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var prefix = new char[256];
        var length = reader.Read(prefix, 0, prefix.Length);
        var text = new string(prefix, 0, length);
        return text.StartsWith("/* Generated by C~", StringComparison.Ordinal) ||
            text.StartsWith("# Generated by C~", StringComparison.Ordinal) ||
            text.StartsWith("#ifndef CTILDE_", StringComparison.Ordinal) ||
            text.Contains("\"generator\": \"C~ draft 0.", StringComparison.Ordinal);
    }

    private static void RemoveStaleGeneratedOutput(params string?[] paths)
    {
        foreach (var path in paths.Where(path => path is not null).Cast<string>())
        {
            if (!File.Exists(path))
                continue;
            if (IsCompilerMarked(path))
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
        Console.Error.WriteLine("Usage: ctilde <input.ct>... -o <program.c> [--c-layout unity|modules] [--output-directory <directory>] [--symbol-map <path>] [--debug-info] [--debug-map <path>] [--header <exports.h>] [--target hosted|esp-idf] [--source-root <directory>] [--check] [--trace]");
        Console.Error.WriteLine("       ctilde <input.ct>... --build [--target hosted|esp-idf] [native build options] [--trace]");
        Console.Error.WriteLine("       ctilde --project <ctilde.json> [--source-root <directory>] [--build] [native build options] [--check] [--trace]");
        Console.Error.WriteLine("       ctilde --project <ctilde.json> --generate-bindings|--verify-bindings [--idf-path <directory>] [--esp-clang <path>]");
        Console.Error.WriteLine("       ctilde --compile-directory <directory> [--target hosted|esp-idf] [--source-root <directory>] [--trace]");
        Console.Error.WriteLine("Native build options: --configuration debug|release --compiler <name|path> --native-output <path> [--lto]");
        Console.Error.WriteLine("                          --idf-project <directory> --idf-path <directory>");
        Console.Error.WriteLine("ESP-IDF bindings: --generate-bindings --verify-bindings --esp-clang <path>");
        Console.Error.WriteLine("Debug preparation: --prepare-debug launch|attach [--debug-target <descriptor.json>] [--debug-memory off|objects|guarded] [--serial-port <port>] [--baud-rate <rate>]");
    }
}
