using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Reflection;
using CTilde.VisualStudio.Core;
using EnvDTE;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Process = System.Diagnostics.Process;

namespace CTilde.VisualStudio;

[ContentType("ctilde")]
[Export(typeof(ILanguageClient))]
[RunOnContext(RunningContext.RunOnHost)]
public sealed class CTildeLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
{
    private const string ServerVersion = "0.14.0";
    private Process? _process;
    private JsonRpc? _rpc;
    private SolutionEvents? _solutionEvents;
    private SelectionEvents? _selectionEvents;
    private HashSet<string> _workspaceFolders = new(StringComparer.OrdinalIgnoreCase);
    private int _workspaceSyncVersion;
    private string? _lastProjectState;
    private readonly StandardLibraryMiddleLayer _middleLayer;

    public CTildeLanguageClient()
    {
        Instance = this;
        _middleLayer = new StandardLibraryMiddleLayer(() => _rpc);
    }

    internal static CTildeLanguageClient? Instance { get; private set; }
    public string Name => "C~ Language Server";
    public IEnumerable<string> ConfigurationSections => new[] { "ctilde" };
    public object? InitializationOptions => new { ctilde = new { client = "visualstudio", version = ServerVersion } };
    public IEnumerable<string> FilesToWatch => new[] { "**/*.ct", "**/ctilde*.json", "**/*.bindings.json", "**/*.ctproj" };
    public object MiddleLayer => _middleLayer;
    public object? CustomMessageTarget => null;
    public bool ShowNotificationOnInitializeFailed => true;

    public event AsyncEventHandler<EventArgs>? StartAsync;
    public event AsyncEventHandler<EventArgs>? StopAsync;

