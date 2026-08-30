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
    private readonly Dictionary<string, ReferenceCodeLensTag> _tags = new(StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCancellation;
    private long _appliedRevision = -1;
    private int _refreshGeneration;
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
            var identity = TagIdentity(item);
            ReferenceCodeLensTag tag;
            lock (_gate)
            {
                if (!_tags.TryGetValue(identity, out tag!))
                {
                    if (!TrySpan(snapshot, item.SelectionRange, out var initialAnchor) || !TrySpan(snapshot, item.Range, out var range))
                        continue;
                    var trackingSpan = snapshot.CreateTrackingSpan(range.Span, SpanTrackingMode.EdgeInclusive);
                    var descriptor = new ReferenceCodeLensDescriptor(
                        _filePath,
                        string.IsNullOrWhiteSpace(item.Detail) ? item.Name : item.Detail,
                        range.Span,
                        CodeElementKind(item.Kind),
                        trackingSpan,
                        new Uri(_filePath).AbsoluteUri,
                        item);
                    tag = new ReferenceCodeLensTag(descriptor,
                        snapshot.CreateTrackingSpan(initialAnchor.Span, SpanTrackingMode.EdgeInclusive));
                    _tags.Add(identity, tag);
                }
            }
            var anchor = tag.AnchorSpan.GetSpan(snapshot);
            if (!spans.IntersectsWith(anchor))
                continue;
            yield return new TagSpan<ICodeLensTag>(anchor, tag);
        }
    }

    private static string TagIdentity(ReferenceCodeLensItem item) =>
        string.Join("|", item.SymbolKey, item.Revision, item.ReferenceCount,
            item.Range.Start.Line, item.Range.Start.Character, item.Range.End.Line, item.Range.End.Character,
            item.SelectionRange.Start.Line, item.SelectionRange.Start.Character, item.SelectionRange.End.Line, item.SelectionRange.End.Character);

    private void BufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        // Tracking spans keep the last complete result positioned while the language server analyzes the edit.
        var snapshot = eventArgs.After;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    private void QueueRefresh(long revision)
    {
        if (revision <= Volatile.Read(ref _appliedRevision))
            return;
        QueueRefresh(revision, force: false);
    }

    private void QueueRefresh() => QueueRefresh(-1, force: true);

    private void QueueRefresh(long revision, bool force)
    {
        if (_disposed)
            return;
        if (!CTildeToolPaths.Current.ShowReferenceCodeLens)
        {
            lock (_gate)
            {
                _items = Array.Empty<ReferenceCodeLensItem>();
                _tags.Clear();
            }
            var hiddenSnapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(hiddenSnapshot, 0, hiddenSnapshot.Length)));
            return;
        }
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = RefreshAsync(generation, revision, force, next.Token);
    }

    private async Task RefreshAsync(int generation, long requestedRevision, bool force, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var client = CTildeLanguageClient.Instance;
            var items = client is null || !CTildeToolPaths.Current.ShowReferenceCodeLens
                ? Array.Empty<ReferenceCodeLensItem>()
                : await client.GetReferenceCodeLensesAsync(new Uri(_filePath).AbsoluteUri, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _refreshGeneration) ||
                !force && items.Length != 0 && items.Max(item => item.Revision) < requestedRevision)
                return;
            lock (_gate)
            {
                _items = items;
                _tags.Clear();
            }
            var appliedRevision = items.Length == 0 ? requestedRevision : items.Max(item => item.Revision);
            if (appliedRevision >= 0)
                Interlocked.Exchange(ref _appliedRevision, appliedRevision);
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
        internal ReferenceCodeLensTag(ReferenceCodeLensDescriptor descriptor, ITrackingSpan anchorSpan)
        {
            Descriptor = descriptor;
            AnchorSpan = anchorSpan;
            Properties = new CodeLensTagProperties(displayBeforeCreatingDataPoints: true);
        }

        public ICodeLensDescriptor Descriptor { get; }
        internal ITrackingSpan AnchorSpan { get; }
        public ICodeLensDescriptorContextProvider DescriptorContextProvider => (ICodeLensDescriptorContextProvider)Descriptor;
        public CodeLensTagProperties Properties { get; }
        public event EventHandler Disconnected { add { } remove { } }

        public Task<CodeLensDescriptorContext> GetCurrentContextAsync() => DescriptorContextProvider.GetCurrentContextAsync();
    }

    private sealed class ReferenceCodeLensDescriptor : ICodeLensDescriptor, ICodeLensDescriptorContextProvider
    {
        private readonly ITrackingSpan _trackingSpan;
        private readonly Dictionary<object, object> _properties;

        internal ReferenceCodeLensDescriptor(string filePath, string elementDescription, Span applicableSpan, CodeElementKinds kind,
            ITrackingSpan trackingSpan, string documentUri, ReferenceCodeLensItem item)
        {
            FilePath = filePath;
            ElementDescription = elementDescription;
            ApplicableSpan = applicableSpan;
            Kind = kind;
            _trackingSpan = trackingSpan;
            _properties = new Dictionary<object, object>
            {
                [ReferenceCodeLensContracts.MarkerProperty] = true,
                [ReferenceCodeLensContracts.DocumentUriProperty] = documentUri,
                [ReferenceCodeLensContracts.SymbolKeyProperty] = item.SymbolKey,
                [ReferenceCodeLensContracts.RevisionProperty] = item.Revision,
                [ReferenceCodeLensContracts.CountProperty] = item.ReferenceCount,
            };
        }

        public string FilePath { get; }
        public Guid ProjectGuid => Guid.Empty;
        public string ElementDescription { get; }
        public Span? ApplicableSpan { get; }
        public CodeElementKinds Kind { get; }

        public Task<CodeLensDescriptorContext> GetCurrentContextAsync()
        {
            var span = _trackingSpan.GetSpan(_trackingSpan.TextBuffer.CurrentSnapshot).Span;
            return Task.FromResult(new CodeLensDescriptorContext(span, _properties));
        }
    }
}
