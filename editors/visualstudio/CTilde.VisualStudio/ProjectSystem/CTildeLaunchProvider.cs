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

    public Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        try
        {
            _ = CTildeProjectContract.Load(_project.UnconfiguredProject.FullPath);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Task.FromResult(false);
        }
    }

    public Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        if ((launchOptions & DebugLaunchOptions.NoDebug) == 0)
            throw new InvalidOperationException(CTildeRunManager.DebuggingUnavailableMessage);
        var contract = CTildeProjectContract.Load(_project.UnconfiguredProject.FullPath);
        CTildeRunManager.Start(contract);
        return Task.CompletedTask;
    }
}
