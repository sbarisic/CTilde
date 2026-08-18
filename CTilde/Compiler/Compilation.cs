using System.Collections.Immutable;

namespace CTilde;

public sealed class Compilation
{
    private readonly object _gate = new();
    private ImmutableArray<Diagnostic> _diagnostics;
    private string? _generatedC;
    private CEmitter? _emitter;
    private TypedIrProgram? _ir;
    private bool _analyzed;

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options)
    {
        SyntaxTrees = syntaxTrees;
        Options = options;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public CompilationOptions Options { get; }

    public static Compilation Create(IEnumerable<SyntaxTree> syntaxTrees, CompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var trees = syntaxTrees.ToImmutableArray();
        if (trees.Any(tree => tree is null))
            throw new ArgumentException("A compilation cannot contain a null syntax tree.", nameof(syntaxTrees));
        return new Compilation(trees, options ?? new CompilationOptions());
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
        {
            lock (_gate)
                _generatedC ??= _emitter!.Emit(_ir!);
            writer.Write(_generatedC);
        }
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
            var target = Enum.IsDefined(Options.Target) ? Options.Target : CompilationTarget.Hosted;
            var allSyntaxTrees = StandardLibrary.GetSyntaxTrees(target).AddRange(SyntaxTrees);
            foreach (var tree in allSyntaxTrees)
                diagnostics.AddRange(tree.Diagnostics);
            if (SyntaxTrees.Length == 0)
                diagnostics.Add("CT1000", "A compilation requires at least one source file.", SourceText.From(string.Empty), new TextSpan(0, 0));
            var model = new CompilationModel(allSyntaxTrees, SyntaxTrees, diagnostics);
            _emitter = new CEmitter(model, Options.Target);
            _ir = new TypedIrLowerer(model, _emitter).Lower();
            TargetValidator.Validate(model, _emitter, Options.Target);
            _diagnostics = diagnostics.ToImmutable();
            _analyzed = true;
        }
    }
}
