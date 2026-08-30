using System.Collections.Immutable;

namespace CTilde;

public enum LanguageReferenceSearchScope
{
    ProjectSource,
    StandardLibrary,
    Documentation,
}

public sealed record LanguageReference(
    string SymbolKey,
    string FilePath,
    string SourceIdentity,
    string SymbolSourceIdentity,
    LanguageReferenceSearchScope SearchScope,
    TextSpan Span,
    bool IsDeclaration);

public sealed record LanguageReferenceLens(
    string SymbolKey,
    string Name,
    string Detail,
    LanguageSymbolKind Kind,
    string FilePath,
    string SourceIdentity,
    LanguageReferenceSearchScope SearchScope,
    TextSpan Range,
    TextSpan SelectionRange,
    int ReferenceCount);

public sealed partial class LanguageServiceSnapshot
{
    private readonly Lazy<ReferenceIndex> _referenceIndex;

    public ImmutableArray<LanguageReference> GetReferences(string filePath, int position, bool includeDeclaration = false)
    {
        var index = ReferenceIndexValue;
        var path = NormalizePath(filePath);
        var occurrence = index.Occurrences
            .Where(candidate => _pathComparer.Equals(NormalizePath(candidate.FilePath), path) && ContainsReferencePosition(candidate.Span, position))
            .OrderBy(candidate => candidate.IsDeclaration ? 0 : 1)
            .ThenBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        return occurrence is null ? [] : GetReferences(occurrence.SymbolKey, includeDeclaration);
    }

