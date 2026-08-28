using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public sealed record CTildeProjectConfiguration(
    CompilationTarget Target,
    CompilationArchitecture Architecture,
    ImmutableArray<string> Sources,
    ImmutableArray<string> Exclude,
    CTildeProjectBuildConfiguration Build,
    ImmutableArray<EspIdfBindingManifest> BindingManifests,
    ImmutableArray<CpuFeature> CpuFeatures,
    ImmutableArray<RepositoryModuleReference> Modules,
    CTildeProjectRunConfiguration? Run,
    bool NoRecursion,
    EspIdfPanicPolicy PanicPolicy,
    FreestandingProjectConfiguration? Freestanding,
    CosmopolitanProjectConfiguration? Cosmopolitan);

public enum CTildeRunExecutor
{
    Host,
    Wsl,
}

public sealed record CTildeProjectRunConfiguration(
    CTildeRunExecutor Executor,
    string? Command,
    ImmutableArray<string> Arguments,
    string WorkingDirectoryPath,
    ImmutableDictionary<string, string> Environment,
    ImmutableArray<int> SuccessExitCodes);

public enum CosmopolitanRuntimeMode
{
    Default,
    Tiny,
    Debug,
}

public sealed record CosmopolitanProjectConfiguration(CosmopolitanRuntimeMode Mode);

public sealed record FreestandingProjectConfiguration(
    string? LinkerScriptPath,
    string? EntrySymbol,
    ImmutableArray<string> NativeSources,
    ImmutableArray<string> ObjectFiles,
    ImmutableArray<string> Libraries,
    ImmutableArray<string> CompileOptions,
    ImmutableArray<string> LinkOptions);

public enum CTildeNativeBuildConfiguration
{
    Debug,
    Release,
}

public sealed record CTildeProjectBuildConfiguration(
    string GeneratedCPath,
    string GeneratedHeaderPath,
    GeneratedCLayout CLayout,
    string GeneratedDirectory,
    string? SymbolMapPath,
    bool Lto,
    CTildeNativeBuildConfiguration Configuration,
    string Compiler,
    string? ExecutablePath,
    string? EspIdfProjectDirectory);

public sealed record CTildeProject(
    string ManifestPath,
    string RootDirectory,
    CTildeProjectConfiguration Configuration,
    ImmutableArray<string> SourceFiles,
    ImmutableDictionary<string, SourceOwnerIdentity> SourceOwners);

public sealed record RepositoryModuleReference(
    string ModulePath,
    string Repository,
    string Selector,
    string? Alias,
    ImmutableArray<string> Sources,
    string? Vendor,
    RepositoryModuleUpdatePolicy UpdatePolicy);

public enum RepositoryModuleUpdatePolicy
{
    Locked,
    Refresh,
}

public sealed class CTildeProjectException : Exception
{
    public CTildeProjectException(string message) : base(message) { }

    public CTildeProjectException(string message, Exception innerException) : base(message, innerException) { }
}

public static class CTildeProjectFile
{
    private static readonly string[] DefaultExcludes =
    [
        ".git/**", ".ctilde/**", "vendor/**", "**/bin/**", "**/obj/**", "**/build/**", "**/node_modules/**", "**/managed_components/**",
    ];

    public static CTildeProject Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' does not exist.");

        ProjectDocument? document;
        try
        {
            using var stream = File.OpenRead(fullManifestPath);
            document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read project manifest '{fullManifestPath}': {exception.Message}", exception);
        }

        if (document?.Sources is not { Length: > 0 })
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' requires a non-empty 'sources' array.");

