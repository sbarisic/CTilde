using System.Collections.Immutable;

namespace CTilde;

public enum LanguageSemanticTokenKind
{
    Namespace,
    Class,
    Struct,
    Enum,
    EnumMember,
    Parameter,
    Variable,
    Property,
    Method,
}

[Flags]
public enum LanguageSemanticTokenModifiers
{
    None = 0,
    Declaration = 1,
    Static = 2,
    Readonly = 4,
    DefaultLibrary = 8,
}

public sealed record LanguageSemanticToken(
    TextSpan Span,
    LanguageSemanticTokenKind Kind,
    LanguageSemanticTokenModifiers Modifiers);

public sealed partial class LanguageServiceSnapshot
{
    public ImmutableArray<LanguageSemanticToken> GetSemanticTokens(string filePath, CancellationToken cancellationToken = default)
    {
        if (!TryGetTree(filePath, out var tree))
            return [];

        var result = new Dictionary<TextSpan, ClassifiedToken>();
        var index = _documentIndexes[NormalizePath(tree.Text.FilePath)];
        AddNamespaceTokens(tree, result, cancellationToken);
        AddDeclarationTokens(tree, index, result, cancellationToken);
        AddTypeReferenceTokens(tree, index, result, cancellationToken);

        foreach (var reference in index.Nodes.OfType<InlineAssemblyReferenceSyntax>())
        {
            if (!_boundProgram.SemanticMap.TryGetValue(reference, out var semantic) || semantic.Symbol is null)
                continue;
            if (ClassifySymbol(semantic.Symbol) is { } classification)
                Add(result, reference.Span, classification.Kind, classification.Modifiers);
        }

        foreach (var token in tree.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind != SyntaxKind.IdentifierToken || token.IsMissing || token.Span.Length == 0 || result.ContainsKey(token.Span))
                continue;
            var context = new DocumentContext(index, token.Span.Start);
            var classifications = ResolveToken(context, token)
                .Select(ClassifySymbol)
                .Where(classification => classification is not null)
                .Select(classification => classification!.Value)
                .ToArray();
            if (classifications.Length == 0 || classifications.Select(classification => classification.Kind).Distinct().Count() != 1)
                continue;
            Add(result, token.Span, classifications[0].Kind, classifications.Aggregate(LanguageSemanticTokenModifiers.None, (value, classification) => value | classification.Modifiers));
        }

