using System.Collections.Immutable;
using CTilde;

namespace CTilde.LanguageServer;

internal sealed class WorkspaceState
{
    private readonly object _gate = new();
    private readonly object _syntaxGate = new();
    private readonly Dictionary<string, CachedSyntax> _syntaxTrees = new(PathComparer);
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedProject> _projects = new(PathComparer);
    private readonly Dictionary<string, CTildeProject> _projectDefinitions = new(PathComparer);
    private readonly HashSet<string> _workspaceRoots = new(PathComparer);
    private readonly Dictionary<string, string> _explicitProjects = new(PathComparer);
    private ImmutableArray<string> _fallbackManifests = [];
    private bool _fallbackManifestsDirty = true;
    private string? _activeManifest;
    private long _revision;

    public event Func<Task>? AnalysisChanged;

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

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
            ResetProjectState(clearDefinitions: true);
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
            ResetProjectState(clearDefinitions: true);
            _revision++;
        }
        SignalChanged();
    }

    public void Open(TextDocumentItem document)
    {
        lock (_gate)
        {
            var path = UriHelpers.ToPath(document.Uri);
            _documents[document.Uri] = new OpenDocument(document.Uri, path, document.Version, document.Text);
            InvalidatePath(path);
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
            InvalidatePath(document.Path);
            _revision++;
        }
        SignalChanged();
    }

    public void Close(string uri)
    {
        lock (_gate)
        {
            if (!_documents.Remove(uri, out var document))
                return;
            InvalidatePath(document.Path);
            _revision++;
        }
        SignalChanged();
    }

    public void Save(string uri)
    {
        // The open buffer already owns the current text and snapshot revision. The matching watched-file
        // notification is ignored as well, so saving cannot trigger a duplicate semantic rebuild.
    }

    public void FilesChanged(IReadOnlyList<FileEvent> changes)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var change in changes)
            {
                var path = UriHelpers.ToPath(change.Uri);
                if (IsProjectMetadata(path))
                {
                    ResetProjectState(clearDefinitions: true);
                    changed = true;
                    continue;
                }
                if (!Path.GetExtension(path).Equals(".ct", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (change.Type is 1 or 3)
                {
                    ResetProjectState(clearDefinitions: true, preserveSyntax: true);
                    if (change.Type == 3)
                        lock (_syntaxGate) _syntaxTrees.Remove(Path.GetFullPath(path));
                    changed = true;
                    continue;
                }
                if (_documents.Values.Any(document => PathComparer.Equals(document.Path, path)))
                    continue;
                InvalidatePath(path);
                changed = true;
            }
            if (changed)
                _revision++;
        }
        if (changed)
            SignalChanged();
    }

    public void SetProjectContexts(CTildeProjectContextsParams parameters)
    {
        lock (_gate)
        {
            var next = parameters.Projects.ToDictionary(
                project => UriHelpers.ToPath(project.ProjectUri),
                project => UriHelpers.ToPath(project.ManifestUri),
                PathComparer);
            var contextsChanged = next.Count != _explicitProjects.Count || next.Any(pair =>
                !_explicitProjects.TryGetValue(pair.Key, out var manifest) || !PathComparer.Equals(manifest, pair.Value));
            var active = string.IsNullOrWhiteSpace(parameters.ActiveManifestUri) ? null : UriHelpers.ToPath(parameters.ActiveManifestUri);
            var activeChanged = !PathComparer.Equals(_activeManifest, active);
            if (!contextsChanged && !activeChanged)
                return;
            if (contextsChanged)
            {
                _explicitProjects.Clear();
                foreach (var pair in next)
                    _explicitProjects[pair.Key] = pair.Value;
                ResetProjectState(clearDefinitions: true);
            }
            _activeManifest = active;
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
            _revision++;
        }
        SignalChanged();
    }

    public ProjectSnapshot GetProject(string uri)
    {
        CachedProject cached;
        lock (_gate)
        {
            var path = UriHelpers.ToPath(uri);
            var manifest = ResolveManifest(path);
            CTildeProject? project = null;
            string? projectError = null;
            if (manifest is not null)
            {
                try { project = LoadProject(manifest); }
                catch (CTildeProjectException exception) { projectError = exception.Message; }
            }
            var included = project is not null && project.SourceFiles.Contains(Path.GetFullPath(path), PathComparer);
            var target = project?.Configuration.Target ?? CompilationTarget.Hosted;
            var key = included ? project!.ManifestPath : $"standalone:{Path.GetFullPath(path)}:{target}";
            cached = GetOrCreateProject(key, included ? project : null, path, projectError);
        }
        return cached.Snapshot.Value;
    }

    public bool IsCurrent(OpenDocument document, ProjectSnapshot project)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(document.Uri, out var currentDocument) || currentDocument.Version != document.Version)
                return false;
            var path = Path.GetFullPath(currentDocument.Path);
            var manifest = ResolveManifest(path);
            CTildeProject? currentProject = null;
            if (manifest is not null)
            {
                try { currentProject = LoadProject(manifest); }
                catch (CTildeProjectException) { }
            }
            var included = currentProject is not null && currentProject.SourceFiles.Contains(path, PathComparer);
            var target = currentProject?.Configuration.Target ?? CompilationTarget.Hosted;
            var key = included ? currentProject!.ManifestPath : $"standalone:{path}:{target}";
            return key.Equals(project.Key, PathComparison) &&
                _projects.TryGetValue(key, out var cached) && cached.Snapshot.IsValueCreated &&
                ReferenceEquals(cached.Snapshot.Value, project);
        }
    }

    public ImmutableArray<ProjectSnapshot> GetWorkspaceProjects() => GetWorkspaceProjects(null, includeShared: true);

    public ImmutableArray<ProjectSnapshot> GetWorkspaceProjects(IReadOnlySet<string>? projectSourceIdentities, bool includeShared)
    {
        List<CachedProject> selected;
        OpenDocument[] documents;
        lock (_gate)
        {
            selected = [];
            foreach (var manifest in WorkspaceManifests())
            {
                CTildeProject project;
                try { project = LoadProject(manifest); }
                catch (CTildeProjectException) { continue; }
                if (!includeShared && projectSourceIdentities is not null && !ProjectContainsIdentity(project, projectSourceIdentities))
                    continue;
                selected.Add(GetOrCreateProject(project.ManifestPath, project, project.SourceFiles[0], null));
            }
            documents = [.. _documents.Values];
        }

        var snapshots = selected.Select(project => project.Snapshot.Value).ToList();
        foreach (var document in documents)
        {
            var snapshot = GetProject(document.Uri);
            if (includeShared || projectSourceIdentities is null || SnapshotContainsIdentity(snapshot, projectSourceIdentities))
                snapshots.Add(snapshot);
        }
        return [.. snapshots.DistinctBy(project => project.Key)];
    }

    public ImmutableArray<OpenDocument> OpenDocuments
    {
        get { lock (_gate) return [.. _documents.Values]; }
    }

    public string? GetStandardLibraryText(string path)
    {
        CachedProject[] projects;
        lock (_gate)
            projects = [.. _projects.Values];
        foreach (var project in projects)
            if (project.Snapshot.Value.LanguageService.TryGetSourceText(path, out var text))
                return text.Text;
        var target = path.Contains("Esp/Idf", StringComparison.Ordinal) ? CompilationTarget.EspIdf : CompilationTarget.Hosted;
        var temporary = LanguageServiceSnapshot.Create([SyntaxTree.ParseText("public static class __Editor { [EntryPoint] public static void Main() { } }", "<editor>")], new CompilationOptions(target));
        return temporary.TryGetSourceText(path, out var source) ? source.Text : null;
    }

    private CachedProject GetOrCreateProject(string key, CTildeProject? project, string documentPath, string? projectError)
    {
        if (_projects.TryGetValue(key, out var cached))
            return cached;
        var sourceFiles = project?.SourceFiles ?? [Path.GetFullPath(documentPath)];
        var overrides = _documents.Values.ToDictionary(document => Path.GetFullPath(document.Path), document => document.Text, PathComparer);
        var revision = _revision;
        var lazy = new Lazy<ProjectSnapshot>(() => project?.Configuration.Kind == CTildeProjectKind.StandardLibrary
            ? CreateStandardLibrarySnapshot(project, documentPath, key, overrides, revision)
            : CreateSnapshot(key, sourceFiles, project?.Configuration.Target ?? CompilationTarget.Hosted,
                project?.Configuration.Architecture ?? CompilationArchitecture.Auto,
                project?.Configuration.Environment ?? TargetEnvironment.Native,
                project?.Configuration.NoRecursion ?? false,
                project?.Configuration.PanicPolicy ?? EspIdfPanicPolicy.Abort,
                project?.Configuration.CpuFeatures ?? [],
                project?.Configuration.SimdOptimizations ?? false,
                project?.RootDirectory ?? Path.GetDirectoryName(Path.GetFullPath(documentPath)),
                projectError ?? (project is null ? null : BindingProjectError(project)), BindingPaths(project), overrides, revision),
            LazyThreadSafetyMode.ExecutionAndPublication);
        cached = new CachedProject(sourceFiles, lazy);
        _projects[key] = cached;
        return cached;
    }

    private ImmutableArray<string> WorkspaceManifests()
    {
        if (_explicitProjects.Count != 0)
            return [.. _explicitProjects.Values.Distinct(PathComparer)];
        if (!_fallbackManifestsDirty)
            return _fallbackManifests;
        var manifests = new HashSet<string>(PathComparer);
        foreach (var root in _workspaceRoots.Where(Directory.Exists))
        {
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(root, "ctilde.json", SearchOption.AllDirectories).Where(path => !IsIgnored(path)))
                    manifests.Add(Path.GetFullPath(manifest));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        _fallbackManifests = [.. manifests.OrderBy(path => path, PathComparer)];
        _fallbackManifestsDirty = false;
        return _fallbackManifests;
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

    private bool ManifestContains(string manifest, string path)
    {
        try { return LoadProject(manifest).SourceFiles.Contains(Path.GetFullPath(path), PathComparer); }
        catch (CTildeProjectException) { return false; }
    }

    private CTildeProject LoadProject(string manifest)
    {
        manifest = Path.GetFullPath(manifest);
        if (_projectDefinitions.TryGetValue(manifest, out var project))
            return project;
        project = CTildeProjectFile.Load(manifest);
        _projectDefinitions[manifest] = project;
        return project;
    }

    private void InvalidatePath(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var key in _projects.Where(pair => pair.Value.SourceFiles.Contains(path, PathComparer)).Select(pair => pair.Key).ToArray())
            _projects.Remove(key);
    }

    private void ResetProjectState(bool clearDefinitions, bool preserveSyntax = false)
    {
        _projects.Clear();
        if (!preserveSyntax)
            lock (_syntaxGate) _syntaxTrees.Clear();
        _fallbackManifests = [];
        _fallbackManifestsDirty = true;
        if (clearDefinitions)
            _projectDefinitions.Clear();
    }

    private static bool ProjectContainsIdentity(CTildeProject project, IReadOnlySet<string> identities) =>
        project.SourceFiles.Any(path => identities.Contains(NormalizeSourceIdentity(path)));

    private static bool SnapshotContainsIdentity(ProjectSnapshot project, IReadOnlySet<string> identities) =>
        project.SourceFiles.Any(path => identities.Contains(NormalizeSourceIdentity(path)));

    private static string NormalizeSourceIdentity(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private ProjectSnapshot CreateStandardLibrarySnapshot(CTildeProject project, string documentPath, string key,
        IReadOnlyDictionary<string, string> overrides, long revision)
    {
        try
        {
            var service = LanguageServiceSnapshot.CreateStandardLibraryProject(project.RootDirectory, documentPath, overrides);
            return new ProjectSnapshot(key, project.SourceFiles, service.Options.Target, service.Options.Architecture, false,
                EspIdfPanicPolicy.Abort, service.Options.EffectiveCpuFeatures, service.Options.SimdOptimizations, service, null, revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return CreateSnapshot($"standalone:{documentPath}:Hosted", [documentPath], CompilationTarget.Hosted, CompilationArchitecture.Auto,
                TargetEnvironment.Native, false, EspIdfPanicPolicy.Abort, [], false, Path.GetDirectoryName(Path.GetFullPath(documentPath)), exception.Message, null,
                overrides, revision);
        }
    }

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

    private ProjectSnapshot CreateSnapshot(string key, ImmutableArray<string> sourceFiles, CompilationTarget target,
        CompilationArchitecture architecture, TargetEnvironment environment, bool noRecursion, EspIdfPanicPolicy panicPolicy,
        ImmutableArray<CpuFeature> cpuFeatures, bool simdOptimizations,
        string? sourceIdentityRoot, string? projectError, IReadOnlySet<string>? bindingPaths,
        IReadOnlyDictionary<string, string> overrides, long revision)
    {
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
        foreach (var path in sourceFiles)
        {
            overrides.TryGetValue(Path.GetFullPath(path), out var openText);
            try
            {
                var text = openText is null ? SourceText.FromFile(path) : SourceText.From(openText, path);
                trees.Add(ParseCached(text, bindingPaths?.Contains(path) == true, revision));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                projectError ??= exception.Message;
                trees.Add(SyntaxTree.ParseText(openText ?? string.Empty, path));
            }
        }
        var service = LanguageServiceSnapshot.Create(trees, new CompilationOptions(target, Architecture: architecture, NoRecursion: noRecursion,
            SourceIdentityRoot: sourceIdentityRoot, PanicPolicy: panicPolicy, CpuFeatures: cpuFeatures, Environment: environment,
            SimdOptimizations: simdOptimizations));
        return new ProjectSnapshot(key, sourceFiles, target, architecture, noRecursion, panicPolicy, service.Options.EffectiveCpuFeatures,
            simdOptimizations, service, projectError, revision);
    }

    private void SignalChanged()
    {
        var handler = AnalysisChanged;
        if (handler is not null)
            _ = handler();
    }

    internal SyntaxTree ParseCached(SourceText text, bool binding, long revision)
    {
        var path = Path.GetFullPath(text.FilePath);
        lock (_syntaxGate)
        {
            if (_syntaxTrees.TryGetValue(path, out var cached) && cached.Binding == binding && cached.Tree.Text.Text == text.Text)
                return cached.Tree;
            var tree = binding ? SyntaxTree.ParseEspIdfBinding(text) : SyntaxTree.Parse(text);
            if (cached is null || cached.Revision <= revision)
                _syntaxTrees[path] = new CachedSyntax(tree, binding, revision);
            return tree;
        }
    }

    private sealed record CachedSyntax(SyntaxTree Tree, bool Binding, long Revision);

    private static bool IsProjectMetadata(string path) =>
        Path.GetFileName(path).StartsWith("ctilde", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ctproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnored(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/build/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.artifacts/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/templates/", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record CachedProject(ImmutableArray<string> SourceFiles, Lazy<ProjectSnapshot> Snapshot);
}

internal sealed record OpenDocument(string Uri, string Path, int Version, string Text);
internal sealed record ProjectSnapshot(string Key, ImmutableArray<string> SourceFiles, CompilationTarget Target, CompilationArchitecture Architecture, bool NoRecursion,
    EspIdfPanicPolicy PanicPolicy, ImmutableArray<CpuFeature> CpuFeatures, bool SimdOptimizations,
    LanguageServiceSnapshot LanguageService, string? ProjectError, long Revision);
