using System.Text;
using System.Text.RegularExpressions;

namespace CTilde.VisualStudio.Core;

public enum CTildeCommandKind { Check, Build, Clean, Run }

public static class CommandContracts
{
    public static IReadOnlyList<string> Arguments(CTildeCommandKind command, string compilerDll, string manifestPath)
    {
        var result = new List<string> { Path.GetFullPath(compilerDll) };
        if (command == CTildeCommandKind.Clean)
            result.Add("clean");
        result.Add("--project");
        result.Add(Path.GetFullPath(manifestPath));
        result.Add(command switch
        {
            CTildeCommandKind.Check => "--check",
            CTildeCommandKind.Build => "--build",
            CTildeCommandKind.Run => "--run",
            _ => string.Empty,
        });
        result.RemoveAll(string.IsNullOrEmpty);
        return result;
    }

    public static string QuoteWindowsArgument(string value)
    {
        if (value.Length != 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    public static string JoinWindowsArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteWindowsArgument));
}

public sealed record ParsedDiagnostic(string File, int Line, int Column, string Severity, string Code, string Message);

public static class DiagnosticParser
{
    private static readonly Regex Pattern = new(
        @"^(?<file>.+)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning|info)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string line, out ParsedDiagnostic? diagnostic)
    {
        var match = Pattern.Match(line);
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var row) || !int.TryParse(match.Groups["column"].Value, out var column))
        {
            diagnostic = null;
            return false;
        }
        diagnostic = new ParsedDiagnostic(match.Groups["file"].Value, row, column, match.Groups["severity"].Value.ToLowerInvariant(), match.Groups["code"].Value, match.Groups["message"].Value);
        return true;
    }
}

public static class CommandEnablement
{
    public static bool ProjectCommandsEnabled(bool hasLoadedCTildeProject, bool operationRunning) => hasLoadedCTildeProject && !operationRunning;
    public static bool RunProjectEnabled(bool hasLoadedCTildeProject, bool operationRunning, bool manifestSupportsRun) =>
        ProjectCommandsEnabled(hasLoadedCTildeProject, operationRunning) && manifestSupportsRun;
    public static bool RestartLanguageServerEnabled(bool languageClientLoaded) => languageClientLoaded;
}

public static class RunSupport
{
    public static bool IsSupported(string? projectKind, string? target, bool hasExplicitRun) =>
        !string.Equals(projectKind, "standard-library", StringComparison.Ordinal) &&
        (target is null || target is "hosted" or "cosmopolitan" || hasExplicitRun);
}

public static class CommandOutcomes
{
    public const int CanceledExitCode = 130;

    public static bool Succeeded(int exitCode) => exitCode == 0;

    public static string MissingDotNetMessage(string details) =>
        "Could not start .NET 10. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or configure dotnet under Tools > Options > C~. " + details;
}
