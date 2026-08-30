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
[DetailsTemplateName("references", new[] { "ShowCodeMap=false", "GroupBy=Item" })]
[Priority(100)]
internal sealed class ReferenceDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string ProviderName = "C~ References";
    private static readonly CodeLensDetailHeaderDescriptor[] ReferenceHeaders =
    [
        new() { UniqueName = ReferenceEntryFieldNames.FilePath },
        new() { UniqueName = ReferenceEntryFieldNames.LineNumber },
        new() { UniqueName = ReferenceEntryFieldNames.ColumnNumber },
        new() { UniqueName = ReferenceEntryFieldNames.ReferenceText },
        new() { UniqueName = ReferenceEntryFieldNames.ReferenceStart },
        new() { UniqueName = ReferenceEntryFieldNames.ReferenceEnd },
        new() { UniqueName = ReferenceEntryFieldNames.ReferenceLongDescription },
        new() { UniqueName = ReferenceEntryFieldNames.ReferenceImageId },
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
            return Task.FromResult(new CodeLensDataPointDescriptor
            {
                Description = ReferenceCodeLensContracts.Label(count),
                TooltipText = count == 0 ? "No references found" : "Show C~ references",
                IntValue = count,
            });
        }

        public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext context, CancellationToken token)
        {
            var uri = ContextString(context, ReferenceCodeLensContracts.DocumentUriProperty);
            var symbolKey = ContextString(context, ReferenceCodeLensContracts.SymbolKeyProperty);
            var revision = ContextLong(context, ReferenceCodeLensContracts.RevisionProperty);
            var details = await _callbackService.InvokeAsync<ReferenceCodeLensDetails>(this, ReferenceCodeLensContracts.DetailsCallback,
                new object[] { uri, symbolKey, revision }, token).ConfigureAwait(false);
            return new CodeLensDetailsDescriptor
            {
                Headers = ReferenceHeaders,
                Entries = ReferenceCodeLensContracts.DetailRows(details?.References ?? Array.Empty<ReferenceDetail>()).Select(CreateEntry).ToArray(),
            };
        }

        private static CodeLensDetailEntryDescriptor CreateEntry(ReferenceDetailRow row)
        {
            var entry = new CodeLensDetailEntryDescriptor
            {
                Fields =
                [
                    new CodeLensDetailEntryField { Text = row.FilePath },
                    new CodeLensDetailEntryField { Text = row.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    new CodeLensDetailEntryField { Text = row.ColumnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    new CodeLensDetailEntryField { Text = row.ReferenceText },
                    new CodeLensDetailEntryField { Text = row.ReferenceStart.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    new CodeLensDetailEntryField { Text = row.ReferenceEnd.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    new CodeLensDetailEntryField { Text = row.ReferenceLongDescription },
                    new CodeLensDetailEntryField(),
                ],
                Tooltip = row.ReferenceLongDescription,
            };
            if (row.NavigationArgument is not null)
            {
                entry.NavigationCommand = new CodeLensDetailEntryCommand
                {
                    CommandSet = ReferenceCodeLensContracts.CommandSet,
                    CommandId = ReferenceCodeLensContracts.NavigateCommandId,
                    CommandName = "CTilde.NavigateToReference",
                };
                entry.NavigationCommandArgs = [row.NavigationArgument];
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
