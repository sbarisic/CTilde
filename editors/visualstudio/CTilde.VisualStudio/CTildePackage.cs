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
public sealed class CTildePackage : AsyncPackage
{
    public const string PackageGuid = "94c2125f-423b-49ed-a929-2cb765cde05a";
    internal static CTildePackage? Current { get; private set; }

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Current = this;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        CTildeOutput.Initialize();
        CTildeToolPaths.Update(Options);
        ReloadUnmodeledProjects();
        await CTildeCommands.InitializeAsync(this, cancellationToken);
    }

    private void ReloadUnmodeledProjects()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is not IVsSolution solution || solution is not IVsSolution4 solution4)
            return;
        if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_ALLPROJECTS, Guid.Empty, out var projectEnum) < 0)
            return;

        var projects = new IVsHierarchy[1];
        var reload = new List<Guid>();
        while (projectEnum.Next(1, projects, out var fetched) == 0 && fetched == 1)
        {
            var hierarchy = projects[0];
            if (solution.GetUniqueNameOfProject(hierarchy, out var uniqueName) < 0 ||
                !uniqueName.EndsWith(".ctproj", StringComparison.OrdinalIgnoreCase))
                continue;
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
    }

    internal CTildeOptionsPage Options => (CTildeOptionsPage)GetDialogPage(typeof(CTildeOptionsPage));
}
