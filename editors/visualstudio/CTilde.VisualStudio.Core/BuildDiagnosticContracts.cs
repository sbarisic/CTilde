using System.Security.Cryptography;
using System.Text.Json;

namespace CTilde.VisualStudio.Core;

public sealed record BuildDiagnosticReceipt(
    int SchemaVersion,
    string Manifest,
    string Operation,
    string CompletionState,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyDictionary<string, string> SourceHashes,
    IReadOnlyList<BuildReceiptDiagnostic> Diagnostics);

public sealed record BuildReceiptDiagnostic(
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Severity,
    string Code,
    string Message,
    string Kind);

public static class BuildDiagnosticReceipts
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string PathForManifest(string manifestPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, ".ctilde", "build-diagnostics.json");

    public static bool TryRead(string path, out BuildDiagnosticReceipt? receipt)
    {
        try
        {
            receipt = JsonSerializer.Deserialize<BuildDiagnosticReceipt>(File.ReadAllText(path), Options);
            return receipt is { SchemaVersion: SchemaVersion, SourceHashes: not null, Diagnostics: not null } &&
                !string.IsNullOrWhiteSpace(receipt.Manifest) && !string.IsNullOrWhiteSpace(receipt.CompletionState) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(receipt.Manifest)), Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..")),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            receipt = null;
            return false;
        }
    }

    public static bool SourceHashMatches(BuildDiagnosticReceipt receipt, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var expected = receipt.SourceHashes.FirstOrDefault(entry => string.Equals(Path.GetFullPath(entry.Key), fullPath,
            StringComparison.OrdinalIgnoreCase)).Value;
        if (expected is null || !File.Exists(fullPath))
            return false;
        try
        {
            var actual = ToHex(SHA256.Create().ComputeHash(File.ReadAllBytes(fullPath)));
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool SourceTextHashMatches(BuildDiagnosticReceipt receipt, string filePath, string text)
    {
        var fullPath = Path.GetFullPath(filePath);
        var expected = receipt.SourceHashes.FirstOrDefault(entry => string.Equals(Path.GetFullPath(entry.Key), fullPath,
            StringComparison.OrdinalIgnoreCase)).Value;
        if (expected is null)
            return false;
        using var sha = SHA256.Create();
        var actual = ToHex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<BuildReceiptDiagnostic> CurrentDiagnostics(BuildDiagnosticReceipt receipt,
        string filePath, string currentText, string? savedManifestText = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        var isManifest = string.Equals(fullPath, Path.GetFullPath(receipt.Manifest), StringComparison.OrdinalIgnoreCase);
        var current = isManifest
            ? savedManifestText is not null && string.Equals(currentText, savedManifestText, StringComparison.Ordinal)
            : SourceTextHashMatches(receipt, fullPath, currentText);
        if (!current || string.Equals(receipt.CompletionState, "succeeded", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<BuildReceiptDiagnostic>();
        return receipt.Diagnostics.Where(item => string.Equals(Path.GetFullPath(item.File), fullPath, StringComparison.OrdinalIgnoreCase) &&
                (isManifest ? item.Kind == "manifest" : item.Kind == "source"))
            .ToArray();
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            chars[index * 2] = alphabet[bytes[index] >> 4];
            chars[index * 2 + 1] = alphabet[bytes[index] & 15];
        }
        return new string(chars);
    }
}
