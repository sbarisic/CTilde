namespace CTilde.Cli;

internal static class EspIdfEnvironment
{
    public static string? ResolveIdfPath(string? configuredPath)
    {
        foreach (var candidate in new[] { configuredPath, Environment.GetEnvironmentVariable("IDF_PATH") })
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var root in CandidateToolsRoots())
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var profile in EnumerateWindowsProfiles(root))
            {
                var candidate = ReadProfileVariable(profile, "IDF_PATH");
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public static string? FindWindowsProfile(string idfPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var root in CandidateToolsRoots())
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var profile in EnumerateWindowsProfiles(root))
            {
                var configuredIdfPath = ReadProfileVariable(profile, "IDF_PATH");
                if (configuredIdfPath is not null && PathsEqual(configuredIdfPath, idfPath))
                    return profile;
            }
        }
        return null;
    }

    public static string? FindProfileVariable(string? idfPath, string name)
    {
        if (string.IsNullOrWhiteSpace(idfPath))
            return null;
        var profile = FindWindowsProfile(idfPath);
        return profile is null ? null : ReadProfileVariable(profile, name);
    }

    public static IEnumerable<string> ToolsRoots(string? idfPath)
    {
        var configured = Environment.GetEnvironmentVariable("IDF_TOOLS_PATH");
        var profile = FindProfileVariable(idfPath, "IDF_TOOLS_PATH");
        return CandidateToolsRoots(configured, profile).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateToolsRoots(params string?[] additional)
    {
        foreach (var candidate in additional.Prepend(Environment.GetEnvironmentVariable("IDF_TOOLS_PATH")))
            if (!string.IsNullOrWhiteSpace(candidate))
                yield return Path.GetFullPath(candidate);
        if (OperatingSystem.IsWindows())
            yield return @"C:\Espressif\tools";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espressif");
    }

    private static IEnumerable<string> EnumerateWindowsProfiles(string root) =>
        Directory.EnumerateFiles(root, "Microsoft.*.PowerShell_profile.ps1", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);

    private static string? ReadProfileVariable(string profile, string name)
    {
        foreach (var line in File.ReadLines(profile))
        {
            var marker = $"\"{name}\"";
            var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;
            var equalsIndex = line.IndexOf('=', markerIndex + marker.Length);
            if (equalsIndex < 0)
                continue;
            var value = line[(equalsIndex + 1)..].Trim().TrimEnd(';').Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return Environment.ExpandEnvironmentVariables(value[1..^1]);
        }
        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
