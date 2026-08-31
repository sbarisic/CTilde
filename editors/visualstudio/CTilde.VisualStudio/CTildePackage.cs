using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CTilde.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true, RegisterUsing = RegistrationMethod.CodeBase)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(CTildeOptionsPage), "C~", "General", 0, 0, true)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuid)]
public sealed class CTildePackage : AsyncPackage, IVsSolutionEvents
{
    public const string PackageGuid = "94c2125f-423b-49ed-a929-2cb765cde05a";
    private const int ReloadAttemptCount = 20;
    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromMilliseconds(250);

    internal static CTildePackage? Current { get; private set; }
    private uint _solutionEventsCookie;
    private int _reloadGeneration;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Current = this;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        CTildeOutput.Initialize();
        CTildeToolPaths.Update(Options);
        if (await GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution)
            ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out _solutionEventsCookie));
        QueueUnmodeledProjectReload();
        await CTildeCommands.InitializeAsync(this, cancellationToken);
    }

    private void QueueUnmodeledProjectReload()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var generation = ++_reloadGeneration;
        JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < ReloadAttemptCount; attempt++)
                {
                    if (attempt != 0)
                        await Task.Delay(ReloadRetryDelay).ConfigureAwait(false);
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (generation != _reloadGeneration)
                        return;

                    var state = ReloadUnmodeledProjects();
                    if (state.SeenProject && state.RemainingUnmodeledProjects == 0)
                        return;
                }
            }
            catch (Exception exception)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                CTildeOutput.WriteLine($"C~ project reload failed: {exception.Message}");
            }
        }).FileAndForget("CTilde/ReloadProjects");
    }

    private (bool SeenProject, int RemainingUnmodeledProjects) ReloadUnmodeledProjects()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is not IVsSolution solution || solution is not IVsSolution4 solution4)
            return (false, 0);
        if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_ALLPROJECTS, Guid.Empty, out var projectEnum) < 0)
            return (false, 0);

        var projects = new IVsHierarchy[1];
        var reload = new List<Guid>();
        var seenProject = false;
        while (projectEnum.Next(1, projects, out var fetched) == 0 && fetched == 1)
        {
            var hierarchy = projects[0];
            if (solution.GetUniqueNameOfProject(hierarchy, out var uniqueName) < 0 ||
                !uniqueName.EndsWith(".ctproj", StringComparison.OrdinalIgnoreCase))
                continue;
            seenProject = true;
            if (hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out var extObject) >= 0 &&
                extObject is not null)
                continue;
            if (solution.GetGuidOfProject(hierarchy, out var projectGuid) >= 0)
                reload.Add(projectGuid);
        }

        foreach (var projectGuidValue in reload)
        {
            var projectGuid = projectGuidValue;
            var hr = solution4.ReloadProject(ref projectGuid);
            if (hr < 0)
                CTildeOutput.WriteLine($"C~ project reload failed for {projectGuid:B}: 0x{hr:X8}");
        }

        return (seenProject, reload.Count);
    }

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy hierarchy, int added)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        QueueUnmodeledProjectReload();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy stubHierarchy, IVsHierarchy realHierarchy) => VSConstants.S_OK;

    int IVsSolutionEvents.OnAfterOpenSolution(object reserved, int newSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        QueueUnmodeledProjectReload();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy hierarchy, int removed)
    {
        BuildDiagnosticTagger.ClearAll();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object reserved) => VSConstants.S_OK;

    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy realHierarchy, IVsHierarchy stubHierarchy)
    {
        BuildDiagnosticTagger.ClearAll();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterCloseSolution(object reserved)
    {
        _reloadGeneration++;
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy hierarchy, int removing, ref int cancel) => VSConstants.S_OK;

    int IVsSolutionEvents.OnQueryCloseSolution(object reserved, ref int cancel) => VSConstants.S_OK;

    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy realHierarchy, ref int cancel) => VSConstants.S_OK;

    internal CTildeOptionsPage Options => (CTildeOptionsPage)GetDialogPage(typeof(CTildeOptionsPage));
}
