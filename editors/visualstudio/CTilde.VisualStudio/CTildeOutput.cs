using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CTilde.VisualStudio;

internal static class CTildeOutput
{
    internal static readonly Guid PaneGuid = new("6ba59724-089c-41bb-bfea-aa2297adcc80");
    private static IVsOutputWindowPane? _pane;

    public static void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010:Invoke single-threaded types on Main thread", Justification = "OutputStringThreadSafe is explicitly safe from background threads.")]
    public static void WriteLine(string text) => _pane?.OutputStringThreadSafe(text.TrimEnd() + Environment.NewLine);

    public static void Show()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane()?.Activate();
    }

    private static IVsOutputWindowPane? GetPane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane is not null)
            return _pane;
        var output = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (output is null)
            return null;
        var guid = PaneGuid;
        output.CreatePane(ref guid, "C~", fInitVisible: 1, fClearWithSolution: 0);
        output.GetPane(ref guid, out _pane);
        return _pane;
    }
}
