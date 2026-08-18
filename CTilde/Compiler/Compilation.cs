using System.Collections.Immutable;

namespace CTilde;

public sealed class Compilation
{
    private readonly object _gate = new();
    private ImmutableArray<Diagnostic> _diagnostics;
    private string? _generatedC;
    private bool _analyzed;

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees)
    {
        SyntaxTrees = syntaxTrees;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }

    public static Compilation Create(IEnumerable<SyntaxTree> syntaxTrees)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var trees = syntaxTrees.ToImmutableArray();
        if (trees.Any(tree => tree is null))
            throw new ArgumentException("A compilation cannot contain a null syntax tree.", nameof(syntaxTrees));
        return new Compilation(trees);
    }

    public ImmutableArray<Diagnostic> GetDiagnostics()
    {
        EnsureAnalyzed();
        return _diagnostics;
    }

    public EmitResult EmitC(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureAnalyzed();
        var success = !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (success)
            writer.Write(_generatedC);
        return new EmitResult(success, _diagnostics);
    }

    private void EnsureAnalyzed()
    {
        if (_analyzed)
            return;
        lock (_gate)
        {
            if (_analyzed)
                return;
            var diagnostics = new DiagnosticBag();
            foreach (var tree in SyntaxTrees)
                diagnostics.AddRange(tree.Diagnostics);
            if (SyntaxTrees.Length == 0)
                diagnostics.Add("CT1000", "A compilation requires at least one source file.", SourceText.From(string.Empty), new TextSpan(0, 0));
            var model = new CompilationModel(SyntaxTrees, diagnostics);
            _generatedC = new CEmitter(model).Emit();
            _diagnostics = diagnostics.ToImmutable();
            _analyzed = true;
        }
    }
}
