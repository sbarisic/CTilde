using System.Collections.Immutable;

namespace CTilde;

public enum LanguageCompletionKind
{
    Keyword, Namespace, Class, Struct, Enum, EnumMember, Method, Constructor, Property, Field, Variable, Parameter,
}

public enum LanguageSymbolKind
{
    Namespace, Class, Struct, Enum, EnumMember, Method, Constructor, Property, Field, Parameter, Variable,
}

public sealed record LanguageCompletion(
    string Label,
    LanguageCompletionKind Kind,
    string Detail,
    string InsertText,
    TextSpan ReplacementSpan,
    string SortText,
    string? DocumentationId = null,
    int OverloadCount = 1);

public sealed record LanguageDocumentedSignature(string Signature, LanguageDocumentation? Documentation);

public sealed record LanguageHover(
    string Contents,
    TextSpan Span,
    ImmutableArray<LanguageDocumentedSignature> Sections = default);

public sealed record LanguageParameter(string Label, string? Documentation = null);

public sealed record LanguageSignature(
    string Label,
    ImmutableArray<LanguageParameter> Parameters,
    LanguageDocumentation? Documentation = null);

public sealed record LanguageSignatureHelp(
    ImmutableArray<LanguageSignature> Signatures,
    int ActiveSignature,
    int ActiveParameter);

public sealed record LanguageDefinition(string FilePath, TextSpan Span);

public sealed record LanguageDocumentSymbol(
    string Name,
    string Detail,
    LanguageSymbolKind Kind,
    TextSpan Range,
    TextSpan SelectionRange,
    ImmutableArray<LanguageDocumentSymbol> Children);

public sealed record LanguageWorkspaceSymbol(
    string Name,
    string ContainerName,
    LanguageSymbolKind Kind,
    LanguageDefinition Location);

public sealed partial class LanguageServiceSnapshot
{
    private static readonly string[] TopLevelKeywords = ["using", "namespace", "public", "internal", "class", "interface", "struct", "union", "enum", "delegate", "opaque", "newtype", "static", "sealed", "abstract"];
    private static readonly string[] TypeKeywords = ["public", "internal", "protected", "private", "static", "readonly", "const", "volatile", "unsafe", "virtual", "abstract", "override", "sealed", "operator", "where", "void", "asm"];
    private static readonly string[] StatementKeywords = ["if", "else", "while", "do", "for", "foreach", "switch", "case", "default", "break", "continue", "defer", "lock", "return", "throw", "try", "catch", "finally", "unsafe", "asm", "new", "stackalloc", "sizeof", "alignof", "offsetof", "ref", "in", "out", "this", "base", "true", "false", "null", "var"];
    private static readonly string[] BuiltInTypes = ["bool", "byte", "sbyte", "short", "ushort", "char", "rune", "int", "uint", "long", "ulong", "nint", "nuint", "float", "double", "string", "object"];

