using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace CTilde.VisualStudio;

public sealed class CTildeOptionsPage : DialogPage
{
    [Category("Tool paths")]
    [DisplayName("dotnet path")]
    [Description("Optional path to dotnet.exe. Leave empty to use dotnet from PATH.")]
    public string DotNetPath { get; set; } = string.Empty;

    [Category("Tool paths")]
    [DisplayName("Compiler path")]
    [Description("Optional path to ctilde.dll. Leave empty to use the compiler bundled with this extension.")]
    public string CompilerPath { get; set; } = string.Empty;

    [Category("Tool paths")]
    [DisplayName("Language server path")]
    [Description("Optional path to CTilde.LanguageServer.dll. Leave empty to use the bundled server.")]
    public string LanguageServerPath { get; set; } = string.Empty;

    [Category("Language server")]
    [DisplayName("Protocol tracing")]
    [Description("Write complete language-server protocol tracing to the C~ output pane.")]
    public bool TraceProtocol { get; set; }

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        CTildeToolPaths.Update(this);
    }
}

internal static class CTildeToolPaths
{
    private static readonly object Gate = new();
    private static string _dotNetPath = string.Empty;
    private static string _compilerPath = string.Empty;
    private static string _languageServerPath = string.Empty;
    private static bool _traceProtocol;
    internal static event Action? Changed;

    internal static void Update(CTildeOptionsPage options)
    {
        var changed = false;
        lock (Gate)
        {
            changed = _dotNetPath != options.DotNetPath ||
                _compilerPath != options.CompilerPath ||
                _languageServerPath != options.LanguageServerPath ||
                _traceProtocol != options.TraceProtocol;
            _dotNetPath = options.DotNetPath;
            _compilerPath = options.CompilerPath;
            _languageServerPath = options.LanguageServerPath;
            _traceProtocol = options.TraceProtocol;
        }
        if (changed)
            Changed?.Invoke();
    }

    internal static Snapshot Current
    {
        get
        {
            lock (Gate)
                return new Snapshot(_dotNetPath, _compilerPath, _languageServerPath, _traceProtocol);
        }
    }

    internal sealed class Snapshot
    {
        internal Snapshot(string dotNetPath, string compilerPath, string languageServerPath, bool traceProtocol)
        {
            DotNetPath = dotNetPath;
            CompilerPath = compilerPath;
            LanguageServerPath = languageServerPath;
            TraceProtocol = traceProtocol;
        }

        internal string DotNetPath { get; }
        internal string CompilerPath { get; }
        internal string LanguageServerPath { get; }
        internal bool TraceProtocol { get; }
    }
}
