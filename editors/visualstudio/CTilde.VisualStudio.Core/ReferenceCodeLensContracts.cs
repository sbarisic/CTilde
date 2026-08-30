namespace CTilde.VisualStudio.Core;

public static class ReferenceCodeLensContracts
{
    public const string MarkerProperty = "CTilde.ReferenceCodeLens";
    public const string SymbolKeyProperty = "CTilde.ReferenceCodeLens.SymbolKey";
    public const string DocumentUriProperty = "CTilde.ReferenceCodeLens.DocumentUri";
    public const string RevisionProperty = "CTilde.ReferenceCodeLens.Revision";
    public const string CountProperty = "CTilde.ReferenceCodeLens.Count";
    public const string DetailsCallback = "CTilde.ReferenceCodeLens.GetDetails";
    public static readonly Guid CommandSet = new("235dfa97-a3cf-4627-975b-851e22e0ca63");
    public const int NavigateCommandId = 0x0109;

    public static string Label(int count) => count == 1 ? "1 reference" : $"{count} references";

    public static ReferenceDetailRow[] DetailRows(IReadOnlyList<ReferenceDetail> references)
    {
        if (references.Count == 0)
            return [new ReferenceDetailRow { ReferenceText = "No references found" }];
        return [.. references.Select(reference =>
        {
            var filePath = System.Uri.TryCreate(reference.Uri, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : reference.Uri;
            return new ReferenceDetailRow
            {
                FilePath = filePath,
                LineNumber = reference.Range.Start.Line,
                ColumnNumber = reference.Range.Start.Character,
                ReferenceText = reference.ReferenceText,
                ReferenceStart = reference.ReferenceStart,
                ReferenceEnd = reference.ReferenceEnd,
                ReferenceLongDescription = reference.ReferenceLongDescription,
                NavigationArgument = new ReferenceNavigationTarget { Uri = reference.Uri, Range = reference.Range }.Serialize(),
            };
        })];
    }
}

public sealed class ReferenceDetailRow
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string ReferenceText { get; set; } = "No references found";
    public int ReferenceStart { get; set; }
    public int ReferenceEnd { get; set; }
    public string ReferenceLongDescription { get; set; } = string.Empty;
    public string? NavigationArgument { get; set; }
}

public sealed class ReferenceCodeLensRequest
{
    public TextDocumentIdentifier TextDocument { get; set; } = new();
}

public sealed class ReferenceCodeLensDetailsRequest
{
    public TextDocumentIdentifier TextDocument { get; set; } = new();
    public string SymbolKey { get; set; } = string.Empty;
    public long Revision { get; set; }
}

public sealed class TextDocumentIdentifier
{
    public string Uri { get; set; } = string.Empty;
}

public sealed class ReferenceCodeLensItem
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int Kind { get; set; }
    public ProtocolRange Range { get; set; } = new();
    public ProtocolRange SelectionRange { get; set; } = new();
    public int ReferenceCount { get; set; }
    public long Revision { get; set; }
}

public sealed class ReferenceCodeLensDetails
{
    public string SymbolKey { get; set; } = string.Empty;
    public long Revision { get; set; }
    public ReferenceDetail[] References { get; set; } = Array.Empty<ReferenceDetail>();
}

public sealed class ReferenceDetail
{
    public string Uri { get; set; } = string.Empty;
    public ProtocolRange Range { get; set; } = new();
    public string ReferenceText { get; set; } = string.Empty;
    public int ReferenceStart { get; set; }
    public int ReferenceEnd { get; set; }
    public string ReferenceLongDescription { get; set; } = string.Empty;
}

public sealed class ProtocolRange
{
    public ProtocolPosition Start { get; set; } = new();
    public ProtocolPosition End { get; set; } = new();
}

public sealed class ProtocolPosition
{
    public int Line { get; set; }
    public int Character { get; set; }
}

public sealed class ReferenceNavigationTarget
{
    public string Uri { get; set; } = string.Empty;
    public ProtocolRange Range { get; set; } = new();

    public string Serialize()
    {
        var uri = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri));
        return string.Join(",", uri, Range.Start.Line, Range.Start.Character, Range.End.Line, Range.End.Character);
    }

    public static bool TryParse(string? value, out ReferenceNavigationTarget target)
    {
        target = new ReferenceNavigationTarget();
        var parts = value?.Split(',');
        if (parts is not { Length: 5 } || !int.TryParse(parts[1], out var startLine) || !int.TryParse(parts[2], out var startCharacter) ||
            !int.TryParse(parts[3], out var endLine) || !int.TryParse(parts[4], out var endCharacter))
            return false;
        try { target.Uri = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
        catch (FormatException) { return false; }
        target.Range = new ProtocolRange
        {
            Start = new ProtocolPosition { Line = startLine, Character = startCharacter },
            End = new ProtocolPosition { Line = endLine, Character = endCharacter },
        };
        return true;
    }
}