    public ImmutableArray<LanguageReference> GetReferences(string symbolKey, bool includeDeclaration = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolKey);
        return [.. ReferenceIndexValue.Occurrences
            .Where(reference => reference.SymbolKey == symbolKey && (includeDeclaration || !reference.IsDeclaration))
            .DistinctBy(reference => (NormalizePath(reference.FilePath), reference.Span.Start, reference.Span.Length, reference.IsDeclaration), ReferenceLocationComparer.Instance)
            .OrderBy(reference => NormalizePath(reference.FilePath), _pathComparer)
            .ThenBy(reference => reference.Span.Start)];
    }

    public ImmutableArray<LanguageReference> GetReferences(IReadOnlySet<string> symbolKeys, bool includeDeclaration = false)
    {
        ArgumentNullException.ThrowIfNull(symbolKeys);
        if (symbolKeys.Count == 0)
            return [];
        return [.. ReferenceIndexValue.Occurrences
            .Where(reference => symbolKeys.Contains(reference.SymbolKey) && (includeDeclaration || !reference.IsDeclaration))
            .DistinctBy(reference => (reference.SymbolKey, NormalizePath(reference.FilePath), reference.Span.Start, reference.Span.Length, reference.IsDeclaration),
                ReferenceBatchLocationComparer.Instance)
            .OrderBy(reference => reference.SymbolKey, StringComparer.Ordinal)
            .ThenBy(reference => NormalizePath(reference.FilePath), _pathComparer)
            .ThenBy(reference => reference.Span.Start)];
    }

    public ImmutableArray<LanguageReferenceLens> GetReferenceLenses(string filePath)
    {
        var path = NormalizePath(filePath);
        var index = ReferenceIndexValue;
        return [.. index.Declarations
            .Where(declaration => _pathComparer.Equals(NormalizePath(declaration.FilePath), path))
            .Select(declaration => new LanguageReferenceLens(
                declaration.SymbolKey,
                declaration.Name,
                declaration.Detail,
                declaration.Kind,
                declaration.FilePath,
                declaration.SourceIdentity,
                declaration.SearchScope,
                declaration.Range,
                declaration.SelectionRange,
                index.ReferenceCounts.GetValueOrDefault(declaration.SymbolKey)))
            .OrderBy(lens => lens.SelectionRange.Start)
            .ThenBy(lens => lens.SelectionRange.Length)];
    }

    public string? GetReferenceDescription(string symbolKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolKey);
        return ReferenceIndexValue.Declarations.FirstOrDefault(declaration => declaration.SymbolKey == symbolKey)?.Detail;
    }

    private ReferenceIndex ReferenceIndexValue => _referenceIndex.Value;

    private ReferenceIndex BuildReferenceIndex()
    {
        var declarations = new Dictionary<string, ReferenceDeclaration>(StringComparer.Ordinal);
        var occurrences = new List<LanguageReference>();

        foreach (var type in _model.Types.Values.Distinct()
            .OrderByDescending(type => type.IsGenericDefinition)
            .ThenBy(type => type.TypeArguments.Length))
        {
            if (type.Syntax is not null)
                AddDeclaration(type, type.Name, TypeKind(type), FormatSymbol(type.GenericDefinition ?? type));
            foreach (var field in type.Fields.Where(field => field.Syntax is FieldDeclarationSyntax))
                AddDeclaration(field, field.Name, LanguageSymbolKind.Field, FormatSymbol(field));
            foreach (var property in type.Properties)
                AddDeclaration(property, property.Name, LanguageSymbolKind.Property, FormatSymbol(property));
            foreach (var method in type.Methods.Concat(type.Constructors).Where(method => method.Syntax is MemberDeclarationSyntax))
            {
                AddDeclaration(method, SymbolName(method),
                    method.IsConstructor ? LanguageSymbolKind.Constructor : LanguageSymbolKind.Method,
                    FormatSymbol(method.GenericDefinition ?? method));
                foreach (var parameter in method.Parameters)
                    AddDeclaration(parameter, parameter.Name, LanguageSymbolKind.Parameter, FormatSymbol(parameter));
            }
            foreach (var value in type.EnumValues)
                AddDeclaration(value, value.Name, LanguageSymbolKind.EnumMember, FormatSymbol(value));
        }

        foreach (var index in _documentIndexes.Values)
        {
            foreach (var local in index.Nodes.OfType<LocalDeclarationStatementSyntax>())
                AddSyntaxDeclaration(local, local.Name, LanguageSymbolKind.Variable, $"{local.Type} {local.Name}");
            foreach (var loop in index.Nodes.OfType<ForeachStatementSyntax>())
                AddSyntaxDeclaration(loop, loop.Name, LanguageSymbolKind.Variable, $"{loop.Type} {loop.Name}");
            foreach (var clause in index.Nodes.OfType<CatchClauseSyntax>().Where(clause => clause.Name is not null))
                AddSyntaxDeclaration(clause, clause.Name!, LanguageSymbolKind.Variable, $"{clause.Type} {clause.Name}");
            foreach (var parameter in index.Nodes.OfType<LambdaParameterSyntax>())
                AddSyntaxDeclaration(parameter, parameter.Name, LanguageSymbolKind.Parameter, $"{parameter.Type} {parameter.Name}");
        }

        foreach (var semantic in _boundProgram.SemanticMap.Values)
        {
            if (semantic.Syntax.Span.Length == 0 || semantic.Symbol is null || semantic.Symbol is MethodGroupBinding)
                continue;
            var key = ReferenceSymbolKey(semantic.Symbol);
            if (key is null)
                continue;
            var span = ReferenceSpan(semantic.Syntax, semantic.Symbol);
            if (span.Length != 0)
                occurrences.Add(CreateReference(key, semantic.Syntax.Source.FilePath, span, false));
        }

        foreach (var index in _documentIndexes.Values)
        {
            foreach (var typeSyntax in index.Nodes.OfType<TypeSyntax>())
            {
                var type = _model.ResolveType(typeSyntax, index.Tree, false).Symbol;
                if (type is null || ReferenceSymbolKey(type) is not { } key)
                    continue;
                occurrences.Add(CreateReference(key, typeSyntax.Source.FilePath, TypeReferenceSpan(typeSyntax), false));
            }
        }

        var declarationLocations = declarations.Values
            .Select(declaration => LocationKey(declaration.FilePath, declaration.SelectionRange))
            .ToHashSet(StringComparer.Ordinal);
        var indexedLocations = occurrences
            .Select(reference => LocationKey(reference.FilePath, reference.Span))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var index in _documentIndexes.Values)
        {
            var scopedNames = index.Nodes.OfType<ForeachStatementSyntax>().Select(loop => loop.Name)
                .Concat(index.Nodes.OfType<CatchClauseSyntax>().Where(clause => clause.Name is not null).Select(clause => clause.Name!))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var token in index.Tree.Tokens.Where(token => !token.IsMissing && token.Span.Length != 0 &&
                token.Kind == SyntaxKind.IdentifierToken && scopedNames.Any(name => IdentifierEquals(IdentifierValue(token), name))))
            {
                var location = LocationKey(index.Tree.Text.FilePath, token.Span);
                if (declarationLocations.Contains(location) || indexedLocations.Contains(location))
                    continue;
                var keys = ResolveToken(new DocumentContext(index, token.Span.Start), token)
                    .Select(ReferenceSymbolKey)
                    .Where(key => key is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (keys.Length == 1)
                {
                    occurrences.Add(CreateReference(keys[0], index.Tree.Text.FilePath, token.Span, false));
                    indexedLocations.Add(location);
                }
            }
        }

        foreach (var declaration in declarations.Values)
            occurrences.Add(CreateReference(declaration.SymbolKey, declaration.FilePath, declaration.SelectionRange, true));

        var distinct = occurrences
            .Where(reference => reference.Span.Length != 0)
            .GroupBy(reference => (reference.SymbolKey, Path: NormalizePath(reference.FilePath), reference.Span.Start, reference.Span.Length))
            .Select(group => group.OrderByDescending(reference => reference.IsDeclaration).First())
            .ToImmutableArray();
        var counts = distinct.Where(reference => !reference.IsDeclaration)
            .GroupBy(reference => reference.SymbolKey, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new ReferenceIndex([.. declarations.Values], distinct, counts);

        string LocationKey(string filePath, TextSpan span)
        {
            var path = ReferenceSourcePath(filePath);
            if (OperatingSystem.IsWindows())
                path = path.ToUpperInvariant();
            return $"{path}:{span.Start}:{span.Length}";
        }

        void AddDeclaration(object symbol, string name, LanguageSymbolKind kind, string detail)
        {
            var syntax = SymbolSyntax(symbol);
            var key = ReferenceSymbolKey(symbol);
            if (syntax is null || key is null || declarations.ContainsKey(key))
                return;
            var selection = DeclarationSelectionSpan(syntax, name);
            var sourceIdentity = ReferenceSourcePath(syntax.Source.FilePath);
            declarations.Add(key, new ReferenceDeclaration(key, name, detail, kind, syntax.Source.FilePath, sourceIdentity,
                ReferenceSearchScope(key, sourceIdentity), syntax.Span, selection));
        }

        void AddSyntaxDeclaration(SyntaxNode syntax, string name, LanguageSymbolKind kind, string detail)
        {
            var key = SyntaxSymbolKey(syntax, name, kind);
            if (declarations.ContainsKey(key))
                return;
            var selection = DeclarationSelectionSpan(syntax, name);
            var sourceIdentity = ReferenceSourcePath(syntax.Source.FilePath);
            declarations.Add(key, new ReferenceDeclaration(key, name, detail, kind, syntax.Source.FilePath, sourceIdentity,
                ReferenceSearchScope(key, sourceIdentity), syntax.Span, selection));
        }

        LanguageReference CreateReference(string symbolKey, string filePath, TextSpan span, bool isDeclaration)
        {
            var sourceIdentity = ReferenceSourcePath(filePath);
            var symbolSourceIdentity = ReferenceSymbolSourceIdentity(symbolKey, sourceIdentity);
            return new LanguageReference(symbolKey, filePath, sourceIdentity, symbolSourceIdentity,
                ReferenceSearchScope(symbolKey, symbolSourceIdentity), span, isDeclaration);
        }
    }

    private static LanguageReferenceSearchScope ReferenceSearchScope(string symbolKey, string sourceIdentity) =>
        symbolKey.StartsWith("doc:", StringComparison.Ordinal)
            ? LanguageReferenceSearchScope.Documentation
            : symbolKey.StartsWith("source:stdlib/", StringComparison.Ordinal) || sourceIdentity.StartsWith("stdlib/", StringComparison.Ordinal)
                ? LanguageReferenceSearchScope.StandardLibrary
                : LanguageReferenceSearchScope.ProjectSource;

    private static string ReferenceSymbolSourceIdentity(string symbolKey, string fallback)
    {
        if (!symbolKey.StartsWith("source:", StringComparison.Ordinal))
            return fallback;
        var end = symbolKey.Length;
        for (var field = 0; field < 4; field++)
        {
            end = symbolKey.LastIndexOf(':', end - 1);
            if (end < "source:".Length)
                return fallback;
        }
        return symbolKey["source:".Length..end];
    }

    private string? ReferenceSymbolKey(object symbol)
    {
        symbol = symbol switch
        {
            TypeSymbol { GenericDefinition: not null } type => type.GenericDefinition,
            MethodSymbol { GenericDefinition: not null } method => method.GenericDefinition,
            _ => symbol,
        };
        if (symbol is ParameterSymbol { Syntax: null } lambdaParameter)
        {
            var owner = _model.LambdaMethods.Values.FirstOrDefault(method => method.Parameters.Contains(lambdaParameter));
            if (owner?.Syntax is LambdaExpressionSyntax lambda)
            {
                var index = owner.Parameters.IndexOf(lambdaParameter);
                if (index >= 0 && index < lambda.Parameters.Length)
                    return SyntaxSymbolKey(lambda.Parameters[index], lambda.Parameters[index].Name, LanguageSymbolKind.Parameter);
            }
        }
        if (SymbolSyntax(symbol) is { } syntax)
            return SyntaxSymbolKey(syntax, SymbolName(symbol), ReferenceSymbolKind(symbol));
        if (symbol is TypeSymbol or MemberSymbol)
        {
            var documentationId = _model.Documentation.GetId(symbol);
            if (!string.IsNullOrWhiteSpace(documentationId))
                return "doc:" + documentationId;
        }
        return null;
    }

    private static LanguageSymbolKind ReferenceSymbolKind(object symbol) => symbol switch
    {
        TypeSymbol type => TypeKind(type),
        MethodSymbol { IsConstructor: true } => LanguageSymbolKind.Constructor,
        MethodSymbol => LanguageSymbolKind.Method,
        PropertySymbol => LanguageSymbolKind.Property,
        FieldSymbol => LanguageSymbolKind.Field,
        EnumValueSymbol => LanguageSymbolKind.EnumMember,
        ParameterSymbol or ParameterSyntax or LambdaParameterSyntax => LanguageSymbolKind.Parameter,
        _ => LanguageSymbolKind.Variable,
    };

    private string SyntaxSymbolKey(SyntaxNode syntax, string name, LanguageSymbolKind kind)
    {
        var path = ReferenceSourcePath(syntax.Source.FilePath);
        return $"source:{path}:{syntax.Span.Start}:{syntax.Span.Length}:{kind}:{NormalizeIdentifier(name)}";
    }

    private string ReferenceSourcePath(string filePath)
    {
        var normalized = NormalizePath(filePath);
        if (_treesByPath.TryGetValue(normalized, out var tree) && tree.Origin == SyntaxTreeOrigin.StandardLibrary)
            return "stdlib/System/" + Path.GetFileName(normalized);
        return normalized.Replace('\\', '/');
    }

    private TextSpan ReferenceSpan(SyntaxNode syntax, object symbol) => syntax switch
    {
        InlineAssemblyReferenceSyntax reference => reference.Span,
        NameExpressionSyntax name => NameSpanFromEnd(name, name.Name),
        MemberAccessExpressionSyntax member => NameSpanFromEnd(member, member.Name),
        CallExpressionSyntax call => ReferenceSpan(call.Target, symbol),
        NewExpressionSyntax @new when symbol is MethodSymbol { IsConstructor: true } => TypeReferenceSpan(@new.Type),
        OffsetOfExpressionSyntax offset when symbol is FieldSymbol field => NameSpanFromEnd(offset, field.Name),
        BinaryExpressionSyntax binary when symbol is MethodSymbol { IsOperator: true } =>
            OperatorReferenceSpan(binary, binary.OperatorKind, binary.Left.Span.End, binary.Right.Span.Start),
        AssignmentExpressionSyntax assignment when symbol is MethodSymbol { IsOperator: true } =>
            OperatorReferenceSpan(assignment, assignment.OperatorKind, assignment.Left.Span.End, assignment.Right.Span.Start),
        UnaryExpressionSyntax unary when symbol is MethodSymbol { IsOperator: true } && unary.IsPostfix =>
            OperatorReferenceSpan(unary, unary.OperatorKind, unary.Operand.Span.End, unary.Span.End),
        UnaryExpressionSyntax unary when symbol is MethodSymbol { IsOperator: true } =>
            OperatorReferenceSpan(unary, unary.OperatorKind, unary.Span.Start, unary.Operand.Span.Start),
        _ => SelectionSpan(syntax, SymbolName(symbol)),
    };

    private TextSpan OperatorReferenceSpan(SyntaxNode syntax, SyntaxKind kind, int start, int end)
    {
        if (_treesByPath.TryGetValue(NormalizePath(syntax.Source.FilePath), out var tree))
        {
            foreach (var token in tree.Tokens)
                if (token.Kind == kind && token.Span.Start >= start && token.Span.End <= end)
                    return token.Span;
        }
        return syntax.Span;
    }

    private static TextSpan TypeReferenceSpan(TypeSyntax syntax) => NameSpanFromEnd(syntax, syntax.Name);

    private TextSpan DeclarationSelectionSpan(SyntaxNode syntax, string name) => syntax switch
    {
        OperatorDeclarationSyntax @operator => @operator.OperatorToken.Span,
        FieldDeclarationSyntax field => NameSpanBetween(field, name, field.Type.Span.End, field.Initializer?.Span.Start ?? field.Span.End),
        MethodDeclarationSyntax method => NameSpanBetween(method, name, method.ReturnType.Span.End,
            method.Parameters.FirstOrDefault()?.Span.Start ?? method.Body?.Span.Start ?? method.AssemblyBody?.Span.Start ?? method.Span.End),
        ConstructorDeclarationSyntax constructor => NameSpanBetween(constructor, name, constructor.Span.Start,
            constructor.Parameters.FirstOrDefault()?.Span.Start ?? constructor.Initializer?.Span.Start ?? constructor.Body.Span.Start, preferLast: true),
        PropertyDeclarationSyntax property => NameSpanBetween(property, name, property.Type.Span.End,
            property.IndexParameter?.Span.Start ?? property.Getter?.Span.Start ?? property.Setter?.Span.Start ?? property.Span.End),
        ParameterSyntax parameter => NameSpanBetween(parameter, name, parameter.Type.Span.End, parameter.Span.End),
        LocalDeclarationStatementSyntax local => NameSpanBetween(local, name, local.Type.Span.End, local.Initializer?.Span.Start ?? local.Span.End),
        ForeachStatementSyntax loop => NameSpanBetween(loop, name, loop.Type.Span.End, loop.Collection.Span.Start),
        CatchClauseSyntax clause => NameSpanBetween(clause, name, clause.Type?.Span.End ?? clause.Span.Start, clause.Body.Span.Start),
        LambdaParameterSyntax parameter => NameSpanBetween(parameter, name, parameter.Type?.Span.End ?? parameter.Span.Start, parameter.Span.End),
        _ => NameSpan(syntax, name),
    };

    private TextSpan NameSpanBetween(SyntaxNode syntax, string name, int start, int end, bool preferLast = false)
    {
        if (_treesByPath.TryGetValue(NormalizePath(syntax.Source.FilePath), out var tree))
        {
            var matches = tree.Tokens.Where(token => token.Kind == SyntaxKind.IdentifierToken && token.Span.Start >= start && token.Span.End <= end &&
                IdentifierEquals(IdentifierValue(token), name));
            var token = preferLast ? matches.LastOrDefault() : matches.FirstOrDefault();
            if (token is not null)
                return token.Span;
        }
        return NameSpan(syntax, name);
    }

    private static TextSpan NameSpanFromEnd(SyntaxNode syntax, string name)
    {
        if (string.IsNullOrEmpty(name))
            return syntax.Span;
        var source = syntax.Source.Text;
        var end = Math.Min(syntax.Span.End, source.Length);
        var start = end == 0 ? -1 : source.LastIndexOf(name, end - 1, Math.Max(0, end - syntax.Span.Start), StringComparison.Ordinal);
        if (start >= syntax.Span.Start && start + name.Length <= end)
            return new TextSpan(start, name.Length);
        return NameSpan(syntax, name);
    }

    private static bool ContainsReferencePosition(TextSpan span, int position) =>
        position >= span.Start && position < span.End;

    private sealed record ReferenceDeclaration(
        string SymbolKey,
        string Name,
        string Detail,
        LanguageSymbolKind Kind,
        string FilePath,
        string SourceIdentity,
        LanguageReferenceSearchScope SearchScope,
        TextSpan Range,
        TextSpan SelectionRange);

    private sealed record ReferenceIndex(
        ImmutableArray<ReferenceDeclaration> Declarations,
        ImmutableArray<LanguageReference> Occurrences,
        ImmutableDictionary<string, int> ReferenceCounts);

    private sealed class ReferenceLocationComparer : IEqualityComparer<(string Path, int Start, int Length, bool IsDeclaration)>
    {
        public static readonly ReferenceLocationComparer Instance = new();

        public bool Equals((string Path, int Start, int Length, bool IsDeclaration) left, (string Path, int Start, int Length, bool IsDeclaration) right) =>
            left.Start == right.Start && left.Length == right.Length && left.IsDeclaration == right.IsDeclaration &&
            (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(left.Path, right.Path);

        public int GetHashCode((string Path, int Start, int Length, bool IsDeclaration) value)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return HashCode.Combine(comparer.GetHashCode(value.Path), value.Start, value.Length, value.IsDeclaration);
        }
    }

    private sealed class ReferenceBatchLocationComparer : IEqualityComparer<(string SymbolKey, string Path, int Start, int Length, bool IsDeclaration)>
    {
        public static readonly ReferenceBatchLocationComparer Instance = new();

        public bool Equals((string SymbolKey, string Path, int Start, int Length, bool IsDeclaration) left,
            (string SymbolKey, string Path, int Start, int Length, bool IsDeclaration) right) =>
            left.Start == right.Start && left.Length == right.Length && left.IsDeclaration == right.IsDeclaration &&
            StringComparer.Ordinal.Equals(left.SymbolKey, right.SymbolKey) &&
            (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(left.Path, right.Path);

        public int GetHashCode((string SymbolKey, string Path, int Start, int Length, bool IsDeclaration) value)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.SymbolKey), comparer.GetHashCode(value.Path), value.Start, value.Length, value.IsDeclaration);
        }
    }
}
