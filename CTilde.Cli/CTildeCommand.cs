using System.Diagnostics;
using System.Text;
using CTilde;

namespace CTilde.Cli;

internal static class CTildeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "clean")
            return CleanCommand.Run(args);
        if (args.Length > 0 && args[0] == "format")
            return FormatCommand.Run(args);
        if (args.Length > 0 && args[0] is "restore" or "update" or "vendor")
            return RunModuleCommand(args);
        if (!CommandLineOptions.TryParse(args, out var options, out var parseError, out var showHelp))
            return UsageError(parseError!);
        if (args.Length == 0 || showHelp)
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        using var cancellation = new CancellationTokenSource();
        using var reporter = new BuildReporter(options!.Verbosity, options.Trace);
        var operation = options.Run ? "Run" : options.CheckOnly ? "Check" : "Build";
        BuildRequest? activeRequest = null;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (options.InputDirectory is not null)
                return CompileDirectory(options);

            if (options.ProjectManifest is not null)
            {
                CTildeProject project;
                try { project = CTildeProjectFile.Load(options.ProjectManifest); }
                catch (CTildeProjectException exception)
                {
                    var diagnostic = reporter.ProjectDiagnostic(exception, options.ProjectManifest);
                    BuildDiagnosticReceipt.Write(options.ProjectManifest, operation, "failed", [diagnostic]);
                    reporter.Complete(1);
                    return 1;
                }
                if (project.Configuration.Kind == CTildeProjectKind.StandardLibrary)
                    return await ValidateStandardLibraryAsync(options, project, reporter, operation, cancellation.Token);
            }

            BuildRequest request;
            try
            {
                request = BuildRequestResolver.Resolve(options);
            }
            catch (Exception exception) when (exception is CommandLineException or CTildeProjectException)
            {
                if (exception is CTildeProjectException projectException)
                {
                    var diagnostic = reporter.ProjectDiagnostic(projectException, options.ProjectManifest);
                    if (options.ProjectManifest is not null)
                        BuildDiagnosticReceipt.Write(options.ProjectManifest, operation, "failed", [diagnostic]);
                }
                else
                {
                    Console.Error.WriteLine($"ctilde: {exception.Message}");
                }
                reporter.Complete(1);
                return exception is CommandLineException ? 2 : 1;
            }
            activeRequest = request;
            reporter.Begin(request, operation);

            if (request.PrepareDebug == "attach")
                return DebugPreparation.ValidateAttach(request);

            await using (var buildLock = request.BuildNative || request.BindingManifests is { Count: > 0 }
                ? await BuildLock.AcquireAsync(request.LockDirectory, operation, request.ManifestPath, cancellation.Token)
                : null)
            {
                if (request.BindingManifests is { Count: > 0 })
                {
                    if (!await EspIdfBindingGenerator.RefreshAsync(request, request.VerifyBindings, cancellation.Token))
                    {
                        var diagnostic = reporter.InfrastructureDiagnostic(request.ManifestPath!, "CT6003", "ESP-IDF binding refresh failed. See the preceding diagnostics.");
                        WriteReceipt(request, operation, "failed", [diagnostic]);
                        reporter.Complete(1);
                        return 1;
                    }
                    if (request.GenerateBindingsOnly || request.VerifyBindings)
                    {
                        WriteReceipt(request, operation, "succeeded", []);
                        reporter.Complete(0);
                        return 0;
                    }
                    var refreshedProject = CTildeProjectFile.Load(request.ManifestPath!);
                    request = request with { Inputs = refreshedProject.SourceFiles, SourceOwners = refreshedProject.SourceOwners };
                }
                var compileElapsed = Stopwatch.StartNew();
                var result = Compile(request, reporter);
                if (request.Trace)
                    Console.Error.WriteLine($"trace: C~ compile phase {compileElapsed.ElapsedMilliseconds} ms");
                if (result.ExitCode != 0 || !request.BuildNative)
                {
                    WriteReceipt(request, operation, result.ExitCode == 0 ? "succeeded" : "failed", result.Diagnostics);
                    reporter.Complete(result.ExitCode, result.ExitCode == 0 ? request.GeneratedCPath ?? request.GeneratedDirectory : null);
                    return result.ExitCode;
                }
                reporter.Phase("Compiling and linking native output...");
                var nativeElapsed = Stopwatch.StartNew();
                var nativeResult = await NativeBuildDriver.BuildAsync(request, result.UsesInlineAssembly, cancellation.Token);
                reporter.Phase($"Native toolchain: {nativeResult.Backend}" + (nativeResult.CompilerCommand is null ? string.Empty : $" ({nativeResult.CompilerCommand})"));
                if (request.Trace)
                    Console.Error.WriteLine($"trace: native build phase {nativeElapsed.ElapsedMilliseconds} ms");
                if (nativeResult.ExitCode != 0)
                {
                    RemoveStaleGeneratedOutput(request.StackReportPath);
                    var diagnostic = reporter.InfrastructureDiagnostic(request.ManifestPath ?? request.Inputs.First(), "CT6003", "The native compiler or linker failed. See the preceding native diagnostic output.");
                    WriteReceipt(request, operation, "failed", [diagnostic]);
                    reporter.Complete(nativeResult.ExitCode);
                    return nativeResult.ExitCode;
                }
                if (request.StackReportPath is not null)
                {
                    var stack = StackUsageReporter.Analyze(request, nativeResult);
                    reporter.Phase($"Stack report: {request.StackReportPath}");
                    if (stack.ContractFailure)
                    {
                        foreach (var message in stack.Messages)
                            Console.Error.WriteLine(message);
                        var diagnostic = reporter.InfrastructureDiagnostic(request.ManifestPath ?? request.Inputs.First(),
                            "CT2226", "One or more static stack-usage contracts could not be verified or were exceeded. See the stack report.");
                        WriteReceipt(request, operation, "failed", [diagnostic]);
                        reporter.Complete(1);
                        return 1;
                    }
                }
                if (request.PrepareDebug == "launch")
                {
                    if (request.Target == CompilationTarget.EspIdf)
                        await EspIdfBuildDriver.PrepareDebugLaunchAsync(request, cancellation.Token);
                    DebugPreparation.WriteDescriptor(request, nativeResult);
                }
            }
            WriteReceipt(request, operation, "succeeded", []);
            reporter.Complete(0, request.ExecutablePath);
            return request.RunAfterBuild ? await ProjectRunDriver.RunAsync(request, cancellation.Token) : 0;
        }
        catch (BuildLockException exception)
        {
            var manifest = activeRequest?.ManifestPath ?? options.ProjectManifest;
            if (manifest is not null)
            {
                var diagnostic = reporter.InfrastructureDiagnostic(manifest, "CT6002", exception.Message);
                BuildDiagnosticReceipt.Write(manifest, operation, "failed", [diagnostic], activeRequest?.Inputs);
            }
            else
                Console.Error.WriteLine($"ctilde: {exception.Message}");
            reporter.Complete(1);
            return 1;
        }
        catch (NativeBuildException exception)
        {
            RemoveStaleGeneratedOutput(activeRequest?.StackReportPath);
            var manifest = activeRequest?.ManifestPath ?? options.ProjectManifest;
            if (manifest is not null)
            {
                var diagnostic = reporter.InfrastructureDiagnostic(manifest, "CT6003", exception.Message);
                BuildDiagnosticReceipt.Write(manifest, operation, "failed", [diagnostic], activeRequest?.Inputs);
            }
            else
                Console.Error.WriteLine($"ctilde: {exception.Message}");
            reporter.Complete(1);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            RemoveStaleGeneratedOutput(activeRequest?.StackReportPath);
            var manifest = activeRequest?.ManifestPath ?? options.ProjectManifest;
            if (manifest is not null)
                reporter.InfrastructureDiagnostic(manifest, "CT6003", exception.Message);
            else
                Console.Error.WriteLine($"ctilde: {exception.Message}");
            reporter.Complete(1);
            return 1;
        }
        catch (OperationCanceledException)
        {
            RemoveStaleGeneratedOutput(activeRequest?.StackReportPath);
            Console.Error.WriteLine("ctilde: Build canceled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> ValidateStandardLibraryAsync(CommandLineOptions options, CTildeProject project,
        BuildReporter reporter, string operation, CancellationToken cancellationToken)
    {
        if (options.Run || options.PrepareDebug is not null || options.GenerateBindings || options.VerifyBindings)
            return UsageError("Standard-library projects support only --check or --build.");
        if (!options.CheckOnly && !options.Build)
            return UsageError("A standard-library project requires --check or --build.");
        if (options.Inputs.Count != 0 || options.InputDirectory is not null || options.TargetSpecified ||
            options.Output is not null || options.OutputDirectory is not null || options.HeaderOutput is not null ||
            options.SymbolMap is not null || options.StackReport is not null || options.NativeOutput is not null || options.Configuration is not null ||
            options.Compiler is not null || options.Lto || options.Optimization is not null || options.CpuTarget is not null ||
            options.FloatingPoint is not null || options.PgoMode is not null || options.PgoDirectory is not null ||
            options.DebugInfo || options.DebugMemory is not null)
            return UsageError("Standard-library validation cannot be combined with application build options.");

        try
        {
            reporter.BeginStandardLibrary(project, operation);
            await using var buildLock = await BuildLock.AcquireAsync(Path.Combine(project.RootDirectory, "build"), operation,
                project.ManifestPath, cancellationToken);
            var failed = false;
            var diagnostics = new List<Diagnostic>();
            foreach (var result in StandardLibraryProjectService.Validate(project))
            {
                Console.Out.WriteLine($"C~ standard library: validating {result.Variant}");
                foreach (var diagnostic in result.Diagnostics)
                {
                    diagnostics.Add(diagnostic);
                    reporter.Diagnostic(diagnostic);
                }
                if (result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    failed = true;
                else
                    Console.Out.WriteLine($"C~ standard library: {result.Variant} passed");
            }
            BuildDiagnosticReceipt.Write(project.ManifestPath, operation, failed ? "failed" : "succeeded", diagnostics, project.SourceFiles);
            reporter.Complete(failed ? 1 : 0);
            return failed ? 1 : 0;
        }
        catch (Exception exception) when (exception is CTildeProjectException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            var diagnostic = exception is CTildeProjectException projectException
                ? reporter.ProjectDiagnostic(projectException, project.ManifestPath)
                : reporter.InfrastructureDiagnostic(project.ManifestPath, "CT6003", exception.Message);
            BuildDiagnosticReceipt.Write(project.ManifestPath, operation, "failed", [diagnostic], project.SourceFiles);
            reporter.Complete(1);
            return 1;
        }
    }

    private static CompilationOutcome Compile(BuildRequest request, BuildReporter? reporter = null)
    {
        try
        {
            if (request.Trace)
            {
                Console.Error.WriteLine($"trace: target {TargetName(request)}");
                Console.Error.WriteLine($"trace: reading {request.Inputs.Count} source file(s)");
                if (request.ManifestPath is not null)
                    Console.Error.WriteLine($"trace: loaded project {request.ManifestPath}");
            }
            var bindingDeclarations = (request.BindingManifests ?? []).Select(manifest => manifest.DeclarationsPath)
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var sourceRoot = request.DebugInformation != DebugInformationMode.None
                ? request.SourceRoot ?? (request.ManifestPath is null ? null : request.RootDirectory)
                : request.SourceRoot;
            var sourceIdentityRoot = request.ManifestPath is null
                ? CommonSourceIdentityRoot(request.Inputs)
                : request.RootDirectory;
            var rootOwner = new SourceOwnerIdentity("<root>", request.RootDirectory, sourceIdentityRoot, true, null);
            var trees = request.Inputs.Select(path =>
            {
                var fullPath = Path.GetFullPath(path);
                if (bindingDeclarations.Contains(fullPath))
                    return SyntaxTree.ParseEspIdfBinding(SourceText.FromFile(fullPath));
                var owner = request.SourceOwners is not null && request.SourceOwners.TryGetValue(fullPath, out var moduleOwner)
                    ? moduleOwner
                    : rootOwner;
                return SyntaxTree.Parse(SourceText.FromFile(fullPath), owner);
            }).ToArray();
            var compilation = Compilation.Create(trees, new CompilationOptions(request.Target, sourceRoot,
                request.DebugInformation, request.DebugMemory, request.Architecture, request.NoRecursion,
                sourceIdentityRoot, request.PanicPolicy, [.. request.CpuFeatures ?? []], request.Environment, request.SimdOptimizations,
                request.ManagedModule?.Kind, request.ManagedModule));
            using var generated = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using var generatedHeader = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            CBundleEmitResult? bundle = null;
            var diagnostics = request.CheckOnly
                ? compilation.GetDiagnostics()
                : request.CLayout == GeneratedCLayout.Unity
                    ? compilation.EmitC(generated).Diagnostics
                    : (bundle = compilation.EmitCBundle()).Diagnostics;
            reporter?.Phase(request.CheckOnly ? "Semantic analysis complete." : "Semantic analysis and C lowering complete.");
            if (!request.CheckOnly && request.GeneratedHeaderPath is not null && !HasErrors(diagnostics))
                diagnostics = compilation.EmitCHeader(generatedHeader).Diagnostics;
            foreach (var diagnostic in diagnostics)
                (reporter ?? BuildReporter.Current)?.Diagnostic(diagnostic);
            if (HasErrors(diagnostics))
            {
                if (request.BuildNative)
                    RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath, request.SymbolMapPath,
                        request.DebugMapPath, request.DebugTargetPath, request.StackReportPath);
                return new CompilationOutcome(1, compilation.UsesInlineAssembly, diagnostics);
            }

            if (request.CheckOnly)
            {
                if (request.Trace)
                    Console.Error.WriteLine("trace: semantic analysis complete");
                return new CompilationOutcome(0, compilation.UsesInlineAssembly, diagnostics);
            }

            var changedOutputs = 0;
            if (request.CLayout == GeneratedCLayout.Unity)
                changedOutputs += AtomicFile.WriteTextIfChanged(request.GeneratedCPath!, generated.ToString()) ? 1 : 0;
            else
            {
                var artifacts = bundle!.Artifacts.AsEnumerable();
                if (request.Target == CompilationTarget.EspIdf)
                    artifacts = artifacts.Select(artifact => artifact.Kind == GeneratedCArtifactKind.CMakeFragment && artifact.RelativePath == "ctilde_sources.cmake"
                        ? artifact with { Content = NativeOptimizationSettings.AppendEspGeneratedSourceOptions(artifact.Content, request) }
                        : artifact);
                changedOutputs += WriteBundle(request.GeneratedDirectory!, artifacts, request.GeneratedHeaderPath);
            }
            if (request.GeneratedHeaderPath is not null)
                changedOutputs += AtomicFile.WriteTextIfChanged(request.GeneratedHeaderPath, generatedHeader.ToString()) ? 1 : 0;
            if (request.SymbolMapPath is not null)
            {
                using var map = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                compilation.EmitSymbolMap(map);
                changedOutputs += AtomicFile.WriteTextIfChanged(request.SymbolMapPath, map.ToString()) ? 1 : 0;
            }
            if (request.DebugMapPath is not null)
            {
                using var debugMap = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                compilation.EmitDebugMap(debugMap);
                changedOutputs += AtomicFile.WriteTextIfChanged(request.DebugMapPath, debugMap.ToString()) ? 1 : 0;
            }
            if (request.ManagedModule is not null)
            {
                using var metadata = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                compilation.EmitManagedModuleMetadata(metadata, request.ManagedModule);
                changedOutputs += AtomicFile.WriteTextIfChanged(request.ManagedModuleMetadataPath!, metadata.ToString()) ? 1 : 0;
            }
            if (request.Trace)
            {
                Console.Error.WriteLine("trace: semantic analysis and GNU C23 lowering complete");
                Console.Error.WriteLine($"trace: generated outputs changed={changedOutputs}");
                Console.Error.WriteLine($"trace: emitted {(request.CLayout == GeneratedCLayout.Unity ? request.GeneratedCPath : request.GeneratedDirectory)}");
                if (request.GeneratedHeaderPath is not null)
                    Console.Error.WriteLine($"trace: emitted {request.GeneratedHeaderPath}");
                if (request.ManagedModuleMetadataPath is not null)
                    Console.Error.WriteLine($"trace: emitted {request.ManagedModuleMetadataPath}");
            }
            reporter?.Detail($"Generated artifacts changed: {changedOutputs}");
            reporter?.Phase($"Generated C: {request.GeneratedCPath ?? request.GeneratedDirectory}");
            if (reporter?.Verbosity >= BuildVerbosity.Detailed)
            {
                foreach (var path in request.GeneratedSourcePaths)
                    reporter.Detail(path);
                if (request.GeneratedHeaderPath is not null)
                    reporter.Detail(request.GeneratedHeaderPath);
            }
            return new CompilationOutcome(0, compilation.UsesInlineAssembly, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (request.BuildNative)
                RemoveStaleGeneratedOutput(request.GeneratedCPath, request.GeneratedHeaderPath, request.SymbolMapPath,
                    request.DebugMapPath, request.DebugTargetPath, request.StackReportPath);
            var diagnostics = request.ManifestPath is null
                ? []
                : new[] { (reporter ?? BuildReporter.Current)!.InfrastructureDiagnostic(request.ManifestPath, "CT6003", exception.Message) };
            if (request.ManifestPath is null)
                Console.Error.WriteLine($"ctilde: {exception.Message}");
            return new CompilationOutcome(1, false, diagnostics);
        }
    }

    private static int CompileDirectory(CommandLineOptions options)
    {
        if (options.Target == CompilationTarget.EspIdf && options.SourceRoot is not null)
            return UsageError("--source-root is unavailable for ESP-IDF compilations.");
        if (options.Inputs.Count != 0 || options.Output is not null || options.HeaderOutput is not null ||
            options.CheckOnly || options.ProjectManifest is not null || options.Build || options.Run || options.Configuration is not null ||
            options.Compiler is not null || options.NativeOutput is not null || options.EspIdfProject is not null || options.EspIdfPath is not null ||
            options.CLayout is not null || options.OutputDirectory is not null || options.SymbolMap is not null || options.StackReport is not null || options.Lto ||
            options.Optimization is not null || options.CpuTarget is not null || options.FloatingPoint is not null || options.PgoMode is not null || options.PgoDirectory is not null ||
            options.DebugInfo || options.DebugMemory is not null || options.DebugMap is not null || options.PrepareDebug is not null || options.DebugTarget is not null || options.SerialPort is not null ||
            options.GenerateBindings || options.VerifyBindings || options.EspClangPath is not null || options.LinkerScript is not null || options.EntrySymbol is not null ||
            options.NativeSources.Count != 0 || options.ObjectFiles.Count != 0 || options.Libraries.Count != 0 || options.CompileOptions.Count != 0 || options.LinkOptions.Count != 0)
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
                var request = new BuildRequest([input], options.Target, options.Architecture, null, directory, sourceRoot, Path.ChangeExtension(input, ".c"),
                    null, false, options.Trace, false, false, CTildeNativeBuildConfiguration.Debug, "auto", null, null, null,
                    GeneratedCLayout.Unity, null, null, null, false);
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

    private readonly record struct CompilationOutcome(int ExitCode, bool UsesInlineAssembly, IReadOnlyList<Diagnostic> Diagnostics);

    private static void WriteReceipt(BuildRequest request, string operation, string state, IEnumerable<Diagnostic> diagnostics)
    {
        if (request.ManifestPath is not null)
            BuildDiagnosticReceipt.Write(request.ManifestPath, operation, state, diagnostics, request.Inputs);
    }

    private static int WriteBundle(string outputDirectory, IEnumerable<GeneratedCArtifact> artifacts, string? additionalOutput)
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

        var changed = 0;
        foreach (var artifact in materialized)
            changed += AtomicFile.WriteTextIfChanged(Path.Combine(outputDirectory, artifact.RelativePath), artifact.Content) ? 1 : 0;

        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (!expected.Contains(fullPath) && IsCompilerMarked(fullPath))
            {
                File.Delete(fullPath);
                changed++;
            }
        }
        return changed;
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

    private static string CommonSourceIdentityRoot(IEnumerable<string> inputs)
    {
        var paths = inputs.Select(Path.GetFullPath).ToArray();
        var candidate = Path.GetDirectoryName(paths[0])!;
        while (!paths.All(path =>
        {
            var relative = Path.GetRelativePath(candidate, path);
            return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
        }))
            candidate = Directory.GetParent(candidate)?.FullName ?? Path.GetPathRoot(candidate)!;
        return candidate;
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

    private static string TargetName(BuildRequest request) => request.Target switch
    {
        CompilationTarget.EspIdf when request.Environment == TargetEnvironment.Qemu && request.EspIdfChip == CTilde.EspIdfChip.Esp32 => "esp32_qemu",
        CompilationTarget.EspIdf when request.Environment == TargetEnvironment.Qemu => "esp32c3_qemu",
        CompilationTarget.EspIdf => "esp-idf",
        CompilationTarget.Freestanding => "freestanding",
        CompilationTarget.Cosmopolitan => "cosmopolitan",
        _ => "hosted",
    };

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: ctilde <input.ct>... -o <program.c> [--c-layout unity|modules] [--output-directory <directory>] [--symbol-map <path>] [--debug-info] [--debug-map <path>] [--header <exports.h>] [--target hosted|esp-idf|esp32_qemu|esp32c3_qemu|freestanding|cosmopolitan] [--architecture auto|x86|x64|arm32|arm64|xtensa|riscv32|riscv64] [--cpu-feature simd128] [--panic-policy abort|restart|halt] [--no-recursion] [--source-root <directory>] [--check] [--trace]");
        Console.Error.WriteLine("       ctilde <input.ct>... --build [--target hosted|esp-idf|esp32_qemu|esp32c3_qemu|freestanding|cosmopolitan] [native build options] [--trace]");
        Console.Error.WriteLine("       ctilde --project <ctilde.json> [--source-root <directory>] --build|--run [native build options] [--verbosity quiet|minimal|normal|detailed] [--trace]");
        Console.Error.WriteLine("       ctilde --project <ctilde.json> --generate-bindings|--verify-bindings [--idf-path <directory>] [--esp-clang <path>]");
        Console.Error.WriteLine("       ctilde --compile-directory <directory> [--target hosted|esp-idf|esp32_qemu|esp32c3_qemu|freestanding|cosmopolitan] [--source-root <directory>] [--trace]");
        Console.Error.WriteLine("       ctilde restore|update|vendor --project <ctilde.json>");
        Console.Error.WriteLine("       ctilde clean --project <ctilde.json> [--trace]");
        Console.Error.WriteLine("       ctilde format [--check] <file-or-directory>...");
        Console.Error.WriteLine("Native build options: --configuration debug|release --compiler <name|path> --native-output <path> [--lto] [--stack-report <report.json>]");
        Console.Error.WriteLine("                      --optimization speed|aggressive --cpu-target baseline|avx2 --floating-point precise|fast");
        Console.Error.WriteLine("                      --pgo off|generate|use [--pgo-directory <project-relative-directory>]");
        Console.Error.WriteLine("                          --idf-project <directory> --idf-path <directory>");
        Console.Error.WriteLine("Freestanding build: --linker-script <file> --entry-symbol <name> --native-source <file> --object <file> --library <file>");
        Console.Error.WriteLine("                    --compile-option <value> --link-option <value> --native-output <image>");
        Console.Error.WriteLine("Cosmopolitan build: --architecture x64 [--cosmopolitan-mode default|tiny|debug] [--compiler wsl:<cosmocc>] [--native-output <program.com>]");
        Console.Error.WriteLine("ESP-IDF bindings: --generate-bindings --verify-bindings --esp-clang <path>");
        Console.Error.WriteLine("Debug preparation: --prepare-debug launch|attach [--debug-target <descriptor.json>] [--debug-memory off|objects|guarded] [--serial-port <port>] [--baud-rate <rate>]");
    }

    private static int RunModuleCommand(string[] args)
    {
        if (args.Length != 3 || args[1] != "--project" || string.IsNullOrWhiteSpace(args[2]))
            return UsageError($"{args[0]} requires exactly --project <ctilde.json>.");
        try
        {
            var (root, modules) = CTildeProjectFile.ReadModuleReferences(args[2]);
            if (args[0] == "vendor")
                RepositoryModules.Vendor(root, modules);
            else
                RepositoryModules.Restore(root, modules, update: args[0] == "update");
            Console.WriteLine(args[0] switch
            {
                "restore" => $"Restored {modules.Length} exact module(s).",
                "update" => $"Updated and locked {modules.Length} module(s).",
                _ => $"Vendored {modules.Length} exact module(s).",
            });
            return 0;
        }
        catch (CTildeProjectException exception)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
    }
}
