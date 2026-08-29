using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

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
        await CTildeCommands.InitializeAsync(this, cancellationToken);
    }

    internal CTildeOptionsPage Options => (CTildeOptionsPage)GetDialogPage(typeof(CTildeOptionsPage));
}
