using System.Collections.Immutable;

namespace CTilde;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceLocation Location,
    SourceLocation? RelatedLocation = null)
{
    public override string ToString() => $"{Location}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

internal sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public int Count => _diagnostics.Count;

    public bool HasErrors => _diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void Add(string code, string message, SourceText source, TextSpan span, SourceLocation? related = null) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, source.GetLocation(span), related));

    public void AddWarning(string code, string message, SourceText source, TextSpan span, SourceLocation? related = null) =>
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, source.GetLocation(span), related));

    public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    public ImmutableArray<Diagnostic> ToImmutable() => [.. _diagnostics
        .OrderBy(diagnostic => diagnostic.Location.FilePath, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.Location.Span.Start)
        .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)];
}

public sealed record EmitResult(bool Success, ImmutableArray<Diagnostic> Diagnostics);
