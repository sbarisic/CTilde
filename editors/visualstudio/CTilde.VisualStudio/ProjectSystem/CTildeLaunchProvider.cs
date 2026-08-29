using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;

namespace CTilde.VisualStudio.ProjectSystem;

[ExportDebugger(DebuggerName)]
[AppliesTo(CTildeProjectType.Capability)]
internal sealed class CTildeLaunchProvider : IDebugLaunchProvider
{
    internal const string DebuggerName = "CTilde";
    private readonly ConfiguredProject _project;

    [ImportingConstructor]
    public CTildeLaunchProvider(ConfiguredProject project) => _project = project;

    // CPS calls this while updating command state and enforces a 100 ms timeout.
    // Validation belongs in LaunchAsync so cold disk access cannot disable F5/Ctrl+F5.
    public Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions) => Task.FromResult(true);

    public Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        if ((launchOptions & DebugLaunchOptions.NoDebug) == 0)
            throw new InvalidOperationException(CTildeRunManager.DebuggingUnavailableMessage);
        var contract = CTildeProjectContract.Load(_project.UnconfiguredProject.FullPath);
        CTildeRunManager.Start(contract);
        return Task.CompletedTask;
    }
}
