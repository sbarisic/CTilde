using System.Collections.Immutable;

namespace CTilde;

public enum LanguageCompletionKind
{
    Keyword, Namespace, Class, Struct, Enum, EnumMember, Method, Constructor, Property, Field, Variable, Parameter,
}

public enum LanguageSymbolKind
{
    Namespace, Class, Struct, Enum, EnumMember, Method, Constructor, Property, Field,
}

public sealed record LanguageCompletion(
    string Label,
    LanguageCompletionKind Kind,
    string Detail,
    string InsertText,
    TextSpan ReplacementSpan,
    string SortText);

public sealed record LanguageHover(string Contents, TextSpan Span);

public sealed record LanguageParameter(string Label);

public sealed record LanguageSignature(string Label, ImmutableArray<LanguageParameter> Parameters);

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
    private static readonly string[] TopLevelKeywords = ["using", "namespace", "public", "internal", "class", "struct", "enum", "delegate", "static", "sealed"];
    private static readonly string[] TypeKeywords = ["public", "internal", "protected", "private", "static", "readonly", "const", "unsafe", "virtual", "override", "sealed", "void"];
    private static readonly string[] StatementKeywords = ["if", "else", "while", "do", "for", "foreach", "switch", "case", "default", "break", "continue", "defer", "return", "throw", "try", "catch", "finally", "unsafe", "new", "this", "base", "true", "false", "null", "var"];
    private static readonly string[] BuiltInTypes = ["bool", "byte", "sbyte", "short", "ushort", "char", "int", "uint", "long", "ulong", "float", "string", "object"];

    private readonly ImmutableArray<SyntaxTree> _userTrees;
    private readonly ImmutableArray<SyntaxTree> _allTrees;
    private readonly CompilationModel _model;
    private readonly BoundProgram _boundProgram;
    private readonly ImmutableArray<Diagnostic> _diagnostics;
    private readonly Dictionary<string, SyntaxTree> _treesByPath;
    private readonly Dictionary<string, DocumentIndex> _documentIndexes;
    private readonly Dictionary<string, ImmutableArray<BoundSemanticEntry>> _semanticEntries;
    private readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private LanguageServiceSnapshot(IEnumerable<SyntaxTree> syntaxTrees, CompilationOptions options)
    {
        _userTrees = syntaxTrees.ToImmutableArray();
        Options = options;
        _allTrees = StandardLibrary.GetSyntaxTrees(options.Target).AddRange(_userTrees);
        var declarationDiagnostics = new DiagnosticBag();
        foreach (var tree in _allTrees)
            declarationDiagnostics.AddRange(tree.Diagnostics);
        _model = new CompilationModel(_allTrees, _userTrees, declarationDiagnostics);
        _boundProgram = BoundProgramBuilder.Build(_model, options.Target);
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
    }

    public CompilationOptions Options { get; }

    public ImmutableArray<Diagnostic> Diagnostics => _diagnostics;

    public static LanguageServiceSnapshot Create(IEnumerable<SyntaxTree> syntaxTrees, CompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        return new LanguageServiceSnapshot(syntaxTrees, options ?? new CompilationOptions());
    }

    public ImmutableArray<LanguageCompletion> GetCompletions(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return [];
        position = Math.Clamp(position, 0, tree.Text.Length);
        var replacement = IdentifierSpan(tree.Text.Text, position);
        var context = CreateContext(tree, position);
        var member = FindMemberAccess(context, replacement.Start);
        var results = new List<LanguageCompletion>();
        if (member is not null)
            AddMemberCompletions(results, context, member, replacement);
        else
            AddContextCompletions(results, context, replacement);
        return [.. results
            .Where(item => replacement.Length == 0 || item.Label.StartsWith(tree.Text.Slice(replacement), StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => (item.Label, item.Detail, item.Kind))
            .Select(group => group.First())
            .OrderBy(item => item.SortText, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Detail, StringComparer.Ordinal)];
    }

    public LanguageHover? GetHover(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return null;
        var token = IdentifierTokenAt(tree, position);
        if (token is null)
            return null;
        var context = CreateContext(tree, position);
        var symbols = ResolveToken(context, token).ToArray();
        if (symbols.Length == 0)
            return null;
        return new LanguageHover(string.Join("\n\n", symbols.Select(FormatSymbol).Distinct(StringComparer.Ordinal)), token.Span);
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
        var signatures = methods.Select(method => new LanguageSignature(FormatMethod(method), [.. method.Parameters.Select(parameter => new LanguageParameter($"{parameter.Type.DisplayName} {parameter.Name}"))])).ToImmutableArray();
        var activeSignature = Array.FindIndex(methods, method => method.Parameters.Length > activeParameter);
        return new LanguageSignatureHelp(signatures, Math.Max(0, activeSignature), activeParameter);
    }

    public LanguageDefinition? GetDefinition(string filePath, int position)
    {
        if (!TryGetTree(filePath, out var tree))
            return null;
        var token = IdentifierTokenAt(tree, position);
        if (token is null)
            return null;
        var context = CreateContext(tree, position);
        foreach (var symbol in ResolveToken(context, token))
        {
            var syntax = SymbolSyntax(symbol);
            if (syntax is not null)
                return new LanguageDefinition(syntax.Source.FilePath, NameSpan(syntax, SymbolName(symbol)));
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
                    _ => string.Empty,
                };
                children.Add(new LanguageDocumentSymbol(name, MemberDetail(member), kind, member.Span, NameSpan(member, name), []));
            }
            foreach (var enumMember in type.EnumMembers)
                children.Add(new LanguageDocumentSymbol(enumMember.Name, string.Empty, LanguageSymbolKind.EnumMember, enumMember.Span, NameSpan(enumMember, enumMember.Name), []));
            var typeKind = type.Kind switch
            {
                TypeDeclarationKind.Struct => LanguageSymbolKind.Struct,
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
            result.Add(new LanguageWorkspaceSymbol(name, container, kind, new LanguageDefinition(syntax.Source.FilePath, NameSpan(syntax, selection))));
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
        foreach (var builtIn in BuiltInTypes)
            Add(builtIn, LanguageCompletionKind.Keyword, "built-in type", "1");

        foreach (var type in VisibleTypes(context.Tree))
            Add(type.Name, CompletionKind(type), $"{type.Kind.ToString().ToLowerInvariant()} {type.FullName}", "2");
        foreach (var @namespace in _model.Types.Values.Select(type => type.Namespace).Where(value => !string.IsNullOrEmpty(value)).Select(value => value.Split('.')[0]).Distinct(StringComparer.Ordinal))
            Add(@namespace, LanguageCompletionKind.Namespace, "namespace", "2");

        if (context.MemberDeclaration is MethodDeclarationSyntax method)
            foreach (var parameter in method.Parameters)
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
                Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "4");
            foreach (var property in Hierarchy(context.TypeSymbol).SelectMany(type => type.Properties).Where(property => !requireStatic || property.IsStatic).Where(property => IsAccessible(property, context.TypeSymbol)))
                Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "4");
            foreach (var candidate in Hierarchy(context.TypeSymbol).SelectMany(type => type.Methods).Where(methodSymbol => !requireStatic || methodSymbol.IsStatic).Where(methodSymbol => IsAccessible(methodSymbol, context.TypeSymbol)))
                Add(candidate.Name, LanguageCompletionKind.Method, FormatMethod(candidate), "5");
            if (!requireStatic)
            {
                Add("this", LanguageCompletionKind.Keyword, context.TypeSymbol.FullName, "0");
                if (context.TypeSymbol.BaseType is not null)
                    Add("base", LanguageCompletionKind.Keyword, context.TypeSymbol.BaseType.FullName, "0");
            }
        }

        void Add(string label, LanguageCompletionKind kind, string detail, string order) =>
            results.Add(new LanguageCompletion(label, kind, detail, label, replacement, order + label));
    }

    private void AddMemberCompletions(List<LanguageCompletion> results, DocumentContext context, MemberAccessExpressionSyntax member, TextSpan replacement)
    {
        var receiver = InferExpression(context, member.Receiver);
        if (receiver.StaticType is not null)
        {
            if (receiver.StaticType.Kind == DeclaredTypeKind.Enum)
                foreach (var value in receiver.StaticType.EnumValues)
                    Add(value.Name, LanguageCompletionKind.EnumMember, $"{receiver.StaticType.FullName}.{value.Name}", "0");
            foreach (var field in Hierarchy(receiver.StaticType).SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Syntax is not null && IsAccessible(field, context.TypeSymbol)))
                Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "1");
            foreach (var property in Hierarchy(receiver.StaticType).SelectMany(type => type.Properties).Where(property => property.IsStatic && IsAccessible(property, context.TypeSymbol)))
                Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "1");
            foreach (var method in Hierarchy(receiver.StaticType).SelectMany(type => type.Methods).Where(method => method.IsStatic && IsAccessible(method, context.TypeSymbol)))
                Add(method.Name, LanguageCompletionKind.Method, FormatMethod(method), "2");
            return;
        }

        if (receiver.Type is null || receiver.Type.IsError)
            return;
        if (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array)
            Add("Length", LanguageCompletionKind.Property, "int Length", "0");
        var type = receiver.Type.Symbol;
        if (type is null && (receiver.Type.IsValueType || receiver.Type.Kind is CTypeKind.String or CTypeKind.Array))
            type = _model.Types.GetValueOrDefault("System.Object");
        if (type is null)
            return;
        foreach (var field in Hierarchy(type).SelectMany(candidate => candidate.Fields).Where(field => !field.IsStatic && field.Syntax is not null && IsAccessible(field, context.TypeSymbol)))
            Add(field.Name, LanguageCompletionKind.Field, FormatSymbol(field), "1");
        foreach (var property in Hierarchy(type).SelectMany(candidate => candidate.Properties).Where(property => !property.IsStatic && IsAccessible(property, context.TypeSymbol)))
            Add(property.Name, LanguageCompletionKind.Property, FormatSymbol(property), "1");
        foreach (var method in Hierarchy(type).SelectMany(candidate => candidate.Methods).Where(method => !method.IsStatic && IsAccessible(method, context.TypeSymbol)))
            Add(method.Name, LanguageCompletionKind.Method, FormatMethod(method), "2");

        void Add(string label, LanguageCompletionKind kind, string detail, string order) =>
            results.Add(new LanguageCompletion(label, kind, detail, label, replacement, order + label));
    }

    private IEnumerable<object> ResolveToken(DocumentContext context, SyntaxToken token)
    {
        var tokenName = IdentifierValue(token);
        var member = context.Nodes.OfType<MemberAccessExpressionSyntax>()
            .Where(candidate => IdentifierEquals(candidate.Name, tokenName) && candidate.Receiver.Span.End <= token.Span.Start && candidate.Span.End >= token.Span.End)
            .OrderBy(candidate => candidate.Span.Length).FirstOrDefault();
        if (member is not null)
        {
            var receiver = InferExpression(context, member.Receiver);
            var receiverType = receiver.StaticType ?? receiver.Type?.Symbol;
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
            return Hierarchy(context.TypeSymbol).SelectMany(type => type.Methods).Where(method => method.Name == name.Name);
        if (call.Target is not MemberAccessExpressionSyntax member)
            return [];
        var receiver = InferExpression(context, member.Receiver);
        var type = receiver.StaticType ?? receiver.Type?.Symbol;
        if (type is null && receiver.Type is { } valueType && (valueType.IsValueType || valueType.Kind is CTypeKind.String or CTypeKind.Array))
            type = _model.Types.GetValueOrDefault("System.Object");
        return type is null ? [] : Hierarchy(type).SelectMany(candidate => candidate.Methods).Where(method => method.Name == member.Name && method.IsStatic == (receiver.StaticType is not null));
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
                var receiver = InferExpression(context, member.Receiver);
                if (receiver.StaticType is not null)
                {
                    var staticField = Hierarchy(receiver.StaticType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == member.Name && candidate.IsStatic);
                    if (staticField is not null)
                        return new(staticField.Type, null);
                    var staticProperty = Hierarchy(receiver.StaticType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == member.Name && candidate.IsStatic);
                    if (staticProperty is not null)
                        return new(staticProperty.Type, null);
                    var qualified = QualifiedName(member);
                    return new(null, qualified is null ? null : _model.ResolveNamedType(qualified, context.Tree));
                }
                if (receiver.Type?.Kind is CTypeKind.String or CTypeKind.Array && member.Name == "Length")
                    return new(CType.Int, null);
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
            return CType.Float;
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
        ConstructorDeclarationSyntax constructor => constructor.Parameters,
        _ => [],
    };

    private MemberAccessExpressionSyntax? FindMemberAccess(DocumentContext context, int replacementStart) => context.Nodes
        .OfType<MemberAccessExpressionSyntax>()
        .Where(member => member.Receiver.Span.End < replacementStart || member.Receiver.Span.End < context.Position)
        .Where(member => member.Span.Start <= replacementStart && member.Span.End >= replacementStart)
        .OrderBy(member => member.Span.Length)
        .FirstOrDefault();

    private static SyntaxToken? IdentifierTokenAt(SyntaxTree tree, int position) => tree.Tokens
        .Where(token => token.Kind == SyntaxKind.IdentifierToken && !token.IsMissing)
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

    private static IEnumerable<TypeSymbol> Hierarchy(TypeSymbol type) => type.BaseTypesAndSelf();

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
        DeclaredTypeKind.Struct => LanguageCompletionKind.Struct,
        DeclaredTypeKind.Enum => LanguageCompletionKind.Enum,
        _ => LanguageCompletionKind.Class,
    };

    private static LanguageSymbolKind TypeKind(TypeSymbol type) => type.Kind switch
    {
        DeclaredTypeKind.Struct => LanguageSymbolKind.Struct,
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

    private static string FormatSymbol(object symbol) => symbol switch
    {
        TypeSymbol { Kind: DeclaredTypeKind.Delegate } type => $"delegate {type.DelegateReturnType!.DisplayName} {type.FullName}({string.Join(", ", type.DelegateParameters.Select(parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))})",
        TypeSymbol type => $"{type.Kind.ToString().ToLowerInvariant()} {type.FullName}",
        FieldSymbol field => $"{AccessibilityText(field.Accessibility)}{(field.IsStatic ? "static " : string.Empty)}{field.Type.DisplayName} {field.ContainingType.FullName}.{field.Name}",
        PropertySymbol property => $"{AccessibilityText(property.Accessibility)}{(property.IsStatic ? "static " : string.Empty)}{property.Type.DisplayName} {property.ContainingType.FullName}.{property.Name}",
        MethodSymbol method => FormatMethod(method),
        ParameterSymbol parameter => $"{parameter.Type.DisplayName} {parameter.Name}",
        LocalSymbol local => $"{local.Type.DisplayName} {local.Name}",
        ParameterSyntax parameter => $"{parameter.Type} {parameter.Name}",
        LocalDeclarationStatementSyntax local => $"{local.Type} {local.Name}",
        LocalSemanticSymbol local => $"{local.Type} {local.Name}",
        EnumValueSymbol value => $"{value.Name} = {value.Value}",
        _ => string.Empty,
    };

    private static string FormatMethod(MethodSymbol method) =>
        $"{AccessibilityText(method.Accessibility)}{(method.IsStatic ? "static " : string.Empty)}{(method.IsConstructor ? string.Empty : method.ReturnType.DisplayName + " ")}{method.ContainingType.FullName}.{method.Name}({string.Join(", ", method.Parameters.Select(parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))})";

    private static string AccessibilityText(Accessibility accessibility) => accessibility.ToString().ToLowerInvariant() + " ";

    private static SyntaxNode? SymbolSyntax(object symbol) => symbol switch
    {
        TypeSymbol type => type.Syntax,
        MemberSymbol member => member.Syntax,
        ParameterSymbol parameter => parameter.Syntax,
        LocalSymbol local => local.Syntax,
        ParameterSyntax parameter => parameter,
        LocalDeclarationStatementSyntax local => local,
        LocalSemanticSymbol local => local.Syntax,
        EnumValueSymbol value => value.Syntax,
        _ => null,
    };

    private static string SymbolName(object symbol) => symbol switch
    {
        TypeSymbol type => type.Name,
        MemberSymbol member => member.Name,
        ParameterSymbol parameter => parameter.Name,
        LocalSymbol local => local.Name,
        ParameterSyntax parameter => parameter.Name,
        LocalDeclarationStatementSyntax local => local.Name,
        LocalSemanticSymbol local => local.Name,
        EnumValueSymbol value => value.Name,
        _ => string.Empty,
    };

    private static string MemberDetail(MemberDeclarationSyntax member) => member switch
    {
        FieldDeclarationSyntax field => field.Type.ToString(),
        PropertyDeclarationSyntax property => property.Type.ToString(),
        MethodDeclarationSyntax method => $"{method.ReturnType}({string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToString()))})",
        ConstructorDeclarationSyntax constructor => $"({string.Join(", ", constructor.Parameters.Select(parameter => parameter.Type.ToString()))})",
        _ => string.Empty,
    };

    private static TextSpan NameSpan(SyntaxNode syntax, string name)
    {
        if (name.Length == 0)
            return syntax.Span;
        var source = syntax.Source.Text;
        var start = source.IndexOf(name, syntax.Span.Start, Math.Min(syntax.Span.Length, source.Length - syntax.Span.Start), StringComparison.Ordinal);
        return start < 0 ? syntax.Span : new TextSpan(start, name.Length);
    }

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

        private static bool Contains(TextSpan span, int position) => position >= span.Start && position <= span.End;
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
