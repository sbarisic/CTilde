using System.Security.Cryptography;
using System.Text;

namespace CTilde.VisualStudio.Core;

public static class StandardLibraryUri
{
    public const string Scheme = "ctilde-stdlib:";

    public static bool TryGetDocumentId(string value, out string? documentId)
    {
        documentId = null;
        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var raw = Uri.UnescapeDataString(value.Substring(Scheme.Length).TrimStart('/'));
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf("..", StringComparison.Ordinal) >= 0 || raw.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        documentId = raw;
        return true;
    }

    public static string CachePath(string cacheRoot, string serverVersion, string uri)
    {
        if (!TryGetDocumentId(uri, out var documentId))
            throw new ArgumentException("The URI is not a valid ctilde-stdlib location.", nameof(uri));
        var safeVersion = string.Concat(serverVersion.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var hash = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(uri))).Replace("-", string.Empty).ToLowerInvariant();
        var extension = Path.GetExtension(documentId);
        if (string.IsNullOrEmpty(extension))
            extension = ".ct";
        return Path.Combine(Path.GetFullPath(cacheRoot), safeVersion, hash + extension);
    }

    public static Uri FileUri(string path) => new(Path.GetFullPath(path));
}
