using CTilde;
using StreamJsonRpc;
using System.Text;

namespace CTilde.LanguageServer;

internal sealed class LanguageServer
{
    private static readonly string[] SemanticTokenTypes = ["namespace", "class", "struct", "enum", "enumMember", "parameter", "variable", "property", "method"];
    private static readonly string[] SemanticTokenModifiers = ["declaration", "static", "readonly", "defaultLibrary"];
    private readonly WorkspaceState _workspace = new();
    private JsonRpc? _rpc;
    private CancellationTokenSource? _diagnosticDelay;
    private bool _shutdown;
    private bool _semanticRefreshSupported;

    public LanguageServer() => _workspace.AnalysisChanged += ScheduleDiagnosticsAsync;

    public void Attach(JsonRpc rpc) => _rpc = rpc;

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams parameters)
    {
        _workspace.Initialize(parameters.RootUri, parameters.WorkspaceFolders);
        _semanticRefreshSupported = SupportsSemanticTokenRefresh(parameters.Capabilities);
        return new InitializeResult(
            new ServerCapabilities(
                new TextDocumentSyncOptions(true, 2, true),
                new CompletionOptions(true, ["."]),
                new SignatureHelpOptions(["(", ","], [","]),
                true, true, true, true,
                new WorkspaceCapabilities(new WorkspaceFoldersCapabilities(true, true)),
                new SemanticTokensOptions(new SemanticTokensLegend(SemanticTokenTypes, SemanticTokenModifiers), true, false)),
            new ServerInfo("C~ Language Server", "0.11.0"));
    }

    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public Task InitializedAsync(object? parameters = null) => ScheduleDiagnosticsAsync();

    [JsonRpcMethod("shutdown")]
    public object? Shutdown()
    {
        _shutdown = true;
        _diagnosticDelay?.Cancel();
        return null;
    }

    [JsonRpcMethod("exit")]
    public void Exit()
    {
        _diagnosticDelay?.Cancel();
        Environment.Exit(_shutdown ? 0 : 1);
    }

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams parameters) => _workspace.Open(parameters.TextDocument);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams parameters) => _workspace.Change(parameters.TextDocument, parameters.ContentChanges);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public async Task DidCloseAsync(DidCloseTextDocumentParams parameters)
    {
        _workspace.Close(parameters.TextDocument.Uri);
        if (_rpc is not null)
            await _rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(parameters.TextDocument.Uri, [], null)).ConfigureAwait(false);
    }

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams parameters) => _workspace.FilesChanged();

    [JsonRpcMethod("workspace/didChangeWorkspaceFolders", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWorkspaceFolders(DidChangeWorkspaceFoldersParams parameters) => _workspace.ChangeFolders(parameters.Event);

    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeWatchedFiles(DidChangeWatchedFilesParams parameters) => _workspace.FilesChanged();

    [JsonRpcMethod("ctilde/didChangeProjects", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeProjects(CTildeProjectContextsParams parameters) => _workspace.SetProjectContexts(parameters);

    [JsonRpcMethod("ctilde/didChangeActiveProject", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeActiveProject(CTildeActiveProjectParams parameters) => _workspace.SetActiveProject(parameters.ManifestUri);

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public CompletionList Completion(CompletionParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        var offset = PositionToOffset(project, path, parameters.Position);
        var items = project.LanguageService.GetCompletions(path, offset).Select(item => new CompletionItem(
            item.Label, CompletionKind(item.Kind), CompletionDetail(item), item.SortText, item.Label,
            new TextEdit(ToRange(project, path, item.ReplacementSpan), item.InsertText),
            Data: item.DocumentationId is null ? null : new CompletionItemData(parameters.TextDocument.Uri, item.DocumentationId, project.Revision))).ToArray();
        return new CompletionList(false, items);
    }

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public CompletionItem ResolveCompletion(CompletionItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Data is not { } data || string.IsNullOrWhiteSpace(data.Uri) || string.IsNullOrWhiteSpace(data.DocumentationId))
            return item;
        var project = _workspace.GetProject(data.Uri);
        if (project.Revision != data.Revision)
            return item;
        var documentation = project.LanguageService.GetDocumentation(data.DocumentationId);
        return documentation is null ? item : item with { Documentation = new MarkupContent("markdown", RenderDocumentation(documentation)) };
    }

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public Hover? Hover(HoverParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        var hover = project.LanguageService.GetHover(path, PositionToOffset(project, path, parameters.Position));
        return hover is null ? null : new Hover(new MarkupContent("markdown", RenderHover(hover)), ToRange(project, path, hover.Span));
    }

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public SignatureHelp? SignatureHelp(SignatureHelpParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        var help = project.LanguageService.GetSignatureHelp(path, PositionToOffset(project, path, parameters.Position));
        return help is null ? null : new SignatureHelp(
            [.. help.Signatures.Select(signature => new SignatureInformation(
                signature.Label,
                [.. signature.Parameters.Select(parameter => new ParameterInformation(
                    parameter.Label,
                    parameter.Documentation is null ? null : new MarkupContent("markdown", parameter.Documentation)))],
                signature.Documentation is null ? null : new MarkupContent("markdown", RenderDocumentation(signature.Documentation))))],
            help.ActiveSignature, help.ActiveParameter);
    }

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public Location? Definition(DefinitionParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        var definition = project.LanguageService.GetDefinition(path, PositionToOffset(project, path, parameters.Position));
        return definition is null ? null : ToLocation(project, definition);
    }

    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public DocumentSymbol[] DocumentSymbols(DocumentSymbolParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        return [.. project.LanguageService.GetDocumentSymbols(path).Select(symbol => ToDocumentSymbol(project, path, symbol))];
    }

    [JsonRpcMethod("workspace/symbol", UseSingleObjectParameterDeserialization = true)]
    public SymbolInformation[] WorkspaceSymbols(WorkspaceSymbolParams parameters, CancellationToken cancellationToken)
    {
        var results = new List<SymbolInformation>();
        foreach (var project in _workspace.GetWorkspaceProjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(project.LanguageService.GetWorkspaceSymbols(parameters.Query).Select(symbol => new SymbolInformation(
                symbol.Name, SymbolKind(symbol.Kind), ToLocation(project, symbol.Location), symbol.ContainerName)));
        }
        return [.. results.GroupBy(symbol => (symbol.Name, symbol.ContainerName, symbol.Location.Uri, symbol.Location.Range.Start.Line, symbol.Location.Range.Start.Character)).Select(group => group.First())];
    }

    [JsonRpcMethod("textDocument/semanticTokens/full", UseSingleObjectParameterDeserialization = true)]
    public SemanticTokens SemanticTokens(SemanticTokensParams parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _workspace.GetProject(parameters.TextDocument.Uri);
        var path = UriHelpers.ToPath(parameters.TextDocument.Uri);
        if (!project.LanguageService.TryGetSourceText(path, out var source))
            return new SemanticTokens([]);
        var tokens = project.LanguageService.GetSemanticTokens(path, cancellationToken);
        var data = new List<int>(tokens.Length * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        var first = true;
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = source.GetLocation(new TextSpan(token.Span.Start, 0));
            var line = location.Line - 1;
            var character = location.Column - 1;
            var deltaLine = first ? line : line - previousLine;
            var deltaCharacter = first || deltaLine != 0 ? character : character - previousCharacter;
            data.Add(deltaLine);
            data.Add(deltaCharacter);
            data.Add(token.Span.Length);
            data.Add((int)token.Kind);
            data.Add((int)token.Modifiers);
            previousLine = line;
            previousCharacter = character;
            first = false;
        }
        return new SemanticTokens([.. data]);
    }

    [JsonRpcMethod("ctilde/standardLibraryText", UseSingleObjectParameterDeserialization = true)]
    public string? StandardLibraryText(StandardLibraryTextParams parameters) => _workspace.GetStandardLibraryText(UriHelpers.StandardLibraryPath(parameters.Uri));

    private async Task ScheduleDiagnosticsAsync()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _diagnosticDelay, next);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            await Task.Delay(150, next.Token).ConfigureAwait(false);
            await PublishDiagnosticsAsync(next.Token).ConfigureAwait(false);
            if (_semanticRefreshSupported && _rpc is { } rpc)
                await rpc.InvokeAsync("workspace/semanticTokens/refresh", Array.Empty<object>()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"C~ language server diagnostics failed: {exception}");
        }
    }

    private async Task PublishDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var rpc = _rpc;
        if (rpc is null)
            return;
        foreach (var document in _workspace.OpenDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = _workspace.GetProject(document.Uri);
            if (project.ProjectError is not null)
                await rpc.NotifyWithParameterObjectAsync("window/showMessage", new ShowMessageParams(1, project.ProjectError)).ConfigureAwait(false);
            var diagnostics = project.LanguageService.Diagnostics
                .Where(diagnostic => PathEquals(diagnostic.Location.FilePath, document.Path))
                .Select(diagnostic => ToDiagnostic(project, diagnostic)).ToArray();
            await rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new PublishDiagnosticsParams(document.Uri, diagnostics, document.Version)).ConfigureAwait(false);
        }
    }

    private static Diagnostic ToDiagnostic(ProjectSnapshot project, CTilde.Diagnostic diagnostic)
    {
        DiagnosticRelatedInformation[]? related = null;
        if (diagnostic.RelatedLocation is { } location)
        {
            related = [new DiagnosticRelatedInformation(new Location(UriHelpers.ToUri(location.FilePath), ToRange(project, location.FilePath, location.Span)), "Related location")];
        }
        return new Diagnostic(ToRange(project, diagnostic.Location.FilePath, diagnostic.Location.Span), diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2, diagnostic.Code, "ctilde", diagnostic.Message, related);
    }

    private static DocumentSymbol ToDocumentSymbol(ProjectSnapshot project, string path, LanguageDocumentSymbol symbol) => new(
        symbol.Name, symbol.Detail, SymbolKind(symbol.Kind), ToRange(project, path, symbol.Range), ToRange(project, path, symbol.SelectionRange),
        [.. symbol.Children.Select(child => ToDocumentSymbol(project, path, child))]);

    private static Location ToLocation(ProjectSnapshot project, LanguageDefinition definition) => new(UriHelpers.ToUri(definition.FilePath), ToRange(project, definition.FilePath, definition.Span));

    private static int PositionToOffset(ProjectSnapshot project, string path, Position position) =>
        project.LanguageService.TryGetSourceText(path, out var source) ? source.GetPosition(position.Line, position.Character) : 0;

    private static Range ToRange(ProjectSnapshot project, string path, TextSpan span)
    {
        if (!project.LanguageService.TryGetSourceText(path, out var source))
            return new Range(new Position(0, 0), new Position(0, 0));
        var start = source.GetLocation(new TextSpan(span.Start, 0));
        var end = source.GetLocation(new TextSpan(span.End, 0));
        return new Range(new Position(start.Line - 1, start.Column - 1), new Position(end.Line - 1, end.Column - 1));
    }

    private static int CompletionKind(LanguageCompletionKind kind) => kind switch
    {
        LanguageCompletionKind.Method => 2,
        LanguageCompletionKind.Constructor => 4,
        LanguageCompletionKind.Field => 5,
        LanguageCompletionKind.Variable => 6,
        LanguageCompletionKind.Class => 7,
        LanguageCompletionKind.Enum => 13,
        LanguageCompletionKind.Keyword => 14,
        LanguageCompletionKind.Property => 10,
        LanguageCompletionKind.Parameter => 6,
        LanguageCompletionKind.Struct => 22,
        LanguageCompletionKind.EnumMember => 20,
        LanguageCompletionKind.Namespace => 9,
        _ => 1,
    };

    private static string CompletionDetail(LanguageCompletion completion) => completion.OverloadCount > 1
        ? $"{completion.Detail} (+{completion.OverloadCount - 1} overloads)"
        : completion.Detail;

    private static int SymbolKind(LanguageSymbolKind kind) => kind switch
    {
        LanguageSymbolKind.Namespace => 3,
        LanguageSymbolKind.Class => 5,
        LanguageSymbolKind.Method => 6,
        LanguageSymbolKind.Property => 7,
        LanguageSymbolKind.Field => 8,
        LanguageSymbolKind.Constructor => 9,
        LanguageSymbolKind.Enum => 10,
        LanguageSymbolKind.Struct => 23,
        LanguageSymbolKind.EnumMember => 22,
        _ => 13,
    };

    private static bool PathEquals(string left, string right) => (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static string RenderHover(LanguageHover hover)
    {
        var sections = hover.Sections.IsDefaultOrEmpty
            ? [new LanguageDocumentedSignature(hover.Contents, null)]
            : hover.Sections;
        return string.Join("\n\n---\n\n", sections.Select(section =>
            $"```ctilde\n{section.Signature}\n```" +
            (section.Documentation is null ? string.Empty : "\n\n" + RenderDocumentation(section.Documentation))));
    }

    private static string RenderDocumentation(LanguageDocumentation documentation)
    {
        var result = new StringBuilder();
        AppendText(documentation.Summary);
        if (!documentation.Parameters.IsDefaultOrEmpty)
        {
            AppendHeading("Parameters");
            foreach (var parameter in documentation.Parameters)
                result.Append("- `").Append(parameter.Name).Append("`: ").Append(parameter.Text).Append('\n');
        }
        if (!string.IsNullOrEmpty(documentation.Returns))
        {
            AppendHeading("Returns");
            result.Append(documentation.Returns);
        }
        if (!documentation.Exceptions.IsDefaultOrEmpty)
        {
            AppendHeading("Exceptions");
            foreach (var exception in documentation.Exceptions)
                result.Append("- `").Append(exception.TypeName).Append("`: ").Append(exception.Text).Append('\n');
        }
        if (!string.IsNullOrEmpty(documentation.Remarks))
        {
            AppendHeading("Remarks");
            result.Append(documentation.Remarks);
        }
        return result.ToString().TrimEnd();

        void AppendText(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            Separate();
            result.Append(value);
        }

        void AppendHeading(string heading)
        {
            Separate();
            result.Append("**").Append(heading).Append("**\n\n");
        }

        void Separate()
        {
            if (result.Length != 0)
                result.Append("\n\n");
        }
    }

    private static bool SupportsSemanticTokenRefresh(System.Text.Json.JsonElement? capabilities)
    {
        if (capabilities is not { ValueKind: System.Text.Json.JsonValueKind.Object } root ||
            !root.TryGetProperty("workspace", out var workspace) || workspace.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !workspace.TryGetProperty("semanticTokens", out var semanticTokens) || semanticTokens.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !semanticTokens.TryGetProperty("refreshSupport", out var refreshSupport))
            return false;
        return refreshSupport.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}
