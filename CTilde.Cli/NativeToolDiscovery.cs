namespace CTilde.Cli;

internal static class NativeToolDiscovery
{
    public static string? FindOnPath(string name, string? pathOverride = null)
    {
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD;.PY")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        if (OperatingSystem.IsWindows() && Path.HasExtension(name))
            extensions = [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}
