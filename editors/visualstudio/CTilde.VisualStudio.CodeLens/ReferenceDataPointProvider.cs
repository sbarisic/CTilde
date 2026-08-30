using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace CTilde.VisualStudio.CodeLens;

[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(ProviderName)]
[ContentType("ctilde")]
[DynamicVisibility(true)]
[Priority(100)]
internal sealed class ReferenceDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string ProviderName = "C~ References";
    private static readonly CodeLensDetailHeaderDescriptor[] ReferenceHeaders =
    [
        new() { UniqueName = "reference", DisplayName = "Reference", Width = 835, IsVisible = true },
    ];

    [Import]
    internal ICodeLensCallbackService CallbackService { get; set; } = null!;

    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext context, CancellationToken token) =>
        Task.FromResult(context.Properties.ContainsKey(ReferenceCodeLensContracts.MarkerProperty));

    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext context, CancellationToken token) =>
        Task.FromResult<IAsyncCodeLensDataPoint>(new ReferenceDataPoint(descriptor, CallbackService));

    private sealed class ReferenceDataPoint : IAsyncCodeLensDataPoint
    {
        private readonly ICodeLensCallbackService _callbackService;
        private readonly object _gate = new();
        private ReferenceCodeLensDetails? _details;
        private Task? _detailsTask;
        private long _detailsRevision = -1;
        private string? _detailsError;

        internal ReferenceDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService)
        {
            Descriptor = descriptor;
            _callbackService = callbackService;
        }

        public event AsyncEventHandler? InvalidatedAsync;

        public CodeLensDescriptor Descriptor { get; }

        public Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext context, CancellationToken token)
        {
            var count = ContextInt(context, ReferenceCodeLensContracts.CountProperty);
            var uri = ContextString(context, ReferenceCodeLensContracts.DocumentUriProperty);
            var symbolKey = ContextString(context, ReferenceCodeLensContracts.SymbolKeyProperty);
            var revision = ContextLong(context, ReferenceCodeLensContracts.RevisionProperty);
            StartDetailsFetch(uri, symbolKey, revision, token);
            return Task.FromResult(new CodeLensDataPointDescriptor
            {
                Description = ReferenceCodeLensContracts.Label(count),
                TooltipText = count == 0 ? "No references found" : "Show C~ references",
                IntValue = count,
            });
        }

        public Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Visual Studio synchronously joins this call on its UI thread. Details are fetched from
            // the in-process callback during GetDataAsync so opening the popup cannot deadlock.
            ReferenceCodeLensDetails? details;
            string? error;
            lock (_gate)
            {
                details = _details;
                error = _detailsError;
            }
            var rows = details is not null
                ? ReferenceCodeLensContracts.DetailRows(details.References)
                : new[] { new ReferenceDetailRow { ReferenceText = error is null ? "Loading references..." : "Reference details unavailable" } };
            return Task.FromResult(new CodeLensDetailsDescriptor
            {
                Headers = ReferenceHeaders,
                Entries = rows.Select(CreateEntry).ToArray(),
            });
        }

        private void StartDetailsFetch(string uri, string symbolKey, long revision, CancellationToken token)
        {
            var start = false;
            lock (_gate)
            {
                if (_detailsRevision == revision && (_details is not null || _detailsTask is not null))
                    return;
                _detailsRevision = revision;
                _details = null;
                _detailsError = null;
                _detailsTask = Task.CompletedTask;
                start = true;
            }
            if (!start)
                return;
            var task = FetchDetailsAsync(uri, symbolKey, revision, token);
            lock (_gate)
                if (_detailsRevision == revision && _details is null && _detailsError is null && _detailsTask is not null)
                    _detailsTask = task;
        }

        private async Task FetchDetailsAsync(string uri, string symbolKey, long revision, CancellationToken token)
        {
            try
            {
                var payload = await _callbackService.InvokeAsync<string>(this, ReferenceCodeLensContracts.DetailsCallback,
                    new object[] { uri, symbolKey, revision }, token).ConfigureAwait(false);
                var details = ReferenceCodeLensContracts.DeserializeDetails(payload);
                ReferenceCodeLensContracts.RestoreMissingReferenceText(details);
                lock (_gate)
                {
                    if (_detailsRevision == revision)
                    {
                        _details = details;
                        _detailsError = null;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (_gate)
                    if (_detailsRevision == revision)
                        _detailsTask = null;
                return;
            }
            catch (Exception exception)
            {
                lock (_gate)
                    if (_detailsRevision == revision)
                        _detailsError = exception.Message;
                System.Diagnostics.Trace.WriteLine($"C~ reference CodeLens details unavailable: {exception.Message}");
            }
            finally
            {
                lock (_gate)
                    if (_detailsRevision == revision)
                        _detailsTask = null;
            }
            Invalidate();
        }

        private static CodeLensDetailEntryDescriptor CreateEntry(ReferenceDetailRow row)
        {
            var navigationArgument = row.NavigationArgument;
            var navigable = navigationArgument is not null;
            var entry = new CodeLensDetailEntryDescriptor
            {
                Fields =
                [
                    new CodeLensDetailEntryField { Text = ReferenceCodeLensContracts.DisplayReference(row) },
                ],
                Tooltip = row.ReferenceLongDescription,
            };
            if (navigationArgument is not null)
            {
                entry.NavigationCommand = new CodeLensDetailEntryCommand
                {
                    CommandSet = ReferenceCodeLensContracts.CommandSet,
                    CommandId = ReferenceCodeLensContracts.NavigateCommandId,
                    CommandName = "CTilde.NavigateToReference",
                };
                entry.NavigationCommandArgs = [navigationArgument];
            }
            return entry;
        }

        private static string ContextString(CodeLensDescriptorContext context, string key) =>
            context.Properties.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

        private static int ContextInt(CodeLensDescriptorContext context, string key) =>
            context.Properties.TryGetValue(key, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

        private static long ContextLong(CodeLensDescriptorContext context, string key) =>
            context.Properties.TryGetValue(key, out var value) ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : 0L;

        private void Invalidate()
        {
            if (InvalidatedAsync is not null)
                _ = InvalidatedAsync.InvokeAsync(this, EventArgs.Empty);
        }
    }
}