    private readonly ImmutableArray<SyntaxTree> _userTrees;
    private readonly ImmutableArray<SyntaxTree> _allTrees;
    private readonly CompilationModel _model;
    private readonly BoundProgram _boundProgram;
    private readonly ImmutableArray<Diagnostic> _diagnostics;
    private readonly Dictionary<string, SyntaxTree> _treesByPath;
    private readonly Dictionary<string, DocumentIndex> _documentIndexes;
    private readonly Dictionary<string, ImmutableArray<BoundSemanticEntry>> _semanticEntries;
    private readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private LanguageServiceSnapshot(
        IEnumerable<SyntaxTree> syntaxTrees,
        CompilationOptions options,
        ImmutableArray<SyntaxTree>? standardLibraryOverride = null,
        bool requireEntryPoint = true)
    {
        _userTrees = syntaxTrees.ToImmutableArray();
        Options = options;
        var nativeIntegers = _userTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind is SyntaxKind.NintKeyword or SyntaxKind.NuintKeyword or SyntaxKind.SizeofKeyword or SyntaxKind.AlignofKeyword or SyntaxKind.OffsetofKeyword);
        var nativeUtf8 = _userTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind == SyntaxKind.IdentifierToken && token.Text == "NativeUtf8String");
        _allTrees = (standardLibraryOverride ?? StandardLibrary.GetSyntaxTrees(
                options.Target,
                nativeIntegers,
                nativeUtf8,
                options.Target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan,
                StandardVectorTypes.All,
                StandardFoundationTypes.All))
            .AddRange(_userTrees);
        var declarationDiagnostics = new DiagnosticBag();
        foreach (var tree in _allTrees)
            declarationDiagnostics.AddRange(tree.Diagnostics);
        var sourceRoot = (options.Target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && options.SourceRoot is not null && Path.IsPathFullyQualified(options.SourceRoot)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceRoot))
            : null;
        var architecture = options.Architecture == CompilationArchitecture.Auto && options.Target == CompilationTarget.Hosted
            ? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X86 => CompilationArchitecture.X86,
                System.Runtime.InteropServices.Architecture.X64 => CompilationArchitecture.X64,
                System.Runtime.InteropServices.Architecture.Arm => CompilationArchitecture.Arm32,
                System.Runtime.InteropServices.Architecture.Arm64 => CompilationArchitecture.Arm64,
                _ => CompilationArchitecture.Auto,
            }
            : options.Architecture;
        _model = new CompilationModel(_allTrees, _userTrees, declarationDiagnostics, options.Target, architecture, options.EffectiveCpuFeatures,
            options.Environment, options.SimdOptimizations, requireEntryPoint, requireRuntimeImplementations: false);
        _boundProgram = BoundProgramBuilder.Build(_model, options.Target, architecture, sourceRoot);
        _diagnostics = declarationDiagnostics.ToImmutable();
        _treesByPath = new Dictionary<string, SyntaxTree>(_pathComparer);
        _documentIndexes = new Dictionary<string, DocumentIndex>(_pathComparer);
        _semanticEntries = new Dictionary<string, ImmutableArray<BoundSemanticEntry>>(_pathComparer);
        foreach (var tree in _allTrees)
        {
            var path = NormalizePath(tree.Text.FilePath);
            _treesByPath[path] = tree;
            _documentIndexes[path] = new DocumentIndex(tree, _model);
            _semanticEntries[path] = [.. _boundProgram.SemanticMap.Values
                .Where(entry => ReferenceEquals(entry.Syntax.Source, tree.Text))
                .OrderBy(entry => entry.Syntax.Span.Length)
                .ThenBy(entry => entry.Syntax.Span.Start)];
        }
        _referenceIndex = new Lazy<ReferenceIndex>(BuildReferenceIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CompilationOptions Options { get; }

    public ImmutableArray<Diagnostic> Diagnostics => _diagnostics;

    public LanguageDocumentation? GetDocumentation(string documentationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentationId);
        return _model.Documentation.GetDocumentation(documentationId);
    }

    public static LanguageServiceSnapshot Create(IEnumerable<SyntaxTree> syntaxTrees, CompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        return new LanguageServiceSnapshot(syntaxTrees, options ?? new CompilationOptions());
    }

    public static LanguageServiceSnapshot CreateStandardLibraryProject(
        string sourceRoot,
        string documentPath,
        IReadOnlyDictionary<string, string>? sourceOverrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var normalized = Path.GetFullPath(documentPath).Replace('\\', '/');
        var target = normalized.Contains("/Esp/Idf/", StringComparison.OrdinalIgnoreCase)
            ? CompilationTarget.EspIdf
            : normalized.EndsWith("/MemoryFreestanding.ct", StringComparison.OrdinalIgnoreCase)
                ? CompilationTarget.Freestanding
                : CompilationTarget.Hosted;
        var architecture = target == CompilationTarget.Freestanding ? CompilationArchitecture.X64 : CompilationArchitecture.Auto;
        var trees = StandardLibraryProjectService.LoadEditorTrees(sourceRoot, documentPath, sourceOverrides);
        return new LanguageServiceSnapshot([], new CompilationOptions(target, Architecture: architecture), trees, requireEntryPoint: false);
    }

    public ImmutableArray<LanguageCompletion> GetCompletions(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return [];
        position = Math.Clamp(position, 0, tree.Text.Length);
        var replacement = IdentifierSpan(tree.Text.Text, position);
        var context = CreateContext(tree, position);
        var member = FindMemberAccess(context, replacement);
        var results = new List<LanguageCompletion>();
        if (member is not null)
            AddMemberCompletions(results, context, member, replacement);
        else
            AddContextCompletions(results, context, replacement);
        foreach (var assembly in context.Nodes.OfType<InlineAssemblyStatementSyntax>()
                     .Where(assembly => position >= assembly.BodySpan.Start && position <= assembly.BodySpan.End)
                     .OrderBy(assembly => assembly.Span.Length).Take(1))
        {
            foreach (var operand in assembly.Operands)
                results.Add(new LanguageCompletion(operand.Name, LanguageCompletionKind.Variable, "inline assembly operand", operand.Name, replacement, "0"));
        }
        foreach (var assembly in context.Nodes.OfType<AssemblyFunctionBodySyntax>()
                     .Where(assembly => position >= assembly.BodySpan.Start && position <= assembly.BodySpan.End)
                     .OrderBy(assembly => assembly.Span.Length).Take(1))
        {
            foreach (var operand in assembly.Operands)
                results.Add(new LanguageCompletion(operand.Name, LanguageCompletionKind.Variable, "assembly-function operand", operand.Name, replacement, "0"));
        }
        var distinct = results
            .Where(item => replacement.Length == 0 || item.Label.StartsWith(tree.Text.Slice(replacement), StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => (item.Label, item.Detail, item.Kind))
            .Select(group => group.First())
            .ToArray();
        var collapsedMethods = distinct
            .Where(item => item.Kind == LanguageCompletionKind.Method)
            .GroupBy(item => item.Label, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.SortText, StringComparer.Ordinal)
                .ThenBy(item => item.Detail, StringComparer.Ordinal)
                .First() with
            {
                OverloadCount = group.Count(),
            });
        return [.. distinct
            .Where(item => item.Kind != LanguageCompletionKind.Method)
            .Concat(collapsedMethods)
            .OrderBy(item => item.SortText, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Detail, StringComparer.Ordinal)];
    }

    public LanguageHover? GetHover(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return null;
        if (InlineAssemblyReferenceAt(tree, position) is { } assemblyReference &&
            _boundProgram.SemanticMap.TryGetValue(assemblyReference, out var assemblySemantic))
        {
            if (assemblySemantic.Symbol is { } assemblySymbol)
            {
                var section = new LanguageDocumentedSignature(FormatSymbol(assemblySymbol), _model.Documentation.GetDocumentation(assemblySymbol));
                return new LanguageHover(section.Signature, assemblyReference.Span, [section]);
            }
            if (IsAssemblyResult(assemblySemantic))
                return new LanguageHover($"{assemblySemantic.Type.DisplayName} {assemblyReference.Name} (assembly-function result)", assemblyReference.Span);
        }
        var token = HoverTokenAt(tree, position);
        if (token is null)
            return null;
        if (token.Kind == SyntaxKind.IdentifierToken && ContractAttributeHover(token.Text) is { } effectHover)
            return new LanguageHover(effectHover, token.Span);
        if (TryGetBoundEntry(tree, token.Span, out var boundEntry) && IsAssemblyResult(boundEntry))
            return new LanguageHover($"{boundEntry.Type.DisplayName} {token.Text} (assembly-function result)", token.Span);
        if (token.Kind != SyntaxKind.IdentifierToken && !OperatorFacts.IsSupported(token.Kind))
        {
            var builtIn = TypeFacts.BuiltIn(token.Text);
            if (builtIn is not null)
                return new LanguageHover(builtIn.DisplayName, token.Span);
        }
        var context = CreateContext(tree, position);
        var symbols = ResolveToken(context, token).ToArray();
        if (symbols.Length == 0)
            return null;
        var sections = symbols
            .Select(symbol => new LanguageDocumentedSignature(FormatSymbol(symbol), _model.Documentation.GetDocumentation(symbol)))
            .DistinctBy(section => section.Signature, StringComparer.Ordinal)
            .ToImmutableArray();
        return new LanguageHover(string.Join("\n\n", sections.Select(section => section.Signature)), token.Span, sections);
    }

    public LanguageSignatureHelp? GetSignatureHelp(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return null;
        position = Math.Clamp(position, 0, tree.Text.Length);
        var context = CreateContext(tree, position);
        var call = context.Nodes.OfType<CallExpressionSyntax>()
            .Where(candidate => candidate.Span.Start <= position && candidate.Span.End >= position)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (call is null)
            return null;
        var methods = ResolveCallCandidates(context, call).ToArray();
        if (methods.Length == 0)
            return null;
        var open = tree.Text.Text.IndexOf('(', call.Target.Span.End, Math.Max(0, Math.Min(position, tree.Text.Length) - call.Target.Span.End));
        var activeParameter = 0;
        if (open >= 0)
        {
            var depth = 0;
            for (var index = open + 1; index < position; index++)
            {
                if (tree.Text.Text[index] is '(' or '[')
                    depth++;
                else if (tree.Text.Text[index] is ')' or ']')
                    depth = Math.Max(0, depth - 1);
                else if (tree.Text.Text[index] == ',' && depth == 0)
                    activeParameter++;
            }
        }
        var signatures = methods.Select(method =>
        {
            var documentation = _model.Documentation.GetDocumentation(method);
            return new LanguageSignature(
                FormatMethod(method),
                [.. method.Parameters.Select(parameter => new LanguageParameter(
                    FormatParameter(parameter),
                    documentation?.Parameters.FirstOrDefault(item => item.Name == parameter.Name)?.Text))],
                documentation);
        }).ToImmutableArray();
        var activeSignature = Array.FindIndex(methods, method => method.Parameters.Length > activeParameter);
        return new LanguageSignatureHelp(signatures, Math.Max(0, activeSignature), activeParameter);
    }

    public LanguageDefinition? GetDefinition(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return null;
        if (InlineAssemblyReferenceAt(tree, position) is { } assemblyReference &&
            _boundProgram.SemanticMap.TryGetValue(assemblyReference, out var assemblySemantic) &&
            assemblySemantic.Symbol is { } assemblySymbol && SymbolSyntax(assemblySymbol) is { } assemblyDeclaration)
            return new LanguageDefinition(assemblyDeclaration.Source.FilePath, SelectionSpan(assemblyDeclaration, SymbolName(assemblySymbol)));
        var token = NavigationTokenAt(tree, position);
        if (token is null)
            return null;
        var context = CreateContext(tree, position);
        foreach (var symbol in ResolveToken(context, token))
        {
            var syntax = SymbolSyntax(symbol);
            if (syntax is not null)
                return new LanguageDefinition(syntax.Source.FilePath, SelectionSpan(syntax, SymbolName(symbol)));
        }
        return null;
    }

    public ImmutableArray<LanguageDocumentSymbol> GetDocumentSymbols(string filePath)
    {
        if (!TryGetTree(filePath, out var tree))
            return [];
        var result = ImmutableArray.CreateBuilder<LanguageDocumentSymbol>();
        foreach (var type in tree.Root.Types)
        {
            var children = ImmutableArray.CreateBuilder<LanguageDocumentSymbol>();
            foreach (var member in type.Members)
            {
                var kind = member switch
                {
                    FieldDeclarationSyntax => LanguageSymbolKind.Field,
                    PropertyDeclarationSyntax => LanguageSymbolKind.Property,
                    ConstructorDeclarationSyntax => LanguageSymbolKind.Constructor,
                    _ => LanguageSymbolKind.Method,
                };
                var name = member switch
                {
                    FieldDeclarationSyntax field => field.Name,
                    PropertyDeclarationSyntax property => property.Name,
                    ConstructorDeclarationSyntax constructor => constructor.Name,
                    MethodDeclarationSyntax method => method.Name,
                    OperatorDeclarationSyntax @operator => OperatorFacts.DisplayName(@operator.OperatorToken.Kind),
                    _ => string.Empty,
                };
                children.Add(new LanguageDocumentSymbol(name, MemberDetail(member), kind, member.Span, SelectionSpan(member, name), []));
            }
            foreach (var enumMember in type.EnumMembers)
                children.Add(new LanguageDocumentSymbol(enumMember.Name, string.Empty, LanguageSymbolKind.EnumMember, enumMember.Span, NameSpan(enumMember, enumMember.Name), []));
            var typeKind = type.Kind switch
            {
                TypeDeclarationKind.Struct or TypeDeclarationKind.Union or TypeDeclarationKind.Newtype => LanguageSymbolKind.Struct,
                TypeDeclarationKind.Enum => LanguageSymbolKind.Enum,
                _ => LanguageSymbolKind.Class,
            };
            var detail = type.Kind == TypeDeclarationKind.Delegate
                ? $"{type.DelegateReturnType}({string.Join(", ", type.DelegateParameters.Select(parameter => parameter.Type.ToString()))})"
                : tree.Root.Namespace?.Name ?? string.Empty;
            result.Add(new LanguageDocumentSymbol(type.Name, detail, typeKind, type.Span, NameSpan(type, type.Name), children.ToImmutable()));
        }
        return result.ToImmutable();
    }

    public ImmutableArray<LanguageWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        query ??= string.Empty;
        var result = ImmutableArray.CreateBuilder<LanguageWorkspaceSymbol>();
        foreach (var type in _model.UserTypes)
        {
            if (type.Syntax is null)
                continue;
            Add(type.Name, type.Namespace, TypeKind(type), type.Syntax, type.Name);
            foreach (var member in type.Fields.Cast<object>().Concat(type.Properties).Concat(type.Methods).Concat(type.Constructors))
            {
                if (SymbolSyntax(member) is { } syntax)
                    Add(SymbolName(member), type.FullName, MemberKind(member), syntax, SymbolName(member));
            }
            foreach (var value in type.EnumValues)
                Add(value.Name, type.FullName, LanguageSymbolKind.EnumMember, value.Syntax, value.Name);
        }
        return result.ToImmutable();

        void Add(string name, string container, LanguageSymbolKind kind, SyntaxNode syntax, string selection)
        {
            if (query.Length != 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return;
            result.Add(new LanguageWorkspaceSymbol(name, container, kind, new LanguageDefinition(syntax.Source.FilePath, SelectionSpan(syntax, selection))));
        }
    }

    public bool TryGetSourceText(string filePath, out SourceText sourceText)
    {
        if (TryGetTree(filePath, out var tree))
        {
            sourceText = tree.Text;
            return true;
        }
        sourceText = null!;
        return false;
    }

    private void AddContextCompletions(List<LanguageCompletion> results, DocumentContext context, TextSpan replacement)
    {
        var insideType = context.TypeDeclaration is not null;
        var insideBody = context.MemberDeclaration is not null;
        foreach (var keyword in insideBody ? StatementKeywords : insideType ? TypeKeywords : TopLevelKeywords)
            Add(keyword, LanguageCompletionKind.Keyword, "keyword", "0");
        if (!insideBody)
        {
            Add("Packed", LanguageCompletionKind.Keyword, "aggregate layout attribute", "0");
            if (insideType)
                Add("FieldOffset", LanguageCompletionKind.Keyword, "explicit field layout attribute", "0");
        }
        foreach (var effect in new[] { "NoAlloc", "NoThrow", "NoBlock", "NoRuntime" })
            Add(effect, LanguageCompletionKind.Keyword, "analysis-only effect contract attribute", "0");
        if (insideType)
        {
            Add("ConstInit", LanguageCompletionKind.Keyword, "compile-time initialized immutable data attribute", "0");
            Add("Naked", LanguageCompletionKind.Keyword, "freestanding naked startup attribute", "0");
            Add("Interrupt", LanguageCompletionKind.Keyword, "ESP-IDF interrupt entry attribute", "0");
            Add("InterruptSafe", LanguageCompletionKind.Keyword, "trusted interrupt-safe native boundary attribute", "0");
        }
        foreach (var builtIn in BuiltInTypes)
            Add(builtIn, LanguageCompletionKind.Keyword, "built-in type", "1");
        Add("NativeBuffer", LanguageCompletionKind.Struct, "System.Runtime.NativeBuffer<T>", "1", documentationId: "T:System.Runtime.NativeBuffer<T>");
        Add("ReadOnlyNativeBuffer", LanguageCompletionKind.Struct, "System.Runtime.ReadOnlyNativeBuffer<T>", "1", documentationId: "T:System.Runtime.ReadOnlyNativeBuffer<T>");
        if (!_model.Types.ContainsKey("System.Runtime.NativeUtf8String"))
            Add("NativeUtf8String", LanguageCompletionKind.Struct, "System.Runtime.NativeUtf8String", "1", documentationId: "T:System.Runtime.NativeUtf8String");

        foreach (var type in VisibleTypes(context.Tree))
            Add(type.Name, CompletionKind(type), FormatType(type), "2", type);
        foreach (var @namespace in _model.Types.Values.Select(type => type.Namespace).Where(value => !string.IsNullOrEmpty(value)).Select(value => value.Split('.')[0]).Distinct(StringComparer.Ordinal))
            Add(@namespace, LanguageCompletionKind.Namespace, "namespace", "2");

        if (context.MemberDeclaration is MethodDeclarationSyntax method)
            foreach (var parameter in method.Parameters)
                Add(parameter.Name, LanguageCompletionKind.Parameter, $"{parameter.Type} {parameter.Name}", "3");
        if (context.MemberDeclaration is OperatorDeclarationSyntax @operator)
            foreach (var parameter in @operator.Parameters)
                Add(parameter.Name, LanguageCompletionKind.Parameter, $"{parameter.Type} {parameter.Name}", "3");
        if (context.MemberDeclaration is ConstructorDeclarationSyntax constructor)
            foreach (var parameter in constructor.Parameters)
                Add(parameter.Name, LanguageCompletionKind.Parameter, $"{parameter.Type} {parameter.Name}", "3");
        foreach (var local in VisibleLocals(context))
            Add(local.Name, LanguageCompletionKind.Variable, $"{local.Type} {local.Name}", "3");

        if (context.TypeSymbol is not null)
        {
            var requireStatic = IsStatic(context.MemberDeclaration);
            foreach (var field in Hierarchy(context.TypeSymbol).SelectMany(type => type.Fields).Where(field => field.Syntax is not null && (!requireStatic || field.IsStatic) && IsAccessible(field, context.TypeSymbol)))
                Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "4", field);
            foreach (var property in Hierarchy(context.TypeSymbol).SelectMany(type => type.Properties).Where(property => !requireStatic || property.IsStatic).Where(property => IsAccessible(property, context.TypeSymbol)))
                Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "4", property);
            foreach (var candidate in Hierarchy(context.TypeSymbol).SelectMany(type => type.Methods).Where(methodSymbol => !methodSymbol.IsOperator && (!requireStatic || methodSymbol.IsStatic)).Where(methodSymbol => IsAccessible(methodSymbol, context.TypeSymbol)))
                Add(candidate.Name, LanguageCompletionKind.Method, FormatMethod(candidate), "5", candidate);
            if (!requireStatic)
            {
                Add("this", LanguageCompletionKind.Keyword, context.TypeSymbol.FullName, "0");
                if (context.TypeSymbol.BaseType is not null)
                    Add("base", LanguageCompletionKind.Keyword, context.TypeSymbol.BaseType.FullName, "0");
            }
        }

        void Add(string label, LanguageCompletionKind kind, string detail, string order, object? symbol = null, string? documentationId = null) =>
            results.Add(new LanguageCompletion(label, kind, detail, label, replacement, order + label, documentationId ?? (symbol is null ? null : _model.Documentation.GetId(symbol))));
    }

    private static string? ContractAttributeHover(string name) => name switch
    {
        "NoAlloc" => "[NoAlloc] - the callable and its transitive calls do not allocate managed storage.",
        "NoThrow" => "[NoThrow] - the callable uses no exception machinery or potentially failing runtime checks; implies NoAlloc.",
        "NoBlock" => "[NoBlock] - the callable does not wait for time, I/O, synchronization, another thread, or external progress.",
        "NoRuntime" => "[NoRuntime] - the callable is bootstrap-safe and uses no managed runtime; implies NoThrow and NoAlloc.",
        "Interrupt" => "[Interrupt] - an ESP-IDF native-only void(void*) entry whose transitive C~ closure is NoRuntime, NoBlock, and IRAM/DRAM safe.",
        "InterruptSafe" => "[InterruptSafe] - a trusted cache-disabled-safe extern, extern-data, inline-assembly, or assembly-function boundary; effect contracts remain explicit.",
        "ConstInit" => "[ConstInit] - emit immutable unmanaged static readonly data directly in the native image without module initialization.",
        "Naked" => "[Naked] - a freestanding exported startup function whose complete raw assembly body owns control flow.",
        _ => null,
    };

    private static bool IsAssemblyResult(BoundSemanticEntry semantic) =>
        semantic.Symbol is null && semantic.ValueCategory == BoundValueCategory.Variable && !semantic.Type.IsError &&
        (semantic.Syntax is InlineAssemblyReferenceSyntax || semantic.Syntax is NameExpressionSyntax { Name: "result" });

    private void AddMemberCompletions(List<LanguageCompletion> results, DocumentContext context, MemberAccessExpressionSyntax member, TextSpan replacement)
    {
        var receiver = InferExpression(context, member.Receiver);
        if (receiver.StaticType is not null)
        {
            if (receiver.StaticType.Kind == DeclaredTypeKind.Enum)
                foreach (var value in receiver.StaticType.EnumValues)
                    Add(value.Name, LanguageCompletionKind.EnumMember, $"{receiver.StaticType.FullName}.{value.Name}", "0", value);
            foreach (var field in Hierarchy(receiver.StaticType).SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Syntax is not null && IsAccessible(field, context.TypeSymbol)))
                Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "1", field);
            foreach (var property in Hierarchy(receiver.StaticType).SelectMany(type => type.Properties).Where(property => property.IsStatic && IsAccessible(property, context.TypeSymbol)))
                Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "1", property);
            foreach (var method in Hierarchy(receiver.StaticType).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.IsStatic && IsAccessible(method, context.TypeSymbol)))
                Add(method.Name, LanguageCompletionKind.Method, FormatMethod(method), "2", method);
            return;
        }

        if (receiver.Type is null || receiver.Type.IsError)
        {
            AddNamespaceCompletions(results, member, replacement);
            return;
        }
        if (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array)
            Add("Length", LanguageCompletionKind.Property, "int Length", "0");
        if (receiver.Type.IsNativeBuffer)
        {
            var bufferType = receiver.Type.Kind == CTypeKind.NativeBuffer ? "NativeBuffer<T>" : "ReadOnlyNativeBuffer<T>";
            Add("Length", LanguageCompletionKind.Property, "nuint Length", "0", documentationId: $"P:System.Runtime.{bufferType}.Length");
            Add("Pointer", LanguageCompletionKind.Property, $"{receiver.Type.ElementType!.DisplayName}* Pointer", "0", documentationId: $"P:System.Runtime.{bufferType}.Pointer");
        }
        if (receiver.Type.IsNativeUtf8String)
        {
            Add("ByteLength", LanguageCompletionKind.Property, "nuint ByteLength", "0", documentationId: "P:System.Runtime.NativeUtf8String.ByteLength");
            Add("Pointer", LanguageCompletionKind.Property, "byte* Pointer", "0", documentationId: "P:System.Runtime.NativeUtf8String.Pointer");
        }
        var type = receiver.Type.Symbol;
        if (type is null && (receiver.Type.IsValueType || receiver.Type.Kind is CTypeKind.String or CTypeKind.Array))
            type = _model.Types.GetValueOrDefault("System.Object");
        if (type is null)
            return;
        foreach (var field in Hierarchy(type).SelectMany(candidate => candidate.Fields).Where(field => !field.IsStatic && field.Syntax is not null && IsAccessible(field, context.TypeSymbol)))
            Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "1", field);
        foreach (var property in Hierarchy(type).SelectMany(candidate => candidate.Properties).Where(property => !property.IsStatic && IsAccessible(property, context.TypeSymbol)))
            Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "1", property);
        foreach (var method in Hierarchy(type).SelectMany(candidate => candidate.Methods).Where(method => !method.IsOperator && !method.IsStatic && IsAccessible(method, context.TypeSymbol)))
            Add(method.Name, LanguageCompletionKind.Method, FormatMethod(method), "2", method);

        void Add(string label, LanguageCompletionKind kind, string detail, string order, object? symbol = null, string? documentationId = null) =>
            results.Add(new LanguageCompletion(label, kind, detail, label, replacement, order + label, documentationId ?? (symbol is null ? null : _model.Documentation.GetId(symbol))));
    }

    private void AddNamespaceCompletions(List<LanguageCompletion> results, MemberAccessExpressionSyntax member,
        TextSpan replacement)
    {
        var namespaceName = QualifiedName(member.Receiver);
        if (string.IsNullOrEmpty(namespaceName))
            return;
        var prefix = namespaceName + ".";
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in _model.Types.Values)
        {
            if (type.Namespace == namespaceName)
            {
                results.Add(new LanguageCompletion(type.Name, CompletionKind(type), FormatType(type), type.Name,
                    replacement, "1" + type.Name, _model.Documentation.GetId(type)));
                continue;
            }
            if (!type.Namespace.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var remainder = type.Namespace[prefix.Length..];
            var separator = remainder.IndexOf('.');
            namespaces.Add(separator < 0 ? remainder : remainder[..separator]);
        }
        foreach (var child in namespaces)
            results.Add(new LanguageCompletion(child, LanguageCompletionKind.Namespace,
                $"namespace {namespaceName}.{child}", child, replacement, "0" + child));
    }

    private IEnumerable<object> ResolveToken(DocumentContext context, SyntaxToken token)
    {
        if (OperatorFacts.IsSupported(token.Kind))
        {
            if (context.MemberDeclaration is OperatorDeclarationSyntax declaration && declaration.OperatorToken.Span == token.Span &&
                context.TypeSymbol?.Methods.FirstOrDefault(method => method.IsOperator && ReferenceEquals(method.Syntax, declaration)) is { } declaredOperator)
            {
                yield return declaredOperator;
                yield break;
            }
            if (TryGetBoundEntry(context.Tree, token.Span, out var entry) && entry.Symbol is MethodSymbol { IsOperator: true } boundOperator)
            {
                yield return boundOperator;
                yield break;
            }
        }
        var tokenName = IdentifierValue(token);
        var offsetOf = context.Nodes.OfType<OffsetOfExpressionSyntax>()
            .Where(candidate => candidate.Span.Start <= token.Span.Start && candidate.Span.End >= token.Span.End && IdentifierEquals(candidate.FieldName, tokenName))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (offsetOf is not null)
        {
            var aggregate = _model.ResolveType(offsetOf.Type, context.Tree, false).Symbol;
            var offsetField = aggregate?.Fields.FirstOrDefault(field => !field.IsStatic && IdentifierEquals(field.Name, tokenName));
            if (offsetField is not null)
                yield return offsetField;
            yield break;
        }
        var member = context.Nodes.OfType<MemberAccessExpressionSyntax>()
            .Where(candidate => IdentifierEquals(candidate.Name, tokenName) && candidate.Receiver.Span.End <= token.Span.Start && candidate.Span.End >= token.Span.End)
            .OrderBy(candidate => candidate.Span.Length).FirstOrDefault();
        if (member is not null)
        {
            var receiver = InferExpression(context, member.Receiver);
            var receiverType = receiver.StaticType ?? receiver.Type?.Symbol;
            if (receiverType is null && receiver.Type?.IsNativeUtf8String == true)
                receiverType = _model.Types.GetValueOrDefault("System.Runtime.NativeUtf8String");
            if (receiverType is not null)
            {
                if (receiver.StaticType?.Kind == DeclaredTypeKind.Enum)
                    foreach (var value in receiverType.EnumValues.Where(value => IdentifierEquals(value.Name, tokenName)))
                        yield return value;
                foreach (var value in Hierarchy(receiverType)
                    .SelectMany(type => type.Fields.Cast<object>().Concat(type.Properties).Concat(type.Methods))
                    .Where(symbol => IdentifierEquals(SymbolName(symbol), tokenName) && IsQualifiedMember(symbol, receiver.StaticType is not null, context.TypeSymbol)))
                    yield return value;
            }
            yield break;
        }

        if (ResolveScopedVariable(context, tokenName) is { } scoped)
        {
            yield return scoped;
            yield break;
        }
        foreach (var parameter in Parameters(context).Where(parameter => IdentifierEquals(parameter.Name, tokenName)))
        {
            yield return parameter;
            yield break;
        }
        if (context.TypeSymbol is not null)
        {
            var requireStatic = IsStatic(context.MemberDeclaration);
            foreach (var value in Hierarchy(context.TypeSymbol)
                .SelectMany(type => type.Fields.Cast<object>().Concat(type.Properties).Concat(type.Methods))
                .Where(symbol => IdentifierEquals(SymbolName(symbol), tokenName) && IsUnqualifiedMember(symbol, requireStatic, context.TypeSymbol)))
                yield return value;
        }
        var typeSymbol = _model.ResolveNamedType(token.Text, context.Tree) ?? VisibleTypes(context.Tree).FirstOrDefault(type => IdentifierEquals(type.Name, tokenName));
        if (typeSymbol is not null)
            yield return typeSymbol;
    }

    private object? ResolveScopedVariable(DocumentContext context, string name)
    {
        var candidates = new List<(object Symbol, TextSpan Scope, int DeclarationStart)>();
        foreach (var local in VisibleLocals(context).Where(local => IdentifierEquals(local.Name, name)))
        {
            var scope = context.Parent(local);
            while (scope is not null && scope is not BlockStatementSyntax and not ForStatementSyntax)
                scope = context.Parent(scope);
            candidates.Add((local, scope?.Span ?? context.Tree.Root.Span, local.Span.Start));
        }
        foreach (var loop in context.Nodes.OfType<ForeachStatementSyntax>()
            .Where(loop => IdentifierEquals(loop.Name, name) && ContainsPosition(loop.Body.Span, context.Position)))
        {
            candidates.Add((new LocalSemanticSymbol(loop.Name, loop.Type, loop, true), loop.Body.Span, loop.Span.Start));
        }
        foreach (var clause in context.Nodes.OfType<CatchClauseSyntax>()
            .Where(clause => clause.Name is not null && IdentifierEquals(clause.Name, name) && ContainsPosition(clause.Body.Span, context.Position)))
        {
            candidates.Add((new LocalSemanticSymbol(clause.Name!, clause.Type, clause, true), clause.Body.Span, clause.Span.Start));
        }
        return candidates.OrderBy(candidate => candidate.Scope.Length).ThenByDescending(candidate => candidate.DeclarationStart).Select(candidate => candidate.Symbol).FirstOrDefault();
    }

    private IEnumerable<MethodSymbol> ResolveCallCandidates(DocumentContext context, CallExpressionSyntax call)
    {
        if (call.Target is NameExpressionSyntax name && context.TypeSymbol is not null)
            return Hierarchy(context.TypeSymbol).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.Name == name.Name);
        if (call.Target is not MemberAccessExpressionSyntax member)
            return [];
        var receiver = InferExpression(context, member.Receiver);
        var type = receiver.StaticType ?? receiver.Type?.Symbol;
        if (type is null && receiver.Type is { } valueType && (valueType.IsValueType || valueType.Kind is CTypeKind.String or CTypeKind.Array))
            type = _model.Types.GetValueOrDefault("System.Object");
        return type is null ? [] : Hierarchy(type).SelectMany(candidate => candidate.Methods).Where(method => !method.IsOperator && method.Name == member.Name && method.IsStatic == (receiver.StaticType is not null));
    }

    private InferredExpression InferExpression(DocumentContext context, ExpressionSyntax expression)
    {
        if (_boundProgram.SemanticMap.TryGetValue(expression, out var bound) && bound.Type != CType.Error)
            return new(bound.Type, null);
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return new(literal.LiteralKind switch
                {
                    SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => CType.Bool,
                    SyntaxKind.StringToken => CType.String,
                    SyntaxKind.CharacterToken => CType.Char,
                    SyntaxKind.RuneToken => CType.Rune,
                    SyntaxKind.NullKeyword => CType.Null,
                    SyntaxKind.NumberToken when literal.Value is NumericLiteralValue numeric => InferNumericLiteralType(numeric),
                    _ => CType.Int,
                }, null);
            case ThisExpressionSyntax:
                return new(context.TypeSymbol?.Type, null);
            case BaseExpressionSyntax:
                return new(context.TypeSymbol?.BaseType?.Type, null);
            case ParenthesizedExpressionSyntax parenthesized:
                return InferExpression(context, parenthesized.Expression);
            case NewExpressionSyntax @new:
                return new(_model.ResolveType(@new.Type, context.Tree, false), null);
            case StackAllocExpressionSyntax stackAlloc:
                return new(new CType(CTypeKind.NativeBuffer, ElementType: _model.ResolveType(stackAlloc.ElementType, context.Tree, false)), null);
            case SizeOfExpressionSyntax or AlignOfExpressionSyntax or OffsetOfExpressionSyntax:
                return new(CType.Nuint, null);
            case CastExpressionSyntax cast:
                return new(_model.ResolveType(cast.Type, context.Tree, false), null);
            case IndexExpressionSyntax index:
                var indexed = InferExpression(context, index.Receiver).Type;
                return new(indexed?.Kind == CTypeKind.String ? CType.Char : indexed?.ElementType, null);
            case NameExpressionSyntax name:
                var local = VisibleLocals(context).LastOrDefault(candidate => candidate.Name == name.Name);
                if (local is not null)
                    return new(_model.ResolveType(local.Type, context.Tree, false), null);
                var parameter = Parameters(context).FirstOrDefault(candidate => candidate.Name == name.Name);
                if (parameter is not null)
                    return new(_model.ResolveType(parameter.Type, context.Tree, false), null);
                if (context.TypeSymbol is not null)
                {
                    var field = Hierarchy(context.TypeSymbol).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == name.Name);
                    if (field is not null)
                        return new(field.Type, null);
                    var property = Hierarchy(context.TypeSymbol).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == name.Name);
                    if (property is not null)
                        return new(property.Type, null);
                }
                return new(null, _model.ResolveNamedType(name.Name, context.Tree));
            case MemberAccessExpressionSyntax member:
                var qualifiedType = QualifiedName(member) is { } qualifiedName
                    ? _model.ResolveNamedType(qualifiedName, context.Tree)
                    : null;
                if (qualifiedType is not null)
                    return new(null, qualifiedType);
                var receiver = InferExpression(context, member.Receiver);
                if (receiver.StaticType is not null)
                {
                    var staticField = Hierarchy(receiver.StaticType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == member.Name && candidate.IsStatic);
                    if (staticField is not null)
                        return new(staticField.Type, null);
                    var staticProperty = Hierarchy(receiver.StaticType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == member.Name && candidate.IsStatic);
                    if (staticProperty is not null)
                        return new(staticProperty.Type, null);
                    return new(null, null);
                }
                if (receiver.Type?.Kind is CTypeKind.String or CTypeKind.Array && member.Name == "Length")
                    return new(CType.Int, null);
                if (receiver.Type?.IsNativeUtf8String == true)
                    return new(member.Name == "ByteLength" ? CType.Nuint : member.Name == "Pointer" ? new CType(CTypeKind.Pointer, ElementType: CType.Byte) : null, null);
                var receiverType = receiver.Type?.Symbol;
                if (receiverType is not null)
                {
                    var field = Hierarchy(receiverType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == member.Name && !candidate.IsStatic);
                    if (field is not null)
                        return new(field.Type, null);
                    var property = Hierarchy(receiverType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == member.Name && !candidate.IsStatic);
                    if (property is not null)
                        return new(property.Type, null);
                }
                return new(null, null);
            case CallExpressionSyntax call:
                var candidate = ResolveCallCandidates(context, call).FirstOrDefault(method => method.Parameters.Length == call.Arguments.Length) ?? ResolveCallCandidates(context, call).FirstOrDefault();
                return new(candidate?.ReturnType, null);
            default:
                return new(null, null);
        }
    }

    private static CType InferNumericLiteralType(NumericLiteralValue numeric)
    {
        if (numeric.FloatingPoint is not null)
            return numeric.FloatingKind == FloatingLiteralKind.Double ? CType.Double : CType.Float;
        if (numeric.Suffix == IntegerLiteralSuffix.None && numeric.Integer <= int.MaxValue)
            return CType.Int;
        if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Unsigned && numeric.Integer <= uint.MaxValue)
            return CType.Uint;
        if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Long && numeric.Integer <= long.MaxValue)
            return CType.Long;
        return CType.Ulong;
    }

    private IEnumerable<TypeSymbol> VisibleTypes(SyntaxTree tree)
    {
        var currentNamespace = tree.Root.Namespace?.Name ?? string.Empty;
        var imports = tree.Root.Usings.Select(value => value.Name).Append("System").ToHashSet(StringComparer.Ordinal);
        return _model.Types.Values.Where(type => type.Namespace == currentNamespace || string.IsNullOrEmpty(type.Namespace) || imports.Contains(type.Namespace));
    }

    private IEnumerable<LocalDeclarationStatementSyntax> VisibleLocals(DocumentContext context)
    {
        var current = context.SmallestNode;
        return context.Nodes.OfType<LocalDeclarationStatementSyntax>()
            .Where(local => local.Span.Start < context.Position)
            .Where(local =>
            {
                var scope = context.Parent(local);
                while (scope is not null && scope is not BlockStatementSyntax and not ForStatementSyntax)
                    scope = context.Parent(scope);
                return scope is null || current is not null && context.IsAncestor(scope, current);
            })
            .OrderBy(local => local.Span.Start);
    }

    private static IEnumerable<ParameterSyntax> Parameters(DocumentContext context) => context.MemberDeclaration switch
    {
        MethodDeclarationSyntax method => method.Parameters,
        OperatorDeclarationSyntax @operator => @operator.Parameters,
        ConstructorDeclarationSyntax constructor => constructor.Parameters,
        _ => [],
    };

    private MemberAccessExpressionSyntax? FindMemberAccess(DocumentContext context, TextSpan replacement)
    {
        var parsed = context.Nodes
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Receiver.Span.End < replacement.Start || member.Receiver.Span.End < context.Position)
            .Where(member => member.Span.Start <= replacement.Start && member.Span.End >= replacement.Start)
            .OrderBy(member => member.Span.Length)
            .FirstOrDefault();
        if (parsed is not null)
            return parsed;

        var text = context.Tree.Text.Text;
        var dot = replacement.Start - 1;
        while (dot >= 0 && char.IsWhiteSpace(text[dot]))
            dot--;
        if (dot < 0 || text[dot] != '.')
            return null;

        var receiver = context.Nodes
            .OfType<ExpressionSyntax>()
            .Where(expression => expression.Span.End <= dot)
            .Where(expression => IsWhitespace(text, expression.Span.End, dot))
            .OrderByDescending(expression => expression.Span.Length)
            .FirstOrDefault() ?? TextualReceiver(context.Tree.Text, dot);
        if (receiver is null)
            return null;

        var name = context.Tree.Text.Slice(replacement);
        return new MemberAccessExpressionSyntax(context.Tree.Text, TextSpan.FromBounds(receiver.Span.Start, replacement.End), receiver, name);
    }

    private static ExpressionSyntax? TextualReceiver(SourceText source, int dot)
    {
        var text = source.Text;
        var segments = new Stack<(string Name, TextSpan Span)>();
        var end = dot;
        while (end > 0)
        {
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            var start = end;
            while (start > 0 && IsIdentifierPart(text[start - 1]))
                start--;
            if (start == end)
                break;
            segments.Push((text[start..end], TextSpan.FromBounds(start, end)));
            var separator = start;
            while (separator > 0 && char.IsWhiteSpace(text[separator - 1]))
                separator--;
            if (separator == 0 || text[separator - 1] != '.')
                break;
            end = separator - 1;
        }
        if (!segments.TryPop(out var first))
            return null;
        ExpressionSyntax receiver = first.Name switch
        {
            "this" => new ThisExpressionSyntax(source, first.Span),
            "base" => new BaseExpressionSyntax(source, first.Span),
            _ => new NameExpressionSyntax(source, first.Span, first.Name),
        };
        while (segments.TryPop(out var segment))
            receiver = new MemberAccessExpressionSyntax(source, TextSpan.FromBounds(receiver.Span.Start, segment.Span.End), receiver, segment.Name);
        return receiver;
    }

    private static bool IsWhitespace(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (!char.IsWhiteSpace(text[index]))
                return false;
        return true;
    }

    private static SyntaxToken? IdentifierTokenAt(SyntaxTree tree, int position) => tree.Tokens
        .Where(token => token.Kind == SyntaxKind.IdentifierToken && !token.IsMissing)
        .Where(token => position >= token.Span.Start && position <= token.Span.End)
        .OrderBy(token => token.Span.Length)
        .FirstOrDefault();

    private static InlineAssemblyReferenceSyntax? InlineAssemblyReferenceAt(SyntaxTree tree, int position) => DescendantInlineAssemblyReferences(tree.Root)
        .Where(reference => position >= reference.Span.Start && position <= reference.Span.End)
        .OrderBy(reference => reference.Span.Length)
        .FirstOrDefault();

    private static IEnumerable<InlineAssemblyReferenceSyntax> DescendantInlineAssemblyReferences(SyntaxNode node)
    {
        if (node is InlineAssemblyReferenceSyntax reference)
            yield return reference;
        foreach (var child in node.ChildNodesAndTokens().Where(item => item.IsNode).Select(item => item.Node!))
            foreach (var descendant in DescendantInlineAssemblyReferences(child))
                yield return descendant;
    }

    private static SyntaxToken? HoverTokenAt(SyntaxTree tree, int position) => tree.Tokens
        .Where(token => !token.IsMissing && (token.Kind == SyntaxKind.IdentifierToken || TypeFacts.BuiltIn(token.Text) is not null || OperatorFacts.IsSupported(token.Kind)))
        .Where(token => position >= token.Span.Start && position <= token.Span.End)
        .OrderBy(token => token.Span.Length)
        .FirstOrDefault();

    private static SyntaxToken? NavigationTokenAt(SyntaxTree tree, int position) => tree.Tokens
        .Where(token => !token.IsMissing && (token.Kind == SyntaxKind.IdentifierToken || OperatorFacts.IsSupported(token.Kind)))
        .Where(token => position >= token.Span.Start && position <= token.Span.End)
        .OrderBy(token => token.Span.Length)
        .FirstOrDefault();

    private DocumentContext CreateContext(SyntaxTree tree, int position) =>
        new(_documentIndexes[NormalizePath(tree.Text.FilePath)], position);

    private bool TryGetBoundEntry(SyntaxTree tree, TextSpan span, out BoundSemanticEntry entry)
    {
        if (_semanticEntries.TryGetValue(NormalizePath(tree.Text.FilePath), out var entries))
        {
            foreach (var candidate in entries)
            {
                if (candidate.Syntax.Span.Start <= span.Start && candidate.Syntax.Span.End >= span.End)
                {
                    entry = candidate;
                    return true;
                }
            }
        }
        entry = null!;
        return false;
    }

    private bool TryGetTree(string filePath, out SyntaxTree tree) => _treesByPath.TryGetValue(NormalizePath(filePath), out tree!);

    private string NormalizePath(string path)
    {
        if (path.StartsWith("stdlib/", StringComparison.Ordinal))
            return path.Replace('\\', '/');
        try { return Path.GetFullPath(path); }
        catch (Exception) when (path.Length != 0) { return path; }
    }

    private static TextSpan IdentifierSpan(string text, int position)
    {
        var start = position;
        while (start > 0 && IsIdentifierPart(text[start - 1]))
            start--;
        var end = position;
        while (end < text.Length && IsIdentifierPart(text[end]))
            end++;
        return TextSpan.FromBounds(start, end);
    }

    private static bool IsIdentifierPart(char value) => value == '_' || value == '@' || char.IsLetterOrDigit(value) || value >= 0x80;

    private static string IdentifierValue(SyntaxToken token) => NormalizeIdentifier(token.Value as string ?? token.Text);

    private static bool IdentifierEquals(string left, string right) =>
        string.Equals(NormalizeIdentifier(left), NormalizeIdentifier(right), StringComparison.Ordinal);

    private static string NormalizeIdentifier(string value) => value.StartsWith('@') ? value[1..] : value;

    private static bool ContainsPosition(TextSpan span, int position) => position >= span.Start && position <= span.End;

    private static string? QualifiedName(ExpressionSyntax expression)
    {
        var parts = new Stack<string>();
        while (expression is MemberAccessExpressionSyntax member)
        {
            parts.Push(member.Name);
            expression = member.Receiver;
        }
        if (expression is not NameExpressionSyntax name)
            return null;
        parts.Push(name.Name);
        return string.Join('.', parts);
    }

    private static IEnumerable<TypeSymbol> Hierarchy(TypeSymbol type)
    {
        var pending = new Stack<TypeSymbol>();
        pending.Push(type);
        var visited = new HashSet<TypeSymbol>();
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            yield return current;
            foreach (var contract in current.Interfaces.AsEnumerable().Reverse())
                pending.Push(contract);
            if (current.BaseType is not null)
                pending.Push(current.BaseType);
        }
    }

    private static bool IsAccessible(MemberSymbol member, TypeSymbol? currentType) => member.Accessibility switch
    {
        Accessibility.Public or Accessibility.Internal => true,
        Accessibility.Private => currentType == member.ContainingType,
        Accessibility.Protected => currentType is not null && (currentType == member.ContainingType || currentType.DerivesFrom(member.ContainingType)),
        _ => false,
    };

    private static bool IsQualifiedMember(object symbol, bool staticReceiver, TypeSymbol? currentType) =>
        symbol is MemberSymbol member && member.IsStatic == staticReceiver && IsAccessible(member, currentType);

    private static bool IsUnqualifiedMember(object symbol, bool staticContext, TypeSymbol? currentType) =>
        symbol is MemberSymbol member && (!staticContext || member.IsStatic) && IsAccessible(member, currentType);

    private static bool IsStatic(MemberDeclarationSyntax? member) => member?.Modifiers.Contains("static", StringComparer.Ordinal) == true;

    private static LanguageCompletionKind CompletionKind(TypeSymbol type) => type.Kind switch
    {
        DeclaredTypeKind.Struct or DeclaredTypeKind.Newtype => LanguageCompletionKind.Struct,
        DeclaredTypeKind.Enum => LanguageCompletionKind.Enum,
        _ => LanguageCompletionKind.Class,
    };

    private static LanguageSymbolKind TypeKind(TypeSymbol type) => type.Kind switch
    {
        DeclaredTypeKind.Struct or DeclaredTypeKind.Newtype => LanguageSymbolKind.Struct,
        DeclaredTypeKind.Enum => LanguageSymbolKind.Enum,
        _ => LanguageSymbolKind.Class,
    };

    private static LanguageSymbolKind MemberKind(object symbol) => symbol switch
    {
        FieldSymbol => LanguageSymbolKind.Field,
        PropertySymbol => LanguageSymbolKind.Property,
        MethodSymbol { IsConstructor: true } => LanguageSymbolKind.Constructor,
        _ => LanguageSymbolKind.Method,
    };

    private sealed record InferredExpression(CType? Type, TypeSymbol? StaticType);

    private sealed record LocalSemanticSymbol(string Name, TypeSyntax? Type, SyntaxNode Syntax, bool IsReadonly);

    private sealed class DocumentContext
    {
        private readonly DocumentIndex _index;

        public DocumentContext(DocumentIndex index, int position)
        {
            _index = index;
            Tree = index.Tree;
            Position = position;
            Nodes = index.Nodes;
            SmallestNode = Nodes.Where(node => Contains(node.Span, position)).OrderBy(node => node.Span.Length).FirstOrDefault();
            TypeDeclaration = Nodes.OfType<TypeDeclarationSyntax>().Where(node => Contains(node.Span, position)).OrderBy(node => node.Span.Length).FirstOrDefault();
            MemberDeclaration = Nodes.OfType<MemberDeclarationSyntax>().Where(node => Contains(node.Span, position)).OrderBy(node => node.Span.Length).FirstOrDefault();
            TypeSymbol = TypeDeclaration is null ? null : index.TypeSymbols.GetValueOrDefault(TypeDeclaration);
        }

        public SyntaxTree Tree { get; }
        public int Position { get; }
        public IReadOnlyList<SyntaxNode> Nodes { get; }
        public SyntaxNode? SmallestNode { get; }
        public TypeDeclarationSyntax? TypeDeclaration { get; }
        public MemberDeclarationSyntax? MemberDeclaration { get; }
        public TypeSymbol? TypeSymbol { get; }

        public SyntaxNode? Parent(SyntaxNode node) => _index.Parent(node);

        public bool IsAncestor(SyntaxNode ancestor, SyntaxNode node)
        {
            for (var current = node; current is not null; current = Parent(current))
                if (ReferenceEquals(current, ancestor))
                    return true;
            return false;
        }

        private static bool Contains(TextSpan span, int position) =>
            position >= span.Start && (position < span.End || span.Length == 0 && position == span.End);
    }

    private sealed class DocumentIndex
    {
        private readonly Dictionary<SyntaxNode, SyntaxNode?> _parents = new(ReferenceEqualityComparer.Instance);

        public DocumentIndex(SyntaxTree tree, CompilationModel model)
        {
            Tree = tree;
            var nodes = new List<SyntaxNode>();
            Visit(tree.Root, null, nodes);
            Nodes = nodes;
            TypeSymbols = new Dictionary<TypeDeclarationSyntax, TypeSymbol>(ReferenceEqualityComparer.Instance);
            foreach (var declaration in nodes.OfType<TypeDeclarationSyntax>())
                if (model.Types.Values.FirstOrDefault(type => ReferenceEquals(type.Syntax, declaration)) is { } symbol)
                    TypeSymbols[declaration] = symbol;
        }

        public SyntaxTree Tree { get; }
        public IReadOnlyList<SyntaxNode> Nodes { get; }
        public Dictionary<TypeDeclarationSyntax, TypeSymbol> TypeSymbols { get; }

        public SyntaxNode? Parent(SyntaxNode node) => _parents.GetValueOrDefault(node);

        private void Visit(SyntaxNode node, SyntaxNode? parent, List<SyntaxNode> nodes)
        {
            nodes.Add(node);
            _parents[node] = parent;
            foreach (var child in node.ChildNodesAndTokens().Where(item => item.Node is not null).Select(item => item.Node!))
                Visit(child, node, nodes);
        }
    }
}
