using System.Text;
using System.Security.Cryptography;
using CTilde;

namespace CTilde.Cli;

internal sealed record NativeBuildOutcome(int ExitCode, string Backend, string? CompilerCommand, string? WslCompiler = null);

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
    public static async Task<NativeBuildOutcome> BuildAsync(BuildRequest request, bool usesInlineAssembly, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.ExecutablePath!)!);
        var compiler = await ResolveCompilerAsync(request.Compiler, request.RootDirectory, cancellationToken);
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
        var result = compiler.Kind == HostedCompilerKind.Msvc
            ? await CompileMsvcAsync(compiler, request, cancellationToken)
            : await CompileGnuAsync(compiler, request, runtimeFiles.Count != 0, cancellationToken);
        if (result == 0)
            HostedRuntimeFileStager.Stage(request, runtimeFiles);
        if (result == 0 && request.Trace)
            Console.Error.WriteLine($"trace: wrote native executable {request.ExecutablePath}");
        return new NativeBuildOutcome(result, compiler.Kind == HostedCompilerKind.Msvc ? "msvc" : "gdb",
            compiler.Command, compiler.WslCompiler);
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

    private static async Task<int> CompileMsvcAsync(HostedCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.Configuration == CTildeNativeBuildConfiguration.Debug
            ? new[] { "/Od", "/Zi", "/Oy-" }
            : ["/O2"];
        var common = new List<string> { "/nologo", "/std:clatest", "/W4", "/WX", "/wd4702" };
        common.AddRange(configuration);
        if (request.Lto)
            common.Add("/GL");
        var objects = new List<string>();
        foreach (var source in request.GeneratedSourcePaths.Concat(request.Hosted?.NativeSources ?? []))
        {
            var objectPath = CachedObjectPath(request, compiler, source, string.Join('\n', common), ".obj");
            objects.Add(objectPath);
            if (File.Exists(objectPath))
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
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        return linked.ExitCode;
    }

    private static async Task<int> CompileGnuAsync(
        HostedCompiler compiler,
        BuildRequest request,
        bool useExecutableRuntimePath,
        CancellationToken cancellationToken)
    {
        var executable = request.ExecutablePath!;
        var prefix = Array.Empty<string>();
        if (compiler.Kind == HostedCompilerKind.WslGnu)
        {
            executable = await WslPathAsync(compiler.Command, executable, request.RootDirectory, cancellationToken);
            prefix = ["--exec", compiler.WslCompiler!];
        }
        var configuration = request.Configuration == CTildeNativeBuildConfiguration.Debug
            ? new List<string> { "-Og", "-g3", "-fno-omit-frame-pointer", "-fno-optimize-sibling-calls" }
            : ["-O2"];
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
        common.AddRange(["-Wall", "-Wextra", "-Werror"]);

        var objects = new List<string>();
        foreach (var originalSource in hostedSources)
        {
            var objectPath = CachedObjectPath(request, compiler, originalSource, string.Join('\n', common), ".o");
            objects.Add(objectPath);
            if (File.Exists(objectPath))
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
                return first.ExitCode;
            }
        }

        var linkedObjects = objects.ToArray();
        if (compiler.Kind == HostedCompilerKind.WslGnu)
            for (var index = 0; index < linkedObjects.Length; index++)
                linkedObjects[index] = await WslPathAsync(compiler.Command, linkedObjects[index], request.RootDirectory, cancellationToken);
        var link = new List<string>(prefix);
        link.AddRange(linkedObjects);
        link.AddRange(["-o", executable]);
        if (request.Lto)
            link.Add("-flto");
        if (usesPthreads)
            link.Add("-pthread");
        if (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows())
            link.Add("-lm");
        if (usesDynamicLoader && (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows()))
            link.Add("-ldl");
        if (useExecutableRuntimePath && (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows()))
            link.Add("-Wl,-rpath,$ORIGIN");
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        return linked.ExitCode;
    }

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
            .Append(flags).Append('\n')
            .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))).Append('\n');
        if (request.CLayout == GeneratedCLayout.Modules)
        {
            foreach (var header in new[] { "ctilde_internal.h", "ctilde_runtime.h" })
            {
                var path = Path.Combine(request.GeneratedDirectory!, header);
                identity.Append(header).Append(':').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
            }
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
        return Path.Combine(cache, key + extension);
    }

    private static async Task<string> WslPathAsync(string wsl, string path, string workingDirectory, CancellationToken cancellationToken)
    {
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
        await ValidateCompilerAsync(compiler, request, cancellationToken);

        var configuration = request.Configuration == CTildeNativeBuildConfiguration.Debug
            ? new[] { "-Og", "-g3", "-fno-omit-frame-pointer" }
            : ["-O2"];
        var common = new List<string>
        {
            "-std=gnu23", "-ffreestanding", "-fno-builtin", "-fno-stack-protector",
            "-fno-pie", "-ffunction-sections", "-fdata-sections", "-Wall", "-Wextra", "-Werror",
        };
        common.AddRange(configuration);
        common.AddRange(ArchitectureFlags(request.Architecture));
        if (request.Lto)
            common.Add("-flto");
        common.AddRange(settings.CompileOptions);

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
            if (File.Exists(objectPath))
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
        if (request.Lto)
            link.Add("-flto");
        link.Add("-T");
        link.Add(await ToolPathAsync(compiler, settings.LinkerScriptPath!, request.RootDirectory, cancellationToken));
        link.Add($"-Wl,-e,{settings.EntrySymbol}");
        link.AddRange(settings.LinkOptions);
        link.Add("-o");
        link.Add(await ToolPathAsync(compiler, image, request.RootDirectory, cancellationToken));
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            request.RootDirectory, ForwardOutput: true), cancellationToken);
        if (linked.ExitCode == 0 && request.Trace)
            Console.Error.WriteLine($"trace: wrote freestanding image {image}");
        return new NativeBuildOutcome(linked.ExitCode, "freestanding", compiler.Command, compiler.WslCompiler);
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

    private static async Task ValidateCompilerAsync(FreestandingCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
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
            .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))).Append('\n');
        if (request.CLayout == GeneratedCLayout.Modules)
        {
            foreach (var header in new[] { "ctilde_internal.h", "ctilde_runtime.h" })
            {
                var path = Path.Combine(request.GeneratedDirectory!, header);
                identity.Append(header).Append(':')
                    .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
            }
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
        return Path.Combine(directory, hash + ".o");
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

        var process = CreateIdfRequest(request, ["build"]);
        if (request.Trace)
            Console.Error.WriteLine($"trace: running ESP-IDF build in {project}");
        var result = await NativeProcessRunner.RunAsync(process, cancellationToken);
        return new NativeBuildOutcome(result.ExitCode, "gdb", null);
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
