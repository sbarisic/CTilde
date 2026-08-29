using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace CTilde.VisualStudio;

internal static class ContentTypes
{
#pragma warning disable CS0649
    [Export]
    [Name("ctilde")]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? CTildeContentType;

    [Export]
    [FileExtension(".ct")]
    [ContentType("ctilde")]
    internal static FileExtensionToContentTypeDefinition? CTildeFileExtension;
#pragma warning restore CS0649
}
