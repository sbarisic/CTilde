using System.Text;
using System.Text.RegularExpressions;
using CTilde;

namespace CTilde.Cli;

internal static class FormatCommand
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".artifacts", "artifacts", "bin", "obj", "node_modules", ".vscode-test", ".ctilde", ".ctilde-cache", ".modules",
    };

    public static int Run(string[] args)
    {
        if (!TryParse(args, out var paths, out var checkOnly, out var error))
        {
            Console.Error.WriteLine($"ctilde: {error}");
            return 2;
        }

        try
        {
            var files = ResolveFiles(paths);
            if (files.Count == 0)
            {
                Console.Error.WriteLine("ctilde: format did not find any .ct files.");
                return 1;
            }

            var changes = new List<(string Path, string Text)>();
            var failed = false;
            foreach (var path in files)
            {
                SourceText source;
                string originalText;
                try
                {
                    source = SourceText.FromFile(path);
                    originalText = source.Text;
                    if (source.Text.StartsWith('\ufeff'))
                        source = SourceText.From(source.Text[1..], source.FilePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    Console.Error.WriteLine($"ctilde: could not read '{path}': {exception.Message}");
                    failed = true;
                    continue;
                }

                if (Regex.IsMatch(source.Text, "^[ \\t]*//[ \\t]*ctilde-format:[ \\t]*preserve[ \\t]*\\r?$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant))
                {
                    var preserved = NormalizePreserved(source.Text);
                    if (!string.Equals(originalText, preserved, StringComparison.Ordinal))
                        changes.Add((path, preserved));
                    continue;
                }

                var (parseText, placeholders) = ReplaceTemplatePlaceholders(source.Text);
                var tree = SyntaxTree.Parse(SourceText.From(parseText, source.FilePath));
                var errors = tree.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length != 0 || tree.SkippedTokens.Length != 0 || tree.Tokens.Any(token => token.IsMissing))
                {
                    foreach (var diagnostic in errors)
                        Console.Error.WriteLine(diagnostic);
                    if (errors.Length == 0)
                        Console.Error.WriteLine($"ctilde: '{path}' contains recovered or missing syntax and cannot be formatted.");
                    failed = true;
                    continue;
                }

                var formatted = CTildeFormatter.Format(tree);
                foreach (var placeholder in placeholders)
                    formatted = formatted.Replace(placeholder.Replacement, placeholder.Original, StringComparison.Ordinal);
                if (!string.Equals(originalText, formatted, StringComparison.Ordinal))
                    changes.Add((path, formatted));
            }

            if (failed)
                return 1;
            if (checkOnly)
            {
                foreach (var change in changes)
                    Console.Error.WriteLine($"ctilde: formatting required: {change.Path}");
                return changes.Count == 0 ? 0 : 1;
            }

            foreach (var change in changes)
                WriteAtomically(change.Path, change.Text);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ctilde: formatting failed: {exception.Message}");
            return 1;
        }
    }

    private static SortedSet<string> ResolveFiles(IReadOnlyList<string> inputs)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new SortedSet<string>(comparer);
        foreach (var input in inputs)
        {
            var path = Path.GetFullPath(input);
            if (File.Exists(path))
            {
                if (!path.EndsWith(".ct", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Format input is not a .ct file: {path}");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Format input is a reparse point: {path}");
                files.Add(path);
                continue;
            }
            if (!Directory.Exists(path))
                throw new IOException($"Format input does not exist: {path}");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Format input is a reparse point: {path}");
            Enumerate(path, files);
        }
        return files;
    }

    private static void Enumerate(string directory, ISet<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.ct", SearchOption.TopDirectoryOnly))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                files.Add(Path.GetFullPath(file));
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(child);
            if (ExcludedDirectories.Contains(info.Name) || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            Enumerate(child, files);
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static (string Text, IReadOnlyList<(string Original, string Replacement)> Placeholders) ReplaceTemplatePlaceholders(string text)
    {
        var replacements = new List<(string Original, string Replacement)>();
        var prefix = "__CTildeFormatPlaceholder_";
        while (text.Contains(prefix, StringComparison.Ordinal))
            prefix = "_" + prefix;
        var index = 0;
        var result = Regex.Replace(text, "\\$[A-Za-z][A-Za-z0-9]*\\$", match =>
        {
            var replacement = $"{prefix}{index++}__";
            replacements.Add((match.Value, replacement));
            return replacement;
        }, RegexOptions.CultureInvariant);
        return (result, replacements);
    }

    private static string NormalizePreserved(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Join('\n', normalized.Split('\n').Select(line => line.TrimEnd(' ', '\t'))).TrimEnd('\n') + "\n";
    }

    private static bool TryParse(string[] args, out IReadOnlyList<string> paths, out bool checkOnly, out string? error)
    {
        var values = new List<string>();
        checkOnly = false;
        error = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--check" && !checkOnly)
                checkOnly = true;
            else if (args[index].StartsWith("-", StringComparison.Ordinal))
            {
                paths = [];
                error = $"Unknown format option '{args[index]}'.";
                return false;
            }
            else
                values.Add(args[index]);
        }
        paths = values;
        if (values.Count == 0)
        {
            error = "format requires at least one .ct file or directory.";
            return false;
        }
        return true;
    }
}
