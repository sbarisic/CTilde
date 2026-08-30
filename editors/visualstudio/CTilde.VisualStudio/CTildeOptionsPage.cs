using System.ComponentModel;
using CTilde.VisualStudio.Core;
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

    [Category("Language server")]
    [DisplayName("Show reference CodeLens")]
    [Description("Show C#-style reference counts above C~ declarations when Visual Studio CodeLens is enabled.")]
    [DefaultValue(true)]
    public bool ShowReferenceCodeLens { get; set; } = true;

    [Category("Debugger")]
    [DisplayName("Debug compiler")]
    [Description("GCC, Clang, wsl:gcc, or an executable path. Blank uses the manifest and then CTILDE_CC.")]
    public string DebugCompiler { get; set; } = string.Empty;

    [Category("Debugger")]
    [DisplayName("GDB path")]
    [Description("Optional GDB executable override. Blank uses the prepared descriptor.")]
    public string GdbPath { get; set; } = string.Empty;

    [Category("ESP-IDF")]
    [DisplayName("ESP-IDF path")]
    [Description("Optional ESP-IDF root for QEMU debugging. Blank uses IDF_PATH and the compiler's normal discovery.")]
    public string EspIdfPath { get; set; } = string.Empty;

    [Category("ESP-IDF")]
    [DisplayName("Espressif Clang path")]
    [Description("Optional Espressif Clang executable for ESP-IDF preparation. Blank uses CTILDE_ESP_CLANG and the compiler's normal discovery.")]
    public string EspClangPath { get; set; } = string.Empty;

    [Category("Debugger")]
    [DisplayName("Memory diagnostics")]
    [Description("Runtime memory diagnostics prepared for debug sessions: Off, Objects, or Guarded.")]
    [DefaultValue(CTildeDebugMemoryMode.Objects)]
    public CTildeDebugMemoryMode DebugMemory { get; set; } = CTildeDebugMemoryMode.Objects;

    [Category("Debugger")]
    [DisplayName("Stop at entry")]
    public bool StopAtEntry { get; set; }

    [Category("Debugger")]
    [DisplayName("Show runtime frames")]
    public bool ShowRuntimeFrames { get; set; }

    [Category("Debugger")]
    [DisplayName("Debugger tracing")]
    public bool TraceDebugger { get; set; }

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
    private static bool _showReferenceCodeLens = true;
    private static string _debugCompiler = string.Empty;
    private static string _gdbPath = string.Empty;
    private static string _espIdfPath = string.Empty;
    private static string _espClangPath = string.Empty;
    private static CTildeDebugMemoryMode _debugMemory = CTildeDebugMemoryMode.Objects;
    private static bool _stopAtEntry;
    private static bool _showRuntimeFrames;
    private static bool _traceDebugger;
    internal static event Action? Changed;

    internal static void Update(CTildeOptionsPage options)
    {
        var changed = false;
        lock (Gate)
        {
            changed = _dotNetPath != options.DotNetPath ||
                _compilerPath != options.CompilerPath ||
                _languageServerPath != options.LanguageServerPath ||
                _traceProtocol != options.TraceProtocol ||
                _showReferenceCodeLens != options.ShowReferenceCodeLens ||
                _debugCompiler != options.DebugCompiler || _gdbPath != options.GdbPath ||
                _espIdfPath != options.EspIdfPath || _espClangPath != options.EspClangPath ||
                _debugMemory != options.DebugMemory || _stopAtEntry != options.StopAtEntry ||
                _showRuntimeFrames != options.ShowRuntimeFrames || _traceDebugger != options.TraceDebugger;
            _dotNetPath = options.DotNetPath;
            _compilerPath = options.CompilerPath;
            _languageServerPath = options.LanguageServerPath;
            _traceProtocol = options.TraceProtocol;
            _showReferenceCodeLens = options.ShowReferenceCodeLens;
            _debugCompiler = options.DebugCompiler;
            _gdbPath = options.GdbPath;
            _espIdfPath = options.EspIdfPath;
            _espClangPath = options.EspClangPath;
            _debugMemory = options.DebugMemory;
            _stopAtEntry = options.StopAtEntry;
            _showRuntimeFrames = options.ShowRuntimeFrames;
            _traceDebugger = options.TraceDebugger;
        }
        if (changed)
            Changed?.Invoke();
    }

    internal static Snapshot Current
    {
        get
        {
            lock (Gate)
                return new Snapshot(_dotNetPath, _compilerPath, _languageServerPath, _traceProtocol,
                    _showReferenceCodeLens, _debugCompiler, _gdbPath, _espIdfPath, _espClangPath, _debugMemory, _stopAtEntry, _showRuntimeFrames, _traceDebugger);
        }
    }

    internal sealed class Snapshot
    {
        internal Snapshot(string dotNetPath, string compilerPath, string languageServerPath, bool traceProtocol,
            bool showReferenceCodeLens, string debugCompiler, string gdbPath, string espIdfPath, string espClangPath,
            CTildeDebugMemoryMode debugMemory, bool stopAtEntry, bool showRuntimeFrames, bool traceDebugger)
        {
            DotNetPath = dotNetPath;
            CompilerPath = compilerPath;
            LanguageServerPath = languageServerPath;
            TraceProtocol = traceProtocol;
            ShowReferenceCodeLens = showReferenceCodeLens;
            DebugCompiler = debugCompiler;
            GdbPath = gdbPath;
            EspIdfPath = espIdfPath;
            EspClangPath = espClangPath;
            DebugMemory = debugMemory;
            StopAtEntry = stopAtEntry;
            ShowRuntimeFrames = showRuntimeFrames;
            TraceDebugger = traceDebugger;
        }

        internal string DotNetPath { get; }
        internal string CompilerPath { get; }
        internal string LanguageServerPath { get; }
        internal bool TraceProtocol { get; }
        internal bool ShowReferenceCodeLens { get; }
        internal string DebugCompiler { get; }
        internal string GdbPath { get; }
        internal string EspIdfPath { get; }
        internal string EspClangPath { get; }
        internal CTildeDebugMemoryMode DebugMemory { get; }
        internal bool StopAtEntry { get; }
        internal bool ShowRuntimeFrames { get; }
        internal bool TraceDebugger { get; }
    }
}