        return [.. result.Values
            .Where(token => IsSingleLine(tree.Text, token.Span))
            .OrderBy(token => token.Span.Start)
            .ThenBy(token => token.Span.Length)
            .Select(token => new LanguageSemanticToken(token.Span, token.Kind, token.Modifiers))];
    }

    private void AddNamespaceTokens(SyntaxTree tree, Dictionary<TextSpan, ClassifiedToken> result, CancellationToken cancellationToken)
    {
        foreach (var directive in tree.Root.Usings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddQualifiedNamespace(tree, directive.Span, directive.Name, result, IsDefaultLibraryNamespace(directive.Name));
        }
        if (tree.Root.Namespace is { } @namespace)
            AddQualifiedNamespace(tree, @namespace.Span, @namespace.Name, result, IsStandardLibrary(@namespace.Source.FilePath));
    }

    private void AddDeclarationTokens(SyntaxTree tree, DocumentIndex index, Dictionary<TextSpan, ClassifiedToken> result, CancellationToken cancellationToken)
    {
        foreach (var declaration in index.Nodes.OfType<TypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = index.TypeSymbols.GetValueOrDefault(declaration);
            if (symbol is not null && FindDeclarationToken(tree, declaration, declaration.Name) is { } token)
                Add(result, token.Span, TypeTokenKind(symbol), DeclarationModifiers(symbol));

            foreach (var member in declaration.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var memberSymbol = FindMemberSymbol(symbol, member);
                var name = MemberName(member);
                if (name.Length != 0 && FindDeclarationToken(tree, member, name) is { } memberToken)
                {
                    var kind = member is MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax ? LanguageSemanticTokenKind.Method : LanguageSemanticTokenKind.Property;
                    Add(result, memberToken.Span, kind, DeclarationModifiers(memberSymbol, member));
                }
                foreach (var parameter in Parameters(member))
                    if (FindDeclarationToken(tree, parameter, parameter.Name) is { } parameterToken)
                        Add(result, parameterToken.Span, LanguageSemanticTokenKind.Parameter, DeclarationModifier(parameter.Source.FilePath));
            }

            foreach (var parameter in declaration.DelegateParameters)
                if (FindDeclarationToken(tree, parameter, parameter.Name) is { } delegateParameterToken)
                    Add(result, delegateParameterToken.Span, LanguageSemanticTokenKind.Parameter, DeclarationModifier(parameter.Source.FilePath));

            foreach (var enumMember in declaration.EnumMembers)
                if (FindDeclarationToken(tree, enumMember, enumMember.Name) is { } enumToken)
                    Add(result, enumToken.Span, LanguageSemanticTokenKind.EnumMember,
                        DeclarationModifier(enumMember.Source.FilePath) | LanguageSemanticTokenModifiers.Static | LanguageSemanticTokenModifiers.Readonly);
        }

        foreach (var local in index.Nodes.OfType<LocalDeclarationStatementSyntax>())
            if (FindDeclarationToken(tree, local, local.Name) is { } token)
                Add(result, token.Span, LanguageSemanticTokenKind.Variable,
                    DeclarationModifier(local.Source.FilePath) | (local.IsConst || local.IsReadonly ? LanguageSemanticTokenModifiers.Readonly : 0));
        foreach (var loop in index.Nodes.OfType<ForeachStatementSyntax>())
            if (FindDeclarationToken(tree, loop, loop.Name) is { } token)
                Add(result, token.Span, LanguageSemanticTokenKind.Variable, DeclarationModifier(loop.Source.FilePath) | LanguageSemanticTokenModifiers.Readonly);
        foreach (var clause in index.Nodes.OfType<CatchClauseSyntax>().Where(clause => clause.Name is not null))
            if (FindDeclarationToken(tree, clause, clause.Name!) is { } token)
                Add(result, token.Span, LanguageSemanticTokenKind.Variable, DeclarationModifier(clause.Source.FilePath) | LanguageSemanticTokenModifiers.Readonly);
    }

    private void AddTypeReferenceTokens(SyntaxTree tree, DocumentIndex index, Dictionary<TextSpan, ClassifiedToken> result, CancellationToken cancellationToken)
    {
        foreach (var syntax in index.Nodes.OfType<TypeSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = _model.ResolveType(syntax, tree, false);
            if (resolved.IsNativeBuffer)
            {
                var intrinsic = IdentifierTokens(tree, syntax.Span).FirstOrDefault();
                if (intrinsic is not null)
                    Add(result, intrinsic.Span, LanguageSemanticTokenKind.Struct, LanguageSemanticTokenModifiers.DefaultLibrary);
                continue;
            }
            var type = resolved.Symbol;
            if (type is null)
                continue;
            var identifiers = IdentifierTokens(tree, syntax.Span).ToArray();
            if (identifiers.Length == 0)
                continue;
            foreach (var namespaceToken in identifiers[..^1])
                Add(result, namespaceToken.Span, LanguageSemanticTokenKind.Namespace, IsStandardLibrary(type.Syntax?.Source.FilePath) ? LanguageSemanticTokenModifiers.DefaultLibrary : 0);
            Add(result, identifiers[^1].Span, TypeTokenKind(type), SymbolModifiers(type));
        }
    }

    private ClassifiedToken? ClassifySymbol(object symbol)
    {
        var modifiers = SymbolModifiers(symbol);
        return symbol switch
        {
            TypeSymbol type => new(default, TypeTokenKind(type), modifiers),
            EnumValueSymbol => new(default, LanguageSemanticTokenKind.EnumMember, modifiers | LanguageSemanticTokenModifiers.Static | LanguageSemanticTokenModifiers.Readonly),
            FieldSymbol or PropertySymbol => new(default, LanguageSemanticTokenKind.Property, modifiers),
            MethodSymbol => new(default, LanguageSemanticTokenKind.Method, modifiers),
            ParameterSymbol => new(default, LanguageSemanticTokenKind.Parameter, modifiers),
            LocalSymbol => new(default, LanguageSemanticTokenKind.Variable, modifiers),
            ParameterSyntax => new(default, LanguageSemanticTokenKind.Parameter, modifiers),
            LocalDeclarationStatementSyntax or LocalSemanticSymbol => new(default, LanguageSemanticTokenKind.Variable, modifiers),
            _ => null,
        };
    }

    private LanguageSemanticTokenModifiers SymbolModifiers(object symbol)
    {
        var modifiers = IsStandardLibrary(SymbolSyntax(symbol)?.Source.FilePath) ? LanguageSemanticTokenModifiers.DefaultLibrary : LanguageSemanticTokenModifiers.None;
        return symbol switch
        {
            TypeSymbol { IsStatic: true } => modifiers | LanguageSemanticTokenModifiers.Static,
            FieldSymbol field => modifiers | (field.IsStatic ? LanguageSemanticTokenModifiers.Static : 0) |
                (field.IsConst || field.IsReadonly ? LanguageSemanticTokenModifiers.Readonly : 0),
            PropertySymbol property => modifiers | (property.IsStatic ? LanguageSemanticTokenModifiers.Static : 0),
            MethodSymbol method => modifiers | (method.IsStatic ? LanguageSemanticTokenModifiers.Static : 0),
            LocalSymbol local when local.IsConst || local.IsReadonly => modifiers | LanguageSemanticTokenModifiers.Readonly,
            LocalDeclarationStatementSyntax local when local.IsConst || local.IsReadonly => modifiers | LanguageSemanticTokenModifiers.Readonly,
            LocalSemanticSymbol { IsReadonly: true } => modifiers | LanguageSemanticTokenModifiers.Readonly,
            _ => modifiers,
        };
    }

    private LanguageSemanticTokenModifiers DeclarationModifiers(object? symbol, MemberDeclarationSyntax syntax)
    {
        var modifiers = symbol is null ? DeclarationModifier(syntax.Source.FilePath) : SymbolModifiers(symbol) | LanguageSemanticTokenModifiers.Declaration;
        if (syntax.Modifiers.Contains("static", StringComparer.Ordinal))
            modifiers |= LanguageSemanticTokenModifiers.Static;
        if (syntax.Modifiers.Contains("readonly", StringComparer.Ordinal) || syntax.Modifiers.Contains("const", StringComparer.Ordinal))
            modifiers |= LanguageSemanticTokenModifiers.Readonly;
        return modifiers;
    }

    private LanguageSemanticTokenModifiers DeclarationModifiers(TypeSymbol symbol) => SymbolModifiers(symbol) | LanguageSemanticTokenModifiers.Declaration;

    private static LanguageSemanticTokenModifiers DeclarationModifier(string filePath) =>
        LanguageSemanticTokenModifiers.Declaration | (IsStandardLibrary(filePath) ? LanguageSemanticTokenModifiers.DefaultLibrary : 0);

    private static LanguageSemanticTokenKind TypeTokenKind(TypeSymbol type) => type.Kind switch
    {
        DeclaredTypeKind.Struct => LanguageSemanticTokenKind.Struct,
        DeclaredTypeKind.Enum => LanguageSemanticTokenKind.Enum,
        _ => LanguageSemanticTokenKind.Class,
    };

    private static MemberSymbol? FindMemberSymbol(TypeSymbol? type, MemberDeclarationSyntax syntax)
    {
        if (type is null)
            return null;
        return type.Fields.Cast<MemberSymbol>().Concat(type.Properties).Concat(type.Methods).Concat(type.Constructors)
            .FirstOrDefault(member => ReferenceEquals(member.Syntax, syntax));
    }

    private static string MemberName(MemberDeclarationSyntax syntax) => syntax switch
    {
        FieldDeclarationSyntax field => field.Name,
        PropertyDeclarationSyntax property => property.Name,
        MethodDeclarationSyntax method => method.Name,
        OperatorDeclarationSyntax @operator => OperatorFacts.DisplayName(@operator.OperatorToken.Kind),
        ConstructorDeclarationSyntax constructor => constructor.Name,
        _ => string.Empty,
    };

    private static IEnumerable<ParameterSyntax> Parameters(MemberDeclarationSyntax syntax) => syntax switch
    {
        MethodDeclarationSyntax method => method.Parameters,
        OperatorDeclarationSyntax @operator => @operator.Parameters,
        ConstructorDeclarationSyntax constructor => constructor.Parameters,
        _ => [],
    };

    private static SyntaxToken? FindDeclarationToken(SyntaxTree tree, SyntaxNode syntax, string name)
    {
        if (syntax is OperatorDeclarationSyntax @operator)
            return @operator.OperatorToken;
        var candidates = IdentifierTokens(tree, syntax.Span).Where(token => IdentifierEquals(IdentifierValue(token), name));
        return syntax switch
        {
            FieldDeclarationSyntax field => candidates.FirstOrDefault(token => token.Span.Start >= field.Type.Span.End),
            PropertyDeclarationSyntax property => candidates.FirstOrDefault(token => token.Span.Start >= property.Type.Span.End),
            MethodDeclarationSyntax method => candidates.FirstOrDefault(token => token.Span.Start >= method.ReturnType.Span.End),
            ParameterSyntax parameter => candidates.FirstOrDefault(token => token.Span.Start >= parameter.Type.Span.End),
            LocalDeclarationStatementSyntax local => candidates.FirstOrDefault(token => token.Span.Start >= local.Type.Span.End),
            ForeachStatementSyntax loop => candidates.FirstOrDefault(token => token.Span.Start >= loop.Type.Span.End && token.Span.End <= loop.Collection.Span.Start),
            CatchClauseSyntax clause when clause.Type is not null => candidates.FirstOrDefault(token => token.Span.Start >= clause.Type.Span.End && token.Span.End <= clause.Body.Span.Start),
            ConstructorDeclarationSyntax constructor => candidates.LastOrDefault(token => token.Span.End <= (constructor.Parameters.FirstOrDefault()?.Span.Start ?? constructor.Body.Span.Start)),
            EnumMemberSyntax enumMember => candidates.FirstOrDefault(token => token.Span.End <= (enumMember.Value?.Span.Start ?? enumMember.Span.End)),
            TypeDeclarationSyntax declaration => candidates.FirstOrDefault(token => token.Span.End <= (declaration.BaseType?.Span.Start ?? declaration.Members.FirstOrDefault()?.Span.Start ?? declaration.EnumMembers.FirstOrDefault()?.Span.Start ?? declaration.Span.End)),
            _ => candidates.FirstOrDefault(),
        };
    }

    private static IEnumerable<SyntaxToken> IdentifierTokens(SyntaxTree tree, TextSpan span) => tree.Tokens
        .Where(token => token.Kind == SyntaxKind.IdentifierToken && !token.IsMissing && token.Span.Length != 0 && token.Span.Start >= span.Start && token.Span.End <= span.End)
        .OrderBy(token => token.Span.Start);

    private void AddQualifiedNamespace(SyntaxTree tree, TextSpan span, string name, Dictionary<TextSpan, ClassifiedToken> result, bool defaultLibrary)
    {
        var expected = name.Split('.').Select(NormalizeIdentifier).ToArray();
        var actual = IdentifierTokens(tree, span).Where(token => expected.Contains(IdentifierValue(token), StringComparer.Ordinal)).Take(expected.Length);
        foreach (var token in actual)
            Add(result, token.Span, LanguageSemanticTokenKind.Namespace, defaultLibrary ? LanguageSemanticTokenModifiers.DefaultLibrary : 0);
    }

    private bool IsDefaultLibraryNamespace(string name)
    {
        var normalized = string.Join('.', name.Split('.').Select(NormalizeIdentifier));
        return _model.Types.Values.Any(type => IsStandardLibrary(type.Syntax?.Source.FilePath) &&
            (type.Namespace == normalized || type.Namespace.StartsWith(normalized + ".", StringComparison.Ordinal)));
    }

    private static void Add(Dictionary<TextSpan, ClassifiedToken> result, TextSpan span, LanguageSemanticTokenKind kind, LanguageSemanticTokenModifiers modifiers)
    {
        if (span.Length == 0)
            return;
        var candidate = new ClassifiedToken(span, kind, modifiers);
        if (!result.TryGetValue(span, out var existing) || modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration) && !existing.Modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration))
            result[span] = candidate;
    }

    private static bool IsSingleLine(SourceText source, TextSpan span) =>
        source.GetLocation(new TextSpan(span.Start, 0)).Line == source.GetLocation(new TextSpan(span.End, 0)).Line;

    private static bool IsStandardLibrary(string? filePath) => filePath is not null && filePath.Replace('\\', '/').StartsWith("stdlib/", StringComparison.Ordinal);

    private readonly record struct ClassifiedToken(TextSpan Span, LanguageSemanticTokenKind Kind, LanguageSemanticTokenModifiers Modifiers);
}
