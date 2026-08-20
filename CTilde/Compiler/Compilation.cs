using System.Collections.Immutable;

namespace CTilde;

public sealed class Compilation
{
    private readonly object _gate = new();
    private ImmutableArray<Diagnostic> _diagnostics;
    private string? _generatedC;
    private string? _generatedHeader;
    private CEmitterOutput? _generatedOutput;
    private BoundProgram? _boundProgram;
    private bool _analyzed;

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options)
    {
        SyntaxTrees = syntaxTrees;
        Options = options;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public CompilationOptions Options { get; }
    public bool UsesInlineAssembly
    {
        get
        {
            EnsureAnalyzed();
            return _boundProgram!.UsesInlineAssembly;
        }
    }

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
                _generatedC ??= GenerateC();
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
            var nativeIntegers = SyntaxTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind is SyntaxKind.NintKeyword or SyntaxKind.NuintKeyword);
            var nativeUtf8 = SyntaxTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind == SyntaxKind.IdentifierToken && token.Text == "NativeUtf8String");
            var hostedIo = target == CompilationTarget.Hosted && StandardLibrary.RequiresHostedIo(SyntaxTrees);
            var vectors = StandardLibrary.RequiredVectors(SyntaxTrees);
            var allSyntaxTrees = StandardLibrary.GetSyntaxTrees(target, nativeIntegers, nativeUtf8, hostedIo, vectors).AddRange(SyntaxTrees);
            foreach (var tree in allSyntaxTrees)
                diagnostics.AddRange(tree.Diagnostics);
            if (SyntaxTrees.Length == 0)
                diagnostics.Add("CT1000", "A compilation requires at least one source file.", SourceText.From(string.Empty), new TextSpan(0, 0));
            var sourceRoot = ValidateSourceRoot(diagnostics, target);
            var model = new CompilationModel(allSyntaxTrees, SyntaxTrees, diagnostics, target);
            _boundProgram = BoundProgramBuilder.Build(model, Options.Target, sourceRoot);
            _diagnostics = diagnostics.ToImmutable();
            _analyzed = true;
        }
    }

    public EmitResult EmitCHeader(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureAnalyzed();
        var success = !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (success)
        {
            lock (_gate)
                _generatedHeader ??= new CHeaderEmitter(_boundProgram!).Emit();
            writer.Write(_generatedHeader);
        }
        return new EmitResult(success, _diagnostics);
    }

    public CBundleEmitResult EmitCBundle()
    {
        EnsureAnalyzed();
        var success = !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (!success)
            return new CBundleEmitResult(false, [], _diagnostics);
        lock (_gate)
            EnsureGeneratedOutput();
        return new CBundleEmitResult(true, _generatedOutput!.Artifacts, _diagnostics);
    }

    public EmitResult EmitSymbolMap(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureAnalyzed();
        var success = !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (success)
        {
            lock (_gate)
                EnsureGeneratedOutput();
            writer.Write(_generatedOutput!.SymbolMap);
        }
        return new EmitResult(success, _diagnostics);
    }

    private string GenerateC()
    {
        EnsureGeneratedOutput();
        return _generatedOutput!.Unity;
    }

    private void EnsureGeneratedOutput()
    {
        if (_generatedOutput is not null)
            return;
        var emitter = new CEmitter(_boundProgram!.Model, Options.Target, ValidatedSourceRoot());
        var ir = new TypedIrLowerer(_boundProgram).Lower();
        var optimizedIr = new TypedIrOptimizer(_boundProgram).Optimize(ir);
        var emissionIr = new TypedIrEmissionLowerer(emitter).Lower(optimizedIr);
        _generatedOutput = emitter.EmitOutput(emissionIr, new CHeaderEmitter(_boundProgram).Emit());
    }

    private string? ValidateSourceRoot(DiagnosticBag diagnostics, CompilationTarget target)
    {
        if (Options.SourceRoot is null)
            return null;

        var source = SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        if (target != CompilationTarget.Hosted)
        {
            diagnostics.Add("CT4106", "A source root is supported only for the hosted target.", source, new TextSpan(0, 0));
            return null;
        }
        if (!Path.IsPathFullyQualified(Options.SourceRoot))
        {
            diagnostics.Add("CT4106", "The source root must be an absolute path.", source, new TextSpan(0, 0));
            return null;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.SourceRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add("CT4106", $"The source root is invalid: {exception.Message}", source, new TextSpan(0, 0));
            return null;
        }
        foreach (var tree in SyntaxTrees.Where(tree => Path.IsPathFullyQualified(tree.Text.FilePath)))
        {
            var path = Path.GetFullPath(tree.Text.FilePath);
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            {
                diagnostics.Add("CT4106", $"Source file '{tree.Text.FilePath}' is outside source root '{root}'.", tree.Text, new TextSpan(0, 0));
                return null;
            }
        }
        return root;
    }

    private string? ValidatedSourceRoot()
    {
        if (Options.SourceRoot is null || Options.Target != CompilationTarget.Hosted || !Path.IsPathFullyQualified(Options.SourceRoot))
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.SourceRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
