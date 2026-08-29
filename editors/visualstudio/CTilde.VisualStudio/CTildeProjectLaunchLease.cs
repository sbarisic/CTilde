using CTilde.VisualStudio.Core;

namespace CTilde.VisualStudio;

internal static class CTildeProjectLaunchLease
{
    internal static IDisposable Acquire(string manifestPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, ".ctilde");
        Directory.CreateDirectory(directory);
        var identity = DebugLaunchContracts.ManifestIdentity(manifestPath);
        var path = Path.Combine(directory, $"visualstudio-launch-{identity}.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("This C~ project is already running or being debugged.", exception);
        }
    }
}
