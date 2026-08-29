using System.Collections.Immutable;
using CTilde;

namespace CTilde.LanguageServer;

internal sealed class WorkspaceState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedProject> _projects = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _workspaceRoots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, string> _explicitProjects = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private string? _activeManifest;
    private long _revision;

    public event Func<Task>? AnalysisChanged;

    public void Initialize(string? rootUri, WorkspaceFolder[]? folders)
    {
        lock (_gate)
        {
            _workspaceRoots.Clear();
            if (folders is { Length: > 0 })
            {
                foreach (var folder in folders)
                    _workspaceRoots.Add(UriHelpers.ToPath(folder.Uri));
            }
            else if (!string.IsNullOrWhiteSpace(rootUri))
                _workspaceRoots.Add(UriHelpers.ToPath(rootUri));
            _projects.Clear();
            _explicitProjects.Clear();
            _activeManifest = null;
            _revision++;
        }
    }

    public void ChangeFolders(WorkspaceFoldersChangeEvent change)
    {
        lock (_gate)
        {
            foreach (var removed in change.Removed)
                _workspaceRoots.Remove(UriHelpers.ToPath(removed.Uri));
            foreach (var added in change.Added)
                _workspaceRoots.Add(UriHelpers.ToPath(added.Uri));
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void Open(TextDocumentItem document)
    {
        lock (_gate)
        {
            _documents[document.Uri] = new OpenDocument(document.Uri, UriHelpers.ToPath(document.Uri), document.Version, document.Text);
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void Change(VersionedTextDocumentIdentifier identifier, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(identifier.Uri, out var document) || identifier.Version <= document.Version)
                return;
            var text = document.Text;
            foreach (var change in changes)
            {
                if (change.Range is null)
                {
                    text = change.Text;
                    continue;
                }
                var source = SourceText.From(text, document.Path);
                var start = source.GetPosition(change.Range.Start.Line, change.Range.Start.Character);
                var end = source.GetPosition(change.Range.End.Line, change.Range.End.Character);
                text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
            }
            _documents[identifier.Uri] = document with { Version = identifier.Version, Text = text };
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void Close(string uri)
    {
        lock (_gate)
        {
            _documents.Remove(uri);
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void FilesChanged()
    {
        lock (_gate)
        {
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void SetProjectContexts(CTildeProjectContextsParams parameters)
    {
        lock (_gate)
        {
            _explicitProjects.Clear();
            foreach (var project in parameters.Projects)
            {
                var projectPath = UriHelpers.ToPath(project.ProjectUri);
                var manifestPath = UriHelpers.ToPath(project.ManifestUri);
                _explicitProjects[projectPath] = manifestPath;
            }
            _activeManifest = string.IsNullOrWhiteSpace(parameters.ActiveManifestUri) ? null : UriHelpers.ToPath(parameters.ActiveManifestUri);
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public void SetActiveProject(string? manifestUri)
    {
        lock (_gate)
        {
            var next = string.IsNullOrWhiteSpace(manifestUri) ? null : UriHelpers.ToPath(manifestUri);
            if (PathComparer.Equals(_activeManifest, next))
                return;
            _activeManifest = next;
            _projects.Clear();
            _revision++;
        }
        SignalChanged();
    }

    public ProjectSnapshot GetProject(string uri)
    {
        lock (_gate)
        {
            var path = UriHelpers.ToPath(uri);
            var manifest = ResolveManifest(path);
            CTildeProject? project = null;
            if (manifest is not null)
            {
                try { project = CTildeProjectFile.Load(manifest); }
                catch (CTildeProjectException exception) { return CreateStandalone(path, CompilationTarget.Hosted, exception.Message); }
            }
            var included = project is not null && project.SourceFiles.Contains(Path.GetFullPath(path), PathComparer);
            var key = included ? project!.ManifestPath : $"standalone:{Path.GetFullPath(path)}:{project?.Configuration.Target ?? CompilationTarget.Hosted}";
            if (_projects.TryGetValue(key, out var cached))
                return cached.Snapshot;
            var sourceFiles = included ? project!.SourceFiles : [Path.GetFullPath(path)];
            var target = project?.Configuration.Target ?? CompilationTarget.Hosted;
            var architecture = project?.Configuration.Architecture ?? CompilationArchitecture.Auto;
            var snapshot = project?.Configuration.Kind == CTildeProjectKind.StandardLibrary
                ? CreateStandardLibrarySnapshot(project, path, key)
                : CreateSnapshot(key, sourceFiles, target, architecture, project?.Configuration.Environment ?? TargetEnvironment.Native, project?.Configuration.NoRecursion ?? false,
                    project?.Configuration.PanicPolicy ?? EspIdfPanicPolicy.Abort, project?.RootDirectory, project is null ? null : BindingProjectError(project), BindingPaths(project));
            _projects[key] = new CachedProject(snapshot);
            return snapshot;
        }
    }

    public ImmutableArray<ProjectSnapshot> GetWorkspaceProjects()
    {
        lock (_gate)
        {
            foreach (var document in _documents.Values)
                _ = GetProject(document.Uri);
            foreach (var manifest in _explicitProjects.Values.Distinct(PathComparer))
                AddWorkspaceProject(manifest);
            foreach (var root in _workspaceRoots.Where(Directory.Exists))
            {
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(root, "ctilde.json", SearchOption.AllDirectories).ToArray(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var manifest in manifests.Where(path => !IsIgnored(path)))
                {
                    AddWorkspaceProject(manifest);
                }
            }
            return [.. _projects.Values.Select(value => value.Snapshot).DistinctBy(value => value.Key)];
        }
    }

    private void AddWorkspaceProject(string manifest)
    {
        CTildeProject project;
        try { project = CTildeProjectFile.Load(manifest); }
        catch (CTildeProjectException) { return; }
        if (_projects.ContainsKey(project.ManifestPath))
            return;
        var snapshot = project.Configuration.Kind == CTildeProjectKind.StandardLibrary
            ? CreateStandardLibrarySnapshot(project, project.SourceFiles[0], project.ManifestPath)
            : CreateSnapshot(project.ManifestPath, project.SourceFiles, project.Configuration.Target, project.Configuration.Architecture, project.Configuration.Environment,
                project.Configuration.NoRecursion, project.Configuration.PanicPolicy, project.RootDirectory, BindingProjectError(project), BindingPaths(project));
        _projects[project.ManifestPath] = new CachedProject(snapshot);
    }

    private string? ResolveManifest(string path)
    {
        if (_activeManifest is not null && ManifestContains(_activeManifest, path))
            return _activeManifest;
        var candidates = _explicitProjects.Values.Distinct(PathComparer).Where(manifest => ManifestContains(manifest, path)).ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        if (candidates.Length > 1)
            return candidates.FirstOrDefault(candidate => Path.GetFileName(candidate).Equals("ctilde.json", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
        return CTildeProjectFile.FindNearest(path);
    }

    private static bool ManifestContains(string manifest, string path)
    {
        try { return CTildeProjectFile.Load(manifest).SourceFiles.Contains(Path.GetFullPath(path), PathComparer); }
        catch (CTildeProjectException) { return false; }
    }

    private ProjectSnapshot CreateStandardLibrarySnapshot(CTildeProject project, string documentPath, string key)
    {
        var overrides = _documents.Values.ToDictionary(document => Path.GetFullPath(document.Path), document => document.Text, PathComparer);
        try
        {
            var service = LanguageServiceSnapshot.CreateStandardLibraryProject(project.RootDirectory, documentPath, overrides);
            return new ProjectSnapshot(key, project.SourceFiles, service.Options.Target, service.Options.Architecture, false,
                EspIdfPanicPolicy.Abort, service, null, _revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return CreateStandalone(documentPath, CompilationTarget.Hosted, exception.Message);
        }
    }

    public ImmutableArray<OpenDocument> OpenDocuments
    {
        get { lock (_gate) return [.. _documents.Values]; }
    }

    public string? GetStandardLibraryText(string path)
    {
        lock (_gate)
        {
            foreach (var project in _projects.Values)
                if (project.Snapshot.LanguageService.TryGetSourceText(path, out var text))
                    return text.Text;
            var target = path.Contains("Esp/Idf", StringComparison.Ordinal) ? CompilationTarget.EspIdf : CompilationTarget.Hosted;
            var temporary = LanguageServiceSnapshot.Create([SyntaxTree.ParseText("public static class __Editor { [EntryPoint] public static void Main() { } }", "<editor>")], new CompilationOptions(target));
            return temporary.TryGetSourceText(path, out var source) ? source.Text : null;
        }
    }

    private ProjectSnapshot CreateStandalone(string path, CompilationTarget target, string error) => CreateSnapshot($"standalone:{path}:{target}", [path], target,
        CompilationArchitecture.Auto, TargetEnvironment.Native, false, EspIdfPanicPolicy.Abort, Path.GetDirectoryName(Path.GetFullPath(path)), error, null);

    private static IReadOnlySet<string>? BindingPaths(CTildeProject? project) => project?.Configuration.BindingManifests
        .Select(manifest => manifest.DeclarationsPath).ToHashSet(PathComparer);

    private static string? BindingProjectError(CTildeProject project)
    {
        foreach (var manifest in project.Configuration.BindingManifests)
        {
            if (!File.Exists(manifest.DeclarationsPath) || !File.Exists(manifest.AdapterSourcePath))
                return $"ESP-IDF binding output for '{Path.GetFileName(manifest.ManifestPath)}' is missing. Run C~: Generate ESP-IDF Bindings.";
            try
            {
                var firstLine = File.ReadLines(manifest.DeclarationsPath).FirstOrDefault() ?? string.Empty;
                if (!firstLine.Contains($"manifest-fingerprint=\"{manifest.ManifestFingerprint}\"", StringComparison.Ordinal))
                    return $"ESP-IDF binding output for '{Path.GetFileName(manifest.ManifestPath)}' is stale. Run C~: Generate ESP-IDF Bindings.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"Could not inspect ESP-IDF binding output '{manifest.DeclarationsPath}': {exception.Message}";
            }
        }
        return null;
    }

    private ProjectSnapshot CreateSnapshot(string key, ImmutableArray<string> sourceFiles, CompilationTarget target, CompilationArchitecture architecture, TargetEnvironment environment, bool noRecursion,
        EspIdfPanicPolicy panicPolicy, string? sourceIdentityRoot, string? projectError, IReadOnlySet<string>? bindingPaths)
    {
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
        foreach (var path in sourceFiles)
        {
            var open = _documents.Values.FirstOrDefault(document => PathComparer.Equals(document.Path, path));
            try
            {
                var text = open is null ? SourceText.FromFile(path) : SourceText.From(open.Text, path);
                trees.Add(bindingPaths?.Contains(path) == true ? SyntaxTree.ParseEspIdfBinding(text) : SyntaxTree.Parse(text));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                projectError ??= exception.Message;
                trees.Add(SyntaxTree.ParseText(open?.Text ?? string.Empty, path));
            }
        }
        var service = LanguageServiceSnapshot.Create(trees, new CompilationOptions(target, Architecture: architecture, NoRecursion: noRecursion,
            SourceIdentityRoot: sourceIdentityRoot, PanicPolicy: panicPolicy, Environment: environment));
        return new ProjectSnapshot(key, sourceFiles, target, architecture, noRecursion, panicPolicy, service, projectError, _revision);
    }

    private void SignalChanged()
    {
        var handler = AnalysisChanged;
        if (handler is not null)
            _ = handler();
    }

    private static bool IsIgnored(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/build/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record CachedProject(ProjectSnapshot Snapshot);
}

internal sealed record OpenDocument(string Uri, string Path, int Version, string Text);
internal sealed record ProjectSnapshot(string Key, ImmutableArray<string> SourceFiles, CompilationTarget Target, CompilationArchitecture Architecture, bool NoRecursion,
    EspIdfPanicPolicy PanicPolicy, LanguageServiceSnapshot LanguageService, string? ProjectError, long Revision);
