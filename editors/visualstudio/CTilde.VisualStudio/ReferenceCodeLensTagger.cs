using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace CTilde.VisualStudio;

[Export(typeof(ITaggerProvider))]
[ContentType("ctilde")]
[TagType(typeof(ICodeLensTag))]
internal sealed class ReferenceCodeLensTaggerProvider : ITaggerProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocuments { get; set; } = null!;

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) != typeof(ICodeLensTag) || !TextDocuments.TryGetTextDocument(buffer, out var document))
            return null;
        return new ReferenceCodeLensTagger(buffer, document.FilePath) as ITagger<T>;
    }
}

internal sealed class ReferenceCodeLensTagger : ITagger<ICodeLensTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly string _filePath;
    private readonly object _gate = new();
    private ReferenceCodeLensItem[] _items = Array.Empty<ReferenceCodeLensItem>();
    private CancellationTokenSource? _refreshCancellation;
    private bool _disposed;

    internal ReferenceCodeLensTagger(ITextBuffer buffer, string filePath)
    {
        _buffer = buffer;
        _filePath = filePath;
        _buffer.Changed += BufferChanged;
        CTildeLanguageClient.ReferenceCodeLensesChanged += QueueRefresh;
        CTildeToolPaths.Changed += QueueRefresh;
        QueueRefresh();
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<ICodeLensTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0 || !CTildeToolPaths.Current.ShowReferenceCodeLens)
            yield break;
        ReferenceCodeLensItem[] items;
        lock (_gate)
            items = _items;
        var snapshot = spans[0].Snapshot;
        foreach (var item in items)
        {
            if (!TrySpan(snapshot, item.Range, out var range) || !spans.IntersectsWith(range))
                continue;
            var descriptor = new ReferenceCodeLensDescriptor
            {
                FilePath = _filePath,
                ProjectGuid = Guid.Empty,
                ElementDescription = string.IsNullOrWhiteSpace(item.Detail) ? item.Name : item.Detail,
                ApplicableSpan = range.Span,
                Kind = CodeElementKind(item.Kind),
            };
            var tag = new ReferenceCodeLensTag(descriptor, snapshot.CreateTrackingSpan(range.Span, SpanTrackingMode.EdgeInclusive),
                new Uri(_filePath).AbsoluteUri, item);
            yield return new TagSpan<ICodeLensTag>(range, tag);
        }
    }

    private void BufferChanged(object sender, TextContentChangedEventArgs eventArgs) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_disposed)
            return;
        lock (_gate)
            _items = Array.Empty<ReferenceCodeLensItem>();
        var snapshot = _buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = RefreshAsync(next.Token);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var client = CTildeLanguageClient.Instance;
            var items = client is null || !CTildeToolPaths.Current.ShowReferenceCodeLens
                ? Array.Empty<ReferenceCodeLensItem>()
                : await client.GetReferenceCodeLensesAsync(new Uri(_filePath).AbsoluteUri, cancellationToken).ConfigureAwait(false);
            lock (_gate)
                _items = items;
            var snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            CTildeOutput.WriteLine($"Reference CodeLens refresh failed: {exception.Message}");
        }
    }

    private static bool TrySpan(ITextSnapshot snapshot, ProtocolRange range, out SnapshotSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.Start.Line >= snapshot.LineCount || range.End.Line < 0 || range.End.Line >= snapshot.LineCount)
            return false;
        var startLine = snapshot.GetLineFromLineNumber(range.Start.Line);
        var endLine = snapshot.GetLineFromLineNumber(range.End.Line);
        var start = Math.Min(startLine.End.Position, startLine.Start.Position + Math.Max(0, range.Start.Character));
        var end = Math.Min(endLine.End.Position, endLine.Start.Position + Math.Max(0, range.End.Character));
        span = new SnapshotSpan(snapshot, Span.FromBounds(start, Math.Max(start, end)));
        return true;
    }

    private static CodeElementKinds CodeElementKind(int kind) => kind switch
    {
        5 => CodeElementKinds.Class,
        6 => CodeElementKinds.Method,
        7 => CodeElementKinds.Property,
        8 => CodeElementKinds.Field,
        9 => CodeElementKinds.Constructor,
        10 => CodeElementKinds.Enum,
        23 => CodeElementKinds.Struct,
        _ => CodeElementKinds.Field,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _buffer.Changed -= BufferChanged;
        CTildeLanguageClient.ReferenceCodeLensesChanged -= QueueRefresh;
        CTildeToolPaths.Changed -= QueueRefresh;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
    }

    private sealed class ReferenceCodeLensTag : ICodeLensTag3, ICodeLensDescriptorContextProvider
    {
        private readonly ITrackingSpan _trackingSpan;
        private readonly Dictionary<object, object> _properties;

        internal ReferenceCodeLensTag(ICodeLensDescriptor descriptor, ITrackingSpan trackingSpan, string documentUri, ReferenceCodeLensItem item)
        {
            Descriptor = descriptor;
            _trackingSpan = trackingSpan;
            _properties = new Dictionary<object, object>
            {
                [ReferenceCodeLensContracts.MarkerProperty] = true,
                [ReferenceCodeLensContracts.DocumentUriProperty] = documentUri,
                [ReferenceCodeLensContracts.SymbolKeyProperty] = item.SymbolKey,
                [ReferenceCodeLensContracts.RevisionProperty] = item.Revision,
                [ReferenceCodeLensContracts.CountProperty] = item.ReferenceCount,
            };
            Properties = new CodeLensTagProperties(displayBeforeCreatingDataPoints: true);
        }

        public ICodeLensDescriptor Descriptor { get; }
        public ICodeLensDescriptorContextProvider DescriptorContextProvider => this;
        public CodeLensTagProperties Properties { get; }
        public event EventHandler Disconnected { add { } remove { } }

        public Task<CodeLensDescriptorContext> GetCurrentContextAsync()
        {
            var span = _trackingSpan.GetSpan(_trackingSpan.TextBuffer.CurrentSnapshot).Span;
            return Task.FromResult(new CodeLensDescriptorContext(span, _properties));
        }
    }

    private sealed class ReferenceCodeLensDescriptor : ICodeLensDescriptor
    {
        public string FilePath { get; set; } = string.Empty;
        public Guid ProjectGuid { get; set; }
        public string ElementDescription { get; set; } = string.Empty;
        public Span? ApplicableSpan { get; set; }
        public CodeElementKinds Kind { get; set; }
    }
}
