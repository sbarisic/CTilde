using System.Text;
using System.Security.Cryptography;
using CTilde;

namespace CTilde.Cli;

internal sealed record NativeBuildOutcome(int ExitCode, string Backend, string? CompilerCommand, string? WslCompiler = null,
    IReadOnlyList<string>? StackUsageFiles = null);

internal static class NativeBuildDriver
{
    public static Task<NativeBuildOutcome> BuildAsync(BuildRequest request, bool usesInlineAssembly, CancellationToken cancellationToken) =>
        request.Target switch
        {
            CompilationTarget.Hosted => HostedBuildDriver.BuildAsync(request, usesInlineAssembly, cancellationToken),
            CompilationTarget.EspIdf => EspIdfBuildDriver.BuildAsync(request, cancellationToken),
            CompilationTarget.Freestanding => FreestandingBuildDriver.BuildAsync(request, cancellationToken),
            CompilationTarget.Cosmopolitan => CosmopolitanBuildDriver.BuildAsync(request, cancellationToken),
            _ => throw new NativeBuildException($"Unsupported native target '{request.Target}'."),
        };
}

internal static class HostedBuildDriver
{
    private static readonly Dictionary<string, string> WslDriveRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WslDriveRootsLock = new();

    public static async Task<NativeBuildOutcome> BuildAsync(BuildRequest request, bool usesInlineAssembly, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.ExecutablePath!)!);
        var compiler = await ResolveCompilerAsync(request.Compiler, request.RootDirectory, cancellationToken);
        if (request.StackReportPath is not null)
        {
            if (compiler.Kind == HostedCompilerKind.Msvc)
                throw new NativeBuildException("Static stack reporting requires GCC; MSVC does not emit the required stack and callgraph artifacts.");
            var identity = await ReadCompilerIdentityAsync(compiler, request.RootDirectory, cancellationToken);
            if (identity.Contains("clang", StringComparison.OrdinalIgnoreCase))
                throw new NativeBuildException("Static stack reporting requires GCC; Clang does not emit GCC callgraph-info artifacts.");
        }
        var compilerIdentity = request.PgoMode == NativePgoMode.Off
            ? string.Empty
            : await ReadCompilerIdentityAsync(compiler, request.RootDirectory, cancellationToken);
        var pgo = await PreparePgoAsync(compiler, compilerIdentity, request, cancellationToken);
        if (usesInlineAssembly && compiler.Kind == HostedCompilerKind.Msvc)
            throw new NativeBuildException("Inline assembly requires a GNU-compatible GCC or Clang compiler; MSVC is not supported for programs containing asm.");
        var operatingSystem = ResolveOperatingSystem(compiler);
        var configuredRuntimeFiles = request.Hosted?.RuntimeFiles ?? [];
        if (operatingSystem is null && configuredRuntimeFiles.Length != 0)
            throw new NativeBuildException("Hosted runtime files are currently supported only for Windows and Linux native toolchains.");
        var runtimeFiles = operatingSystem is null
            ? []
            : HostedRuntimeFileStager.Select(request, operatingSystem.Value);
        if (request.Trace)
            Console.Error.WriteLine($"trace: native compiler {compiler.Command}");
        var stackUsageFiles = Array.Empty<string>();
        int result;
        if (compiler.Kind == HostedCompilerKind.Msvc)
            result = await CompileMsvcAsync(compiler, request, pgo, cancellationToken);
        else
        {
            var gnu = await CompileGnuAsync(compiler, request, runtimeFiles.Count != 0, pgo, cancellationToken);
            result = gnu.ExitCode;
            stackUsageFiles = gnu.StackUsageFiles;
        }
        if (result == 0 && compiler.Kind == HostedCompilerKind.Msvc && request.PgoMode == NativePgoMode.Generate)
        {
            var runtime = Path.Combine(Path.GetDirectoryName(compiler.Command)!, "pgort140.dll");
            if (!File.Exists(runtime))
                throw new NativeBuildException($"MSVC PGO runtime was not found beside cl.exe: {runtime}");
            var destination = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, Path.GetFileName(runtime));
            File.Copy(runtime, destination, overwrite: true);
            if (request.Trace)
                Console.Error.WriteLine($"trace: staged MSVC PGO runtime {destination}");
        }
        if (result == 0)
            HostedRuntimeFileStager.Stage(request, runtimeFiles);
        if (result == 0 && request.Trace)
            Console.Error.WriteLine($"trace: wrote native executable {request.ExecutablePath}");
        return new NativeBuildOutcome(result, BackendName(compiler),
            compiler.Command, compiler.WslCompiler, stackUsageFiles);
    }

    private static string BackendName(HostedCompiler compiler)
        => ClassifyBackend(compiler.Kind == HostedCompilerKind.Msvc, compiler.Command, compiler.WslCompiler);

    internal static string ClassifyBackend(bool isMsvc, string compilerCommand, string? wslCompiler)
    {
        if (isMsvc)
            return "msvc";
        var executable = wslCompiler ?? Path.GetFileNameWithoutExtension(compilerCommand);
        return executable.Contains("clang", StringComparison.OrdinalIgnoreCase) ? "clang" : "gcc";
    }

    private static async Task<HostedCompiler> ResolveCompilerAsync(string configured, string workingDirectory, CancellationToken cancellationToken)
    {
        var value = configured;
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            value = Environment.GetEnvironmentVariable("CTILDE_CC") ?? "auto";
        if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsWindows() && value.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
            {
                var wsl = NativeToolDiscovery.FindOnPath("wsl") ?? throw new NativeBuildException("wsl.exe was not found.");
                return new HostedCompiler(wsl, HostedCompilerKind.WslGnu, null, value[4..]);
            }
            var known = value.ToLowerInvariant() switch
            {
                "msvc" => "cl",
                "gcc" => "gcc",
                "clang" => "clang",
                _ => value,
            };
            if (Path.GetFileNameWithoutExtension(known).Equals("cl", StringComparison.OrdinalIgnoreCase))
                return await ResolveMsvcAsync(known, workingDirectory, cancellationToken);
            var command = NativeToolDiscovery.FindOnPath(known) ?? throw new NativeBuildException($"Configured C compiler '{value}' was not found.");
            return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
        }

        if (OperatingSystem.IsWindows())
        {
            var pathCl = NativeToolDiscovery.FindOnPath("cl");
            if (pathCl is not null)
                return new HostedCompiler(pathCl, HostedCompilerKind.Msvc, null, null);
            try
            {
                return await ResolveMsvcAsync("cl", workingDirectory, cancellationToken);
            }
            catch (NativeBuildException)
            {
                foreach (var candidate in new[] { "clang", "gcc" })
                {
                    var command = NativeToolDiscovery.FindOnPath(candidate);
                    if (command is not null)
                        return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
                }
                throw;
            }
        }

        foreach (var candidate in new[] { "cc", "clang", "gcc" })
        {
            var command = NativeToolDiscovery.FindOnPath(candidate);
            if (command is not null)
                return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
        }
        throw new NativeBuildException("No hosted C compiler was found. Install MSVC, GCC, or Clang, or pass --compiler.");
    }

    private static async Task<HostedCompiler> ResolveMsvcAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var existing = NativeToolDiscovery.FindOnPath(command);
        if (existing is not null)
            return new HostedCompiler(existing, HostedCompilerKind.Msvc, null, null);
        if (!OperatingSystem.IsWindows())
            throw new NativeBuildException("MSVC is available only on Windows.");

        var vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vsWhere))
            throw new NativeBuildException("MSVC was not found and vswhere.exe is unavailable.");
        var discovery = await NativeProcessRunner.RunAsync(new NativeProcessRequest(vsWhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            workingDirectory, ForwardOutput: false), cancellationToken);
        var installation = discovery.StandardOutput.Trim();
        if (discovery.ExitCode != 0 || installation.Length == 0)
            throw new NativeBuildException("Visual Studio C tools were not found.");
        var vcVars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcVars))
            throw new NativeBuildException($"MSVC environment script was not found: {vcVars}");

        NativeProcessResult environmentResult;
        var environmentScript = Path.Combine(Path.GetTempPath(), $"ctilde-vcvars-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(environmentScript, $"@call \"{vcVars}\" >nul{Environment.NewLine}@set{Environment.NewLine}", Encoding.ASCII);
            environmentResult = await NativeProcessRunner.RunAsync(new NativeProcessRequest("cmd.exe",
                ["/d", "/c", environmentScript], workingDirectory, ForwardOutput: false), cancellationToken);
        }
        finally
        {
            if (File.Exists(environmentScript))
                File.Delete(environmentScript);
        }
        if (environmentResult.ExitCode != 0)
            throw new NativeBuildException($"Could not initialize the MSVC build environment: {environmentResult.StandardError.Trim()}");
        var environment = ParseEnvironment(environmentResult.StandardOutput);
        var cl = NativeToolDiscovery.FindOnPath("cl", environment.GetValueOrDefault("PATH"));
        if (cl is null)
            throw new NativeBuildException("The initialized Visual Studio environment did not contain cl.exe.");
        return new HostedCompiler(cl, HostedCompilerKind.Msvc, environment, null);
    }

    private static Dictionary<string, string> ParseEnvironment(string contents)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                environment[line[..separator]] = line[(separator + 1)..];
        }
        return environment;
    }

    private static async Task<int> CompileMsvcAsync(HostedCompiler compiler, BuildRequest request, PgoContext pgo, CancellationToken cancellationToken)
    {
        var common = new List<string> { "/nologo", "/std:clatest", "/W4", "/WX", "/wd4702" };
        common.AddRange(NativeOptimizationSettings.MsvcCompile(request));
        if (request.Lto)
            common.Add("/GL");
        common.AddRange(pgo.CompileFlags);
        if (request.Trace)
        {
            Console.Error.WriteLine($"trace: native profile {NativeOptimizationSettings.Describe(request)}");
            Console.Error.WriteLine($"trace: native compile flags {string.Join(' ', common)}");
        }
        var objects = new List<string>();
        foreach (var source in request.GeneratedSourcePaths.Concat(request.Hosted?.NativeSources ?? []))
        {
            var objectPath = pgo.Enabled ? PgoObjectPath(pgo, source, ".obj") : CachedObjectPath(request, compiler, source, string.Join('\n', common), ".obj");
            objects.Add(objectPath);
            if ((!pgo.Enabled || pgo.ReuseObjects) && File.Exists(objectPath))
            {
                if (request.Trace)
                    Console.Error.WriteLine($"trace: reused native object {Path.GetFileName(objectPath)}");
                continue;
            }
            var arguments = new List<string>(common)
            {
                "/c",
                $"/Fo:{objectPath}",
                $"/Fd:{Path.ChangeExtension(objectPath, ".pdb")}",
                source,
            };
            var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
            if (result.ExitCode != 0)
                return result.ExitCode;
        }

        var link = new List<string> { "/nologo", $"/Fe:{request.ExecutablePath}" };
        link.AddRange(objects);
        link.Add("/link");
        if (request.Configuration == CTildeNativeBuildConfiguration.Debug)
            link.Add("/DEBUG");
        if (request.Lto)
            link.Add("/LTCG");
        if (request.Configuration == CTildeNativeBuildConfiguration.Release)
            link.Add("/OPT:REF,ICF");
        link.AddRange(pgo.LinkFlags);
        if (request.Trace)
            Console.Error.WriteLine($"trace: native link flags {string.Join(' ', link)}");
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        return linked.ExitCode;
    }

    private static async Task<GnuCompileResult> CompileGnuAsync(
        HostedCompiler compiler,
        BuildRequest request,
        bool useExecutableRuntimePath,
        PgoContext pgo,
        CancellationToken cancellationToken)
    {
        var executable = request.ExecutablePath!;
        var prefix = Array.Empty<string>();
        if (compiler.Kind == HostedCompilerKind.WslGnu)
        {
            executable = await WslPathAsync(compiler.Command, executable, request.RootDirectory, cancellationToken);
            prefix = ["--exec", compiler.WslCompiler!];
        }
        var configuration = NativeOptimizationSettings.GnuCompile(request, includeSections: true).ToList();
        var compilerName = (compiler.WslCompiler ?? compiler.Command).ToLowerInvariant();
        if (request.Configuration == CTildeNativeBuildConfiguration.Debug && compilerName.Contains("gcc", StringComparison.Ordinal))
            configuration.Add("-fvar-tracking-assignments");
        var common = new List<string> { "-std=gnu23" };
        common.AddRange(configuration);
        var hostedSources = request.GeneratedSourcePaths.Concat(request.Hosted?.NativeSources ?? []).ToArray();
        var usesPthreads = hostedSources.Any(path => File.ReadAllText(path).Contains("pthread_", StringComparison.Ordinal));
        var usesDynamicLoader = request.GeneratedSourcePaths.Any(path => File.ReadAllText(path).Contains("dlopen(", StringComparison.Ordinal));
        if (usesPthreads)
            common.Add("-pthread");
        if (request.Lto)
            common.Add("-flto");
        if (request.StackReportPath is not null)
            common.AddRange(["-fstack-usage", "-fcallgraph-info=su"]);
        common.AddRange(pgo.CompileFlags);
        common.AddRange(["-Wall", "-Wextra", "-Werror"]);
        if (request.Trace)
        {
            Console.Error.WriteLine($"trace: native profile {NativeOptimizationSettings.Describe(request)}");
            Console.Error.WriteLine($"trace: native compile flags {string.Join(' ', common)}");
        }

        var objects = new List<string>();
        foreach (var originalSource in hostedSources)
        {
            var objectPath = pgo.Enabled ? PgoObjectPath(pgo, originalSource, ".o") : CachedObjectPath(request, compiler, originalSource, string.Join('\n', common), ".o");
            objects.Add(objectPath);
            var hasStackSidecars = request.StackReportPath is null ||
                (File.Exists(Path.ChangeExtension(objectPath, ".su")) && (request.Lto || File.Exists(Path.ChangeExtension(objectPath, ".ci"))));
            if (!pgo.Enabled && File.Exists(objectPath) && hasStackSidecars)
            {
                if (request.Trace)
                    Console.Error.WriteLine($"trace: reused native object {Path.GetFileName(objectPath)}");
                continue;
            }
            var source = originalSource;
            var output = objectPath;
            if (compiler.Kind == HostedCompilerKind.WslGnu)
            {
                source = await WslPathAsync(compiler.Command, source, request.RootDirectory, cancellationToken);
                output = await WslPathAsync(compiler.Command, output, request.RootDirectory, cancellationToken);
            }
            var arguments = new List<string>(prefix);
            arguments.AddRange(common);
            arguments.AddRange(["-c", source, "-o", output]);
            var first = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment, ForwardOutput: false), cancellationToken);
            if (first.ExitCode != 0 && RejectedCStandard(first))
            {
                arguments[prefix.Length] = "-std=gnu2x";
                if (request.Trace)
                    Console.Error.WriteLine("trace: compiler rejected gnu23; retrying with gnu2x");
                first = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                    Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment, ForwardOutput: false), cancellationToken);
            }
            if (first.ExitCode != 0)
            {
                Console.Out.Write(first.StandardOutput);
                Console.Error.Write(first.StandardError);
                return new GnuCompileResult(first.ExitCode, []);
            }
        }

        var linkedObjects = objects.ToArray();
        if (compiler.Kind == HostedCompilerKind.WslGnu)
            for (var index = 0; index < linkedObjects.Length; index++)
                linkedObjects[index] = await WslPathAsync(compiler.Command, linkedObjects[index], request.RootDirectory, cancellationToken);
        var link = new List<string>(prefix);
        link.AddRange(linkedObjects);
        link.AddRange(["-o", executable]);
        link.AddRange(NativeOptimizationSettings.GnuLink(request));
        link.AddRange(pgo.LinkFlags);
        if (request.StackReportPath is not null)
            link.AddRange(["-fstack-usage", "-fcallgraph-info=su"]);
        if (request.Configuration == CTildeNativeBuildConfiguration.Release)
            link.Add("-Wl,--gc-sections");
        if (usesPthreads)
            link.Add("-pthread");
        if (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows())
            link.Add("-lm");
        if (usesDynamicLoader && (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows()))
            link.Add("-ldl");
        if (useExecutableRuntimePath && (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows()))
            link.Add("-Wl,-rpath,$ORIGIN");
        if (request.Trace)
            Console.Error.WriteLine($"trace: native link flags {string.Join(' ', link)}");
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        var stackFiles = new List<string>();
        if (request.StackReportPath is not null)
        {
            foreach (var objectPath in objects)
                foreach (var extension in new[] { ".su", ".ci" })
                {
                    var sidecar = Path.ChangeExtension(objectPath, extension);
                    if (File.Exists(sidecar))
                        stackFiles.Add(sidecar);
                }
            var outputDirectory = Path.GetDirectoryName(request.ExecutablePath!)!;
            var ltransFiles = Directory.EnumerateFiles(outputDirectory, "*.ltrans*.su", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(outputDirectory, "*.ltrans*.ci", SearchOption.TopDirectoryOnly)).ToArray();
            if (request.Lto && ltransFiles.Length != 0)
                stackFiles = [.. ltransFiles];
            else
                stackFiles.AddRange(ltransFiles);
        }
        return new GnuCompileResult(linked.ExitCode, stackFiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed record GnuCompileResult(int ExitCode, string[] StackUsageFiles);

    private static string CachedObjectPath(BuildRequest request, HostedCompiler compiler, string source, string flags, string extension)
    {
        var cache = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-cache");
        Directory.CreateDirectory(cache);
        var identity = new StringBuilder()
            .Append("draft-").Append(CompilerContract.DraftVersion).Append('\n')
            .Append(compiler.Command).Append('\n')
            .Append(compiler.WslCompiler).Append('\n')
            .Append(File.Exists(compiler.Command) ? File.GetLastWriteTimeUtc(compiler.Command).Ticks : 0L).Append('\n')
            .Append(request.Configuration).Append('\n')
            .Append(NativeOptimizationSettings.Describe(request)).Append('\n')
            .Append(flags).Append('\n')
            .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))).Append('\n');
        if (request.CLayout == GeneratedCLayout.Modules)
        {
            foreach (var path in GeneratedHeaderClosure(source, request.GeneratedDirectory!))
                identity.Append(Path.GetRelativePath(request.GeneratedDirectory!, path).Replace('\\', '/')).Append(':')
                    .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
        return Path.Combine(cache, key + extension);
    }

    private static IEnumerable<string> GeneratedHeaderClosure(string source, string generatedDirectory)
    {
        var root = Path.GetFullPath(generatedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(Path.GetFullPath(source));
        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            foreach (var line in File.ReadLines(current))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("#include \"", StringComparison.Ordinal))
                    continue;
                var start = trimmed.IndexOf('"') + 1;
                var end = trimmed.IndexOf('"', start);
                if (end <= start)
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, trimmed[start..end]));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate) || !visited.Add(candidate))
                    continue;
                pending.Enqueue(candidate);
            }
        }
        return visited.Order(StringComparer.Ordinal);
    }

    private static string PgoObjectPath(PgoContext pgo, string source, string extension)
    {
        var directory = Path.Combine(pgo.Directory!, "objects");
        Directory.CreateDirectory(directory);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source)))).ToLowerInvariant()[..16];
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "-" + sourceHash + extension);
    }

    private static async Task<string> ReadCompilerIdentityAsync(HostedCompiler compiler, string workingDirectory, CancellationToken cancellationToken)
    {
        var arguments = compiler.Kind == HostedCompilerKind.Msvc
            ? new List<string> { "/Bv" }
            : compiler.Kind == HostedCompilerKind.WslGnu
                ? new List<string> { "--exec", compiler.WslCompiler!, "--version" }
                : new List<string> { "--version" };
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
            workingDirectory, compiler.Environment, ForwardOutput: false), cancellationToken);
        var output = (result.StandardOutput + result.StandardError).Trim();
        if (result.ExitCode != 0 && output.Length == 0)
            throw new NativeBuildException("Could not query the hosted compiler identity required for native build caching and PGO.");
        return $"{compiler.Command}\n{compiler.WslCompiler}\n{output}";
    }

    private static async Task<PgoContext> PreparePgoAsync(
        HostedCompiler compiler,
        string compilerIdentity,
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PgoMode == NativePgoMode.Off)
            return PgoContext.Off;
        var canonical = new StringBuilder()
            .Append("draft-").Append(CompilerContract.DraftVersion).Append('\n')
            .Append(compilerIdentity).Append('\n')
            .Append(request.Architecture).Append('\n')
            .Append(request.Optimization).Append('\n')
            .Append(request.CpuTarget).Append('\n')
            .Append(request.FloatingPoint).Append('\n')
            .Append(request.Lto).Append('\n');
        foreach (var source in request.GeneratedSourcePaths.OrderBy(path => path, StringComparer.Ordinal))
            canonical.Append(Path.GetFileName(source)).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))).Append('\n');
        var identityText = canonical.ToString();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
        var directory = Path.Combine(request.PgoDirectory!, identity);
        var marker = Path.Combine(directory, "identity.txt");
        if (request.PgoMode == NativePgoMode.Generate)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(marker) && !File.ReadAllText(marker).Equals(identityText, StringComparison.Ordinal))
                throw new NativeBuildException($"PGO profile identity in '{directory}' is stale; remove that identity directory and regenerate training data.");
            AtomicFile.WriteTextIfChanged(marker, identityText);
        }
        else
        {
            if (!File.Exists(marker))
                throw new NativeBuildException($"Matching PGO training data is absent in '{directory}'. Build with --pgo generate and run representative training first.");
            if (!File.ReadAllText(marker).Equals(identityText, StringComparison.Ordinal))
                throw new NativeBuildException($"PGO training data in '{directory}' is stale for the current generated C, compiler, or native settings.");
        }

        string ToolPath(string path) => compiler.Kind == HostedCompilerKind.WslGnu
            ? WslPathAsync(compiler.Command, path, request.RootDirectory, cancellationToken).GetAwaiter().GetResult()
            : path;
        var isMsvc = compiler.Kind == HostedCompilerKind.Msvc;
        var isClang = !isMsvc && compilerIdentity.Contains("clang", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> compileFlags;
        IReadOnlyList<string> linkFlags;
        if (isMsvc)
        {
            // The identity directory already isolates this profile. Keep the MSVC
            // database basename short because pgomgr still has MAX_PATH-sensitive
            // code paths when it derives the matching .pgc filenames.
            const string baseName = "ctilde";
            var database = Path.Combine(directory, baseName + ".pgd");
            if (request.PgoMode == NativePgoMode.Generate)
            {
                AtomicFile.WriteTextIfChanged(Path.Combine(directory, "training-environment.txt"), $"VCPROFILE_PATH={directory}{Environment.NewLine}");
                if (request.Trace)
                    Console.Error.WriteLine($"trace: MSVC PGO training requires VCPROFILE_PATH={directory}");
            }
            else if (!Directory.EnumerateFiles(directory, baseName + "!*.pgc", SearchOption.TopDirectoryOnly).Any())
                throw new NativeBuildException($"MSVC PGO training did not produce .pgc data for identity '{identity}'. Run the generate-profile executable with VCPROFILE_PATH='{directory}' before --pgo use.");
            compileFlags = [];
            linkFlags = request.PgoMode == NativePgoMode.Generate
                ? [$"/GENPROFILE:PGD={database}"]
                : [$"/USEPROFILE:PGD={database}"];
        }
        else if (isClang)
        {
            if (request.PgoMode == NativePgoMode.Generate)
            {
                var pattern = ToolPath(Path.Combine(directory, "default-%p.profraw"));
                compileFlags = [$"-fprofile-instr-generate={pattern}"];
                linkFlags = [$"-fprofile-instr-generate={pattern}"];
            }
            else
            {
                var rawProfiles = Directory.EnumerateFiles(directory, "*.profraw", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
                if (rawProfiles.Length == 0)
                    throw new NativeBuildException($"Clang PGO training did not produce .profraw data beneath '{directory}'. Run the generate-profile executable before --pgo use.");
                var merged = Path.Combine(directory, "merged.profdata");
                await MergeClangProfilesAsync(compiler, compilerIdentity, request, rawProfiles, merged, cancellationToken);
                var toolMerged = ToolPath(merged);
                compileFlags = [$"-fprofile-instr-use={toolMerged}"];
                linkFlags = [$"-fprofile-instr-use={toolMerged}"];
            }
        }
        else
        {
            var toolDirectory = ToolPath(directory);
            if (request.PgoMode == NativePgoMode.Use && !Directory.EnumerateFiles(directory, "*.gcda", SearchOption.AllDirectories).Any())
                throw new NativeBuildException($"GCC PGO training did not produce .gcda data beneath '{directory}'. Run the generate-profile executable before --pgo use.");
            compileFlags = request.PgoMode == NativePgoMode.Generate
                ? [$"-fprofile-generate={toolDirectory}"]
                : [$"-fprofile-use={toolDirectory}", "-fprofile-correction"];
            linkFlags = compileFlags;
        }
        if (request.Trace)
            Console.Error.WriteLine($"trace: PGO identity {identity} directory {directory}");
        return new PgoContext(directory, compileFlags, linkFlags, isMsvc && request.PgoMode == NativePgoMode.Use);
    }

    private static async Task MergeClangProfilesAsync(
        HostedCompiler compiler,
        string compilerIdentity,
        BuildRequest request,
        IReadOnlyList<string> profiles,
        string output,
        CancellationToken cancellationToken)
    {
        var version = System.Text.RegularExpressions.Regex.Match(compilerIdentity, @"clang version\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
        var candidates = string.IsNullOrEmpty(version) ? new[] { "llvm-profdata" } : new[] { $"llvm-profdata-{version}", "llvm-profdata" };
        foreach (var candidate in candidates)
        {
            string command;
            var prefix = new List<string>();
            if (compiler.Kind == HostedCompilerKind.WslGnu)
            {
                command = compiler.Command;
                prefix.AddRange(["--exec", candidate]);
            }
            else
            {
                command = NativeToolDiscovery.FindOnPath(candidate) ?? string.Empty;
                if (command.Length == 0)
                    continue;
            }
            var arguments = new List<string>(prefix) { "merge", "-o" };
            arguments.Add(compiler.Kind == HostedCompilerKind.WslGnu
                ? await WslPathAsync(compiler.Command, output, request.RootDirectory, cancellationToken)
                : output);
            foreach (var profile in profiles)
                arguments.Add(compiler.Kind == HostedCompilerKind.WslGnu
                    ? await WslPathAsync(compiler.Command, profile, request.RootDirectory, cancellationToken)
                    : profile);
            var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(command, arguments, request.RootDirectory,
                compiler.Environment, ForwardOutput: false), cancellationToken);
            if (result.ExitCode == 0)
            {
                if (request.Trace)
                    Console.Error.WriteLine($"trace: merged Clang profiles with {candidate}");
                return;
            }
        }
        throw new NativeBuildException($"Could not discover a matching llvm-profdata tool. Tried {string.Join(", ", candidates)}.");
    }

    private static async Task<string> WslPathAsync(string wsl, string path, string workingDirectory, CancellationToken cancellationToken)
    {
        var windowsRoot = OperatingSystem.IsWindows() ? Path.GetPathRoot(path) : null;
        if (windowsRoot is { Length: 3 } && windowsRoot[1] == ':' && windowsRoot[2] == Path.DirectorySeparatorChar)
        {
            var key = wsl + "\n" + windowsRoot;
            string? wslRoot;
            lock (WslDriveRootsLock)
                WslDriveRoots.TryGetValue(key, out wslRoot);
            if (wslRoot is null)
            {
                var rootResult = await NativeProcessRunner.RunAsync(new NativeProcessRequest(wsl,
                    ["--exec", "wslpath", "-a", "-u", windowsRoot], workingDirectory, ForwardOutput: false), cancellationToken);
                if (rootResult.ExitCode != 0 || string.IsNullOrWhiteSpace(rootResult.StandardOutput))
                    throw new NativeBuildException($"Could not translate Windows drive root '{windowsRoot}' to a WSL path: {rootResult.StandardError.Trim()}");
                wslRoot = rootResult.StandardOutput.Trim().TrimEnd('/');
                lock (WslDriveRootsLock)
                    WslDriveRoots[key] = wslRoot;
            }

            var relative = Path.GetRelativePath(windowsRoot, Path.GetFullPath(path)).Replace('\\', '/');
            return relative == "." ? wslRoot : $"{wslRoot}/{relative}";
        }

        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(wsl,
            ["--exec", "wslpath", "-a", "-u", path], workingDirectory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new NativeBuildException($"Could not translate '{path}' to a WSL path: {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    private static bool RejectedCStandard(NativeProcessResult result)
    {
        var output = result.StandardOutput + result.StandardError;
        return output.Contains("gnu23", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("invalid value", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record HostedCompiler(
        string Command,
        HostedCompilerKind Kind,
        IReadOnlyDictionary<string, string>? Environment,
        string? WslCompiler);
    private sealed record PgoContext(string? Directory, IReadOnlyList<string> CompileFlags, IReadOnlyList<string> LinkFlags, bool ReuseObjects)
    {
        public static PgoContext Off { get; } = new(null, [], [], false);
        public bool Enabled => Directory is not null;
    }
    private enum HostedCompilerKind { Msvc, Gnu, WslGnu }

    private static HostedOperatingSystem? ResolveOperatingSystem(HostedCompiler compiler)
    {
        if (compiler.Kind == HostedCompilerKind.Msvc || compiler.Kind == HostedCompilerKind.Gnu && OperatingSystem.IsWindows())
            return HostedOperatingSystem.Windows;
        if (compiler.Kind == HostedCompilerKind.WslGnu || OperatingSystem.IsLinux())
            return HostedOperatingSystem.Linux;
        return null;
    }
}

internal static class FreestandingBuildDriver
{
    public static async Task<NativeBuildOutcome> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Freestanding ?? throw new NativeBuildException("Freestanding build settings are missing.");
        var image = request.ExecutablePath ?? throw new NativeBuildException("Freestanding image output is missing.");
        var compiler = ResolveCompiler(request.Compiler);
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        var compilerIdentity = await ValidateCompilerAsync(compiler, request, cancellationToken);
        if (request.StackReportPath is not null && compilerIdentity.Contains("clang", StringComparison.OrdinalIgnoreCase))
            throw new NativeBuildException("Static stack reporting requires GCC; Clang does not emit GCC callgraph-info artifacts.");

        var configuration = NativeOptimizationSettings.GnuCompile(request, includeSections: false);
        var common = new List<string>
        {
            "-std=gnu23", "-ffreestanding", "-fno-builtin", "-fno-stack-protector",
            "-fno-pie", "-ffunction-sections", "-fdata-sections", "-Wall", "-Wextra", "-Werror",
        };
        common.AddRange(configuration);
        common.AddRange(ArchitectureFlags(request.Architecture));
        if (request.Lto)
            common.Add("-flto");
        if (request.StackReportPath is not null)
            common.AddRange(["-fstack-usage", "-fcallgraph-info=su"]);
        common.AddRange(settings.CompileOptions);
        if (request.Trace)
        {
            Console.Error.WriteLine($"trace: native profile {NativeOptimizationSettings.Describe(request)}");
            Console.Error.WriteLine($"trace: native compile flags {string.Join(' ', common)}");
        }

        var sources = request.GeneratedSourcePaths.Concat(settings.NativeSources).ToArray();
        var objects = new List<string>();
        var useGnu2x = false;
        foreach (var source in sources)
        {
            var assembly = Path.GetExtension(source) is ".s" or ".S";
            var flags = assembly ? common.Where(value => value != "-std=gnu23").ToArray() : common.ToArray();
            if (useGnu2x)
                for (var index = 0; index < flags.Length; index++)
                    if (flags[index] == "-std=gnu23")
                        flags[index] = "-std=gnu2x";
            var objectPath = CachedObjectPath(request, compiler, source, flags);
            objects.Add(objectPath);
            var hasStackSidecars = request.StackReportPath is null ||
                (File.Exists(Path.ChangeExtension(objectPath, ".su")) && (request.Lto || File.Exists(Path.ChangeExtension(objectPath, ".ci"))));
            if (File.Exists(objectPath) && hasStackSidecars)
            {
                if (request.Trace)
                    Console.Error.WriteLine($"trace: reused freestanding object {Path.GetFileName(objectPath)}");
                continue;
            }
            var arguments = new List<string>(compiler.Prefix);
            arguments.AddRange(flags);
            arguments.Add("-c");
            arguments.Add(await ToolPathAsync(compiler, source, request.RootDirectory, cancellationToken));
            arguments.Add("-o");
            arguments.Add(await ToolPathAsync(compiler, objectPath, request.RootDirectory, cancellationToken));
            var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                request.RootDirectory, ForwardOutput: false), cancellationToken);
            var standardIndex = arguments.IndexOf("-std=gnu23");
            if (result.ExitCode != 0 && standardIndex >= 0 && RejectedCStandard(result))
            {
                arguments[standardIndex] = "-std=gnu2x";
                if (request.Trace)
                    Console.Error.WriteLine("trace: freestanding compiler rejected gnu23; retrying with gnu2x");
                result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                    request.RootDirectory, ForwardOutput: false), cancellationToken);
                useGnu2x = result.ExitCode == 0;
            }
            if (result.ExitCode != 0)
            {
                Console.Out.Write(result.StandardOutput);
                Console.Error.Write(result.StandardError);
                return new NativeBuildOutcome(result.ExitCode, "freestanding", compiler.Command, compiler.WslCompiler);
            }
        }

        var link = new List<string>(compiler.Prefix);
        foreach (var path in objects.Concat(settings.ObjectFiles).Concat(settings.Libraries))
            link.Add(await ToolPathAsync(compiler, path, request.RootDirectory, cancellationToken));
        link.AddRange(["-nostdlib", "-nostartfiles", "-no-pie", "-Wl,--gc-sections"]);
        link.AddRange(ArchitectureFlags(request.Architecture));
        link.AddRange(NativeOptimizationSettings.GnuLink(request));
        if (request.StackReportPath is not null)
            link.AddRange(["-fstack-usage", "-fcallgraph-info=su"]);
        link.Add("-T");
        link.Add(await ToolPathAsync(compiler, settings.LinkerScriptPath!, request.RootDirectory, cancellationToken));
        link.Add($"-Wl,-e,{settings.EntrySymbol}");
        link.AddRange(settings.LinkOptions);
        link.Add("-o");
        link.Add(await ToolPathAsync(compiler, image, request.RootDirectory, cancellationToken));
        if (request.Trace)
            Console.Error.WriteLine($"trace: native link flags {string.Join(' ', link)}");
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            request.RootDirectory, ForwardOutput: true), cancellationToken);
        if (linked.ExitCode == 0 && request.Trace)
            Console.Error.WriteLine($"trace: wrote freestanding image {image}");
        var objectStackFiles = objects.SelectMany(path => new[] { Path.ChangeExtension(path, ".su"), Path.ChangeExtension(path, ".ci") }).Where(File.Exists);
        var ltransStackFiles = Directory.EnumerateFiles(Path.GetDirectoryName(image)!, "*.ltrans*.su", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(image)!, "*.ltrans*.ci", SearchOption.TopDirectoryOnly)).ToArray();
        var stackFiles = request.StackReportPath is null ? [] : (request.Lto && ltransStackFiles.Length != 0 ? ltransStackFiles : objectStackFiles.Concat(ltransStackFiles))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        return new NativeBuildOutcome(linked.ExitCode, "freestanding-gcc", compiler.Command, compiler.WslCompiler, stackFiles);
    }

    private static FreestandingCompiler ResolveCompiler(string configured)
    {
        var value = configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("CTILDE_CC") ?? "auto"
            : configured;
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in new[] { "gcc", "clang" })
                if (NativeToolDiscovery.FindOnPath(candidate) is { } command)
                    return new FreestandingCompiler(command, [], null);
            throw new NativeBuildException("No GNU-compatible ELF compiler was found. Pass --compiler gcc, clang, wsl:gcc, or a cross-compiler path.");
        }
        if (OperatingSystem.IsWindows() && value.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var wsl = NativeToolDiscovery.FindOnPath("wsl") ?? throw new NativeBuildException("wsl.exe was not found.");
            var nested = value[4..];
            if (string.IsNullOrWhiteSpace(nested))
                throw new NativeBuildException("A compiler name is required after 'wsl:'.");
            return new FreestandingCompiler(wsl, ["--exec", nested], nested);
        }
        var known = value.ToLowerInvariant() switch
        {
            "gcc" => "gcc",
            "clang" => "clang",
            _ => value,
        };
        if (Path.GetFileNameWithoutExtension(known).Equals("cl", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(known).Equals("clang-cl", StringComparison.OrdinalIgnoreCase))
            throw new NativeBuildException("CT4116: Freestanding builds require a GNU-compatible ELF GCC or Clang driver; MSVC and clang-cl are unsupported.");
        var resolved = NativeToolDiscovery.FindOnPath(known) ?? throw new NativeBuildException($"Configured freestanding compiler '{value}' was not found.");
        return new FreestandingCompiler(resolved, [], null);
    }

    private static async Task<string> ValidateCompilerAsync(FreestandingCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
    {
        var versionArgs = new List<string>(compiler.Prefix) { "--version" };
        var version = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, versionArgs,
            request.RootDirectory, ForwardOutput: false), cancellationToken);
        var versionText = version.StandardOutput + version.StandardError;
        if (version.ExitCode != 0 || (!versionText.Contains("gcc", StringComparison.OrdinalIgnoreCase) &&
            !versionText.Contains("clang", StringComparison.OrdinalIgnoreCase)))
            throw new NativeBuildException("Freestanding builds require a GNU-compatible GCC or Clang compiler driver.");

        var probe = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-architecture-probe.c");
        File.WriteAllText(probe, string.Empty, Encoding.ASCII);
        var arguments = new List<string>(compiler.Prefix);
        arguments.AddRange(ArchitectureFlags(request.Architecture));
        arguments.AddRange(request.Freestanding?.CompileOptions ?? []);
        arguments.AddRange(["-dM", "-E", "-x", "c", await ToolPathAsync(compiler, probe, request.RootDirectory, cancellationToken)]);
        var macros = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
            request.RootDirectory, ForwardOutput: false), cancellationToken);
        if (macros.ExitCode != 0)
            throw new NativeBuildException($"Could not inspect freestanding compiler target macros: {macros.StandardError.Trim()}");
        if (!MatchesArchitecture(macros.StandardOutput, request.Architecture))
            throw new NativeBuildException($"Freestanding compiler target does not match declared architecture '{request.Architecture}'.");
        return versionText;
    }

    private static bool MatchesArchitecture(string macros, CompilationArchitecture architecture) => architecture switch
    {
        CompilationArchitecture.X86 => macros.Contains("#define __i386__", StringComparison.Ordinal),
        CompilationArchitecture.X64 => macros.Contains("#define __x86_64__", StringComparison.Ordinal),
        CompilationArchitecture.Arm32 => macros.Contains("#define __arm__", StringComparison.Ordinal),
        CompilationArchitecture.Arm64 => macros.Contains("#define __aarch64__", StringComparison.Ordinal),
        CompilationArchitecture.Xtensa => macros.Contains("#define __XTENSA__", StringComparison.Ordinal),
        CompilationArchitecture.RiscV32 => macros.Contains("#define __riscv_xlen 32", StringComparison.Ordinal),
        CompilationArchitecture.RiscV64 => macros.Contains("#define __riscv_xlen 64", StringComparison.Ordinal),
        _ => false,
    };

    private static bool RejectedCStandard(NativeProcessResult result)
    {
        var output = result.StandardOutput + result.StandardError;
        return output.Contains("gnu23", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("invalid value", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ArchitectureFlags(CompilationArchitecture architecture) => architecture switch
    {
        CompilationArchitecture.X86 => ["-m32"],
        CompilationArchitecture.X64 => ["-m64"],
        _ => [],
    };

    private static string CachedObjectPath(BuildRequest request, FreestandingCompiler compiler, string source, IReadOnlyList<string> flags)
    {
        var directory = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-cache");
        Directory.CreateDirectory(directory);
        var identity = new StringBuilder()
            .Append("draft-").Append(CompilerContract.DraftVersion).Append('\n')
            .Append(compiler.Command).Append('\n').Append(compiler.WslCompiler).Append('\n')
            .Append(request.Architecture).Append('\n').AppendJoin('\n', flags).Append('\n')
            .Append(NativeOptimizationSettings.Describe(request)).Append('\n')
            .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))).Append('\n');
        if (request.CLayout == GeneratedCLayout.Modules)
        {
            foreach (var path in GeneratedHeaderClosure(source, request.GeneratedDirectory!))
                identity.Append(Path.GetRelativePath(request.GeneratedDirectory!, path).Replace('\\', '/')).Append(':')
                    .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
        return Path.Combine(directory, hash + ".o");
    }

    private static IEnumerable<string> GeneratedHeaderClosure(string source, string generatedDirectory)
    {
        var root = Path.GetFullPath(generatedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(Path.GetFullPath(source));
        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            foreach (var line in File.ReadLines(current))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("#include \"", StringComparison.Ordinal))
                    continue;
                var start = trimmed.IndexOf('"') + 1;
                var end = trimmed.IndexOf('"', start);
                if (end <= start)
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, trimmed[start..end]));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate) || !visited.Add(candidate))
                    continue;
                pending.Enqueue(candidate);
            }
        }
        return visited.Order(StringComparer.Ordinal);
    }

    private static async Task<string> ToolPathAsync(FreestandingCompiler compiler, string path, string workingDirectory, CancellationToken cancellationToken)
    {
        if (compiler.WslCompiler is null)
            return path;
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command,
            ["--exec", "wslpath", "-a", "-u", path], workingDirectory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new NativeBuildException($"Could not translate '{path}' to a WSL path: {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    private sealed record FreestandingCompiler(string Command, IReadOnlyList<string> Prefix, string? WslCompiler);
}

internal static class EspIdfBuildDriver
{
    public static async Task ReconfigureForBindingsAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var process = CreateIdfRequest(request, ["-D", "CMAKE_EXPORT_COMPILE_COMMANDS=ON", "reconfigure"]);
        if (request.Trace)
            Console.Error.WriteLine($"trace: preparing ESP-IDF header context in {request.EspIdfProjectDirectory}");
        var result = await NativeProcessRunner.RunAsync(process, cancellationToken);
        if (result.ExitCode != 0)
            throw new NativeBuildException($"ESP-IDF binding reconfigure failed with exit code {result.ExitCode}.");
    }

    public static async Task<NativeBuildOutcome> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var project = request.EspIdfProjectDirectory!;
        if (!Directory.Exists(project) || !File.Exists(Path.Combine(project, "CMakeLists.txt")))
            throw new NativeBuildException($"ESP-IDF project '{project}' must contain CMakeLists.txt.");
        var componentDirectory = Path.Combine(project, "main");
        var componentFile = Path.Combine(componentDirectory, "CMakeLists.txt");
        if (!File.Exists(componentFile))
            throw new NativeBuildException($"ESP-IDF project '{project}' must contain main/CMakeLists.txt.");
        if (request.PanicPolicy == EspIdfPanicPolicy.Halt)
            ValidateHaltPanicConfiguration(project);
        var componentContents = File.ReadAllText(componentFile);
        if (request.ManagedModule is not null)
        {
            var projectCmake = File.ReadAllText(Path.Combine(project, "CMakeLists.txt"));
            if (!projectCmake.Contains("include(elf_loader)", StringComparison.Ordinal) ||
                !projectCmake.Contains("project_so(", StringComparison.Ordinal))
                throw new NativeBuildException("ESP-IDF managed-module projects must include elf_loader and call project_so(...) in CMakeLists.txt.");
        }
        if (request.CLayout == GeneratedCLayout.Modules)
        {
            var fragment = Path.GetRelativePath(componentDirectory, Path.Combine(request.GeneratedDirectory!, "ctilde_sources.cmake")).Replace('\\', '/');
            if (fragment.StartsWith("../", StringComparison.Ordinal) || !componentContents.Contains(fragment, StringComparison.Ordinal))
                throw new NativeBuildException($"ESP-IDF main/CMakeLists.txt must include generated fragment '{fragment}'.");
        }
        else
        {
            var generatedRelativePath = Path.GetRelativePath(componentDirectory, request.GeneratedCPath!).Replace('\\', '/');
            if (generatedRelativePath.StartsWith("../", StringComparison.Ordinal) || !componentContents.Contains(generatedRelativePath, StringComparison.Ordinal))
                throw new NativeBuildException($"ESP-IDF main/CMakeLists.txt must register generated source '{generatedRelativePath}'.");
        }
        if (request.BindingManifests is { Count: > 0 })
        {
            var bindingFragment = Path.GetRelativePath(componentDirectory, Path.Combine(request.BindingGeneratedDirectory!, "ctilde_bindings.cmake")).Replace('\\', '/');
            if (bindingFragment.StartsWith("../", StringComparison.Ordinal) || !componentContents.Contains(bindingFragment, StringComparison.Ordinal) ||
                !componentContents.Contains("CTILDE_BINDING_SOURCES", StringComparison.Ordinal) ||
                !componentContents.Contains("CTILDE_BINDING_REQUIRES", StringComparison.Ordinal))
                throw new NativeBuildException($"ESP-IDF main/CMakeLists.txt must include generated binding fragment '{bindingFragment}' and register CTILDE_BINDING_SOURCES and CTILDE_BINDING_REQUIRES.");
        }

        var buildArguments = new List<string>();
        AddStackInstrumentationCMakeArguments(request, buildArguments);
        buildArguments.Add(request.ManagedModule is null ? "build" : "so");
        var process = CreateIdfRequest(request, buildArguments);
        if (request.Trace)
            Console.Error.WriteLine($"trace: running ESP-IDF build in {project}");
        var result = await NativeProcessRunner.RunAsync(process, cancellationToken);
        if (result.ExitCode == 0 && request.ManagedModule is not null)
            PublishManagedModule(request);
        var stackFiles = request.StackReportPath is null || result.ExitCode != 0 ? [] :
            Directory.EnumerateFiles(request.EspIdfBuildDirectory, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".su", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ci", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray();
        return new NativeBuildOutcome(result.ExitCode, "esp-idf-gcc", "ESP-IDF GCC", null, stackFiles);
    }

    private static void PublishManagedModule(BuildRequest request)
    {
        var expected = Path.Combine(request.EspIdfBuildDirectory, request.ManagedModule!.Name + ".so");
        var candidates = File.Exists(expected)
            ? new[] { expected }
            : Directory.EnumerateFiles(request.EspIdfBuildDirectory, "*.so", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length != 1)
            throw new NativeBuildException($"Managed-module build did not produce the unique shared object '{expected}'.");
        ValidateManagedElf(candidates[0], request.Architecture);
        Directory.CreateDirectory(request.ManagedModuleOutputDirectory!);
        var temporary = request.ManagedModuleArtifactPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var metadata = ManagedModuleMetadata.Load(request.ManagedModuleMetadataPath!);
            if (metadata.HasOverlays)
            {
                BuildReporter.Current?.Phase("Extracting managed overlays and relinking resident ELF...");
                metadata = ManagedOverlayPackager.Package(request, candidates[0], temporary);
                AtomicFile.WriteTextIfChanged(request.ManagedModuleMetadataPath!, metadata.ToDeterministicJson());
                BuildReporter.Current?.Detail($"Resident executable and {metadata.Overlays.Length} overlay payload(s); largest window {metadata.MaximumOverlayBytes} bytes");
            }
            else
                File.Copy(candidates[0], temporary, overwrite: false);
            File.Move(temporary, request.ManagedModuleArtifactPath!, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateManagedElf(string path, CompilationArchitecture architecture)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 52 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F' ||
            bytes[4] != 1 || bytes[5] != 1)
            throw new NativeBuildException($"Managed-module output '{path}' is not a 32-bit little-endian ELF file.");
        var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16, 2));
        var machine = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18, 2));
        var expectedMachine = architecture switch
        {
            CompilationArchitecture.Xtensa => (ushort)94,
            CompilationArchitecture.RiscV32 => (ushort)243,
            _ => (ushort)0,
        };
        if (type != 3 || expectedMachine == 0 || machine != expectedMachine)
            throw new NativeBuildException($"Managed-module output '{path}' has ELF type {type} and machine {machine}; expected ET_DYN for {architecture}.");
        ReadOnlySpan<byte> magic = [(byte)'C', (byte)'T', (byte)'M', (byte)'O', (byte)'D', (byte)CompilerContract.ManagedModuleAbiVersion, 0, 0];
        if (bytes.AsSpan().IndexOf(magic) < 0)
            throw new NativeBuildException($"Managed-module output '{path}' does not contain the Module ABI {CompilerContract.ManagedModuleAbiVersion} preflight manifest.");
    }

    private static void AddStackInstrumentationCMakeArguments(BuildRequest request, List<string> arguments)
    {
        var cacheExists = File.Exists(Path.Combine(request.EspIdfBuildDirectory, "CMakeCache.txt"));
        if (!cacheExists && request.StackReportPath is null)
            return;
        foreach (var variable in new[] { "CMAKE_C_FLAGS", "CMAKE_CXX_FLAGS" })
        {
            var existing = ReadCMakeCacheValue(request.EspIdfBuildDirectory, variable);
            var value = existing
                .Replace("-fstack-usage", string.Empty, StringComparison.Ordinal)
                .Replace("-fcallgraph-info=su", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (request.StackReportPath is not null)
                value = (value + " -fstack-usage -fcallgraph-info=su").Trim();
            else if (existing == value)
                continue;
            arguments.Add("-D");
            arguments.Add(variable + "=" + value);
        }
    }

    private static string ReadCMakeCacheValue(string buildDirectory, string variable)
    {
        var cache = Path.Combine(buildDirectory, "CMakeCache.txt");
        if (!File.Exists(cache))
            return string.Empty;
        var prefix = variable + ":STRING=";
        var line = File.ReadLines(cache).FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? string.Empty : line[prefix.Length..];
    }

    public static async Task PrepareDebugLaunchAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        if (request.Environment == TargetEnvironment.Qemu)
        {
            ValidateQemuTooling(request);
            return;
        }
        var project = request.EspIdfProjectDirectory!;
        ValidateDebugConfiguration(project);
        var process = CreateIdfRequest(request, ["-p", request.SerialPort!, "flash"]);
        if (request.Trace)
            Console.Error.WriteLine($"trace: flashing ESP-IDF debug firmware to {request.SerialPort}");
        var result = await NativeProcessRunner.RunAsync(process, cancellationToken);
        if (result.ExitCode != 0)
            throw new NativeBuildException($"ESP-IDF debug flash failed with exit code {result.ExitCode}.");
    }

    private static void ValidateDebugConfiguration(string project)
    {
        var sdkconfig = Path.Combine(project, "sdkconfig");
        if (!File.Exists(sdkconfig))
            throw new NativeBuildException($"ESP-IDF debug configuration was not generated: {sdkconfig}");
        var settings = File.ReadAllLines(sdkconfig).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
        {
            "CONFIG_ESP_SYSTEM_GDBSTUB_RUNTIME=y",
            "CONFIG_ESP_GDBSTUB_SUPPORT_TASKS=y",
            "CONFIG_COMPILER_OPTIMIZATION_DEBUG=y",
        })
            if (!settings.Contains(required))
                throw new NativeBuildException($"ESP-IDF runtime debugging requires '{required}' in sdkconfig.");
    }

    private static void ValidateHaltPanicConfiguration(string project)
    {
        var sdkconfig = Path.Combine(project, "sdkconfig");
        var configuration = File.Exists(sdkconfig) ? sdkconfig : Path.Combine(project, "sdkconfig.defaults");
        if (!File.Exists(configuration) || !File.ReadLines(configuration).Any(line => line.Equals("CONFIG_ESP_SYSTEM_PANIC_PRINT_HALT=y", StringComparison.Ordinal)))
            throw new NativeBuildException("ESP-IDF panic policy 'halt' requires CONFIG_ESP_SYSTEM_PANIC_PRINT_HALT=y in the effective sdkconfig.");
    }

    internal static NativeProcessRequest CreateQemuLaunchRequest(BuildRequest request) => CreateIdfRequest(request, ["qemu", "--gdb"]);

    internal static NativeProcessRequest CreateIdfRequest(BuildRequest request, IReadOnlyList<string> arguments)
    {
        var project = request.EspIdfProjectDirectory!;
        var qemuDirectory = request.Environment == TargetEnvironment.Qemu
            ? Path.GetDirectoryName(FindQemuExecutable(request) ?? string.Empty)
            : null;
        if (request.Environment == TargetEnvironment.Qemu)
        {
            var target = request.EspIdfChip == CTilde.EspIdfChip.Esp32 ? "esp32" : "esp32c3";
            var sdkconfig = Path.Combine(project, $"sdkconfig.{(target == "esp32" ? "esp32_qemu" : "esp32c3_qemu")}");
            arguments = ["-B", request.EspIdfBuildDirectory, "-D", $"IDF_TARGET={target}", "-D", $"SDKCONFIG={sdkconfig}", .. arguments];
        }
        var idfCommand = NativeToolDiscovery.FindOnPath("idf.py");
        NativeProcessRequest process;
        if (idfCommand is not null)
        {
            process = OperatingSystem.IsWindows()
                ? CreateWindowsPythonRequest(idfCommand, project, arguments)
                : new NativeProcessRequest(idfCommand, arguments, project);
        }
        else
        {
            var activeRequest = CreateActiveEnvironmentRequest(project, request.EspIdfPath, arguments);
            if (activeRequest is not null)
                process = activeRequest;
            else
            {
                var idfPath = EspIdfEnvironment.ResolveIdfPath(request.EspIdfPath);
                if (idfPath is null)
                    throw new NativeBuildException("ESP-IDF tools are not active. Open an ESP-IDF terminal or pass --idf-path.");
                process = CreateActivatedRequest(idfPath, project, arguments, qemuDirectory);
            }
        }
        if (!string.IsNullOrWhiteSpace(qemuDirectory))
        {
            var environment = process.Environment?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            environment["PATH"] = qemuDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            environment["CTILDE_TARGET_ENVIRONMENT"] = "qemu";
            process = process with { Environment = environment };
        }
        return process;
    }

    private static void ValidateQemuTooling(BuildRequest request)
    {
        var executable = request.EspIdfChip == CTilde.EspIdfChip.Esp32 ? "qemu-system-xtensa" : "qemu-system-riscv32";
        if (FindQemuExecutable(request) is not null)
            return;
        throw new NativeBuildException($"ESP-IDF QEMU tool '{executable}' is not installed. Run: {QemuToolsInstallCommand(request)}");
    }

    private static string? FindQemuExecutable(BuildRequest request)
    {
        var executable = request.EspIdfChip == CTilde.EspIdfChip.Esp32 ? "qemu-system-xtensa" : "qemu-system-riscv32";
        var names = OperatingSystem.IsWindows() ? new[] { executable + ".exe", executable } : new[] { executable };
        foreach (var name in names)
            if (NativeToolDiscovery.FindOnPath(name) is { } path)
                return path;
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var root in EspIdfEnvironment.ToolsRoots(request.EspIdfPath))
        {
            if (!Directory.Exists(root))
                continue;
            var path = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(candidate => names.Contains(Path.GetFileName(candidate), comparison));
            if (path is not null)
                return path;
        }
        return null;
    }

    private static string QemuToolsInstallCommand(BuildRequest request)
    {
        var idfPath = EspIdfEnvironment.ResolveIdfPath(request.EspIdfPath);
        if (idfPath is not null)
        {
            var script = Path.Combine(Path.GetFullPath(idfPath), "tools", "idf_tools.py");
            return OperatingSystem.IsWindows()
                ? $"python \"{script}\" install qemu-xtensa qemu-riscv32"
                : $"python {ShellQuote(script)} install qemu-xtensa qemu-riscv32";
        }
        return OperatingSystem.IsWindows()
            ? "python \"$env:IDF_PATH\\tools\\idf_tools.py\" install qemu-xtensa qemu-riscv32"
            : "python \"$IDF_PATH/tools/idf_tools.py\" install qemu-xtensa qemu-riscv32";
    }

    private static NativeProcessRequest? CreateActiveEnvironmentRequest(string project, string? requestedIdfPath, IReadOnlyList<string> arguments)
    {
        var idfPath = Environment.GetEnvironmentVariable("IDF_PATH");
        var pythonEnvironment = Environment.GetEnvironmentVariable("IDF_PYTHON_ENV_PATH");
        if (string.IsNullOrWhiteSpace(idfPath) || string.IsNullOrWhiteSpace(pythonEnvironment))
            return null;
        if (!string.IsNullOrWhiteSpace(requestedIdfPath) &&
            !Path.GetFullPath(requestedIdfPath).Equals(Path.GetFullPath(idfPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        var idfScript = Path.Combine(idfPath, "tools", "idf.py");
        var python = OperatingSystem.IsWindows()
            ? Path.Combine(pythonEnvironment, "Scripts", "python.exe")
            : Path.Combine(pythonEnvironment, "bin", "python");
        return File.Exists(idfScript) && File.Exists(python)
            ? new NativeProcessRequest(python, [idfScript, .. arguments], project)
            : null;
    }

    private static NativeProcessRequest CreateWindowsPythonRequest(string idfCommand, string project, IReadOnlyList<string> arguments)
    {
        var python = NativeToolDiscovery.FindOnPath("python") ?? NativeToolDiscovery.FindOnPath("python.exe");
        if (python is null)
            throw new NativeBuildException("idf.py was found, but its Python interpreter was not available.");
        return new NativeProcessRequest(python, [idfCommand, .. arguments], project);
    }

    private static NativeProcessRequest CreateActivatedRequest(string idfPath, string project, IReadOnlyList<string> arguments, string? additionalPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var exportScript = Path.Combine(idfPath, "export.ps1");
            var eimProfile = EspIdfEnvironment.FindWindowsProfile(idfPath);
            if (eimProfile is null && !File.Exists(exportScript))
                throw new NativeBuildException($"ESP-IDF activation script was not found: {exportScript}");
            var windowsIdfArguments = string.Join(' ', arguments.Select(argument => $"'{PowerShellQuote(argument)}'"));
            var activation = eimProfile ?? exportScript;
            var pathSetup = string.IsNullOrWhiteSpace(additionalPath)
                ? string.Empty
                : $" $env:PATH='{PowerShellQuote(additionalPath)};'+$env:PATH;";
            var script = $"$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; . '{PowerShellQuote(activation)}' 6>$null;{pathSetup} & idf.py {windowsIdfArguments}; exit $LASTEXITCODE";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return new NativeProcessRequest("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], project);
        }

        var export = Path.Combine(idfPath, "export.sh");
        if (!File.Exists(export))
            throw new NativeBuildException($"ESP-IDF activation script was not found: {export}");
        var shell = NativeToolDiscovery.FindOnPath("bash") ?? throw new NativeBuildException("bash is required to activate ESP-IDF.");
        var idfArguments = string.Join(' ', arguments.Select(ShellQuote));
        var shellPathSetup = string.IsNullOrWhiteSpace(additionalPath) ? string.Empty : $"export PATH={ShellQuote(additionalPath)}:$PATH && ";
        return new NativeProcessRequest(shell, ["-lc", $"source {ShellQuote(export)} >/dev/null && {shellPathSetup}exec idf.py {idfArguments}"], project);
    }

    private static string PowerShellQuote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
