using System.Security.Cryptography;
using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

internal static class BuildDiagnosticReceipt
{
    public const int SchemaVersion = 1;

    public static void Write(string manifestPath, string operation, string completionState,
        IEnumerable<Diagnostic> diagnostics, IEnumerable<string>? sources = null)
    {
        var fullManifest = Path.GetFullPath(manifestPath);
        var sourceHashes = (sources ?? [])
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(path => path, HashFile, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var entries = diagnostics.Select(diagnostic => ToEntry(diagnostic, fullManifest)).OrderBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine).ThenBy(item => item.StartColumn).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray();
        var receipt = new BuildReceipt(SchemaVersion, fullManifest, operation, completionState, DateTimeOffset.UtcNow, sourceHashes, entries);
        var directory = Path.Combine(Path.GetDirectoryName(fullManifest)!, ".ctilde");
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + Environment.NewLine;
        AtomicFile.WriteTextIfChanged(Path.Combine(directory, "build-diagnostics.json"), json);
    }

    private static BuildReceiptDiagnostic ToEntry(Diagnostic diagnostic, string manifestPath)
    {
        var endLine = diagnostic.Location.Line;
        var endColumn = diagnostic.Location.Column + Math.Max(1, diagnostic.Location.Span.Length);
        try
        {
            if (File.Exists(diagnostic.Location.FilePath))
            {
                var source = SourceText.FromFile(diagnostic.Location.FilePath);
                var end = source.GetLocation(new TextSpan(diagnostic.Location.Span.End, 0));
                endLine = end.Line;
                endColumn = end.Column;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
        }
        var kind = string.Equals(Path.GetFullPath(diagnostic.Location.FilePath), manifestPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? "manifest" : diagnostic.Location.FilePath.EndsWith(".ct", StringComparison.OrdinalIgnoreCase) ? "source" : "native";
        return new BuildReceiptDiagnostic(diagnostic.Location.FilePath, diagnostic.Location.Line, diagnostic.Location.Column,
            endLine, endColumn, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Code, diagnostic.Message, kind);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record BuildReceipt(int SchemaVersion, string Manifest, string Operation, string CompletionState,
        DateTimeOffset CompletedAtUtc, IReadOnlyDictionary<string, string> SourceHashes, IReadOnlyList<BuildReceiptDiagnostic> Diagnostics);
    private sealed record BuildReceiptDiagnostic(string File, int StartLine, int StartColumn, int EndLine, int EndColumn,
        string Severity, string Code, string Message, string Kind);
}