        var target = document.Target switch
        {
            null or "hosted" => CompilationTarget.Hosted,
            "esp-idf" => CompilationTarget.EspIdf,
            "freestanding" => CompilationTarget.Freestanding,
            "cosmopolitan" => CompilationTarget.Cosmopolitan,
            _ => throw new CTildeProjectException($"Unknown target '{document.Target}' in '{fullManifestPath}'; expected hosted, esp-idf, freestanding, or cosmopolitan."),
        };
        var architecture = ParseArchitecture(document.Architecture, fullManifestPath);
        var cpuFeatures = ParseCpuFeatures(document.CpuFeatures, fullManifestPath);
        var root = Path.GetDirectoryName(fullManifestPath)!;
        var modules = ParseModules(document.Modules, fullManifestPath);
        var sources = ValidatePatterns(document.Sources, "sources", fullManifestPath);
        var excludes = ValidatePatterns([.. DefaultExcludes, .. document.Exclude ?? []], "exclude", fullManifestPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        ImmutableArray<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.ct", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path =>
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    return sources.Any(pattern => GlobMatches(pattern, relative)) && !excludes.Any(pattern => GlobMatches(pattern, relative));
                })
                .Distinct(comparer)
                .OrderBy(path => path, comparer)
                .ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CTildeProjectException($"Could not enumerate sources for '{fullManifestPath}': {exception.Message}", exception);
        }

        if (files.IsEmpty)
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' did not match any .ct source files.");

        if (target != CompilationTarget.EspIdf && document.EspIdf is not null)
            throw new CTildeProjectException($"Property 'espIdf' in '{fullManifestPath}' is valid only for ESP-IDF projects.");
        if (target != CompilationTarget.Freestanding && document.Freestanding is not null)
            throw new CTildeProjectException($"Property 'freestanding' in '{fullManifestPath}' is valid only for freestanding projects.");
        if (target != CompilationTarget.Cosmopolitan && document.Cosmopolitan is not null)
            throw new CTildeProjectException($"Property 'cosmopolitan' in '{fullManifestPath}' is valid only for Cosmopolitan projects.");
        var bindingPaths = document.EspIdf?.Bindings ?? [];
        var bindingManifests = bindingPaths.Select(path => EspIdfBindingManifest.Load(path, root)).OrderBy(binding => binding.ManifestPath, comparer).ToImmutableArray();
        if (bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }).Distinct(comparer).Count() != bindingManifests.Length * 2)
            throw new CTildeProjectException($"ESP-IDF binding outputs in '{fullManifestPath}' must be distinct.");
        foreach (var declaration in bindingManifests.Select(binding => binding.DeclarationsPath))
            if (files.Contains(declaration, comparer))
                throw new CTildeProjectException($"ESP-IDF binding declaration '{declaration}' cannot overwrite an ordinary project source in '{fullManifestPath}'.");
        files = files.Concat(bindingManifests.Select(binding => binding.DeclarationsPath).Where(File.Exists)).Distinct(comparer).OrderBy(path => path, comparer).ToImmutableArray();
        var restoredModules = RepositoryModules.LoadLocked(root, modules);
        files = files.Concat(restoredModules.SourceFiles).Distinct(comparer).OrderBy(path => path, comparer).ToImmutableArray();
        var build = CreateBuildConfiguration(document.Build, target, root, fullManifestPath, files);
        var run = CreateRunConfiguration(document.Run, build, root, fullManifestPath);
        foreach (var output in bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }))
            if (PathsEqual(output, build.GeneratedCPath) || PathsEqual(output, build.GeneratedHeaderPath) || IsInsideDirectory(output, build.GeneratedDirectory))
                throw new CTildeProjectException($"ESP-IDF binding output '{output}' conflicts with compiler output in '{fullManifestPath}'.");
        var panicPolicy = ParsePanicPolicy(document.PanicPolicy, target, fullManifestPath);
        var freestanding = target == CompilationTarget.Freestanding
            ? CreateFreestandingConfiguration(document.Freestanding, root, fullManifestPath)
            : null;
        var cosmopolitan = target == CompilationTarget.Cosmopolitan
            ? CreateCosmopolitanConfiguration(document.Cosmopolitan, fullManifestPath)
            : null;
        return new CTildeProject(fullManifestPath, root, new CTildeProjectConfiguration(target, architecture, sources, excludes, build, bindingManifests, cpuFeatures, modules, run,
            document.NoRecursion ?? false, panicPolicy, freestanding, cosmopolitan), files, restoredModules.SourceOwners);
    }

    public static (string RootDirectory, ImmutableArray<RepositoryModuleReference> Modules) ReadModuleReferences(string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        ProjectDocument document;
        try
        {
            using var stream = File.OpenRead(fullManifestPath);
            document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions) ?? throw new JsonException("Empty project manifest.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read project manifest '{fullManifestPath}': {exception.Message}", exception);
        }
        return (Path.GetDirectoryName(fullManifestPath)!, ParseModules(document.Modules, fullManifestPath));
    }

    private static ImmutableArray<RepositoryModuleReference> ParseModules(ModuleDocument[]? documents, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<RepositoryModuleReference>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in documents ?? [])
        {
            if (string.IsNullOrWhiteSpace(module.Path) || module.Path.StartsWith(".", StringComparison.Ordinal) || module.Path.Contains('\\') || module.Path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new CTildeProjectException($"Repository module path '{module.Path}' in '{manifestPath}' must be a canonical slash-separated module identity.");
            if (!paths.Add(module.Path))
                throw new CTildeProjectException($"Repository module path '{module.Path}' appears more than once in '{manifestPath}'.");
            if (string.IsNullOrWhiteSpace(module.Repository) || string.IsNullOrWhiteSpace(module.Selector))
                throw new CTildeProjectException($"Repository module '{module.Path}' in '{manifestPath}' requires repository and selector values.");
            if (module.Alias is not null && (!Regex.IsMatch(module.Alias, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) || !aliases.Add(module.Alias)))
                throw new CTildeProjectException($"Repository module alias '{module.Alias}' in '{manifestPath}' is invalid or duplicated.");
            var updatePolicy = module.UpdatePolicy switch
            {
                null or "locked" => RepositoryModuleUpdatePolicy.Locked,
                "refresh" => RepositoryModuleUpdatePolicy.Refresh,
                _ => throw new CTildeProjectException($"Repository module '{module.Path}' in '{manifestPath}' has unknown updatePolicy '{module.UpdatePolicy}'; expected locked or refresh."),
            };
            result.Add(new RepositoryModuleReference(module.Path, module.Repository, module.Selector, module.Alias,
                ValidatePatterns(module.Sources is { Length: > 0 } ? module.Sources : ["**/*.ct"], "modules.sources", manifestPath),
                module.Vendor, updatePolicy));
        }
        return result.ToImmutable();
    }

    private static CompilationArchitecture ParseArchitecture(string? value, string manifestPath) => value switch
    {
        null or "auto" => CompilationArchitecture.Auto,
        "x86" => CompilationArchitecture.X86,
        "x64" => CompilationArchitecture.X64,
        "arm32" => CompilationArchitecture.Arm32,
        "arm64" => CompilationArchitecture.Arm64,
        "xtensa" => CompilationArchitecture.Xtensa,
        "riscv32" => CompilationArchitecture.RiscV32,
        "riscv64" => CompilationArchitecture.RiscV64,
        _ => throw new CTildeProjectException($"Unknown architecture '{value}' in '{manifestPath}'; expected auto, x86, x64, arm32, arm64, xtensa, riscv32, or riscv64."),
    };

    private static ImmutableArray<CpuFeature> ParseCpuFeatures(string[]? values, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<CpuFeature>();
        foreach (var value in values ?? [])
        {
            var feature = value switch
            {
                "simd128" => CpuFeature.Simd128,
                _ => throw new CTildeProjectException($"Unknown CPU feature '{value}' in '{manifestPath}'; expected simd128."),
            };
            if (result.Contains(feature))
                throw new CTildeProjectException($"CPU feature '{value}' appears more than once in '{manifestPath}'.");
            result.Add(feature);
        }
        return result.ToImmutable();
    }

    private static EspIdfPanicPolicy ParsePanicPolicy(string? value, CompilationTarget target, string manifestPath)
    {
        if (target != CompilationTarget.EspIdf && value is not null)
            throw new CTildeProjectException($"Property 'panicPolicy' in '{manifestPath}' is valid only for ESP-IDF projects.");
        return value switch
        {
            null or "abort" => EspIdfPanicPolicy.Abort,
            "restart" => EspIdfPanicPolicy.Restart,
            "halt" => EspIdfPanicPolicy.Halt,
            _ => throw new CTildeProjectException($"Unknown panic policy '{value}' in '{manifestPath}'; expected abort, restart, or halt."),
        };
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    public static string? FindNearest(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : Path.GetDirectoryName(fullPath) ?? fullPath;
        for (var current = new DirectoryInfo(directory!); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "ctilde.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static ImmutableArray<string> ValidatePatterns(IEnumerable<string> patterns, string property, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var value in patterns)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot contain an empty pattern.");
            var normalized = value.Replace('\\', '/');
            if (Path.IsPathRooted(value) || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
                throw new CTildeProjectException($"Pattern '{value}' in '{manifestPath}' must stay within the project directory.");
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            result.Add(normalized);
        }
        return result.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static CTildeProjectBuildConfiguration CreateBuildConfiguration(
        BuildDocument? document,
        CompilationTarget target,
        string root,
        string manifestPath,
        ImmutableArray<string> sourceFiles)
    {
        if ((target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && document?.EspIdfProjectDirectory is not null)
            throw new CTildeProjectException($"Property 'build.espIdfProjectDirectory' in '{manifestPath}' is valid only for ESP-IDF projects.");
        if (target == CompilationTarget.EspIdf &&
            (document?.Compiler is not null || document?.Configuration is not null || document?.Executable is not null || document?.Image is not null || document?.Lto == true))
            throw new CTildeProjectException($"Properties 'build.compiler', 'build.configuration', 'build.executable', and 'build.lto' in '{manifestPath}' are valid only for hosted or Cosmopolitan projects.");
        if ((target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && document?.Image is not null)
            throw new CTildeProjectException($"Property 'build.image' in '{manifestPath}' is valid only for freestanding projects.");
        if (target == CompilationTarget.Freestanding && (document?.Executable is not null || document?.EspIdfProjectDirectory is not null))
            throw new CTildeProjectException($"Properties 'build.executable' and 'build.espIdfProjectDirectory' in '{manifestPath}' are invalid for freestanding projects.");

        var cLayout = document?.CLayout switch
        {
            null or "unity" => GeneratedCLayout.Unity,
            "modules" => GeneratedCLayout.Modules,
            _ => throw new CTildeProjectException($"Unknown C layout '{document.CLayout}' in '{manifestPath}'; expected unity or modules."),
        };

        var generatedCDefault = target == CompilationTarget.EspIdf
            ? "main/generated/ctilde_program.c"
            : "build/generated/ctilde_program.c";
        var generatedHeaderDefault = target == CompilationTarget.EspIdf
            ? "main/generated/ctilde_exports.h"
            : "build/generated/ctilde_exports.h";
        var generatedC = ResolveProjectPath(document?.GeneratedC ?? generatedCDefault, "build.generatedC", root, manifestPath, isDirectory: false);
        var generatedHeader = ResolveProjectPath(document?.GeneratedHeader ?? generatedHeaderDefault, "build.generatedHeader", root, manifestPath, isDirectory: false);
        var generatedDirectoryDefault = target == CompilationTarget.EspIdf ? "main/generated" : "build/generated/modules";
        var generatedDirectory = ResolveProjectPath(document?.GeneratedDirectory ?? generatedDirectoryDefault, "build.generatedDirectory", root, manifestPath, isDirectory: true);
        var symbolMap = document?.SymbolMap is null ? null : ResolveProjectPath(document.SymbolMap, "build.symbolMap", root, manifestPath, isDirectory: false);
        if (PathsEqual(generatedC, generatedHeader))
            throw new CTildeProjectException($"Properties 'build.generatedC' and 'build.generatedHeader' in '{manifestPath}' must name different files.");
        if (sourceFiles.Any(path => PathsEqual(path, generatedC) || PathsEqual(path, generatedHeader)))
            throw new CTildeProjectException($"Generated output paths in '{manifestPath}' must not overwrite a project source file.");

        var configuration = document?.Configuration switch
        {
            null or "debug" => CTildeNativeBuildConfiguration.Debug,
            "release" => CTildeNativeBuildConfiguration.Release,
            _ => throw new CTildeProjectException($"Unknown build configuration '{document.Configuration}' in '{manifestPath}'; expected debug or release."),
        };
        var lto = document?.Lto ?? false;
        if (lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CTildeProjectException($"Property 'build.lto' in '{manifestPath}' requires build.configuration 'release'.");
        var compiler = document?.Compiler ?? "auto";
        if (string.IsNullOrWhiteSpace(compiler))
            throw new CTildeProjectException($"Property 'build.compiler' in '{manifestPath}' cannot be empty.");
        if (!compiler.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase) &&
            (compiler.Contains(Path.DirectorySeparatorChar) || compiler.Contains(Path.AltDirectorySeparatorChar)))
            compiler = ResolveProjectPath(compiler, "build.compiler", root, manifestPath, isDirectory: false);

        string? executable = null;
        string? espIdfProjectDirectory = null;
        if (target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan)
        {
            var projectName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(projectName))
                projectName = "program";
            var extension = target == CompilationTarget.Cosmopolitan ? ".com" : OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var defaultExecutable = $"build/{projectName}{extension}";
            executable = ResolveProjectPath(document?.Executable ?? defaultExecutable, "build.executable", root, manifestPath, isDirectory: false);
            if (PathsEqual(executable, generatedC) || PathsEqual(executable, generatedHeader) || sourceFiles.Any(path => PathsEqual(path, executable)))
                throw new CTildeProjectException($"Property 'build.executable' in '{manifestPath}' must name a distinct non-source file.");
        }
        else if (target == CompilationTarget.EspIdf)
        {
            espIdfProjectDirectory = ResolveProjectPath(document?.EspIdfProjectDirectory ?? ".", "build.espIdfProjectDirectory", root, manifestPath, isDirectory: true);
        }
        else if (document?.Image is not null)
        {
            executable = ResolveProjectPath(document.Image, "build.image", root, manifestPath, isDirectory: false);
            if (PathsEqual(executable, generatedC) || PathsEqual(executable, generatedHeader) || sourceFiles.Any(path => PathsEqual(path, executable)))
                throw new CTildeProjectException($"Property 'build.image' in '{manifestPath}' must name a distinct non-source file.");
        }

        return new CTildeProjectBuildConfiguration(generatedC, generatedHeader, cLayout, generatedDirectory, symbolMap, lto,
            configuration, compiler, executable, espIdfProjectDirectory);
    }

    private static CTildeProjectRunConfiguration? CreateRunConfiguration(
        RunDocument? document,
        CTildeProjectBuildConfiguration build,
        string root,
        string manifestPath)
    {
        if (document is null)
            return null;
        var executor = document.Executor switch
        {
            null or "host" => CTildeRunExecutor.Host,
            "wsl" => CTildeRunExecutor.Wsl,
            _ => throw new CTildeProjectException($"Unknown run executor '{document.Executor}' in '{manifestPath}'; expected host or wsl."),
        };
        if (document.Command is not null)
        {
            ValidateRunTemplate(document.Command, "run.command", manifestPath);
            if (Path.IsPathRooted(document.Command))
                throw new CTildeProjectException($"Property 'run.command' in '{manifestPath}' must be a PATH command, project-relative path, or supported placeholder expression.");
        }
        var arguments = (document.Arguments ?? []).Select((value, index) =>
        {
            ValidateRunTemplate(value, $"run.args[{index}]", manifestPath, allowEmpty: true);
            return value;
        }).ToImmutableArray();
        var workingDirectory = ExpandRunPath(document.WorkingDirectory ?? ".", "run.workingDirectory", root, build.ExecutablePath, manifestPath);
        var environment = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.Environment ?? [])
        {
            if (!Regex.IsMatch(entry.Key, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                throw new CTildeProjectException($"Environment variable name '{entry.Key}' in 'run.environment' in '{manifestPath}' is invalid.");
            if (entry.Value.Contains('\0'))
                throw new CTildeProjectException($"Environment variable '{entry.Key}' in 'run.environment' in '{manifestPath}' contains a null character.");
            ValidateRunTemplate(entry.Value, $"run.environment.{entry.Key}", manifestPath, allowEmpty: true);
            environment.Add(entry.Key, entry.Value);
        }
        var successExitCodes = (document.SuccessExitCodes ?? [0]).ToImmutableArray();
        if (successExitCodes.IsEmpty)
            throw new CTildeProjectException($"Property 'run.successExitCodes' in '{manifestPath}' requires at least one exit code.");
        if (successExitCodes.Distinct().Count() != successExitCodes.Length)
            throw new CTildeProjectException($"Property 'run.successExitCodes' in '{manifestPath}' cannot contain duplicate exit codes.");
        return new CTildeProjectRunConfiguration(executor, document.Command, arguments, workingDirectory,
            environment.ToImmutable(), successExitCodes);
    }

    private static string ExpandRunPath(string value, string property, string root, string? buildOutput, string manifestPath)
    {
        ValidateRunTemplate(value, property, manifestPath);
        var expanded = value.Replace("${projectRoot}", root, StringComparison.Ordinal);
        if (expanded.Contains("${buildOutput}", StringComparison.Ordinal))
        {
            if (buildOutput is null)
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' uses ${{buildOutput}}, but this target has no executable or image output.");
            expanded = expanded.Replace("${buildOutput}", buildOutput, StringComparison.Ordinal);
        }
        var fullPath = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must stay within the project directory.");
        return fullPath;
    }

    private static void ValidateRunTemplate(string value, string property, string manifestPath, bool allowEmpty = false)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot be empty.");
        if (value.Contains('\0'))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains a null character.");
        var remainder = Regex.Replace(value, @"\$\{(projectRoot|buildOutput)\}", string.Empty, RegexOptions.CultureInvariant);
        if (remainder.Contains("${", StringComparison.Ordinal))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains an unknown or malformed placeholder; expected ${{projectRoot}} or ${{buildOutput}}.");
    }

    private static FreestandingProjectConfiguration CreateFreestandingConfiguration(
        FreestandingDocument? document,
        string root,
        string manifestPath)
    {
        string? linkerScript = null;
        if (document?.LinkerScript is not null)
        {
            linkerScript = ResolveProjectPath(document.LinkerScript, "freestanding.linkerScript", root, manifestPath, isDirectory: false);
            if (!File.Exists(linkerScript))
                throw new CTildeProjectException($"Freestanding linker script '{linkerScript}' in '{manifestPath}' does not exist.");
        }

        var entrySymbol = document?.EntrySymbol;
        if (entrySymbol is not null && !IsPortableNativeSymbol(entrySymbol))
            throw new CTildeProjectException($"Property 'freestanding.entrySymbol' in '{manifestPath}' must be a portable native symbol name.");

        var nativeSources = ResolveExistingFiles(document?.NativeSources ?? [], "freestanding.nativeSources", root, manifestPath,
            path => Path.GetExtension(path) is ".c" or ".s" or ".S");
        var objectFiles = ResolveExistingFiles(document?.ObjectFiles ?? [], "freestanding.objectFiles", root, manifestPath,
            path => Path.GetExtension(path).Equals(".o", StringComparison.OrdinalIgnoreCase));
        var libraries = ResolveExistingFiles(document?.Libraries ?? [], "freestanding.libraries", root, manifestPath,
            path => Path.GetExtension(path).Equals(".a", StringComparison.OrdinalIgnoreCase));
        var compileOptions = ValidateNativeOptions(document?.CompileOptions ?? [], "freestanding.compileOptions", manifestPath);
        var linkOptions = ValidateNativeOptions(document?.LinkOptions ?? [], "freestanding.linkOptions", manifestPath);
        return new FreestandingProjectConfiguration(linkerScript, entrySymbol, nativeSources, objectFiles, libraries, compileOptions, linkOptions);
    }

    private static CosmopolitanProjectConfiguration CreateCosmopolitanConfiguration(
        CosmopolitanDocument? document,
        string manifestPath)
    {
        var mode = document?.Mode switch
        {
            null or "default" => CosmopolitanRuntimeMode.Default,
            "tiny" => CosmopolitanRuntimeMode.Tiny,
            "debug" => CosmopolitanRuntimeMode.Debug,
            _ => throw new CTildeProjectException($"Unknown Cosmopolitan mode '{document.Mode}' in '{manifestPath}'; expected default, tiny, or debug."),
        };
        return new CosmopolitanProjectConfiguration(mode);
    }

    private static ImmutableArray<string> ResolveExistingFiles(
        IEnumerable<string> values,
        string property,
        string root,
        string manifestPath,
        Func<string, bool> validExtension)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(comparer);
        foreach (var value in values)
        {
            var path = ResolveProjectPath(value, property, root, manifestPath, isDirectory: false);
            if (!validExtension(path))
                throw new CTildeProjectException($"File '{value}' in '{property}' has an unsupported extension.");
            if (!File.Exists(path))
                throw new CTildeProjectException($"File '{path}' in '{property}' does not exist.");
            if (!unique.Add(path))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains duplicate file '{value}'.");
            result.Add(path);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<string> ValidateNativeOptions(IEnumerable<string> values, string property, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('@'))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot contain empty arguments or response files.");
            if (value is "-c" or "-S" or "-E" or "-o" or "-T" or "--output" or "--entry" or "--script" ||
                value.StartsWith("-o", StringComparison.Ordinal) || value.StartsWith("-T", StringComparison.Ordinal) ||
                value.StartsWith("--output=", StringComparison.Ordinal) || value.StartsWith("--entry=", StringComparison.Ordinal) ||
                value.StartsWith("--script=", StringComparison.Ordinal) || value.StartsWith("-Wl,-e", StringComparison.Ordinal) ||
                value.StartsWith("-Wl,-T", StringComparison.Ordinal) || value.StartsWith("-Wl,--entry", StringComparison.Ordinal) ||
                value.StartsWith("-Wl,--script", StringComparison.Ordinal) || value.StartsWith("-Wl,-o", StringComparison.Ordinal))
                throw new CTildeProjectException($"Option '{value}' in '{property}' overrides a compiler-owned build setting.");
            result.Add(value);
        }
        return result.ToImmutable();
    }

    private static bool IsPortableNativeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !(char.IsAsciiLetter(value[0]) || value[0] is '_' or '$'))
            return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$');
    }

    private static string ResolveProjectPath(string value, string property, string root, string manifestPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a non-empty relative path.");
        var fullPath = Path.GetFullPath(Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must stay within the project directory.");
        if (!isDirectory && Path.EndsInDirectorySeparator(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must name a file.");
        return fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool GlobMatches(string pattern, string relativePath)
    {
        var regex = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    regex.Append("(?:.*/)?");
                }
                else
                    regex.Append(".*");
            }
            else if (character == '*')
                regex.Append("[^/]*");
            else if (character == '?')
                regex.Append("[^/]");
            else
                regex.Append(Regex.Escape(character.ToString()));
        }
        regex.Append('$');
        return Regex.IsMatch(relativePath, regex.ToString(), OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record ProjectDocument(
        [property: JsonPropertyName("target")] string? Target,
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("cpuFeatures")] string[]? CpuFeatures,
        [property: JsonPropertyName("modules")] ModuleDocument[]? Modules,
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("exclude")] string[]? Exclude,
        [property: JsonPropertyName("noRecursion")] bool? NoRecursion,
        [property: JsonPropertyName("panicPolicy")] string? PanicPolicy,
        [property: JsonPropertyName("build")] BuildDocument? Build,
        [property: JsonPropertyName("run")] RunDocument? Run,
        [property: JsonPropertyName("espIdf")] EspIdfDocument? EspIdf,
        [property: JsonPropertyName("freestanding")] FreestandingDocument? Freestanding,
        [property: JsonPropertyName("cosmopolitan")] CosmopolitanDocument? Cosmopolitan);

    private sealed record ModuleDocument(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("selector")] string Selector,
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("vendor")] string? Vendor,
        [property: JsonPropertyName("updatePolicy")] string? UpdatePolicy);

    private sealed record EspIdfDocument([property: JsonPropertyName("bindings")] string[]? Bindings);

    private sealed record FreestandingDocument(
        [property: JsonPropertyName("linkerScript")] string? LinkerScript,
        [property: JsonPropertyName("entrySymbol")] string? EntrySymbol,
        [property: JsonPropertyName("nativeSources")] string[]? NativeSources,
        [property: JsonPropertyName("objectFiles")] string[]? ObjectFiles,
        [property: JsonPropertyName("libraries")] string[]? Libraries,
        [property: JsonPropertyName("compileOptions")] string[]? CompileOptions,
        [property: JsonPropertyName("linkOptions")] string[]? LinkOptions);

    private sealed record CosmopolitanDocument([property: JsonPropertyName("mode")] string? Mode);

    private sealed record BuildDocument(
        [property: JsonPropertyName("generatedC")] string? GeneratedC,
        [property: JsonPropertyName("generatedHeader")] string? GeneratedHeader,
        [property: JsonPropertyName("cLayout")] string? CLayout,
        [property: JsonPropertyName("generatedDirectory")] string? GeneratedDirectory,
        [property: JsonPropertyName("symbolMap")] string? SymbolMap,
        [property: JsonPropertyName("lto")] bool? Lto,
        [property: JsonPropertyName("configuration")] string? Configuration,
        [property: JsonPropertyName("compiler")] string? Compiler,
        [property: JsonPropertyName("executable")] string? Executable,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("espIdfProjectDirectory")] string? EspIdfProjectDirectory);

    private sealed record RunDocument(
        [property: JsonPropertyName("executor")] string? Executor,
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("args")] string[]? Arguments,
        [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
        [property: JsonPropertyName("environment")] Dictionary<string, string>? Environment,
        [property: JsonPropertyName("successExitCodes")] int[]? SuccessExitCodes);
}
