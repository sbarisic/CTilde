using System.Collections.Immutable;
using CTilde;

namespace CTilde.LanguageServer;

internal sealed class WorkspaceState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedProject> _projects = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _workspaceRoots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        }
        SignalChanged();
    }

    public void Open(TextDocumentItem document)
    {
        lock (_gate)
        {
            _documents[document.Uri] = new OpenDocument(document.Uri, UriHelpers.ToPath(document.Uri), document.Version, document.Text);
            _projects.Clear();
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
        }
        SignalChanged();
    }

    public void Close(string uri)
    {
        lock (_gate)
        {
            _documents.Remove(uri);
            _projects.Clear();
        }
        SignalChanged();
    }

    public void FilesChanged()
    {
        lock (_gate)
            _projects.Clear();
        SignalChanged();
    }

    public ProjectSnapshot GetProject(string uri)
    {
        lock (_gate)
        {
            var path = UriHelpers.ToPath(uri);
            var manifest = CTildeProjectFile.FindNearest(path);
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
            var snapshot = CreateSnapshot(key, sourceFiles, target, null);
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
            foreach (var root in _workspaceRoots.Where(Directory.Exists))
            {
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(root, "ctilde.json", SearchOption.AllDirectories).ToArray(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var manifest in manifests.Where(path => !IsIgnored(path)))
                {
                    CTildeProject project;
                    try { project = CTildeProjectFile.Load(manifest); }
                    catch (CTildeProjectException) { continue; }
                    if (_projects.ContainsKey(project.ManifestPath))
                        continue;
                    _projects[project.ManifestPath] = new CachedProject(CreateSnapshot(project.ManifestPath, project.SourceFiles, project.Configuration.Target, null));
                }
            }
            return [.. _projects.Values.Select(value => value.Snapshot).DistinctBy(value => value.Key)];
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

    private ProjectSnapshot CreateStandalone(string path, CompilationTarget target, string error) => CreateSnapshot($"standalone:{path}:{target}", [path], target, error);

    private ProjectSnapshot CreateSnapshot(string key, ImmutableArray<string> sourceFiles, CompilationTarget target, string? projectError)
    {
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
        foreach (var path in sourceFiles)
        {
            var open = _documents.Values.FirstOrDefault(document => PathComparer.Equals(document.Path, path));
            try
            {
                trees.Add(open is null ? SyntaxTree.Parse(SourceText.FromFile(path)) : SyntaxTree.ParseText(open.Text, path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                projectError ??= exception.Message;
                trees.Add(SyntaxTree.ParseText(open?.Text ?? string.Empty, path));
            }
        }
        var service = LanguageServiceSnapshot.Create(trees, new CompilationOptions(target));
        return new ProjectSnapshot(key, sourceFiles, target, service, projectError);
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
internal sealed record ProjectSnapshot(string Key, ImmutableArray<string> SourceFiles, CompilationTarget Target, LanguageServiceSnapshot LanguageService, string? ProjectError);
