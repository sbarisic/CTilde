using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.VS.Debug;
using Newtonsoft.Json;

namespace CTilde.VisualStudio.ProjectSystem;

[ExportDebugger(DebuggerName)]
[AppliesTo(CTildeProjectType.Capability)]
internal sealed class CTildeLaunchProvider : DebugLaunchProviderBase
{
    internal const string DebuggerName = "CTilde";
    private static readonly Guid EngineGuid = new(DebugLaunchContracts.EngineGuid);

    [ImportingConstructor]
    public CTildeLaunchProvider(ConfiguredProject project) : base(project) { }

    // CPS calls this while updating command state and enforces a 100 ms timeout.
    // Validation belongs in LaunchAsync so cold disk access cannot disable F5/Ctrl+F5.
    public override Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions) => Task.FromResult(true);

    public override Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        if ((launchOptions & DebugLaunchOptions.NoDebug) != 0)
        {
            var contract = CTildeProjectContract.Load(ConfiguredProject.UnconfiguredProject.FullPath);
            CTildeRunManager.Start(contract);
            return Task.CompletedTask;
        }
        return base.LaunchAsync(launchOptions);
    }

    public override async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(DebugLaunchOptions launchOptions)
    {
        var contract = CTildeProjectContract.Load(ConfiguredProject.UnconfiguredProject.FullPath);
        var preparation = await CTildeDebugPreparationRunner.PrepareAsync(contract, CancellationToken.None);
        var options = CTildeToolPaths.Current;
        var payload = JsonConvert.SerializeObject(new
        {
            request = "launch",
            debugTarget = preparation.DescriptorPath,
            gdbPath = preparation.Target == "hosted" && !string.IsNullOrWhiteSpace(options.GdbPath) ? options.GdbPath : null,
            stopAtEntry = options.StopAtEntry,
            showRuntimeFrames = options.ShowRuntimeFrames,
            externalConsole = true,
            trace = options.TraceDebugger,
            memoryDiagnostics = options.DebugMemory.ToString().ToLowerInvariant(),
        });
        return
        [
            new DebugLaunchSettings(launchOptions)
            {
                LaunchOperation = DebugLaunchOperation.CreateProcess,
                LaunchDebugEngineGuid = EngineGuid,
                CurrentDirectory = Path.GetDirectoryName(contract.ManifestPath),
                Executable = preparation.DescriptorPath,
                Options = payload,
            },
        ];
    }
}
