using System.Security.Cryptography;
using System.Text;
using CTilde;

namespace CTilde.Cli;

internal static class CosmopolitanBuildDriver
{
    private static readonly IReadOnlyDictionary<string, string> DeterministicEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["SOURCE_DATE_EPOCH"] = "0",
        };

    public static async Task<NativeBuildOutcome> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        if (request.Architecture != CompilationArchitecture.X64)
            throw new NativeBuildException("CT4118: Draft 0.25 Cosmopolitan builds require the explicit x64 architecture.");

        var image = request.ExecutablePath ?? throw new NativeBuildException("Cosmopolitan executable output is missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        var compiler = ResolveCompiler(request.Compiler);
        var compilerIdentity = await ValidateCompilerAsync(compiler, request, cancellationToken);

        var common = new List<string>
        {
            "-std=gnu23", "-ffunction-sections", "-fdata-sections", "-Wall", "-Wextra", "-Werror",
        };
        common.AddRange(NativeOptimizationSettings.CosmopolitanCompile(request));
        common.AddRange(ModeFlags(request.CosmopolitanMode));
        if (request.Lto)
            common.Add("-flto");
        var usesPthreads = request.GeneratedSourcePaths.Any(path => File.ReadAllText(path).Contains("pthread_", StringComparison.Ordinal));
        if (usesPthreads)
            common.Add("-pthread");
        if (request.Trace)
        {
            Console.Error.WriteLine($"trace: native profile {NativeOptimizationSettings.Describe(request)}");
            Console.Error.WriteLine($"trace: native compile flags {string.Join(' ', common)}");
        }

        var objects = new List<string>();
        foreach (var source in request.GeneratedSourcePaths)
        {
            var objectPath = CachedObjectPath(request, compiler, compilerIdentity, source, common);
            objects.Add(objectPath);
            if (File.Exists(objectPath))
            {
                if (request.Trace)
                    Console.Error.WriteLine($"trace: reused Cosmopolitan object {Path.GetFileName(objectPath)}");
                continue;
            }

            var arguments = new List<string>(compiler.Prefix);
            arguments.AddRange(common);
            arguments.AddRange(["-c", await ToolPathAsync(compiler, source, request.RootDirectory, cancellationToken), "-o",
                await ToolPathAsync(compiler, objectPath, request.RootDirectory, cancellationToken)]);
            var compiled = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                request.RootDirectory, DeterministicEnvironment, ForwardOutput: false), cancellationToken);
            if (compiled.ExitCode != 0)
            {
                Console.Out.Write(compiled.StandardOutput);
                Console.Error.Write(compiled.StandardError);
                return new NativeBuildOutcome(compiled.ExitCode, "cosmopolitan", compiler.Command, compiler.WslCompiler);
            }
        }

        var carrier = image + ".dbg";
        var link = new List<string>(compiler.Prefix);
        foreach (var path in objects)
            link.Add(await ToolPathAsync(compiler, path, request.RootDirectory, cancellationToken));
        link.AddRange(ModeFlags(request.CosmopolitanMode));
        link.AddRange(NativeOptimizationSettings.CosmopolitanLink(request));
        if (usesPthreads)
            link.Add("-pthread");
        link.AddRange(["-Wl,--gc-sections", "-o", await ToolPathAsync(compiler, carrier, request.RootDirectory, cancellationToken)]);
        if (request.Trace)
            Console.Error.WriteLine($"trace: native link flags {string.Join(' ', link)}");
        var linked = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, link,
            request.RootDirectory, DeterministicEnvironment), cancellationToken);
        if (linked.ExitCode != 0)
            return new NativeBuildOutcome(linked.ExitCode, "cosmopolitan", compiler.Command, compiler.WslCompiler);

        var objcopy = ResolveObjcopy(compiler);
        var unwrap = new List<string>(objcopy.Prefix)
        {
            "-S", "-O", "binary",
            await ToolPathAsync(compiler, carrier, request.RootDirectory, cancellationToken),
            await ToolPathAsync(compiler, image, request.RootDirectory, cancellationToken),
        };
        var unwrapped = await NativeProcessRunner.RunAsync(new NativeProcessRequest(objcopy.Command, unwrap,
            request.RootDirectory, DeterministicEnvironment), cancellationToken);
        if (unwrapped.ExitCode == 0 && request.Trace)
        {
            Console.Error.WriteLine($"trace: retained Cosmopolitan ELF/DWARF carrier {carrier}");
            Console.Error.WriteLine($"trace: wrote x86-64 APE executable {image}");
        }
        return new NativeBuildOutcome(unwrapped.ExitCode, "cosmopolitan", compiler.Command, compiler.WslCompiler);
    }

    private static CosmopolitanCompiler ResolveCompiler(string configured)
    {
        var value = configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("CTILDE_COSMOCC") ?? "auto"
            : configured;
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            value = OperatingSystem.IsWindows() ? "wsl:x86_64-unknown-cosmo-cc" : "x86_64-unknown-cosmo-cc";

        if (OperatingSystem.IsWindows() && value.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var nested = value[4..];
            if (string.IsNullOrWhiteSpace(nested))
                throw new NativeBuildException("A Cosmopolitan compiler path is required after 'wsl:'.");
            ValidateWrapperName(nested);
            var wsl = NativeToolDiscovery.FindOnPath("wsl") ?? throw new NativeBuildException("wsl.exe was not found.");
            return new CosmopolitanCompiler(wsl, ["--exec", nested], nested);
        }

        ValidateWrapperName(value);
        var command = NativeToolDiscovery.FindOnPath(value) ?? throw new NativeBuildException(
            $"Cosmopolitan compiler '{value}' was not found. Set CTILDE_COSMOCC or pass --compiler wsl:<path-to-x86_64-unknown-cosmo-cc>.");
        return new CosmopolitanCompiler(command, [], null);
    }

    private static void ValidateWrapperName(string value)
    {
        var name = PosixFileName(value);
        if (name.Contains("-linux-cosmo-", StringComparison.Ordinal))
            throw new NativeBuildException("Use the supported x86_64-unknown-cosmo-cc wrapper, not a physical *-linux-cosmo-* tool.");
        if (!name.Equals("x86_64-unknown-cosmo-cc", StringComparison.Ordinal))
            throw new NativeBuildException("Draft 0.25 requires the single-architecture x86_64-unknown-cosmo-cc wrapper; fat cosmocc and Arm64 wrappers are deferred.");
    }

    private static async Task<string> ValidateCompilerAsync(CosmopolitanCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
    {
        var versionArguments = new List<string>(compiler.Prefix) { "--version" };
        var version = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, versionArguments,
            request.RootDirectory, DeterministicEnvironment, ForwardOutput: false), cancellationToken);
        if (version.ExitCode != 0)
            throw new NativeBuildException($"Could not execute the Cosmopolitan compiler wrapper: {version.StandardError.Trim()}");

        var probe = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-cosmopolitan-probe.c");
        File.WriteAllText(probe, string.Empty, Encoding.ASCII);
        try
        {
            var arguments = new List<string>(compiler.Prefix)
            {
                "-dM", "-E", "-x", "c", await ToolPathAsync(compiler, probe, request.RootDirectory, cancellationToken),
            };
            var macros = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
                request.RootDirectory, DeterministicEnvironment, ForwardOutput: false), cancellationToken);
            if (macros.ExitCode != 0)
                throw new NativeBuildException($"Could not inspect Cosmopolitan compiler target macros: {macros.StandardError.Trim()}");
            if (!macros.StandardOutput.Contains("#define __COSMOCC__", StringComparison.Ordinal) ||
                !macros.StandardOutput.Contains("#define __COSMOPOLITAN__", StringComparison.Ordinal) ||
                !macros.StandardOutput.Contains("#define __x86_64__", StringComparison.Ordinal) ||
                !macros.StandardOutput.Contains("#define __SIZEOF_POINTER__ 8", StringComparison.Ordinal))
                throw new NativeBuildException("CT4118: Compiler macros do not describe the required 64-bit Cosmopolitan target.");
            return (version.StandardOutput + version.StandardError).Trim();
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
    }

    private static IReadOnlyList<string> ModeFlags(CosmopolitanRuntimeMode mode) => mode switch
    {
        CosmopolitanRuntimeMode.Tiny => ["-mtiny"],
        CosmopolitanRuntimeMode.Debug => ["-mdbg"],
        _ => [],
    };

    private static string CachedObjectPath(BuildRequest request, CosmopolitanCompiler compiler, string compilerIdentity, string source, IReadOnlyList<string> flags)
    {
        var directory = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-cache");
        Directory.CreateDirectory(directory);
        var identity = new StringBuilder()
            .Append("draft-").Append(CompilerContract.DraftVersion).Append('\n')
            .Append(compiler.Command).Append('\n').Append(compiler.WslCompiler).Append('\n').Append(compilerIdentity).Append('\n')
            .Append(request.Architecture).Append('\n').Append(request.CosmopolitanMode).Append('\n')
            .Append(NativeOptimizationSettings.Describe(request)).Append('\n')
            .AppendJoin('\n', flags).Append('\n')
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

    private static CosmopolitanObjcopy ResolveObjcopy(CosmopolitanCompiler compiler)
    {
        var nested = compiler.WslCompiler;
        if (nested is not null)
        {
            var slash = nested.LastIndexOf('/');
            var command = slash >= 0
                ? nested[..(slash + 1)] + "x86_64-linux-cosmo-objcopy"
                : "x86_64-linux-cosmo-objcopy";
            return new CosmopolitanObjcopy(compiler.Command, ["--exec", command]);
        }

        var directory = Path.GetDirectoryName(compiler.Command);
        var candidate = directory is null ? "x86_64-linux-cosmo-objcopy" : Path.Combine(directory, "x86_64-linux-cosmo-objcopy");
        var resolved = NativeToolDiscovery.FindOnPath(candidate) ?? throw new NativeBuildException(
            "The matching x86_64-linux-cosmo-objcopy tool was not found beside the Cosmopolitan compiler wrapper.");
        return new CosmopolitanObjcopy(resolved, []);
    }

    private static async Task<string> ToolPathAsync(CosmopolitanCompiler compiler, string path, string workingDirectory, CancellationToken cancellationToken)
    {
        if (compiler.WslCompiler is null)
            return path;
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command,
            ["--exec", "wslpath", "-a", "-u", path], workingDirectory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new NativeBuildException($"Could not translate '{path}' to a WSL path: {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    private static string PosixFileName(string path)
    {
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private sealed record CosmopolitanCompiler(string Command, IReadOnlyList<string> Prefix, string? WslCompiler);
    private sealed record CosmopolitanObjcopy(string Command, IReadOnlyList<string> Prefix);
}
