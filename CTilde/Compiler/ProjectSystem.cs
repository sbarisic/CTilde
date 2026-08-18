using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public sealed record CTildeProjectConfiguration(
    CompilationTarget Target,
    ImmutableArray<string> Sources,
    ImmutableArray<string> Exclude);

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

        return new CTildeProject(fullManifestPath, root, new CTildeProjectConfiguration(target, sources, excludes), files);
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
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("exclude")] string[]? Exclude);
}
