using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CTilde;

internal sealed record CEmitterOutput(
    string Unity,
    ImmutableArray<GeneratedCArtifact> Artifacts,
    string SymbolMap,
    string DebugMap);

internal sealed record DebugLocalEntry(
    MethodSymbol Method,
    string Name,
    string Storage,
    string Type,
    bool Durable,
    string File,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength,
    int LiveStart,
    int? LiveEnd);

internal sealed record DebugSiteEntry(
    int Id,
    MethodSymbol Method,
    string Kind,
    string File,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength);

internal sealed partial class CEmitter : ILoweringServices
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _inlineArrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<CType> _functionPointerTypes = [];
    private readonly HashSet<CType> _nativeBufferTypes = [];
    private readonly HashSet<string> _usedMathSymbols = new(StringComparer.Ordinal);
    private readonly HashSet<TypeSymbol> _synchronousDelegateTypes = [];
    private readonly HashSet<string> _emittedThunks = new(StringComparer.Ordinal);
    private readonly Dictionary<(TypeSymbol DelegateType, MethodSymbol Method, bool VirtualDispatch), string> _delegateThunks = [];
    private readonly Dictionary<(CType Type, MethodSymbol Method), string> _functionPointerTrampolines = [];
    private readonly Dictionary<MethodSymbol, (ImmutableArray<KeyValuePair<string, CType>> Fields, ImmutableArray<DirectDeferThunk> Thunks)> _directDeferStates = [];
    private readonly List<(MethodSymbol Method, SyntaxNode Syntax)> _externUses = [];
    private readonly Dictionary<(PropertySymbol Property, bool Getter), MethodSymbol> _accessorMethods = [];
    private readonly CompilationTarget _target;
    private readonly CompilationArchitecture _architecture;
    private readonly string? _sourceRoot;
    private readonly string? _sourceIdentityRoot;
    private readonly EspIdfPanicPolicy _panicPolicy;
    private readonly DebugInformationMode _debugInformation;
    private readonly DebugMemoryMode _debugMemory;
    private readonly List<DebugLocalEntry> _debugLocals = [];
    private readonly Dictionary<MethodSymbol, HashSet<(string File, int Line, int Column, int Start, int Length)>> _debugExecutable = [];
    private readonly List<DebugSiteEntry> _debugSites = [];
    private readonly Dictionary<(MethodSymbol Method, int Start, int Length, string Kind), int> _debugSiteIds = [];
    private bool _usesExceptions;
    private bool _usesHostedIo;
    private bool _usesNativeIntegers;
    private bool _usesNativeUtf8;
    private bool _usesManagedThreading;
    private ImmutableHashSet<MethodSymbol> _reachableMethods = ImmutableHashSet<MethodSymbol>.Empty;
    private ImmutableHashSet<PropertySymbol> _reachableProperties = ImmutableHashSet<PropertySymbol>.Empty;

    public CEmitter(CompilationModel model, CompilationTarget target, CompilationArchitecture architecture, string? sourceRoot = null,
        DebugInformationMode debugInformation = DebugInformationMode.None,
        DebugMemoryMode debugMemory = DebugMemoryMode.Off,
        string? sourceIdentityRoot = null,
        EspIdfPanicPolicy panicPolicy = EspIdfPanicPolicy.Abort)
    {
        Model = model;
        Diagnostics = model.Diagnostics;
        _target = target;
        _architecture = architecture;
        _sourceRoot = sourceRoot;
        _sourceIdentityRoot = sourceIdentityRoot;
        _panicPolicy = panicPolicy;
        _debugInformation = debugInformation;
        _debugMemory = debugMemory;
        _usesExceptions = target != CompilationTarget.Freestanding;
        foreach (var type in model.Types.Values)
        {
            foreach (var field in type.Fields)
                RegisterType(field.Type);
            foreach (var property in type.Properties)
                RegisterType(property.Type);
            foreach (var method in type.Methods.Concat(type.Constructors))
            {
                RegisterType(method.ReturnType);
                foreach (var parameter in method.Parameters)
                {
                    RegisterType(parameter.Type);
                    if (parameter.IsSynchronousCallback && parameter.Type.Symbol is not null)
                    {
                        _synchronousDelegateTypes.Add(parameter.Type.Symbol);
                        _usesExceptions = true;
                    }
                }
            }
        }
        if (target != CompilationTarget.Freestanding && model.UserTypes.SelectMany(type => type.Methods).Any(method => method.ExportName is not null))
            _usesExceptions = true;
    }

    public CEmitter(CompilationModel model, CompilationTarget target, string? sourceRoot = null,
        DebugInformationMode debugInformation = DebugInformationMode.None,
        DebugMemoryMode debugMemory = DebugMemoryMode.Off)
        : this(model, target, CompilationArchitecture.Auto, sourceRoot, debugInformation, debugMemory)
    {
    }

    public CompilationTarget Target => _target;
    public CompilationArchitecture Architecture => _architecture;

    public CompilationModel Model { get; }
    public DiagnosticBag Diagnostics { get; }
    public AllocationEffectRegistry AllocationEffects { get; } = new();
    public IEnumerable<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses => _externUses;
    public bool EmitDebugInformation => _debugInformation != DebugInformationMode.None;
    public bool EmitDebugInstrumentation => _debugInformation == DebugInformationMode.Instrumented;
    private bool EmitDebugObjects => EmitDebugInstrumentation && _debugMemory != DebugMemoryMode.Off;
    private bool EmitDebugGuards => EmitDebugInstrumentation && _debugMemory == DebugMemoryMode.Guarded;
    private bool IsEspIdf => _target == CompilationTarget.EspIdf;
    private bool IsFreestanding => _target == CompilationTarget.Freestanding;

    private string SourceIdentity(MethodSymbol method)
    {
        var path = method.Syntax?.Source.FilePath;
        if (method.Syntax is null)
            return "<generated>/" + Hash96(NameMangler.MethodIdentity(method));
        if (string.IsNullOrWhiteSpace(path) || path == "<memory>")
            return "<memory>/" + Hash96(method.Syntax.Source.Text);
        try
        {
            if (_sourceIdentityRoot is not null && Path.IsPathFullyQualified(path))
            {
                var relative = Path.GetRelativePath(_sourceIdentityRoot, path);
                if (relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative))
                    path = relative;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Invalid roots are diagnosed before emission; retain the logical path here.
        }
        return path.Replace('\\', '/');
    }
    private bool HasExports => _reachableMethods.Any(method => method.ExportName is not null);

    public IEnumerable<string> DynamicGeneratedSymbols =>
        _arrayTypes.SelectMany(type => new[] { NameMangler.Array(type.ElementType!), $"ct_new_{NameMangler.Array(type.ElementType!)}" })
            .Concat(_arrayTypes.Select(type => ArrayDescriptorName(type.ElementType!)))
            .Concat(_stringLiterals.Values.Select(id => $"ct_sl_{id}"))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class)
                .SelectMany(type => new[] { DescriptorName(type), VTableName(type) }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate)
                .SelectMany(type => new[] { DescriptorName(type), DelegateFactoryName(type), DelegateDropName(type) }))
            .Concat(_delegateThunks.Values)
            .Concat(_synchronousDelegateTypes.Select(SynchronousCallbackAdapterName))
            .Concat(_functionPointerTrampolines.Values)
            .Concat(Model.UserTypes.SelectMany(type => type.Constructors).Select(ConstructorInitializerName))
            .Concat(Model.UserTypes.SelectMany(type => type.Methods)
                .Where(method => method.IsVirtual && !method.ContainingType.IsObject)
                .Select(VirtualMethodThunkName))
            .Concat(Model.UserTypes.SelectMany(type => type.Properties)
                .Where(property => property.IsVirtual)
                .SelectMany(property => new[]
                {
                    VirtualPropertyThunkName(property, true),
                    VirtualPropertyThunkName(property, false),
                }))
            .Concat(BoxedTypes.SelectMany(type =>
            {
                var code = NameMangler.TypeCode(type);
                return new[]
                {
                    BoxName(type), BoxDescriptorName(type), BoxFunctionName(type), UnboxFunctionName(type),
                    $"ct_vtable_box_{code}", $"ct_box_to_string_{code}", $"ct_box_equals_{code}",
                    $"ct_box_hash_{code}", $"ct_enum_to_string_{code}",
                };
            }));

    public IEnumerable<CType> BoxedTypes => _boxedTypes.OrderBy(NameMangler.TypeCode, StringComparer.Ordinal);

    public void RegisterExceptions() => _usesExceptions = true;

    public MethodSymbol GetAccessorMethod(PropertySymbol property, bool getter)
    {
        if (_accessorMethods.TryGetValue((property, getter), out var method))
            return method;
        var syntax = getter ? property.Getter! : property.Setter!;
        var parameters = getter ? Array.Empty<ParameterSymbol>() : [new ParameterSymbol { Name = "value", Type = property.Type, Syntax = null }];
        method = new MethodSymbol
        {
            Name = getter ? $"get_{property.Name}" : $"set_{property.Name}",
            ContainingType = property.ContainingType,
            Accessibility = property.Accessibility,
            IsStatic = property.IsStatic,
            Syntax = syntax,
            ReturnType = getter ? property.Type : CType.Void,
            Parameters = [.. parameters],
            Body = syntax.Body,
            IsNoAlloc = property.IsNoAlloc,
            IsNoRecursion = property.IsNoRecursion,
            IsUnsafe = property.Syntax is PropertyDeclarationSyntax propertySyntax && propertySyntax.Modifiers.Contains("unsafe", StringComparer.Ordinal),
            IsVirtual = property.IsVirtual,
            IsOverride = property.IsOverride,
            IsSealedOverride = property.IsSealedOverride,
        };
        _accessorMethods.Add((property, getter), method);
        return method;
    }

    public void RegisterExternUse(MethodSymbol method, SyntaxNode syntax)
    {
        if (method.ExternName is not null)
        {
            _externUses.Add((method, syntax));
            if (IsHostedIoSymbol(method.ExternName))
            {
                _usesHostedIo = true;
                _usesExceptions = true;
            }
            if (IsMathSymbol(method.ExternName))
                _usedMathSymbols.Add(method.ExternName);
        }
    }

    public string Emit(TypedIrProgram program) => EmitOutput(program, string.Empty).Unity;

    public CEmitterOutput EmitOutput(TypedIrProgram program, string runtimeHeader)
    {
        _reachableMethods = program.Functions.Select(function => function.Method).ToImmutableHashSet();
        _reachableProperties = program.Functions.Where(function => function.Property is not null)
            .Select(function => function.Property!).ToImmutableHashSet();
        ComputeReachableTypes(program);
        _usesManagedThreading = EmittedTypes.Any(type => type is { Namespace: "System.Threading", Name: "Thread" or "Mutex" });
        RegisterDeclaredTypes();
        var definitions = program.Functions.Select(function => (Function: function, Text: RenderFunction(function))).ToImmutableArray();
        if (IsFreestanding && !Model.FreestandingRuntimeRequired)
            return EmitNakedOnly(program, definitions, runtimeHeader);
        var moduleLifecycle = RenderModuleLifecycle(program.ModuleInitializers);
        var prefix = new CWriter();
        EmitPreamble(prefix);
        EmitSectionSupport(prefix);
        EmitStringLiterals(prefix);
        EmitForwardDeclarations(prefix);
        EmitTypeLayouts(prefix);
        EmitArrayLayouts(prefix);
        EmitBoxLayouts(prefix);
        EmitCompileTimeAssertions(prefix);
        EmitGlobals(prefix);
        EmitOwnershipHelpers(prefix);
        EmitPrototypes(prefix);
        EmitRuntimeImplementationBridges(prefix);
        EmitUsedAnchors(prefix);
        EmitObjectMetadata(prefix);
        EmitRuntimeFaultSupport(prefix);
        EmitScalarAtomicSupport(prefix);
        EmitManagedThreadingSupport(prefix);
        EmitMathSupport(prefix);
        EmitHostedIoSupport(prefix);
        EmitDelegateSupport(prefix);
        EmitSynchronousDelegateAdapters(prefix);
        EmitFunctionPointerTrampolines(prefix);
        EmitDirectDeferSupport(prefix);
        EmitMemoryLayoutProbe(prefix);
        prefix.WriteLine();

        var suffix = new CWriter();
        suffix.WriteBlock(moduleLifecycle.TrimEnd().Split('\n'));
        suffix.WriteLine();
        EmitExports(suffix);
        if (HasExports)
            suffix.WriteLine();
        EmitMain(suffix);

        var modularEntry = new CWriter();
        modularEntry.WriteBlock(moduleLifecycle.TrimEnd().Split('\n'));
        modularEntry.WriteLine();
        EmitMain(modularEntry);

        var externalRoots = new StringBuilder(suffix.ToString());
        foreach (var definition in definitions)
            externalRoots.Append('\n').Append(definition.Text);
        var prunedPrefix = MarkUnusedGeneratedFields(PruneRuntimeHelpers(prefix.ToString(), externalRoots.ToString()));

        var writer = new CWriter();
        writer.WriteBlock(prunedPrefix.TrimEnd().Split('\n'));
        writer.WriteLine();
        foreach (var definition in definitions)
        {
            writer.WriteBlock(MarkUnusedDefinitions(definition.Text).TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        writer.WriteBlock(MarkUnusedDefinitions(suffix.ToString()).TrimEnd().Split('\n'));
        var unity = writer.ToString();
        var symbolMap = EmitSymbolMapJson(program);
        var debugMap = EmitDebugInformation ? EmitDebugMapJson(program) : string.Empty;
        var artifacts = BuildModularArtifacts(prunedPrefix, modularEntry.ToString(), definitions, runtimeHeader, symbolMap, debugMap);
        return new CEmitterOutput(unity, artifacts, symbolMap, debugMap);
    }

    private CEmitterOutput EmitNakedOnly(
        TypedIrProgram program,
        ImmutableArray<(IrFunction Function, string Text)> definitions,
        string runtimeHeader)
    {
        var prefix = new CWriter();
        prefix.WriteLine($"/* Generated by C~ draft {CompilerContract.DraftVersion} for freestanding GNU/ELF C23. Do not edit. */");
        EmitSectionSupport(prefix);
        var writer = new CWriter();
        writer.WriteBlock(prefix.ToString().TrimEnd().Split('\n'));
        writer.WriteLine();
        foreach (var definition in definitions)
        {
            writer.WriteBlock(definition.Text.TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        var symbolMap = EmitSymbolMapJson(program);
        var artifacts = BuildModularArtifacts(prefix.ToString(), string.Empty, definitions, runtimeHeader, symbolMap, string.Empty);
        return new CEmitterOutput(writer.ToString(), artifacts, symbolMap, string.Empty);
    }

    private void EmitUsedAnchors(CWriter writer)
    {
        var methods = _reachableMethods.Where(method => method.IsUsed).OrderBy(method => method.CName, StringComparer.Ordinal).ToArray();
        var fields = EmittedTypes.SelectMany(type => type.Fields).Where(field => field.IsUsed && field.ExternName is null).OrderBy(field => field.CName, StringComparer.Ordinal).ToArray();
        if (methods.Length == 0 && fields.Length == 0)
            return;
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("#if defined(_M_IX86)");
        writer.WriteLine("#define CT_FORCE_INCLUDE(name) __pragma(comment(linker, \"/include:_\" #name))");
        writer.WriteLine("#else");
        writer.WriteLine("#define CT_FORCE_INCLUDE(name) __pragma(comment(linker, \"/include:\" #name))");
        writer.WriteLine("#endif");
        foreach (var method in methods)
        {
            writer.WriteLine($"CT_FORCE_INCLUDE({method.CName})");
            if (method.ExportName is not null)
                writer.WriteLine($"CT_FORCE_INCLUDE({method.ExportName})");
        }
        foreach (var field in fields)
            writer.WriteLine($"CT_FORCE_INCLUDE({field.CName})");
        writer.WriteLine("#undef CT_FORCE_INCLUDE");
        writer.WriteLine("#endif");
        writer.WriteLine();
    }

    private void EmitCompileTimeAssertions(CWriter writer)
    {
        foreach (var assertion in Model.StaticAssertions.OrderBy(assertion => assertion.Syntax.Source.FilePath, StringComparer.Ordinal)
                     .ThenBy(assertion => assertion.Syntax.Span.Start))
        {
            var message = ("CT2201: " + assertion.Message).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            writer.WriteLine($"static_assert({assertion.ConditionCode}, \"{message}\");");
        }
        if (Model.StaticAssertions.Count != 0)
            writer.WriteLine();
    }

    private ImmutableArray<GeneratedCArtifact> BuildModularArtifacts(
        string prefix,
        string suffix,
        ImmutableArray<(IrFunction Function, string Text)> definitions,
        string runtimeHeader,
        string symbolMap,
        string debugMap)
    {
        var artifacts = ImmutableArray.CreateBuilder<GeneratedCArtifact>();
        var internalHeader = BuildInternalHeader(prefix);
        artifacts.Add(new GeneratedCArtifact("ctilde_runtime.h", runtimeHeader, GeneratedCArtifactKind.RuntimeHeader));
        artifacts.Add(new GeneratedCArtifact("ctilde_internal.h", internalHeader, GeneratedCArtifactKind.InternalHeader));
        artifacts.Add(new GeneratedCArtifact("ctilde_runtime.c", ExternalizeDefinitions(prefix, runtimeUnit: true), GeneratedCArtifactKind.RuntimeSource));

        foreach (var group in definitions.GroupBy(definition => SourceIdentity(definition.Function.Method), StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var name = "source_" + Hash96(group.Key) + ".c";
            var contents = new StringBuilder();
            contents.Append($"/* Generated by C~ draft {CompilerContract.DraftVersion}. Do not edit. */\n#include \"ctilde_internal.h\"\n\n");
            foreach (var definition in group.OrderBy(item => item.Function.Method.CName, StringComparer.Ordinal))
                contents.Append(ExternalizeDefinitions(MarkUnusedDefinitions(definition.Text), runtimeUnit: false)).Append('\n');
            foreach (var export in _reachableMethods.Where(method => method.ExportName is not null && SourceIdentity(method) == group.Key)
                         .OrderBy(method => method.ExportName, StringComparer.Ordinal))
            {
                var exportWriter = new CWriter();
                EmitExports(exportWriter, [export]);
                contents.Append(ExternalizeDefinitions(MarkUnusedDefinitions(exportWriter.ToString()), runtimeUnit: false)).Append('\n');
            }
            artifacts.Add(new GeneratedCArtifact(name, contents.ToString(), GeneratedCArtifactKind.NamespaceSource));
        }

        var entry = $"/* Generated by C~ draft {CompilerContract.DraftVersion}. Do not edit. */\n#include \"ctilde_internal.h\"\n\n" +
            ExternalizeDefinitions(MarkUnusedDefinitions(suffix), runtimeUnit: false);
        artifacts.Add(new GeneratedCArtifact("ctilde_entry.c", entry, GeneratedCArtifactKind.EntrySource));
        artifacts.Add(new GeneratedCArtifact("ctilde_symbols.json", symbolMap, GeneratedCArtifactKind.SymbolMap));
        if (EmitDebugInformation)
            artifacts.Add(new GeneratedCArtifact("ctilde_debug.json", debugMap, GeneratedCArtifactKind.DebugMap));

        var sources = artifacts.Where(artifact => artifact.Kind is GeneratedCArtifactKind.RuntimeSource or GeneratedCArtifactKind.NamespaceSource or GeneratedCArtifactKind.EntrySource)
            .Select(artifact => artifact.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var cmake = new StringBuilder($"# Generated by C~ draft {CompilerContract.DraftVersion}. Do not edit.\nset(CTILDE_GENERATED_SOURCES\n");
        foreach (var source in sources)
            cmake.Append("    ${CMAKE_CURRENT_LIST_DIR}/").Append(source).Append('\n');
        cmake.Append(")\n");
        artifacts.Add(new GeneratedCArtifact("ctilde_sources.cmake", cmake.ToString(), GeneratedCArtifactKind.CMakeFragment));
        return artifacts.OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal).ToImmutableArray();
    }

    private static string BuildInternalHeader(string prefix)
    {
        var guard = "CTILDE_INTERNAL_DRAFT_" + CompilerContract.DraftVersion.Replace(".", string.Empty, StringComparison.Ordinal).PadLeft(3, '0') + "_H";
        var writer = new StringBuilder($"#ifndef {guard}\n#define {guard}\n\n");
        var skipInitializer = false;
        var skipFunction = false;
        var skipFunctionDepth = 0;
        foreach (var sourceLine in prefix.Split('\n'))
        {
            var line = sourceLine.TrimEnd('\r');
            if (skipFunction)
            {
                skipFunctionDepth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (skipFunctionDepth <= 0 && line.Contains('}'))
                    skipFunction = false;
                continue;
            }
            if (skipInitializer)
            {
                if (line.TrimEnd().EndsWith("};", StringComparison.Ordinal))
                    skipInitializer = false;
                continue;
            }

            if (line.StartsWith("ct_debug_control_block ct_debug_control =", StringComparison.Ordinal))
            {
                writer.Append("extern ct_debug_control_block ct_debug_control;\n");
                continue;
            }
            if (line.Equals("ct_debug_runtime_summary_block ct_debug_runtime_summary;", StringComparison.Ordinal))
            {
                writer.Append("extern ct_debug_runtime_summary_block ct_debug_runtime_summary;\n");
                continue;
            }
            if (line.StartsWith("const ct_type_descriptor ", StringComparison.Ordinal) && line.Contains('='))
            {
                writer.Append("extern ").Append(line.AsSpan(0, line.IndexOf('=')).TrimEnd()).Append(";\n");
                continue;
            }
            if (line.Equals("static inline void ct_mmio_barrier(void)", StringComparison.Ordinal))
            {
                writer.Append(line).Append('\n');
                continue;
            }

            var declaration = RemoveInternalLinkage(line);
            if (declaration is not null)
            {
                declaration = NativeSection.StripDataDefinitionMacro(declaration);
                if (LooksLikeFunctionDeclaration(declaration))
                {
                    var openBrace = declaration.IndexOf('{');
                    var signature = openBrace >= 0 ? declaration[..openBrace].TrimEnd() : declaration.TrimEnd();
                    writer.Append("extern ").Append(signature.TrimEnd(';')).Append(";\n");
                    var opens = declaration.Count(character => character == '{');
                    var closes = declaration.Count(character => character == '}');
                    var isDefinition = openBrace >= 0 || !declaration.EndsWith(';');
                    if (isDefinition && (opens == 0 || opens > closes))
                    {
                        skipFunction = true;
                        skipFunctionDepth = opens - closes;
                    }
                    continue;
                }
                var equals = declaration.IndexOf('=');
                var openParenthesis = declaration.IndexOf('(');
                var initializerBrace = declaration.IndexOf('{');
                var isVariableInitializer = equals >= 0 && (initializerBrace < 0 || equals < initializerBrace || declaration.StartsWith("struct ", StringComparison.Ordinal));
                if (isVariableInitializer)
                {
                    writer.Append("extern ").Append(declaration.AsSpan(0, equals).TrimEnd()).Append(";\n");
                    if (!line.TrimEnd().EndsWith(';'))
                        skipInitializer = true;
                    continue;
                }
                var isTentativeVariable = declaration.EndsWith(';') && openParenthesis < 0;
                if (isTentativeVariable)
                {
                    writer.Append("extern ").Append(declaration).Append('\n');
                    continue;
                }
            }
            else if (LooksLikePublicFunction(line))
            {
                var openBrace = line.IndexOf('{');
                var signature = openBrace >= 0 ? line[..openBrace].TrimEnd() : line.TrimEnd();
                writer.Append("extern ").Append(signature.TrimEnd(';')).Append(";\n");
                var opens = line.Count(character => character == '{');
                var closes = line.Count(character => character == '}');
                if (opens == 0 || opens > closes)
                {
                    skipFunction = true;
                    skipFunctionDepth = opens - closes;
                }
                continue;
            }

            writer.Append(line).Append('\n');
        }
        writer.Append("\n#endif\n");
        return writer.ToString();
    }

    private static string? RemoveInternalLinkage(string line)
    {
        const string noReturnMarked = "CT_NORETURN static CT_UNUSED ";
        const string noReturn = "CT_NORETURN static ";
        const string marked = "static CT_UNUSED ";
        const string plain = "static ";
        if (line.StartsWith(noReturnMarked, StringComparison.Ordinal))
            return "CT_NORETURN " + line[noReturnMarked.Length..];
        if (line.StartsWith(noReturn, StringComparison.Ordinal))
            return "CT_NORETURN " + line[noReturn.Length..];
        if (line.StartsWith(marked, StringComparison.Ordinal))
            return line[marked.Length..];
        return line.StartsWith(plain, StringComparison.Ordinal) ? line[plain.Length..] : null;
    }

    private static bool LooksLikeFunctionDeclaration(string declaration)
    {
        var match = RuntimeIdentifierPattern.Match(SanitizeGeneratedC(declaration));
        while (match.Success)
        {
            var next = match.Index + match.Length;
            while (next < declaration.Length && char.IsWhiteSpace(declaration[next]))
                next++;
            if (next < declaration.Length && declaration[next] == '(')
            {
                var equals = declaration.IndexOf('=');
                return equals < 0 || match.Index < equals;
            }
            match = match.NextMatch();
        }
        return false;
    }

    private static bool LooksLikePublicFunction(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line.StartsWith("typedef ", StringComparison.Ordinal) ||
            line.StartsWith("struct ", StringComparison.Ordinal) || line.StartsWith("enum ", StringComparison.Ordinal) ||
            line.StartsWith("static", StringComparison.Ordinal) || line.StartsWith("CT_NORETURN static", StringComparison.Ordinal))
            return false;
        var open = line.IndexOf('(');
        return open > 0 && !line.EndsWith(';');
    }

    private static string ExternalizeDefinitions(string source, bool runtimeUnit)
    {
        var writer = new StringBuilder();
        foreach (var sourceLine in source.Split('\n'))
        {
            var line = sourceLine.TrimEnd('\r');
            if (runtimeUnit && line.Contains("ct_module_descriptor ct_program_module;", StringComparison.Ordinal))
            {
                writer.Append("extern ct_module_descriptor ct_program_module;\n");
                continue;
            }
            line = line.Replace("CT_NORETURN static CT_UNUSED ", "CT_NORETURN ", StringComparison.Ordinal)
                .Replace("CT_NORETURN static ", "CT_NORETURN ", StringComparison.Ordinal);
            if (line.StartsWith("CT_DEBUG_USER_NOINLINE static ", StringComparison.Ordinal))
                line = "CT_DEBUG_USER_NOINLINE " + line["CT_DEBUG_USER_NOINLINE static ".Length..];
            else if (line.StartsWith("static CT_UNUSED ", StringComparison.Ordinal))
                line = line["static CT_UNUSED ".Length..];
            else if (line.StartsWith("static ", StringComparison.Ordinal))
                line = line["static ".Length..];
            writer.Append(line).Append('\n');
        }
        return writer.ToString();
    }

    private string EmitSymbolMapJson(TypedIrProgram program)
    {
        var symbols = new List<object>();
        foreach (var type in EmittedTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var entry = SymbolMapEntry(NameMangler.Type(type), NameMangler.TypeIdentity(type), "type", type.FullName, type.Syntax);
            entry["bitFieldBackingType"] = type.BitFieldBackingType?.DisplayName;
            entry["bitViews"] = type.Fields.Where(field => field.IsBitView)
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => new Dictionary<string, object?>
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type.DisplayName,
                    ["first"] = field.BitFirst,
                    ["last"] = field.BitLast,
                    ["readonly"] = field.IsReadonly,
                }).ToArray();
            symbols.Add(entry);
        }
        foreach (var field in EmittedTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic).OrderBy(field => NameMangler.Member(field), StringComparer.Ordinal))
        {
            var entry = SymbolMapEntry(NameMangler.Member(field), NameMangler.MemberIdentity(field), "field", field.Type.DisplayName, field.Syntax);
            entry["used"] = field.IsUsed;
            entry["linkerRetained"] = field.IsUsed;
            entry["linkerSymbol"] = field.LinkerSymbolName;
            entry["registerAddress"] = field.RegisterAddress?.ToString(CultureInfo.InvariantCulture);
            symbols.Add(entry);
        }
        foreach (var function in program.Functions.OrderBy(function => function.Method.CName, StringComparer.Ordinal))
        {
            var method = function.Method;
            if (method.ExternName is not null)
                continue;
            var cName = function.Property is null ? method.IsNaked ? method.ExportName! : method.CName : function.IsGetter ? NameMangler.Getter(function.Property) : NameMangler.Setter(function.Property);
            var identity = function.Property is null ? NameMangler.MethodIdentity(method) : NameMangler.PropertyIdentity(function.Property, function.IsGetter);
            var entry = SymbolMapEntry(cName, identity, function.Property is null ? "method" : function.IsGetter ? "getter" : "setter",
                NameMangler.CanonicalType(method.ReturnType), method.Syntax);
            entry["used"] = method.IsUsed;
            entry["linkerRetained"] = method.IsUsed;
            entry["runtimeRole"] = method.RuntimeImplementation?.ToString();
            entry["runtimeRequired"] = method.RuntimeImplementation switch
            {
                RuntimeImplementationRole.Panic => Model.FreestandingRuntimeRequired,
                RuntimeImplementationRole.Allocate or RuntimeImplementationRole.Free => Model.FreestandingHeapRequired,
                _ => false,
            };
            entry["naked"] = method.IsNaked;
            symbols.Add(entry);
        }

        var ordered = symbols.Cast<Dictionary<string, object?>>().OrderBy(entry => (string)entry["name"]!, StringComparer.Ordinal).ToArray();
        var collisions = ordered.GroupBy(entry => (string)entry["name"]!, StringComparer.Ordinal)
            .Where(group => group.Select(entry => (string)entry["identity"]!).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .ToArray();
        if (collisions.Length != 0)
            throw new InvalidOperationException($"Generated C symbol collision for '{collisions[0].Key}'.");
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["generator"] = $"C~ draft {CompilerContract.DraftVersion}",
            ["version"] = 1,
            ["runtimeAbi"] = CompilerContract.RuntimeAbiVersion,
            ["symbols"] = ordered,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private string EmitDebugMapJson(TypedIrProgram program)
    {
        var functions = program.Functions
            .Where(function => function.Method.ExternName is null)
            .Select(function =>
            {
                var method = function.Method;
                var cName = function.Property is null
                    ? method.CName
                    : function.IsGetter ? NameMangler.Getter(function.Property) : NameMangler.Setter(function.Property);
                var scopeSource = method.Body ?? method.Syntax;
                SyntaxNode[] scopeNodes = scopeSource is null
                    ? []
                    : DebugDescendantNodes(scopeSource).OfType<BlockStatementSyntax>()
                        .OrderBy(scope => scope.Span.Start).ThenByDescending(scope => scope.Span.Length).Cast<SyntaxNode>().ToArray();
                if (scopeNodes.Length == 0 && scopeSource is not null)
                    scopeNodes = [scopeSource];
                var scopes = scopeNodes.Select((scope, id) =>
                {
                    var parent = scopeNodes.Select((candidate, candidateId) => (candidate, candidateId))
                        .Where(candidate => candidate.candidateId != id && candidate.candidate.Span.Start <= scope.Span.Start && candidate.candidate.Span.End >= scope.Span.End)
                        .OrderBy(candidate => candidate.candidate.Span.Length).FirstOrDefault();
                    return new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["parent"] = parent.candidate is null ? null : parent.candidateId,
                        ["source"] = DebugSourceEntry(scope),
                    };
                }).ToArray();
                var locals = _debugLocals.Where(local => ReferenceEquals(local.Method, method))
                    .OrderBy(local => local.SpanStart)
                    .ThenBy(local => local.Storage, StringComparer.Ordinal)
                    .Select(local =>
                    {
                        var containing = scopeNodes.Select((scope, id) => (scope, id))
                            .Where(candidate => candidate.scope.Span.Start <= local.SpanStart && candidate.scope.Span.End >= local.SpanStart)
                            .OrderBy(candidate => candidate.scope.Span.Length).FirstOrDefault();
                        return new Dictionary<string, object?>
                        {
                            ["name"] = local.Name,
                            ["storage"] = local.Storage,
                            ["type"] = local.Type,
                            ["durable"] = local.Durable,
                            ["scopeId"] = containing.scope is null ? 0 : containing.id,
                            ["liveStart"] = local.LiveStart,
                            ["liveEnd"] = local.LiveEnd ?? containing.scope?.Span.End ?? method.Body?.Span.End ?? method.Syntax?.Span.End ?? local.SpanStart + local.SpanLength,
                            ["source"] = DebugSourceEntry(local.File, local.Line, local.Column, local.SpanStart, local.SpanLength),
                        };
                    }).ToArray();
                var executable = _debugExecutable.GetValueOrDefault(method, [])
                    .OrderBy(location => location.File, StringComparer.Ordinal)
                    .ThenBy(location => location.Line)
                    .ThenBy(location => location.Column)
                    .Select(location => DebugSourceEntry(location.File, location.Line, location.Column, location.Start, location.Length))
                    .ToArray();
                var sites = _debugSites.Where(site => ReferenceEquals(site.Method, method))
                    .OrderBy(site => site.Id)
                    .Select(site => new Dictionary<string, object?>
                    {
                        ["id"] = site.Id,
                        ["kind"] = site.Kind,
                        ["source"] = DebugSourceEntry(site.File, site.Line, site.Column, site.SpanStart, site.SpanLength),
                    }).ToArray();
                return new Dictionary<string, object?>
                {
                    ["name"] = cName,
                    ["displayName"] = DebugMethodDisplayName(method, function.Property, function.IsGetter),
                    ["returnType"] = method.ReturnType.DisplayName,
                    ["source"] = method.Syntax is null ? null : DebugSourceEntry(method.Syntax),
                    ["receiver"] = method.IsStatic || method.IsConstructor ? null : "ct_self",
                    ["receiverType"] = method.IsStatic || method.IsConstructor ? null : method.ContainingType.FullName,
                    ["used"] = method.IsUsed,
                    ["linkerRetained"] = method.IsUsed,
                    ["genericDefinition"] = method.GenericDefinition is null ? null : NameMangler.MethodIdentity(method.GenericDefinition),
                    ["typeArguments"] = method.TypeArguments.Select(argument => argument.DisplayName).ToArray(),
                    ["parameters"] = method.Parameters.Select(parameter => new Dictionary<string, object?>
                    {
                        ["name"] = parameter.Name,
                        ["storage"] = NameMangler.Identifier(parameter.Name),
                        ["type"] = parameter.Type.DisplayName,
                        ["passing"] = parameter.PassingKind.ToString().ToLowerInvariant(),
                    }).ToArray(),
                    ["locals"] = locals,
                    ["executable"] = executable,
                    ["sites"] = sites,
                    ["scopes"] = scopes,
                };
            })
            .OrderBy(function => (string)function["name"]!, StringComparer.Ordinal)
            .ToArray();

        var types = EmittedTypes.OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => new Dictionary<string, object?>
            {
                ["name"] = type.FullName,
                ["storage"] = NameMangler.Type(type),
                ["kind"] = type.Kind.ToString().ToLowerInvariant(),
                ["layout"] = type.AggregateLayout.ToString().ToLowerInvariant(),
                ["pack"] = type.Pack,
                ["alignment"] = type.Alignment,
                ["underlyingType"] = type.UnderlyingType?.DisplayName,
                ["bitFieldBackingType"] = type.BitFieldBackingType?.DisplayName,
                ["base"] = type.BaseType?.FullName,
                ["interfaces"] = type.Interfaces.Select(@interface => @interface.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                ["genericDefinition"] = type.GenericDefinition?.FullName,
                ["typeArguments"] = type.TypeArguments.Select(argument => argument.DisplayName).ToArray(),
                ["runtimeBacked"] = type is { Namespace: "System.Threading", Name: "Thread" or "Mutex" },
                ["source"] = type.Syntax is null ? null : DebugSourceEntry(type.Syntax),
                ["values"] = type.EnumValues.OrderBy(value => value.Value).ThenBy(value => value.Name, StringComparer.Ordinal)
                    .Select(value => new Dictionary<string, object?>
                    {
                        ["name"] = value.Name,
                        ["value"] = value.Value.ToString(CultureInfo.InvariantCulture),
                    }).ToArray(),
                ["fields"] = type.Fields.Where(field => field.Name != "<underlying>")
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .Select(field => new Dictionary<string, object?>
                    {
                        ["name"] = field.Name,
                        ["storage"] = field.CAccessPath,
                        ["type"] = field.Type.DisplayName,
                        ["static"] = field.IsStatic,
                        ["volatile"] = field.IsVolatile,
                        ["atomic"] = field.Type.IsAtomic,
                        ["nativeVolatile"] = field.IsNativeVolatile,
                        ["extern"] = field.ExternName,
                        ["used"] = field.IsUsed,
                        ["linkerRetained"] = field.IsUsed,
                        ["linkerSymbol"] = field.LinkerSymbolName,
                        ["registerAddress"] = field.RegisterAddress?.ToString(CultureInfo.InvariantCulture),
                        ["bitFirst"] = field.BitFirst,
                        ["bitLast"] = field.BitLast,
                        ["offset"] = field.Offset,
                        ["alignment"] = field.Alignment,
                    }).ToArray(),
            }).ToArray();
        var arrays = _arrayTypes.OrderBy(type => type.DisplayName, StringComparer.Ordinal)
            .Select(type => new Dictionary<string, object?>
            {
                ["type"] = type.DisplayName,
                ["storage"] = CTypeName(type),
                ["elementType"] = type.ElementType!.DisplayName,
            }).ToArray();
        var inlineArrays = _inlineArrayTypes.OrderBy(type => type.DisplayName, StringComparer.Ordinal)
            .Select(type => new Dictionary<string, object?>
            {
                ["type"] = type.DisplayName,
                ["storage"] = CTypeName(type),
                ["elementType"] = type.ElementType!.DisplayName,
                ["length"] = type.InlineArrayLength,
            }).ToArray();
        var boxes = _boxedTypes.OrderBy(type => type.DisplayName, StringComparer.Ordinal)
            .Select(type => new Dictionary<string, object?>
            {
                ["type"] = type.DisplayName,
                ["storage"] = BoxName(type),
                ["valueType"] = type.DisplayName,
            }).ToArray();
        var fileSet = new HashSet<string>(_debugLocals.Select(local => local.File), StringComparer.Ordinal);
        foreach (var function in functions)
        {
            if (function["source"] is Dictionary<string, object?> source)
                fileSet.Add((string)source["file"]!);
            foreach (var executableSource in (Dictionary<string, object?>[])function["executable"]!)
                fileSet.Add((string)executableSource["file"]!);
        }
        foreach (var type in types)
            if (type["source"] is Dictionary<string, object?> source)
                fileSet.Add((string)source["file"]!);
        var files = fileSet.OrderBy(file => file, StringComparer.Ordinal).ToArray();
        var entryPoint = program.Functions.FirstOrDefault(function => function.Method.IsEntryPoint)?.Method.CName;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["generator"] = $"C~ draft {CompilerContract.DraftVersion}",
            ["version"] = CompilerContract.DebugMetadataVersion,
            ["runtimeAbi"] = CompilerContract.RuntimeAbiVersion,
            ["instrumented"] = EmitDebugInstrumentation,
            ["memoryDiagnostics"] = _debugMemory.ToString().ToLowerInvariant(),
            ["files"] = files,
            ["functions"] = functions,
            ["types"] = types,
            ["arrays"] = arrays,
            ["inlineArrays"] = inlineArrays,
            ["boxes"] = boxes,
            ["entryPoint"] = entryPoint,
            ["runtimeHooks"] = new Dictionary<string, object?>
            {
                ["throw"] = "ct_debug_throw_hook",
                ["fatal"] = "ct_debug_fatal_hook",
                ["control"] = EmitDebugInstrumentation ? "ct_debug_control" : null,
                ["trap"] = EmitDebugInstrumentation ? "ct_debug_trap" : null,
                ["startup"] = EmitDebugInstrumentation && IsEspIdf ? "ct_debug_startup_probe" : null,
            },
            ["runtimeControl"] = EmitDebugInstrumentation ? new Dictionary<string, object?>
            {
                ["symbol"] = "ct_debug_control",
                ["magic"] = "0x43544432",
                ["enabledSites"] = "Enabled",
                ["eventMask"] = "EventMask",
                ["stepMode"] = "StepMode",
                ["selectedThread"] = "SelectedThread",
                ["currentSite"] = "CurrentSite",
                ["currentReason"] = "CurrentReason",
                ["layouts"] = DebugControlLayouts(),
            } : null,
            ["runtimeSummary"] = EmitDebugInstrumentation ? new Dictionary<string, object?>
            {
                ["symbol"] = "ct_debug_runtime_summary",
                ["layouts"] = DebugRuntimeSummaryLayouts(),
            } : null,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private object[] DebugControlLayouts()
    {
        var enabledWords = Math.Max(1, (_debugSites.Count + 31) / 32);
        return
        [
            DebugControlLayout(4, 28, 68, 68 + enabledWords * 4),
            DebugControlLayout(8, 32, 100, AlignTo(100 + enabledWords * 4, 8)),
        ];
    }

    private static Dictionary<string, object?> DebugControlLayout(int pointerSize, int selectedThreadOffset, int enabledOffset, int size)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Magic"] = DebugField(0, 4),
            ["SiteCount"] = DebugField(4, 4),
            ["SessionActive"] = DebugField(8, 4),
            ["StartupReleased"] = DebugField(12, 4),
            ["EventMask"] = DebugField(16, 4),
            ["StepMode"] = DebugField(20, 4),
            ["StepDepth"] = DebugField(24, 4),
            ["SelectedThread"] = DebugField(selectedThreadOffset, pointerSize),
            ["CurrentThread"] = DebugField(selectedThreadOffset + pointerSize, pointerSize),
            ["CurrentActivation"] = DebugField(selectedThreadOffset + pointerSize * 2, pointerSize),
            ["CurrentSite"] = DebugField(selectedThreadOffset + pointerSize * 3, 4),
            ["CurrentReason"] = DebugField(selectedThreadOffset + pointerSize * 3 + 4, 4),
            ["CurrentObject"] = DebugField(selectedThreadOffset + pointerSize * 3 + 8, pointerSize),
            ["CurrentValue"] = DebugField(selectedThreadOffset + pointerSize * 4 + 8, 4),
            ["CurrentCode"] = DebugField(pointerSize == 4 ? 56 : 80, pointerSize),
            ["CurrentFile"] = DebugField(pointerSize == 4 ? 60 : 88, pointerSize),
            ["CurrentLine"] = DebugField(pointerSize == 4 ? 64 : 96, 4),
        };
        return new Dictionary<string, object?>
        {
            ["pointerSize"] = pointerSize,
            ["size"] = size,
            ["enabledOffset"] = enabledOffset,
            ["fields"] = fields,
        };
    }

    private static object[] DebugRuntimeSummaryLayouts() =>
    [
        new Dictionary<string, object?>
        {
            ["pointerSize"] = 4,
            ["size"] = 24,
            ["fields"] = new Dictionary<string, object?>
            {
                ["LiveObjectCount"] = DebugField(0, 4),
                ["TotalAllocations"] = DebugField(4, 4),
                ["TotalFinalReleases"] = DebugField(8, 4),
                ["QuarantineBlocks"] = DebugField(12, 4),
                ["QuarantineBytes"] = DebugField(16, 4),
                ["CurrentSite"] = DebugField(20, 4),
            },
        },
        new Dictionary<string, object?>
        {
            ["pointerSize"] = 8,
            ["size"] = 32,
            ["fields"] = new Dictionary<string, object?>
            {
                ["LiveObjectCount"] = DebugField(0, 4),
                ["TotalAllocations"] = DebugField(4, 4),
                ["TotalFinalReleases"] = DebugField(8, 4),
                ["QuarantineBlocks"] = DebugField(12, 4),
                ["QuarantineBytes"] = DebugField(16, 8),
                ["CurrentSite"] = DebugField(24, 4),
            },
        },
    ];

    private static Dictionary<string, object?> DebugField(int offset, int width) => new()
    {
        ["offset"] = offset,
        ["width"] = width,
    };

    private static int AlignTo(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static string DebugMethodDisplayName(MethodSymbol method, PropertySymbol? property, bool getter)
    {
        if (property is not null)
            return $"{property.ContainingType.FullName}.{property.Name}.{(getter ? "get" : "set")}";
        if (method.IsConstructor)
            return $"{method.ContainingType.FullName}.{method.ContainingType.Name}";
        if (method.IsOperator)
            return $"{method.ContainingType.FullName}.operator {OperatorFacts.DisplayName(method.OperatorKind)}";
        return $"{method.ContainingType.FullName}.{method.Name}";
    }

    private Dictionary<string, object?> DebugSourceEntry(SyntaxNode syntax)
    {
        var location = syntax.Source.GetLocation(syntax.Span);
        return DebugSourceEntry(NormalizeDebugPath(syntax.Source.FilePath), location.Line, location.Column,
            syntax.Span.Start, syntax.Span.Length);
    }

    private static Dictionary<string, object?> DebugSourceEntry(string file, int line, int column, int start, int length) => new()
    {
        ["file"] = file,
        ["line"] = line,
        ["column"] = column,
        ["spanStart"] = start,
        ["spanLength"] = length,
    };

    private static Dictionary<string, object?> SymbolMapEntry(string name, string identity, string kind, string signature, SyntaxNode? syntax)
    {
        SourceLocation? location = syntax is null ? null : syntax.Source.GetLocation(syntax.Span);
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["identity"] = identity,
            ["kind"] = kind,
            ["signature"] = signature,
            ["source"] = location is null ? null : new Dictionary<string, object?>
            {
                ["file"] = location.Value.FilePath.Replace('\\', '/'),
                ["line"] = location.Value.Line,
                ["column"] = location.Value.Column,
            },
        };
    }

    private static string Hash96(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string RenderFunction(IrFunction function) =>
        function.Emission?.Definition ?? throw new InvalidOperationException($"Typed IR for '{function.Method.CName}' has no emission plan.");

    private string RenderModuleLifecycle(ImmutableArray<IrStaticInitializer> initializers)
    {
        var writer = new CWriter();
        writer.WriteLine("static uint32_t ct_module_phase = 0u;");
        writer.WriteLine("static void ct_module_init(void)");
        writer.WriteLine("{");
        writer.WriteLine("    if (ct_module_phase != 0u) ct_fail(\"CTT0003\", \"<module-init>\", 0);");
        writer.WriteLine("    ct_module_phase = 1u;");
        var initializerIndex = 0;
        foreach (var initializer in initializers)
        {
            var field = initializer.Field;
            var value = initializer.Emission ?? throw new InvalidOperationException($"Typed IR initializer for '{field.CName}' has no emission plan.");
            initializerIndex++;
            foreach (var line in value.Prelude)
                writer.WriteLine("    " + line);
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"    {CTypeName(field.Type)} ct_static_value_{initializerIndex} = {value.Code};");
                if (value.Ownership != OwnershipKind.Owned)
                    writer.WriteLine("    " + RetainValueStatement(field.Type, $"&ct_static_value_{initializerIndex}"));
                writer.WriteLine($"    {field.CName} = ct_static_value_{initializerIndex};");
            }
            else
                writer.WriteLine($"    {field.CName} = {value.Code};");
        }
        writer.WriteLine("    ct_module_phase = 2u;");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_module_fini(void)");
        writer.WriteLine("{");
        writer.WriteLine("    if (ct_module_phase != 1u && ct_module_phase != 2u) ct_fail(\"CTT0003\", \"<module-fini>\", 0);");
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields)
                     .Where(field => field.IsStatic && field.Name != "<underlying>" && field.Type.ContainsManagedReferences)
                     .Reverse())
            writer.WriteLine($"    {DropValueStatement(field.Type, $"&{field.CName}")}");
        writer.WriteLine("    ct_module_phase = 3u;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_module_descriptor ct_program_module = { CTILDE_RUNTIME_ABI_VERSION, \"program\", ct_module_init, ct_module_fini };");
        return writer.ToString();
    }

    public string CTypeName(CType type) => type.Kind switch
    {
        CTypeKind.Void => "void",
        CTypeKind.Bool => "bool",
        CTypeKind.Byte or CTypeKind.Char => "uint8_t",
        CTypeKind.Sbyte => "int8_t",
        CTypeKind.Short => "int16_t",
        CTypeKind.Ushort => "uint16_t",
        CTypeKind.Int => "int32_t",
        CTypeKind.Uint => "uint32_t",
        CTypeKind.Long => "int64_t",
        CTypeKind.Ulong => "uint64_t",
        CTypeKind.Nint => "intptr_t",
        CTypeKind.Nuint => "uintptr_t",
        CTypeKind.Float => "float",
        CTypeKind.String => "ct_string*",
        CTypeKind.Class => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Interface => "ct_object*",
        CTypeKind.Delegate => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Opaque => type.Symbol!.NativeTypeName!,
        CTypeKind.EspError => "esp_err_t",
        CTypeKind.Struct or CTypeKind.Enum or CTypeKind.Newtype => NameMangler.Type(type.Symbol!),
        CTypeKind.InlineArray => NameMangler.InlineArray(type),
        CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*",
        CTypeKind.FunctionPointer => $"ct_fp_{NameMangler.TypeCode(type)}",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"ct_{NameMangler.TypeCode(type)}",
        CTypeKind.NativeUtf8String => "ct_native_utf8_string",
        CTypeKind.Null => "void*",
        _ => "int32_t",
    };

    public string CDeclaration(CType type, string name)
    {
        if (type.Kind != CTypeKind.FunctionPointer)
            return $"{CTypeName(type)} {name}";
        var signature = type.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*{name})({FunctionPointerParameters(signature)})";
    }

    public string CParameterDeclaration(ParameterSymbol parameter, string name) => parameter.PassingKind switch
    {
        _ when parameter.IsSynchronousCallback => SynchronousCallbackDeclaration(parameter.Type.Symbol!, name),
        _ when parameter.Type.IsNativeBuffer => $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}* {name}_data, size_t {name}_length",
        _ when parameter.Type.IsNativeUtf8String => $"const char* {name}",
        ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}* {name}",
        ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}* {name}",
        _ => CDeclaration(parameter.Type, name),
    };

    private string ParameterTypeName(ParameterSymbol parameter) => parameter.PassingKind switch
    {
        _ when parameter.Type.IsNativeBuffer => $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}*, size_t",
        _ when parameter.Type.IsNativeUtf8String => "const char*",
        ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}*",
        ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}*",
        _ => CTypeName(parameter.Type),
    };

    private string SynchronousCallbackDeclaration(TypeSymbol delegateType, string name)
    {
        var parameters = delegateType.DelegateParameters.Select(parameter => ParameterTypeName(parameter)).Append("void*");
        return $"{CTypeName(delegateType.DelegateReturnType!)} (*{name})({string.Join(", ", parameters)})";
    }

    public string SynchronousCallbackAdapterName(TypeSymbol delegateType) => NameMangler.Artifact("ct_k_", $"callback-adapter:{NameMangler.TypeIdentity(delegateType)}");

    private static IEnumerable<string> ParameterArgumentNames(ParameterSymbol parameter, string name) => parameter.Type.IsNativeBuffer
        ? [$"{name}_data", $"{name}_length"]
        : [name];

    public string CCastType(CType type)
    {
        if (type.Kind != CTypeKind.FunctionPointer)
            return CTypeName(type);
        var signature = type.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*)({FunctionPointerParameters(signature)})";
    }

    private string CFunctionDeclaration(CType returnType, string name, IReadOnlyList<string> parameters)
    {
        var arguments = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
        if (returnType.Kind != CTypeKind.FunctionPointer)
            return $"{CTypeName(returnType)} {name}({arguments})";
        var signature = returnType.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*{name}({arguments}))({FunctionPointerParameters(signature)})";
    }

    private string FunctionPointerParameters(FunctionPointerSignature signature) => signature.ParameterTypes.Length == 0
        ? "void"
        : string.Join(", ", signature.ParameterTypes.Select((type, index) => type.IsNativeBuffer
            ? $"{(type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(type.ElementType!)}*, size_t"
            : signature.PassingKinds[index] switch
            {
                ParameterPassingKind.In => $"const {CTypeName(type)}*",
                ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(type)}*",
                _ => CTypeName(type),
            }));

    public string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.Bool => "false",
        CTypeKind.Float => "0.0f",
        CTypeKind.String or CTypeKind.Class or CTypeKind.Interface or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.Null => "NULL",
        CTypeKind.Opaque => $"({CTypeName(type)})0",
        CTypeKind.EspError => "ESP_OK",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"({CTypeName(type)}){{ NULL, (size_t)0 }}",
        CTypeKind.NativeUtf8String => "(ct_native_utf8_string){ NULL, NULL, (size_t)0 }",
        CTypeKind.Struct when type.Symbol?.IsBitField == true => $"({CTypeName(type)})0",
        CTypeKind.Struct => $"({CTypeName(type)}){{0}}",
        CTypeKind.InlineArray => $"({CTypeName(type)}){{0}}",
        CTypeKind.Newtype => $"({CTypeName(type)})0",
        _ => "0",
    };

    public void RegisterType(CType type)
    {
        if (type.Kind is CTypeKind.Nint or CTypeKind.Nuint)
        {
            _usesNativeIntegers = true;
        }
        if (type.Kind == CTypeKind.Array)
        {
            _arrayTypes.Add(type);
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.InlineArray)
        {
            if (type.InlineArrayLength > 0)
                _inlineArrayTypes.Add(type);
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.Pointer)
        {
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.FunctionPointer)
        {
            _functionPointerTypes.Add(type);
            foreach (var parameter in type.FunctionPointer!.ParameterTypes)
                RegisterType(parameter);
            RegisterType(type.FunctionPointer.ReturnType);
        }
        else if (type.IsNativeBuffer)
        {
            _nativeBufferTypes.Add(type);
            _usesNativeIntegers = true;
            RegisterType(type.ElementType!);
        }
        else if (type.IsNativeUtf8String)
        {
            _usesNativeUtf8 = true;
            _usesNativeIntegers = true;
        }
    }

    public void RegisterBox(CType type)
    {
        if (type.Kind is CTypeKind.Void or CTypeKind.Null or CTypeKind.Error or CTypeKind.String or CTypeKind.Class or CTypeKind.Array or CTypeKind.Opaque or CTypeKind.NativeUtf8String)
            return;
        _boxedTypes.Add(type);
        RegisterType(type);
    }

    public static string BoxName(CType type) => $"ct_box_{NameMangler.TypeCode(type)}";
    public static string BoxDescriptorName(CType type) => $"ct_desc_box_{NameMangler.TypeCode(type)}";
    public static string BoxFunctionName(CType type) => $"ct_box_value_{NameMangler.TypeCode(type)}";
    public static string UnboxFunctionName(CType type) => $"ct_unbox_value_{NameMangler.TypeCode(type)}";
    public static string ValueRetainName(CType type) => type.IsReference ? "ct_retain_ref_value" : $"ct_retain_value_{NameMangler.TypeCode(type)}";
    public static string ValueDropName(CType type) => type.IsReference ? "ct_drop_ref_value" : $"ct_drop_value_{NameMangler.TypeCode(type)}";

    public string RetainValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{ValueRetainName(type)}((void*)({address}));"
        : string.Empty;

    public string DropValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{ValueDropName(type)}((void*)({address}));"
        : string.Empty;

    public string DescriptorExpression(CType type) => type.Kind switch
    {
        CTypeKind.String => "&ct_desc_string",
        CTypeKind.Class or CTypeKind.Interface or CTypeKind.Delegate => $"&{DescriptorName(type.Symbol!)}",
        CTypeKind.Array => $"&{ArrayDescriptorName(type.ElementType!)}",
        _ => $"&{BoxDescriptorName(type)}",
    };

    public static string VirtualSlotName(MethodSymbol method)
    {
        var root = method;
        while (root.OverriddenMethod is not null)
            root = root.OverriddenMethod;
        if (root.ContainingType.IsObject)
            return root.Name switch { "ToString" => "ToString", "Equals" => "Equals", "GetHashCode" => "GetHashCode", _ => $"m_{NameMangler.Identifier(root.CName)}" };
        return $"m_{NameMangler.Identifier(root.CName)}";
    }

    public static string VirtualGetterSlotName(PropertySymbol property)
    {
        var root = property;
        while (root.OverriddenProperty is not null)
            root = root.OverriddenProperty;
        return $"g_{NameMangler.Identifier(NameMangler.Getter(root))}";
    }

    public static string VirtualSetterSlotName(PropertySymbol property)
    {
        var root = property;
        while (root.OverriddenProperty is not null)
            root = root.OverriddenProperty;
        return $"s_{NameMangler.Identifier(NameMangler.Setter(root))}";
    }

    public string RegisterString(string value)
    {
        if (!_stringLiterals.TryGetValue(value, out var id))
        {
            id = _stringLiterals.Count;
            _stringLiterals.Add(value, id);
        }
        return $"((ct_string*)(uintptr_t)(const void*)&ct_sl_{id})";
    }

    public static string EscapeCString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder();
        foreach (var valueByte in bytes)
        {
            if (valueByte is >= 32 and <= 126 && valueByte is not (byte)'"' and not (byte)'\\')
                builder.Append((char)valueByte);
            else
                builder.Append("\\x").Append(valueByte.ToString("X2", CultureInfo.InvariantCulture)).Append("\"\"");
        }
        return builder.ToString();
    }

    public string SourceArgument(SyntaxNode syntax)
    {
        var path = IsEspIdf
            ? Path.GetFileName(syntax.Source.FilePath)
            : _sourceRoot is not null && Path.IsPathFullyQualified(syntax.Source.FilePath)
                ? Path.GetRelativePath(_sourceRoot, Path.GetFullPath(syntax.Source.FilePath)).Replace('\\', '/')
                : syntax.Source.FilePath.Replace('\\', '/');
        return $"\"{EscapeCString(path)}\", {syntax.Source.GetLocation(syntax.Span).Line}";
    }

    public string DebugSourceDirective(SyntaxNode syntax)
    {
        if (!EmitDebugInformation)
            return string.Empty;
        var location = syntax.Source.GetLocation(syntax.Span);
        return $"#line {location.Line} \"{EscapeCString(NormalizeDebugPath(syntax.Source.FilePath))}\"";
    }

    public string DebugGeneratedDirective() => EmitDebugInformation
        ? "#line 1 \"<ctilde-generated>\""
        : string.Empty;

    public void RegisterDebugExecutable(MethodSymbol method, SyntaxNode syntax)
    {
        if (!EmitDebugInformation)
            return;
        if (!_debugExecutable.TryGetValue(method, out var locations))
        {
            locations = [];
            _debugExecutable.Add(method, locations);
        }
        var location = syntax.Source.GetLocation(syntax.Span);
        locations.Add((NormalizeDebugPath(syntax.Source.FilePath), location.Line, location.Column,
            syntax.Span.Start, syntax.Span.Length));
    }

    public void RegisterDebugLocal(MethodSymbol method, LocalSymbol local, int liveStart, int? liveEnd)
    {
        if (!EmitDebugInformation)
            return;
        var location = local.Syntax.Source.GetLocation(local.Syntax.Span);
        var entry = new DebugLocalEntry(method, local.Name, local.CName, local.Type.DisplayName, local.IsDurable,
            NormalizeDebugPath(local.Syntax.Source.FilePath), location.Line, location.Column,
            local.Syntax.Span.Start, local.Syntax.Span.Length, liveStart, liveEnd);
        if (!_debugLocals.Contains(entry))
            _debugLocals.Add(entry);
    }

    public int RegisterDebugSite(MethodSymbol method, SyntaxNode syntax, string kind)
    {
        if (!EmitDebugInstrumentation)
            return -1;
        var key = (method, syntax.Span.Start, syntax.Span.Length, kind);
        if (_debugSiteIds.TryGetValue(key, out var existing))
            return existing;
        var location = syntax.Source.GetLocation(syntax.Span);
        var id = _debugSites.Count;
        _debugSiteIds.Add(key, id);
        _debugSites.Add(new DebugSiteEntry(id, method, kind, NormalizeDebugPath(syntax.Source.FilePath),
            location.Line, location.Column, syntax.Span.Start, syntax.Span.Length));
        return id;
    }

    private static IEnumerable<SyntaxNode> DebugDescendantNodes(SyntaxNode root)
    {
        yield return root;
        foreach (var child in root.ChildNodesAndTokens().Where(item => item.IsNode).Select(item => item.Node!))
            foreach (var descendant in DebugDescendantNodes(child))
                yield return descendant;
    }

    private string NormalizeDebugPath(string path)
    {
        if (_sourceRoot is not null && Path.IsPathFullyQualified(path))
        {
            var relative = Path.GetRelativePath(_sourceRoot, Path.GetFullPath(path));
            if (relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        return path.Replace('\\', '/');
    }

    public string DirectDeferThunkName(MethodSymbol method, int id) =>
        $"ct_defer_{NameMangler.Identifier(method.CName)}_{id}";

    public string DurableStateTypeName(MethodSymbol method) =>
        $"ct_state_{NameMangler.Identifier(method.CName)}";

    public void RegisterDirectDeferState(MethodSymbol method, IReadOnlyDictionary<string, CType> fields, IReadOnlyList<DirectDeferThunk> thunks)
    {
        _directDeferStates[method] = ([.. fields], [.. thunks]);
    }

    private void EmitDirectDeferSupport(CWriter writer)
    {
        foreach (var pair in _directDeferStates.OrderBy(pair => pair.Key.CName, StringComparer.Ordinal))
        {
            var stateType = DurableStateTypeName(pair.Key);
            writer.WriteLine($"typedef struct {stateType}");
            using (writer.Block())
            {
                foreach (var field in pair.Value.Fields)
                    writer.WriteLine($"{CDeclaration(field.Value, field.Key)};");
            }
            writer.WriteLine($"{stateType};");
            foreach (var thunk in pair.Value.Thunks)
            {
                writer.WriteLine($"static void {thunk.Name}(void* storage)");
                using (writer.Block())
                {
                    if (thunk.Code.Contains("ct_state.", StringComparison.Ordinal))
                        writer.WriteLine($"{stateType}* capture = ({stateType}*)storage;");
                    else
                        writer.WriteLine("(void)storage;");
                    writer.WriteLine(thunk.Code.Replace("ct_state.", "capture->", StringComparison.Ordinal));
                }
            }
            writer.WriteLine();
        }
    }

    private static string FormatIntegralConstant(BigInteger value, CType type) => type.Kind switch
    {
        CTypeKind.Uint => $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})",
        CTypeKind.Ulong => $"UINT64_C({value.ToString(CultureInfo.InvariantCulture)})",
        CTypeKind.Nuint => $"((uintptr_t)UINT64_C({value.ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Nint => $"((intptr_t){(value < 0 ? "-" : string.Empty)}UINT64_C({BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Long when value == long.MinValue => "INT64_MIN",
        CTypeKind.Long when value < 0 => $"(-INT64_C({BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Long => $"INT64_C({value.ToString(CultureInfo.InvariantCulture)})",
        _ when value == int.MinValue => "INT32_MIN",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static string DescriptorName(TypeSymbol type) => NameMangler.Artifact("ct_d_", $"descriptor:{NameMangler.TypeIdentity(type)}");
    public static string ArrayDescriptorName(CType elementType) => NameMangler.Artifact("ct_d_", $"descriptor:array<{NameMangler.CanonicalType(elementType)}>");
    public static string VTableName(TypeSymbol type) => NameMangler.Artifact("ct_v_", $"vtable:{NameMangler.TypeIdentity(type)}");
    public static string VirtualMethodThunkName(MethodSymbol method) => NameMangler.Artifact("ct_h_", $"virtual-thunk:{NameMangler.MethodIdentity(method)}");
    public static string VirtualPropertyThunkName(PropertySymbol property, bool getter) => NameMangler.Artifact("ct_h_", $"virtual-thunk:{NameMangler.PropertyIdentity(property, getter)}");
    public static string ConstructorInitializerName(MethodSymbol constructor) => NameMangler.Artifact("ct_i_", $"initializer:{NameMangler.MethodIdentity(constructor)}");
    public static string ObjectDropName(TypeSymbol type) => NameMangler.Artifact("ct_x_", $"object-drop:{NameMangler.TypeIdentity(type)}");
    public static string ArrayDropName(CType elementType) => $"ct_drop_array_{NameMangler.TypeCode(elementType)}";
    public static string BoxDropName(CType type) => $"ct_drop_box_{NameMangler.TypeCode(type)}";
    public static string DelegateFactoryName(TypeSymbol type) => NameMangler.Artifact("ct_n_", $"delegate-factory:{NameMangler.TypeIdentity(type)}");
    public static string DelegateDropName(TypeSymbol type) => NameMangler.Artifact("ct_x_", $"delegate-drop:{NameMangler.TypeIdentity(type)}");

    public string RegisterDelegateThunk(TypeSymbol delegateType, MethodSymbol method, bool virtualDispatch)
    {
        var key = (delegateType, method, virtualDispatch);
        if (_delegateThunks.TryGetValue(key, out var existing))
            return existing;
        var name = NameMangler.Artifact("ct_h_", $"delegate-thunk:{NameMangler.TypeIdentity(delegateType)}:{NameMangler.MethodIdentity(method)}:{(virtualDispatch ? "virtual" : "direct")}");
        _delegateThunks.Add(key, name);
        return name;
    }

    public string RegisterFunctionPointerTrampoline(CType type, MethodSymbol method)
    {
        var key = (type, method);
        if (_functionPointerTrampolines.TryGetValue(key, out var existing))
            return existing;
        RegisterExceptions();
        var name = NameMangler.Artifact("ct_k_", $"function-pointer-callback:{NameMangler.CanonicalType(type)}:{NameMangler.MethodIdentity(method)}");
        _functionPointerTrampolines.Add(key, name);
        return name;
    }

    public string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false)
    {
        var returnType = method.IsConstructor ? method.ContainingType.Type : method.ReturnType;
        var parameters = new List<string>();
        if (!method.IsStatic && !method.IsConstructor)
            parameters.Add($"{InstanceStorageType(method.ContainingType)}* ct_self");
        foreach (var parameter in method.Parameters)
        {
            var parameterName = NameMangler.Identifier(parameter.Name);
            parameters.Add(parameter.Type.IsNativeUtf8String && method.ExternName is null
                ? CDeclaration(parameter.Type, parameterName)
                : CParameterDeclaration(parameter, parameterName));
            if (parameter.IsSynchronousCallback)
                parameters.Add($"void* {parameterName}_context");
        }
        var storage = method.ExternName is not null ? "extern " : method.IsUsed ? string.Empty : "static ";
        var signature = storage + UsedAnnotation(method.IsUsed) + SectionAnnotation(NativeSectionKind.Code, method.SectionName) + CFunctionDeclaration(returnType, name ?? method.CName, parameters);
        return prototype ? signature + ";" : signature;
    }

    private static string InstanceStorageType(TypeSymbol type) => type.FullName == "Esp.Idf.EspError"
        ? "esp_err_t"
        : NameMangler.Type(type);

    internal void RegisterDeclaredTypes()
    {
        foreach (var type in EmittedTypes)
        {
            foreach (var field in type.Fields)
                RegisterType(field.Type);
            foreach (var property in type.Properties)
                RegisterType(property.Type);
            foreach (var method in type.Methods.Concat(type.Constructors))
            {
                RegisterType(method.ReturnType);
                foreach (var parameter in method.Parameters)
                    RegisterType(parameter.Type);
            }
        }
    }
}
