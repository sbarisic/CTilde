using System.Text;

namespace CTilde.Cli;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool WriteTextIfChanged(string path, string contents)
    {
        var bytes = Utf8NoBom.GetBytes(contents);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return false;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory ?? Directory.GetCurrentDirectory(), $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
