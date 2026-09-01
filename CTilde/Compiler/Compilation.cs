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
    private readonly ImmutableArray<SyntaxTree>? _standardLibraryOverride;
    private readonly bool _requireEntryPoint;

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options, ImmutableArray<SyntaxTree>? standardLibraryOverride = null, bool requireEntryPoint = true)
    {
        SyntaxTrees = syntaxTrees;
        Options = options;
        _standardLibraryOverride = standardLibraryOverride;
        _requireEntryPoint = requireEntryPoint;
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

    internal static Compilation CreateStandardLibrary(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options) =>
        new([], options, syntaxTrees, requireEntryPoint: false);

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
            var environment = Enum.IsDefined(Options.Environment) ? Options.Environment : TargetEnvironment.Native;
            var architecture = ResolveArchitecture(target, Options.Architecture);
            var nativeIntegers = SyntaxTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind is SyntaxKind.NintKeyword or SyntaxKind.NuintKeyword or SyntaxKind.SizeofKeyword or SyntaxKind.AlignofKeyword or SyntaxKind.OffsetofKeyword);
            var nativeUtf8 = SyntaxTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind == SyntaxKind.IdentifierToken && token.Text == "NativeUtf8String");
            var hostedIo = StandardLibrary.RequiresHostedIo(SyntaxTrees);
            var vectors = StandardLibrary.RequiredVectors(SyntaxTrees);
            var foundations = StandardLibrary.RequiredFoundations(SyntaxTrees);
            var allSyntaxTrees = (_standardLibraryOverride ?? StandardLibrary.GetSyntaxTrees(target, nativeIntegers, nativeUtf8, hostedIo, vectors, foundations)).AddRange(SyntaxTrees);
            foreach (var tree in allSyntaxTrees)
                diagnostics.AddRange(tree.Diagnostics);
            if (SyntaxTrees.Length == 0 && _standardLibraryOverride is null)
                diagnostics.Add("CT1000", "A compilation requires at least one source file.", SourceText.From(string.Empty), new TextSpan(0, 0));
            if (target != CompilationTarget.EspIdf && Options.PanicPolicy != EspIdfPanicPolicy.Abort)
                diagnostics.Add("CT4113", "Restart and halt panic policies are valid only for ESP-IDF compilations.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            if (environment == TargetEnvironment.Qemu && target != CompilationTarget.EspIdf)
                diagnostics.Add("CT4121", "The QEMU target environment is valid only for ESP-IDF compilations.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            if (target == CompilationTarget.Freestanding && architecture == CompilationArchitecture.Auto)
                diagnostics.Add("CT4108", "Freestanding compilations require an explicit target architecture.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            if (target == CompilationTarget.Cosmopolitan && architecture != CompilationArchitecture.X64)
                diagnostics.Add("CT4118", "Draft 0.25 Cosmopolitan compilations require the explicit x64 architecture.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            if (target == CompilationTarget.Freestanding && (Options.DebugInformation != DebugInformationMode.None || Options.DebugMemory != DebugMemoryMode.Off))
                diagnostics.Add("CT4115", "Debug information and debug-memory instrumentation are unavailable for freestanding compilations.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            ValidateSourceIdentityRoot(diagnostics);
            ValidateSourceOwners(diagnostics);
            ValidateCpuFeatures(diagnostics, target, architecture);
            var sourceRoot = ValidateSourceRoot(diagnostics, target);
            var model = new CompilationModel(allSyntaxTrees, SyntaxTrees, diagnostics, target, architecture, Options.EffectiveCpuFeatures, environment,
                Options.SimdOptimizations,
                _requireEntryPoint, _requireEntryPoint);
            _boundProgram = BoundProgramBuilder.Build(model, Options.Target, architecture, sourceRoot, Options.NoRecursion);
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

    public EmitResult EmitDebugMap(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        EnsureAnalyzed();
        var success = !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (success)
        {
            lock (_gate)
                EnsureGeneratedOutput();
            writer.Write(_generatedOutput!.DebugMap);
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
        var emitter = new CEmitter(_boundProgram!.Model, Options.Target, ResolveArchitecture(Options.Target, Options.Architecture), ValidatedSourceRoot(), Options.DebugInformation, Options.DebugMemory,
            Options.SourceIdentityRoot, Options.PanicPolicy, Options.Environment);
        var ir = new TypedIrLowerer(_boundProgram).Lower();
        var optimizedIr = new TypedIrOptimizer(_boundProgram).Optimize(ir);
        var emissionIr = new TypedIrEmissionLowerer(emitter).Lower(optimizedIr);
        _generatedOutput = emitter.EmitOutput(emissionIr, new CHeaderEmitter(_boundProgram).Emit());
    }

    private static CompilationArchitecture ResolveArchitecture(CompilationTarget target, CompilationArchitecture architecture)
    {
        if (architecture != CompilationArchitecture.Auto)
            return architecture;
        if (target is CompilationTarget.EspIdf or CompilationTarget.Freestanding or CompilationTarget.Cosmopolitan)
            return CompilationArchitecture.Auto;
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => CompilationArchitecture.X86,
            System.Runtime.InteropServices.Architecture.X64 => CompilationArchitecture.X64,
            System.Runtime.InteropServices.Architecture.Arm => CompilationArchitecture.Arm32,
            System.Runtime.InteropServices.Architecture.Arm64 => CompilationArchitecture.Arm64,
            _ => CompilationArchitecture.Auto,
        };
    }

    private string? ValidateSourceRoot(DiagnosticBag diagnostics, CompilationTarget target)
    {
        if (Options.SourceRoot is null)
            return null;

        var source = SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        if (target is not CompilationTarget.Hosted and not CompilationTarget.Cosmopolitan && Options.DebugInformation == DebugInformationMode.None)
        {
            diagnostics.Add("CT4106", "A source root is supported only for hosted or Cosmopolitan targets.", source, new TextSpan(0, 0));
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
        if (Options.SourceRoot is null ||
            (Options.Target is not CompilationTarget.Hosted and not CompilationTarget.Cosmopolitan && Options.DebugInformation == DebugInformationMode.None) ||
            !Path.IsPathFullyQualified(Options.SourceRoot))
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

    private void ValidateSourceIdentityRoot(DiagnosticBag diagnostics)
    {
        if (Options.SourceIdentityRoot is not null && !Path.IsPathFullyQualified(Options.SourceIdentityRoot))
        {
            diagnostics.Add("CT4112", "The source identity root must be an absolute path.", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            return;
        }
        string? root = null;
        try
        {
            if (Options.SourceIdentityRoot is not null)
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.SourceIdentityRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add("CT4112", $"The source identity root is invalid: {exception.Message}", SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty), new TextSpan(0, 0));
            return;
        }
        foreach (var tree in SyntaxTrees.Where(tree => root is not null && Path.IsPathFullyQualified(tree.Text.FilePath)))
        {
            var relative = Path.GetRelativePath(root!, Path.GetFullPath(tree.Text.FilePath));
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                diagnostics.Add("CT4112", $"Source file '{tree.Text.FilePath}' is outside source identity root '{root}'.", tree.Text, new TextSpan(0, 0));
        }
        var identities = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in SyntaxTrees)
        {
            string identity;
            if (string.IsNullOrWhiteSpace(tree.Text.FilePath) || tree.Text.FilePath == "<memory>")
                identity = "<memory>/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tree.Text.Text)));
            else if (root is not null && Path.IsPathFullyQualified(tree.Text.FilePath))
                identity = Path.GetRelativePath(root, Path.GetFullPath(tree.Text.FilePath)).Replace('\\', '/');
            else
                identity = tree.Text.FilePath.Replace('\\', '/');
            if (identities.TryGetValue(identity, out var previous) && !ReferenceEquals(previous, tree))
                diagnostics.Add("CT4112", $"Source identity '{identity}' is declared more than once.", tree.Text, new TextSpan(0, 0), previous.Text.GetLocation(new TextSpan(0, 0)));
            else
                identities[identity] = tree;
        }
    }

    private void ValidateSourceOwners(DiagnosticBag diagnostics)
    {
        foreach (var tree in SyntaxTrees.Where(tree => tree.Origin == SyntaxTreeOrigin.User))
        {
            var owner = tree.SourceOwner;
            if (owner is null)
            {
                diagnostics.Add("CT4119", "A user source file requires a source owner.", tree.Text, new TextSpan(0, 0));
                continue;
            }
            if (string.IsNullOrWhiteSpace(owner.ModulePath))
                diagnostics.Add("CT4119", "A source owner requires a non-empty module path.", tree.Text, new TextSpan(0, 0));
            ValidateOwnerRoot(owner.ContentRoot, "content root", tree, diagnostics);
            ValidateOwnerRoot(owner.SourceIdentityRoot, "source identity root", tree, diagnostics);
            if (owner.IsRootApplication && owner.LockedRevision is not null)
                diagnostics.Add("CT4119", "The root application source owner cannot have a locked revision.", tree.Text, new TextSpan(0, 0));
            if (!owner.IsRootApplication && string.IsNullOrWhiteSpace(owner.LockedRevision))
                diagnostics.Add("CT4119", "A dependency source owner requires an exact locked revision.", tree.Text, new TextSpan(0, 0));
        }
    }

    private void ValidateCpuFeatures(DiagnosticBag diagnostics, CompilationTarget target, CompilationArchitecture architecture)
    {
        var features = Options.EffectiveCpuFeatures;
        var source = SyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        foreach (var feature in features)
            if (!Enum.IsDefined(feature))
                diagnostics.Add("CT4120", $"Unknown CPU feature value '{(int)feature}'.", source, new TextSpan(0, 0));
        if (features.Distinct().Count() != features.Length)
            diagnostics.Add("CT4120", "A CPU feature can be selected only once.", source, new TextSpan(0, 0));
        if (features.Contains(CpuFeature.Simd128) && architecture is not (CompilationArchitecture.X86 or CompilationArchitecture.X64 or CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64))
            diagnostics.Add("CT4120", $"CPU feature 'simd128' is not available for architecture '{architecture}'.", source, new TextSpan(0, 0));
        if (Options.SimdOptimizations && (target != CompilationTarget.Hosted || architecture != CompilationArchitecture.X64))
            diagnostics.Add("CT4122", "SIMD geometry optimizations currently require a hosted x64 compilation.", source, new TextSpan(0, 0));
    }

    private static void ValidateOwnerRoot(string? value, string label, SyntaxTree tree, DiagnosticBag diagnostics)
    {
        if (value is null)
            return;
        if (!Path.IsPathFullyQualified(value))
        {
            diagnostics.Add("CT4119", $"The source-owner {label} must be an absolute path.", tree.Text, new TextSpan(0, 0));
            return;
        }
        try
        {
            _ = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add("CT4119", $"The source-owner {label} is invalid: {exception.Message}", tree.Text, new TextSpan(0, 0));
        }
    }
}