    public async Task OnLoadedAsync()
    {
        if (StartAsync is not null)
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var options = CTildeToolPaths.Current;
        var dotnet = string.IsNullOrWhiteSpace(options.DotNetPath) ? "dotnet" : options.DotNetPath;
        var bundledServer = Path.Combine(extensionDirectory, "Tools", "LanguageServer", "CTilde.LanguageServer.dll");
        var server = string.IsNullOrWhiteSpace(options.LanguageServerPath) ? bundledServer : Path.GetFullPath(options.LanguageServerPath);
        if (!File.Exists(server))
            throw new FileNotFoundException("The C~ language server was not found.", server);

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = CommandContracts.QuoteWindowsArgument(server),
            WorkingDirectory = Path.GetDirectoryName(server)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                CTildeOutput.WriteLine("[language server] " + eventArgs.Data);
        };
        if (!_process.Start())
            throw new InvalidOperationException("The C~ language server process did not start.");
        _process.BeginErrorReadLine();
        CTildeOutput.WriteLine($"Started C~ language server {ServerVersion}: {dotnet} {startInfo.Arguments}");
        token.Register(() =>
        {
            try { if (_process is { HasExited: false }) _process.Kill(); }
            catch (InvalidOperationException) { }
        });
        return new Connection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
    }

    public async Task OnServerInitializedAsync()
    {
        CTildeOutput.WriteLine("C~ language server initialized.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Package.GetGlobalService(typeof(DTE)) is DTE dte)
        {
            _solutionEvents = dte.Events.SolutionEvents;
            _solutionEvents.Opened += QueueWorkspaceSync;
            _solutionEvents.AfterClosing += QueueWorkspaceSync;
            _solutionEvents.ProjectAdded += QueueWorkspaceSync;
            _solutionEvents.ProjectRemoved += QueueWorkspaceSync;
            _solutionEvents.ProjectRenamed += QueueWorkspaceSync;
            _selectionEvents = dte.Events.SelectionEvents;
            _selectionEvents.OnChange += QueueWorkspaceSync;
        }
        await SyncWorkspaceFoldersAsync();
    }

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        var details = initializationState.InitializationException?.Message;
        var message = "C~ language support could not start. Install the .NET 10 runtime from https://dotnet.microsoft.com/download/dotnet/10.0 and verify the paths under Tools > Options > C~.";
        if (!string.IsNullOrWhiteSpace(details))
            message += Environment.NewLine + details;
        CTildeOutput.WriteLine(message);
        return Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext { FailureMessage = message });
    }

    public Task AttachForCustomMessageAsync(JsonRpc rpc)
    {
        _rpc = rpc;
        return Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "EnvDTE solution events require void callbacks.")]
    private async void QueueWorkspaceSync()
    {
        var version = Interlocked.Increment(ref _workspaceSyncVersion);
        await Task.Delay(100);
        if (version != Volatile.Read(ref _workspaceSyncVersion))
            return;
        await SyncWorkspaceFoldersAsync();
    }
    private void QueueWorkspaceSync(Project _) => QueueWorkspaceSync();
    private void QueueWorkspaceSync(Project _, string __) => QueueWorkspaceSync();

    private async Task SyncWorkspaceFoldersAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contexts = new List<ProjectContext>();
        DTE? dte = null;
        if (Package.GetGlobalService(typeof(DTE)) is DTE currentDte && currentDte.Solution.IsOpen)
        {
            dte = currentDte;
            foreach (var project in EnumerateProjects(currentDte.Solution.Projects))
            {
                var projectPath = project.FullName;
                if (!string.IsNullOrWhiteSpace(projectPath) && Path.GetExtension(projectPath).Equals(".ctproj", StringComparison.OrdinalIgnoreCase))
                {
                    projectPath = Path.GetFullPath(projectPath);
                    try
                    {
                        var contract = CTildeProjectContract.Load(projectPath);
                        current.Add(Path.GetDirectoryName(projectPath)!);
                        contexts.Add(new ProjectContext(projectPath, contract.ManifestPath));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        CTildeOutput.WriteLine($"C~ project context skipped: {exception.Message}");
                    }
                }
            }
        }

        var added = current.Except(_workspaceFolders, StringComparer.OrdinalIgnoreCase).Select(ToWorkspaceFolder).ToArray();
        var removed = _workspaceFolders.Except(current, StringComparer.OrdinalIgnoreCase).Select(ToWorkspaceFolder).ToArray();
        _workspaceFolders = current;
        var rpc = _rpc;
        if (rpc is null)
            return;
        if (added.Length != 0 || removed.Length != 0)
        {
            await rpc.NotifyWithParameterObjectAsync("workspace/didChangeWorkspaceFolders", new { @event = new { added, removed } });
            CTildeOutput.WriteLine($"C~ workspace folders updated: +{added.Length}, -{removed.Length}.");
        }
        contexts = contexts.OrderBy(context => context.ProjectPath, StringComparer.OrdinalIgnoreCase).ToList();
        var activeManifest = dte is null ? null : FindActiveManifest(dte, contexts);
        var state = string.Join("\n", contexts.Select(context => context.ProjectPath + "|" + context.ManifestPath)) + "\nactive=" + activeManifest;
        if (state.Equals(_lastProjectState, StringComparison.OrdinalIgnoreCase))
            return;
        _lastProjectState = state;
        await rpc.NotifyWithParameterObjectAsync("ctilde/didChangeProjects", new
        {
            projects = contexts.Select(context => new
            {
                projectUri = new Uri(context.ProjectPath).AbsoluteUri,
                manifestUri = new Uri(context.ManifestPath).AbsoluteUri,
            }).ToArray(),
            activeManifestUri = activeManifest is null ? null : new Uri(activeManifest).AbsoluteUri,
        });
    }

    private static object ToWorkspaceFolder(string path) => new
    {
        uri = new Uri(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri,
        name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
    };

    private static string? FindActiveManifest(DTE dte, IReadOnlyList<ProjectContext> contexts)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? selectedProject = null;
        foreach (SelectedItem selected in dte.SelectedItems)
        {
            selectedProject = CTildeProjectPath(selected.Project) ?? CTildeProjectPath(selected.ProjectItem?.ContainingProject);
            if (selectedProject is not null)
                break;
        }
        var resolved = ProjectSelection.Resolve(selectedProject, CTildeProjectPath(dte.ActiveDocument?.ProjectItem?.ContainingProject),
            dte.ActiveDocument?.FullName, contexts.Select(context => context.ProjectPath).ToArray());
        return contexts.FirstOrDefault(context => PathEquals(context.ProjectPath, resolved))?.ManifestPath;
    }

    private static string? CTildeProjectPath(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null || string.IsNullOrWhiteSpace(project.FullName) || !Path.GetExtension(project.FullName).Equals(".ctproj", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetFullPath(project.FullName);
    }

    private static bool PathEquals(string left, string? right) =>
        right is not null && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010:Invoke single-threaded types on Main thread", Justification = "The caller enumerates this iterator on the verified UI thread.")]
    private static IEnumerable<Project> EnumerateProjects(Projects projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project project in projects)
        {
            if (project.Kind == EnvDTE.Constants.vsProjectKindSolutionItems && project.ProjectItems is not null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                    if (item.SubProject is not null)
                        foreach (var nested in EnumerateProject(item.SubProject))
                            yield return nested;
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
        if (project.Kind != EnvDTE.Constants.vsProjectKindSolutionItems || project.ProjectItems is null)
            yield break;
        foreach (ProjectItem item in project.ProjectItems)
            if (item.SubProject is not null)
                foreach (var nested in EnumerateProject(item.SubProject))
                    yield return nested;
    }

    internal async Task RestartAsync()
    {
        if (StopAsync is not null)
            await StopAsync.InvokeAsync(this, EventArgs.Empty);
        if (StartAsync is not null)
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }

    private sealed class ProjectContext
    {
        internal ProjectContext(string projectPath, string manifestPath)
        {
            ProjectPath = projectPath;
            ManifestPath = manifestPath;
        }

        internal string ProjectPath { get; }
        internal string ManifestPath { get; }
    }

    private sealed class StandardLibraryMiddleLayer : ILanguageClientMiddleLayer2<JToken>
    {
        private static readonly HashSet<string> MethodsWithLocations = new(StringComparer.Ordinal)
        {
            "textDocument/definition", "textDocument/references", "workspace/symbol", "textDocument/documentSymbol", "textDocument/implementation", "textDocument/typeDefinition",
        };
        private readonly Func<JsonRpc?> _rpc;

        public StandardLibraryMiddleLayer(Func<JsonRpc?> rpc) => _rpc = rpc;
        public bool CanHandle(string methodName) => CTildeToolPaths.Current.TraceProtocol || MethodsWithLocations.Contains(methodName);

        public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
        {
            Trace("request", methodName, methodParam);
            var result = await sendRequest(methodParam);
            if (result is not null && MethodsWithLocations.Contains(methodName))
                await RewriteAsync(result);
            Trace("response", methodName, result);
            return result;
        }

        public async Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
        {
            Trace("notification", methodName, methodParam);
            if (MethodsWithLocations.Contains(methodName))
                await RewriteAsync(methodParam);
            await sendNotification(methodParam);
        }

        private static void Trace(string direction, string methodName, JToken? payload)
        {
            if (CTildeToolPaths.Current.TraceProtocol)
                CTildeOutput.WriteLine($"[LSP {direction}] {methodName}: {payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"}");
        }

        private async Task RewriteAsync(JToken token)
        {
            var descendants = token is JContainer container ? container.DescendantsAndSelf() : new[] { token };
            var values = descendants.OfType<JValue>()
                .Where(value => value.Type == JTokenType.String && value.Value<string>()?.StartsWith(StandardLibraryUri.Scheme, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            foreach (var value in values)
            {
                var uri = value.Value<string>()!;
                var path = await MaterializeAsync(uri);
                if (path is not null)
                    value.Value = StandardLibraryUri.FileUri(path).AbsoluteUri;
            }
        }

        private async Task<string?> MaterializeAsync(string uri)
        {
            var rpc = _rpc();
            if (rpc is null)
                return null;
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CTilde", "VisualStudio", "StandardLibrary");
            var path = StandardLibraryUri.CachePath(cacheRoot, ServerVersion, uri);
            if (File.Exists(path))
                return path;
            var text = await rpc.InvokeWithParameterObjectAsync<string?>("ctilde/standardLibraryText", new { uri }, CancellationToken.None);
            if (text is null)
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            return path;
        }
    }
}
