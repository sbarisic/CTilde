using System.Text.Json;
using System.Text.Json.Serialization;

namespace CTilde.LanguageServer;

internal sealed record Position(int Line, int Character);
internal sealed record Range(Position Start, Position End);
internal sealed record TextDocumentIdentifier(string Uri);
internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);
internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);
internal sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);
internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);
internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);
internal sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);
internal sealed record TextDocumentContentChangeEvent(Range? Range, int? RangeLength, string Text);
internal sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);
internal sealed record WorkspaceFolder(string Uri, string Name);
internal sealed record InitializeParams(int? ProcessId, string? RootUri, WorkspaceFolder[]? WorkspaceFolders, JsonElement? Capabilities);
internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);
internal sealed record ServerInfo(string Name, string Version);
internal sealed record ServerCapabilities(
    TextDocumentSyncOptions TextDocumentSync,
    CompletionOptions CompletionProvider,
    SignatureHelpOptions SignatureHelpProvider,
    bool HoverProvider,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool DocumentSymbolProvider,
    bool WorkspaceSymbolProvider,
    WorkspaceCapabilities Workspace,
    SemanticTokensOptions SemanticTokensProvider);
internal sealed record TextDocumentSyncOptions(bool OpenClose, int Change, bool Save);
internal sealed record CompletionOptions(bool ResolveProvider, string[] TriggerCharacters);
internal sealed record SignatureHelpOptions(string[] TriggerCharacters, string[] RetriggerCharacters);
internal sealed record WorkspaceCapabilities(WorkspaceFoldersCapabilities WorkspaceFolders);
internal sealed record WorkspaceFoldersCapabilities(bool Supported, bool ChangeNotifications);
internal sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Full, bool Range);
internal sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);
internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);
internal sealed record SemanticTokens(int[] Data);
internal sealed record CompletionParams(TextDocumentIdentifier TextDocument, Position Position);
internal sealed record CompletionList(bool IsIncomplete, CompletionItem[] Items);
internal sealed record CompletionItem(
    string Label,
    int Kind,
    string Detail,
    string SortText,
    string FilterText,
    TextEdit TextEdit,
    int InsertTextFormat = 1,
    MarkupContent? Documentation = null,
    CompletionItemData? Data = null);
internal sealed record CompletionItemData(string Uri, string DocumentationId, long Revision);
internal sealed record TextEdit(Range Range, string NewText);
internal sealed record HoverParams(TextDocumentIdentifier TextDocument, Position Position);
internal sealed record MarkupContent(string Kind, string Value);
internal sealed record Hover(MarkupContent Contents, Range Range);
internal sealed record SignatureHelpParams(TextDocumentIdentifier TextDocument, Position Position);
internal sealed record SignatureHelp(SignatureInformation[] Signatures, int ActiveSignature, int ActiveParameter);
internal sealed record SignatureInformation(string Label, ParameterInformation[] Parameters, MarkupContent? Documentation = null);
internal sealed record ParameterInformation(string Label, MarkupContent? Documentation = null);
internal sealed record DefinitionParams(TextDocumentIdentifier TextDocument, Position Position);
internal sealed record ReferencesParams(TextDocumentIdentifier TextDocument, Position Position, ReferenceContext Context);
internal sealed record ReferenceContext(bool IncludeDeclaration);
internal sealed record Location(string Uri, Range Range);
internal sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);
internal sealed record DocumentSymbol(string Name, string Detail, int Kind, Range Range, Range SelectionRange, DocumentSymbol[] Children);
internal sealed record WorkspaceSymbolParams(string Query);
internal sealed record SymbolInformation(string Name, int Kind, Location Location, string ContainerName);
internal sealed record PublishDiagnosticsParams(string Uri, Diagnostic[] Diagnostics, int? Version);
internal sealed record Diagnostic(Range Range, int Severity, string Code, string Source, string Message, DiagnosticRelatedInformation[]? RelatedInformation = null);
internal sealed record DiagnosticRelatedInformation(Location Location, string Message);
internal sealed record DidChangeWorkspaceFoldersParams(WorkspaceFoldersChangeEvent Event);
internal sealed record WorkspaceFoldersChangeEvent(WorkspaceFolder[] Added, WorkspaceFolder[] Removed);
internal sealed record DidChangeWatchedFilesParams(FileEvent[] Changes);
internal sealed record FileEvent(string Uri, int Type);
internal sealed record StandardLibraryTextParams(string Uri);
internal sealed record CTildeProjectContext(string ProjectUri, string ManifestUri);
internal sealed record CTildeProjectContextsParams(CTildeProjectContext[] Projects, string? ActiveManifestUri);
internal sealed record CTildeActiveProjectParams(string? ManifestUri);
internal sealed record CTildeReferenceCodeLensParams(TextDocumentIdentifier TextDocument);
internal sealed record CTildeReferenceCodeLensDetailsParams(TextDocumentIdentifier TextDocument, string SymbolKey, long Revision);
internal sealed record CTildeReferenceCodeLens(
    string SymbolKey,
    string Name,
    string Detail,
    int Kind,
    Range Range,
    Range SelectionRange,
    int ReferenceCount,
    long Revision);
internal sealed record CTildeReferenceDetail(
    string Uri,
    Range Range,
    string ReferenceText,
    int ReferenceStart,
    int ReferenceEnd,
    string ReferenceLongDescription);
internal sealed record CTildeReferenceCodeLensDetails(string SymbolKey, long Revision, CTildeReferenceDetail[] References);
internal sealed record ShowMessageParams(int Type, string Message);

internal static class UriHelpers
{
    public static string ToPath(string uri)
    {
        var path = Uri.UnescapeDataString(new Uri(uri).AbsolutePath);
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];
        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string ToUri(string path)
    {
        if (path.StartsWith("stdlib/", StringComparison.Ordinal))
            return "ctilde-stdlib:///" + path["stdlib/".Length..].Replace('\\', '/');
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    public static string StandardLibraryPath(string uri)
    {
        var parsed = new Uri(uri);
        return "stdlib/" + parsed.AbsolutePath.TrimStart('/');
    }
}
