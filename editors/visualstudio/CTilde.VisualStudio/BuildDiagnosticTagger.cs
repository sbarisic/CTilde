using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace CTilde.VisualStudio;

[Export(typeof(ITaggerProvider))]
[ContentType("text")]
[TagType(typeof(IErrorTag))]
internal sealed class BuildDiagnosticTaggerProvider : ITaggerProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocuments { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) != typeof(IErrorTag) || !TextDocuments.TryGetTextDocument(buffer, out var document))
            return null;
        var file = document.FilePath;
        if (!file.EndsWith(".ct", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(file).Equals("ctilde.json", StringComparison.OrdinalIgnoreCase))
            return null;
        var receipt = FindReceipt(file);
        return receipt is null ? null : new BuildDiagnosticTagger(buffer, file, receipt) as ITagger<T>;
    }

    private static string? FindReceipt(string filePath)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".ctilde", "build-diagnostics.json");
            if (File.Exists(candidate) || File.Exists(Path.Combine(directory.FullName, "ctilde.json")))
                return candidate;
        }
        return null;
    }
}

internal sealed class BuildDiagnosticTagger : ITagger<IErrorTag>, IDisposable
{
    private static event Action<string>? DiagnosticsPublished;
    private static event Action? ClearRequested;
    private readonly ITextBuffer buffer;
    private readonly string filePath;
    private readonly string receiptPath;
    private readonly FileSystemWatcher watcher;
    private readonly object gate = new();
    private BuildReceiptDiagnostic[] diagnostics = Array.Empty<BuildReceiptDiagnostic>();
    private bool disposed;

    public BuildDiagnosticTagger(ITextBuffer buffer, string filePath, string receiptPath)
    {
        this.buffer = buffer;
        this.filePath = Path.GetFullPath(filePath);
        this.receiptPath = receiptPath;
        buffer.Changed += BufferChanged;
        DiagnosticsPublished += ClearForPublishedDiagnostics;
        ClearRequested += Clear;
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(receiptPath)!, ".."));
        watcher = new FileSystemWatcher(directory, Path.GetFileName(receiptPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        watcher.Changed += ReceiptChanged;
        watcher.Created += ReceiptChanged;
        watcher.Renamed += ReceiptChanged;
        watcher.Deleted += ReceiptChanged;
        Reload();
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    internal static void ClearForUri(string uri) => DiagnosticsPublished?.Invoke(uri);
    internal static void ClearAll() => ClearRequested?.Invoke();

    public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)
            yield break;
        BuildReceiptDiagnostic[] current;
        lock (gate)
            current = diagnostics;
        var snapshot = spans[0].Snapshot;
        foreach (var diagnostic in current)
        {
            if (!TrySpan(snapshot, diagnostic, out var span))
                continue;
            var errorType = diagnostic.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)
                ? PredefinedErrorTypeNames.Warning
                : diagnostic.Severity.Equals("info", StringComparison.OrdinalIgnoreCase)
                    ? PredefinedErrorTypeNames.Suggestion
                    : PredefinedErrorTypeNames.SyntaxError;
            yield return new TagSpan<IErrorTag>(span, new ErrorTag(errorType, $"{diagnostic.Code}: {diagnostic.Message}"));
        }
    }

    private void BufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        Clear();
    }

    private void ClearForPublishedDiagnostics(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var value) && value.IsFile &&
            string.Equals(Path.GetFullPath(value.LocalPath), filePath, StringComparison.OrdinalIgnoreCase))
            Clear();
    }

    private void Clear() => Replace(Array.Empty<BuildReceiptDiagnostic>());

    private void ReceiptChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (disposed)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(75).ConfigureAwait(false);
            Reload();
        });
    }

    private void Reload()
    {
        if (disposed)
            return;
        BuildReceiptDiagnostic[] next = Array.Empty<BuildReceiptDiagnostic>();
        if (BuildDiagnosticReceipts.TryRead(receiptPath, out var receipt) && receipt is not null)
        {
            var manifest = Path.GetFullPath(receipt.Manifest);
            var isManifest = string.Equals(filePath, manifest, StringComparison.OrdinalIgnoreCase);
            var savedManifest = isManifest ? ReadText(filePath) : null;
            next = BuildDiagnosticReceipts.CurrentDiagnostics(receipt, filePath, buffer.CurrentSnapshot.GetText(), savedManifest).ToArray();
            CTildeLanguageClient.Instance?.QueueProjectReanalysis(manifest);
        }
        Replace(next);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Replace(BuildReceiptDiagnostic[] next)
    {
        lock (gate)
            diagnostics = next;
        var snapshot = buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    private static bool TrySpan(ITextSnapshot snapshot, BuildReceiptDiagnostic diagnostic, out SnapshotSpan span)
    {
        span = default;
        var startLine = Math.Max(0, diagnostic.StartLine - 1);
        var endLine = Math.Max(startLine, diagnostic.EndLine - 1);
        if (startLine >= snapshot.LineCount || endLine >= snapshot.LineCount)
            return false;
        var startSnapshotLine = snapshot.GetLineFromLineNumber(startLine);
        var endSnapshotLine = snapshot.GetLineFromLineNumber(endLine);
        var start = startSnapshotLine.Start.Position + Math.Min(Math.Max(0, diagnostic.StartColumn - 1), startSnapshotLine.Length);
        var end = endSnapshotLine.Start.Position + Math.Min(Math.Max(0, diagnostic.EndColumn - 1), endSnapshotLine.Length);
        if (end <= start)
            end = Math.Min(snapshot.Length, start + 1);
        span = new SnapshotSpan(snapshot, Span.FromBounds(start, end));
        return true;
    }

    public void Dispose()
    {
        disposed = true;
        buffer.Changed -= BufferChanged;
        DiagnosticsPublished -= ClearForPublishedDiagnostics;
        ClearRequested -= Clear;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }
}
