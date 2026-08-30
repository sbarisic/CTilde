using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;

namespace CTilde.VisualStudio;

[Export(typeof(ICodeLensCallbackListener))]
[ContentType("ctilde")]
public sealed class ReferenceCodeLensCallbackListener : ICodeLensCallbackListener
{
    [JsonRpcMethod(ReferenceCodeLensContracts.DetailsCallback)]
    public async Task<string> GetDetailsAsync(string uri, string symbolKey, long revision, CancellationToken cancellationToken)
    {
        var client = CTildeLanguageClient.Instance;
        var details = client is null
            ? new ReferenceCodeLensDetails { SymbolKey = symbolKey, Revision = revision }
            : await client.GetReferenceCodeLensDetailsAsync(uri, symbolKey, revision, cancellationToken).ConfigureAwait(false);
        // The CodeLens service runs out of process. Returning one primitive string avoids its
        // callback object projector dropping nested fields that it does not recognize.
        return ReferenceCodeLensContracts.SerializeDetails(details);
    }
}
