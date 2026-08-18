using System.Collections.Immutable;
using System.Text;

namespace CTilde;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));
}

public readonly record struct SourceLocation(string FilePath, TextSpan Span, int Line, int Column)
{
    public override string ToString() => $"{FilePath}({Line},{Column})";
}

public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string text, string filePath)
    {
        Text = text;
        FilePath = string.IsNullOrWhiteSpace(filePath) ? "<memory>" : filePath;

        var starts = ImmutableArray.CreateBuilder<int>();
        starts.Add(0);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            if (text[i] is '\r' or '\n')
                starts.Add(i + 1);
        }

        _lineStarts = starts.ToImmutable();
    }

    public string Text { get; }

    public string FilePath { get; }

    public int Length => Text.Length;

    public char this[int index] => Text[index];

    public static SourceText From(string text, string filePath = "<memory>") => new(text ?? string.Empty, filePath);

    public static SourceText FromFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var utf8 = new UTF8Encoding(false, true);
        return new SourceText(utf8.GetString(bytes), Path.GetFullPath(filePath));
    }

    public SourceLocation GetLocation(TextSpan span)
    {
        var position = Math.Clamp(span.Start, 0, Text.Length);
        var lineIndex = _lineStarts.BinarySearch(position);
        if (lineIndex < 0)
            lineIndex = ~lineIndex - 1;
        lineIndex = Math.Max(0, lineIndex);
        return new SourceLocation(FilePath, span, lineIndex + 1, position - _lineStarts[lineIndex] + 1);
    }

    public string Slice(TextSpan span) => Text.Substring(span.Start, span.Length);
}
