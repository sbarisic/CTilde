namespace CTilde.VisualStudio.Core;

public static class ProjectFiles
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".ctilde", ".ctilde-cache", "bin", "obj", "build", "node_modules", "managed_components",
    };

    private static readonly HashSet<string> VisibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ct", ".json", ".c", ".h", ".hpp", ".cc", ".cpp", ".S", ".s", ".ld", ".cmake", ".txt", ".xml", ".yml", ".yaml",
    };

    public static IReadOnlyList<string> Enumerate(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var result = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var child in directory.EnumerateDirectories().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!ExcludedDirectoryNames.Contains(child.Name) && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
            }
            foreach (var file in directory.EnumerateFiles().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (file.Name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase) ||
                    VisibleExtensions.Contains(file.Extension))
                    result.Add(file.FullName);
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
