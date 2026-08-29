using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using CTilde.VisualStudio.Core;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Process = System.Diagnostics.Process;

namespace CTilde.VisualStudio;

internal sealed class CTildeCommands : IDisposable
{
    private static readonly Guid CommandSet = new("235dfa97-a3cf-4627-975b-851e22e0ca63");
    private static readonly Regex SolutionProjectPattern = new(
        "^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.ctproj)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly CTildePackage _package;
    private readonly ErrorListProvider _errors;
    private CancellationTokenSource? _operationCancellation;

    private CTildeCommands(CTildePackage package)
    {
        _package = package;
        _errors = new ErrorListProvider(package) { ProviderName = "C~", ProviderGuid = CTildeOutput.PaneGuid };
    }

    public static async Task InitializeAsync(CTildePackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var service = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
        if (service is null)
            return;
        var instance = new CTildeCommands(package);
        instance.Add(service, 0x0100, (_, _) => instance.ExecuteProjectCommand(CTildeCommandKind.Check));
        instance.Add(service, 0x0101, (_, _) => instance.ExecuteProjectCommand(CTildeCommandKind.Build));
        instance.Add(service, 0x0102, (_, _) => instance.ExecuteProjectCommand(CTildeCommandKind.Clean));
        instance.Add(service, 0x0103, (_, _) => instance.ExecuteRebuild());
        instance.Add(service, 0x0104, (_, _) => instance.ExecuteProjectCommand(CTildeCommandKind.Run));
        instance.Add(service, 0x0105, (_, _) => instance.Cancel());
        instance.Add(service, 0x0106, (_, _) => instance.RestartLanguageServer());
        instance.Add(service, 0x0107, (_, _) => CTildeOutput.Show());
        instance.Add(service, 0x0108, (_, _) => instance.CreateProjectFromManifest());
        package.DisposalToken.Register(instance.Dispose);
    }

    private void Add(IMenuCommandService service, int id, EventHandler handler)
    {
        var command = new OleMenuCommand(handler, new CommandID(CommandSet, id));
        command.BeforeQueryStatus += QueryStatus;
        service.AddCommand(command);
    }

    private void QueryStatus(object sender, EventArgs eventArgs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var command = (OleMenuCommand)sender;
        var id = command.CommandID.ID;
        var projectPath = FindSelectedProject();
        var hasProject = projectPath is not null;
        command.Enabled = id switch
        {
            >= 0x0100 and <= 0x0103 => CommandEnablement.ProjectCommandsEnabled(hasProject, _operationCancellation is not null),
            0x0104 => CommandEnablement.RunProjectEnabled(hasProject, _operationCancellation is not null,
                hasProject && CTildeRunManager.SupportsRun(projectPath!)),
            0x0105 => _operationCancellation is not null,
            0x0106 => CommandEnablement.RestartLanguageServerEnabled(CTildeLanguageClient.Instance is not null),
            _ => true,
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Visual Studio command callbacks are EventHandler delegates.")]
    private async void ExecuteProjectCommand(CTildeCommandKind kind)
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var contract = LoadSelectedProject();
        if (contract is null)
            return;
        if (kind == CTildeCommandKind.Run)
        {
            try { CTildeRunManager.Start(contract); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowError(exception.Message);
            }
            return;
        }
        await RunOperationAsync(kind, contract);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Visual Studio command callbacks are EventHandler delegates.")]
    private async void ExecuteRebuild()
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var contract = LoadSelectedProject();
        if (contract is not null && await RunOperationAsync(CTildeCommandKind.Clean, contract) == 0)
            await RunOperationAsync(CTildeCommandKind.Build, contract);
    }

    private CTildeProjectContract? LoadSelectedProject()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projectPath = FindSelectedProject();
        if (projectPath is null)
            return null;
        try { return CTildeProjectContract.Load(projectPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError(exception.Message);
            return null;
        }
    }

    private async Task<int> RunOperationAsync(CTildeCommandKind kind, CTildeProjectContract contract)
    {
        if (_operationCancellation is not null)
            return 1;
        _operationCancellation = new CancellationTokenSource();
        _errors.Tasks.Clear();
        CTildeOutput.WriteLine($"{kind} {contract.ManifestPath}");
        if (kind is CTildeCommandKind.Build or CTildeCommandKind.Check)
            CTildeOutput.WriteLine($"Manifest configuration: {ReadManifestConfiguration(contract.ManifestPath)} (the Visual Studio solution configuration does not override it).");
        try
        {
            var options = CTildeToolPaths.Current;
            var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var compiler = string.IsNullOrWhiteSpace(options.CompilerPath) ? Path.Combine(extensionDirectory, "Tools", "Compiler", "ctilde.dll") : Path.GetFullPath(options.CompilerPath);
            var dotnet = string.IsNullOrWhiteSpace(options.DotNetPath) ? "dotnet" : options.DotNetPath;
            if (!File.Exists(compiler))
                throw new FileNotFoundException("The C~ compiler was not found.", compiler);
            var startInfo = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = CommandContracts.JoinWindowsArguments(CommandContracts.Arguments(kind, compiler, contract.ManifestPath)),
                WorkingDirectory = Path.GetDirectoryName(contract.ManifestPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => ReceiveOutput(args.Data);
            process.ErrorDataReceived += (_, args) => ReceiveOutput(args.Data);
            if (!process.Start())
                throw new InvalidOperationException("The C~ command did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using (_operationCancellation.Token.Register(() => KillProcessTree(process)))
                await Task.Run(() => process.WaitForExit(), _operationCancellation.Token);
            CTildeOutput.WriteLine($"{kind} finished with exit code {process.ExitCode}.");
            if (!CommandOutcomes.Succeeded(process.ExitCode))
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                CTildeOutput.Show();
            }
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            CTildeOutput.WriteLine($"{kind} canceled.");
            return CommandOutcomes.CanceledExitCode;
        }
        catch (Win32Exception exception)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowError(CommandOutcomes.MissingDotNetMessage(exception.Message));
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowError(exception.Message);
            return 1;
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void ReceiveOutput(string? line)
    {
        if (line is null)
            return;
        CTildeOutput.WriteLine(line);
        if (!DiagnosticParser.TryParse(line, out var diagnostic))
            return;
        ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var task = new ErrorTask
            {
                Category = TaskCategory.BuildCompile,
                ErrorCategory = diagnostic!.Severity == "error" ? TaskErrorCategory.Error : diagnostic.Severity == "warning" ? TaskErrorCategory.Warning : TaskErrorCategory.Message,
                Text = $"{diagnostic.Code}: {diagnostic.Message}",
                Document = diagnostic.File,
                Line = Math.Max(0, diagnostic.Line - 1),
                Column = Math.Max(0, diagnostic.Column - 1),
            };
            task.Navigate += (_, _) => _errors.Navigate(task, new Guid(EnvDTE.Constants.vsViewKindCode));
            _errors.Tasks.Add(task);
        });
    }

    private void Cancel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _operationCancellation?.Cancel();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Visual Studio command callbacks are EventHandler delegates.")]
    private async void RestartLanguageServer()
    {
        if (CTildeLanguageClient.Instance is not null)
            await CTildeLanguageClient.Instance.RestartAsync();
    }

    private void CreateProjectFromManifest()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "C~ manifest (ctilde.json)|ctilde.json|JSON files (*.json)|*.json", Title = "Select a C~ manifest", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;
        var directory = Path.GetDirectoryName(dialog.FileName)!;
        var projectPath = Path.Combine(directory, new DirectoryInfo(directory).Name + ".ctproj");
        try
        {
            CTildeProjectContract.Create(projectPath, dialog.FileName, Guid.NewGuid());
            var result = VsShellUtilities.ShowMessageBox(_package, "The C~ project was created. Add it to the current solution?", "C~ for Visual Studio", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            if (result == (int)VSConstants.MessageBoxResult.IDYES && Package.GetGlobalService(typeof(DTE)) is DTE dte && dte.Solution.IsOpen)
                dte.Solution.AddFromFile(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(exception.Message); }
    }

    private static string ReadManifestConfiguration(string manifestPath)
    {
        try
        {
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            return manifest["kind"]?.Value<string>() == "standard-library"
                ? "standard-library validation matrix"
                : manifest["build"]?["configuration"]?.Value<string>() ?? "debug";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException) { return "unresolved"; }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {process.Id.ToString(CultureInfo.InvariantCulture)} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
    }

    private static string? FindSelectedProject()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE dte || !dte.Solution.IsOpen)
            return null;

        var projects = EnumerateProjects(dte.Solution.Projects)
            .Select(CTildeProjectPath)
            .Where(path => path is not null)
            .Cast<string>()
            .Concat(SolutionProjectPaths(dte.Solution.FullName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? selectedProject = null;
        foreach (SelectedItem selected in dte.SelectedItems)
        {
            selectedProject = CTildeProjectPath(selected.Project) ?? CTildeProjectPath(selected.ProjectItem?.ContainingProject);
            if (selectedProject is not null)
                break;
        }
        return ProjectSelection.Resolve(selectedProject, CTildeProjectPath(dte.ActiveDocument?.ProjectItem?.ContainingProject),
            dte.ActiveDocument?.FullName, projects);
    }

    private static string? CTildeProjectPath(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
            return null;
        string path;
        try { path = project.FullName; }
        catch (Exception exception) when (exception is NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        return !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".ctproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : null;
    }

    private static IEnumerable<string> SolutionProjectPaths(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath))
            yield break;
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        string[] lines;
        try { lines = File.ReadAllLines(solutionPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var line in lines)
        {
            var match = SolutionProjectPattern.Match(line);
            if (!match.Success)
                continue;
            var path = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            path = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(directory, path));
            if (File.Exists(path))
                yield return path;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010:Invoke single-threaded types on Main thread", Justification = "The caller and method verify the UI thread; iterator analysis does not preserve that fact.")]
    private static IEnumerable<Project> EnumerateProjects(Projects projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project project in projects)
        {
            if (project.Kind == EnvDTE.Constants.vsProjectKindSolutionItems && project.ProjectItems is not null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject is not null)
                    {
                        foreach (var nested in EnumerateProject(item.SubProject))
                            yield return nested;
                    }
                }
            }
            else
            {
                yield return project;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010:Invoke single-threaded types on Main thread", Justification = "The caller enumerates this iterator on the verified UI thread.")]
    private static IEnumerable<Project> EnumerateProject(Project project)
    {
        yield return project;
        if (project.Kind != EnvDTE.Constants.vsProjectKindSolutionItems || project.ProjectItems is null) yield break;
        foreach (ProjectItem item in project.ProjectItems)
            if (item.SubProject is not null)
                foreach (var nested in EnumerateProject(item.SubProject)) yield return nested;
    }

    private void ShowError(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CTildeOutput.WriteLine("Error: " + message);
        VsShellUtilities.ShowMessageBox(_package, message, "C~ for Visual Studio", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _errors.Dispose();
    }
}
