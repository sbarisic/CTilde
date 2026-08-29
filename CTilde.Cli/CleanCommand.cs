using CTilde;

namespace CTilde.Cli;

internal static class CleanCommand
{
    public static int Run(string[] args)
    {
        if (!TryParse(args, out var manifestPath, out var trace, out var error))
        {
            Console.Error.WriteLine($"ctilde: {error}");
            return 2;
        }

        try
        {
            var project = CTildeProjectFile.Load(manifestPath!);
            if (project.Configuration.Kind == CTildeProjectKind.StandardLibrary)
            {
                if (trace)
                    Console.Error.WriteLine($"trace: standard-library clean has no outputs: {project.ManifestPath}");
                return 0;
            }
            return Clean(project, trace);
        }
        catch (CTildeProjectException exception)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return 1;
        }
    }

    private static int Clean(CTildeProject project, bool trace)
    {
        var build = project.Configuration.Build!;
        var files = new HashSet<string>(PathComparer)
        {
            build.GeneratedHeaderPath,
        };
        var directories = new HashSet<string>(PathComparer);

        if (build.CLayout == GeneratedCLayout.Unity)
            files.Add(build.GeneratedCPath);
        else
            directories.Add(build.GeneratedDirectory);

        if (build.SymbolMapPath is not null)
            files.Add(build.SymbolMapPath);

        var debugMapDirectory = build.CLayout == GeneratedCLayout.Modules
            ? build.GeneratedDirectory
            : Path.GetDirectoryName(build.GeneratedCPath)!;
        files.Add(Path.Combine(debugMapDirectory, "ctilde_debug.json"));

        foreach (var binding in project.Configuration.BindingManifests)
        {
            files.Add(binding.DeclarationsPath);
            files.Add(binding.AdapterSourcePath);
        }
        files.Add(Path.Combine(build.GeneratedDirectory, "ctilde_bindings.cmake"));
        files.Add(Path.Combine(build.GeneratedDirectory, "ctilde_bindings_probe.c"));

        if (build.ExecutablePath is not null)
        {
            files.Add(build.ExecutablePath);
            files.Add(build.ExecutablePath + ".dbg");
            var outputDirectory = Path.GetDirectoryName(build.ExecutablePath)!;
            files.Add(Path.Combine(outputDirectory, ".ctilde", "ctilde-debug-target.json"));
            directories.Add(Path.Combine(outputDirectory, ".ctilde-cache"));
        }

        if (build.EspIdfProjectDirectory is not null)
        {
            var idfBuild = project.Configuration.Environment == TargetEnvironment.Qemu
                ? Path.Combine(build.EspIdfProjectDirectory, "build", project.Configuration.EspIdfChip == CTilde.EspIdfChip.Esp32 ? "esp32_qemu" : "esp32c3_qemu")
                : Path.Combine(build.EspIdfProjectDirectory, "build");
            files.Add(Path.Combine(idfBuild, ".ctilde", "ctilde-debug-target.json"));
            directories.Add(idfBuild);
        }

        var protectedFiles = ProtectedFiles(project);
        var failed = false;
        foreach (var path in files.OrderBy(path => path, PathComparer))
        {
            if (protectedFiles.Contains(path))
            {
                Trace(trace, "skipped protected file", path);
                failed = true;
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    Trace(trace, "skipped missing file", path);
                    continue;
                }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    Trace(trace, "skipped reparse-point file", path);
                    failed = true;
                    continue;
                }
                File.Delete(path);
                Trace(trace, "removed file", path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ctilde: Could not remove '{path}': {exception.Message}");
                failed = true;
            }
        }

        foreach (var path in directories.OrderByDescending(path => path.Length).ThenBy(path => path, PathComparer))
        {
            if (!ValidateOwnedDirectory(project.RootDirectory, path, protectedFiles, out var reason))
            {
                Trace(trace, $"skipped directory ({reason})", path);
                failed = true;
                continue;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Trace(trace, "skipped missing directory", path);
                    continue;
                }
                if (ContainsReparsePoint(path))
                {
                    Trace(trace, "skipped directory (contains a reparse point)", path);
                    failed = true;
                    continue;
                }
                Directory.Delete(path, recursive: true);
                Trace(trace, "removed directory", path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ctilde: Could not remove '{path}': {exception.Message}");
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static HashSet<string> ProtectedFiles(CTildeProject project)
    {
        var files = project.SourceFiles.Select(Path.GetFullPath).ToHashSet(PathComparer);
        foreach (var binding in project.Configuration.BindingManifests)
            files.Remove(binding.DeclarationsPath);
        var freestanding = project.Configuration.Freestanding;
        if (freestanding is not null)
        {
            if (freestanding.LinkerScriptPath is not null)
                files.Add(freestanding.LinkerScriptPath);
            files.UnionWith(freestanding.NativeSources);
            files.UnionWith(freestanding.ObjectFiles);
            files.UnionWith(freestanding.Libraries);
        }
        if (project.Configuration.Hosted is { } hosted)
            files.UnionWith(hosted.NativeSources);
        return files;
    }

    private static bool ValidateOwnedDirectory(string root, string path, HashSet<string> protectedFiles, out string reason)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var driveRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? fullPath);
        if (PathsEqual(fullPath, fullRoot) || PathsEqual(fullPath, driveRoot))
        {
            reason = "root paths are never recursive-clean targets";
            return false;
        }
        if (!IsStrictlyBelow(fullRoot, fullPath))
        {
            reason = "recursive targets must be below the project root";
            return false;
        }
        if (protectedFiles.Any(file => IsAtOrBelow(fullPath, file)))
        {
            reason = "contains a source or native input";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (entry is DirectoryInfo child)
                    pending.Push(child);
            }
        }
        return false;
    }

    private static bool TryParse(string[] args, out string? manifestPath, out bool trace, out string? error)
    {
        manifestPath = null;
        trace = false;
        error = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--trace" && !trace)
            {
                trace = true;
                continue;
            }
            if (args[index] == "--project" && manifestPath is null && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                manifestPath = args[++index];
                continue;
            }
            error = "clean requires exactly --project <ctilde.json> and optional --trace.";
            return false;
        }
        if (manifestPath is not null)
            return true;
        error = "clean requires --project <ctilde.json>.";
        return false;
    }

    private static void Trace(bool enabled, string action, string path)
    {
        if (enabled)
            Console.Error.WriteLine($"trace: clean {action}: {path}");
    }

    private static bool IsStrictlyBelow(string root, string path) =>
        !PathsEqual(root, path) && IsAtOrBelow(root, path);

    private static bool IsAtOrBelow(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
