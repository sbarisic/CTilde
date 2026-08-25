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
    bool NoRecursion);

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
    ImmutableArray<string> SourceFiles);

public sealed class CTildeProjectException : Exception
{
    public CTildeProjectException(string message) : base(message) { }

    public CTildeProjectException(string message, Exception innerException) : base(message, innerException) { }
}

public static class CTildeProjectFile
{
    private static readonly string[] DefaultExcludes =
    [
        ".git/**", "**/bin/**", "**/obj/**", "**/build/**", "**/node_modules/**", "**/managed_components/**",
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
            _ => throw new CTildeProjectException($"Unknown target '{document.Target}' in '{fullManifestPath}'; expected hosted or esp-idf."),
        };
        var architecture = ParseArchitecture(document.Architecture, fullManifestPath);
        var root = Path.GetDirectoryName(fullManifestPath)!;
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

        if (target == CompilationTarget.Hosted && document.EspIdf is not null)
            throw new CTildeProjectException($"Property 'espIdf' in '{fullManifestPath}' is valid only for ESP-IDF projects.");
        var bindingPaths = document.EspIdf?.Bindings ?? [];
        var bindingManifests = bindingPaths.Select(path => EspIdfBindingManifest.Load(path, root)).OrderBy(binding => binding.ManifestPath, comparer).ToImmutableArray();
        if (bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }).Distinct(comparer).Count() != bindingManifests.Length * 2)
            throw new CTildeProjectException($"ESP-IDF binding outputs in '{fullManifestPath}' must be distinct.");
        foreach (var declaration in bindingManifests.Select(binding => binding.DeclarationsPath))
            if (files.Contains(declaration, comparer))
                throw new CTildeProjectException($"ESP-IDF binding declaration '{declaration}' cannot overwrite an ordinary project source in '{fullManifestPath}'.");
        files = files.Concat(bindingManifests.Select(binding => binding.DeclarationsPath).Where(File.Exists)).Distinct(comparer).OrderBy(path => path, comparer).ToImmutableArray();
        var build = CreateBuildConfiguration(document.Build, target, root, fullManifestPath, files);
        foreach (var output in bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }))
            if (PathsEqual(output, build.GeneratedCPath) || PathsEqual(output, build.GeneratedHeaderPath) || IsInsideDirectory(output, build.GeneratedDirectory))
                throw new CTildeProjectException($"ESP-IDF binding output '{output}' conflicts with compiler output in '{fullManifestPath}'.");
        return new CTildeProject(fullManifestPath, root, new CTildeProjectConfiguration(target, architecture, sources, excludes, build, bindingManifests, document.NoRecursion ?? false), files);
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
        if (target == CompilationTarget.Hosted && document?.EspIdfProjectDirectory is not null)
            throw new CTildeProjectException($"Property 'build.espIdfProjectDirectory' in '{manifestPath}' is valid only for ESP-IDF projects.");
        if (target == CompilationTarget.EspIdf &&
            (document?.Compiler is not null || document?.Configuration is not null || document?.Executable is not null || document?.Lto == true))
            throw new CTildeProjectException($"Properties 'build.compiler', 'build.configuration', 'build.executable', and 'build.lto' in '{manifestPath}' are valid only for hosted projects.");

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
        if (compiler.Contains(Path.DirectorySeparatorChar) || compiler.Contains(Path.AltDirectorySeparatorChar))
            compiler = ResolveProjectPath(compiler, "build.compiler", root, manifestPath, isDirectory: false);

        string? executable = null;
        string? espIdfProjectDirectory = null;
        if (target == CompilationTarget.Hosted)
        {
            var projectName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(projectName))
                projectName = "program";
            var defaultExecutable = $"build/{projectName}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}";
            executable = ResolveProjectPath(document?.Executable ?? defaultExecutable, "build.executable", root, manifestPath, isDirectory: false);
            if (PathsEqual(executable, generatedC) || PathsEqual(executable, generatedHeader) || sourceFiles.Any(path => PathsEqual(path, executable)))
                throw new CTildeProjectException($"Property 'build.executable' in '{manifestPath}' must name a distinct non-source file.");
        }
        else
        {
            espIdfProjectDirectory = ResolveProjectPath(document?.EspIdfProjectDirectory ?? ".", "build.espIdfProjectDirectory", root, manifestPath, isDirectory: true);
        }

        return new CTildeProjectBuildConfiguration(generatedC, generatedHeader, cLayout, generatedDirectory, symbolMap, lto,
            configuration, compiler, executable, espIdfProjectDirectory);
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
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("exclude")] string[]? Exclude,
        [property: JsonPropertyName("noRecursion")] bool? NoRecursion,
        [property: JsonPropertyName("build")] BuildDocument? Build,
        [property: JsonPropertyName("espIdf")] EspIdfDocument? EspIdf);

    private sealed record EspIdfDocument([property: JsonPropertyName("bindings")] string[]? Bindings);

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
        [property: JsonPropertyName("espIdfProjectDirectory")] string? EspIdfProjectDirectory);
}
