namespace CTilde.VisualStudio.Core;

public static class ProjectSelection
{
    public static string? Resolve(string? selectedProject, string? activeDocumentProject, string? activeDocument,
        IReadOnlyCollection<string> loadedProjects)
    {
        if (loadedProjects is null)
            throw new ArgumentNullException(nameof(loadedProjects));
        var projects = loadedProjects.Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
        var selected = Match(selectedProject, projects);
        if (selected is not null)
            return selected;
        var active = Match(activeDocumentProject, projects);
        if (active is not null)
            return active;
        if (!string.IsNullOrWhiteSpace(activeDocument))
        {
            var document = Path.GetFullPath(activeDocument);
            if (Path.GetExtension(document).Equals(".ctproj", StringComparison.OrdinalIgnoreCase))
                return document;
            var candidates = projects.Where(project => IsAtOrBelow(Path.GetDirectoryName(project)!, document)).ToArray();
            if (candidates.Length != 0)
            {
                var longest = candidates.Max(project => Path.GetDirectoryName(project)!.Length);
                var mostSpecific = candidates.Where(project => Path.GetDirectoryName(project)!.Length == longest).ToArray();
                return mostSpecific.Length == 1 ? mostSpecific[0] : null;
            }
        }
        return projects.Length == 1 ? projects[0] : null;
    }

    private static string? Match(string? candidate, IEnumerable<string> projects)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        var fullCandidate = Path.GetFullPath(candidate);
        return projects.FirstOrDefault(project => PathComparer.Equals(project, fullCandidate));
    }

    private static bool IsAtOrBelow(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return PathComparer.Equals(root, fullPath) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsWindows { get; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
    private static StringComparer PathComparer { get; } = IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison { get; } = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
