using System.ComponentModel.Composition;
using CTilde.VisualStudio.Core;
using Microsoft.VisualStudio.Language.CodeLens;
using StreamJsonRpc;

namespace CTilde.VisualStudio;

[Export(typeof(ICodeLensCallbackListener))]
public sealed class ReferenceCodeLensCallbackListener : ICodeLensCallbackListener
{
    [JsonRpcMethod(ReferenceCodeLensContracts.DetailsCallback)]
    public Task<ReferenceCodeLensDetails> GetDetailsAsync(string uri, string symbolKey, long revision, CancellationToken cancellationToken)
    {
        var client = CTildeLanguageClient.Instance;
        return client is null
            ? Task.FromResult(new ReferenceCodeLensDetails { SymbolKey = symbolKey, Revision = revision })
            : client.GetReferenceCodeLensDetailsAsync(uri, symbolKey, revision, cancellationToken);
    }
}
