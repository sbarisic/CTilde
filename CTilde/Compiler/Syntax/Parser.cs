using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class Parser
{
    private static readonly HashSet<SyntaxKind> ModifierKinds =
    [
        SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword,
        SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword, SyntaxKind.SealedKeyword,
        SyntaxKind.ReadonlyKeyword, SyntaxKind.ConstKeyword, SyntaxKind.UnsafeKeyword,
        SyntaxKind.VirtualKeyword, SyntaxKind.OverrideKeyword, SyntaxKind.AbstractKeyword,
        SyntaxKind.VolatileKeyword,
    ];

    private readonly SourceText _source;
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<SyntaxToken> _missingTokens = [];
    private readonly List<SyntaxToken> _skippedTokens = [];
    private int _position;
    private int _splitGreaterPosition = -1;

    public Parser(SourceText source, ImmutableArray<SyntaxToken> tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _tokens = [.. tokens.Where(token => token.Kind != SyntaxKind.BadToken)];
        _skippedTokens.AddRange(tokens.Where(token => token.Kind == SyntaxKind.BadToken));
        _diagnostics = diagnostics;
    }

    public ImmutableArray<SyntaxToken> MissingTokens => [.. _missingTokens];
    public ImmutableArray<SyntaxToken> SkippedTokens => [.. _skippedTokens];

    private SyntaxToken Current => Peek(0);
    private SyntaxToken Peek(int offset) => _tokens[Math.Clamp(_position + offset, 0, _tokens.Length - 1)];
    private SyntaxToken NextToken() { var token = Current; _position = Math.Min(_position + 1, _tokens.Length - 1); return token; }
    private SyntaxToken SkipToken() { var token = NextToken(); if (token.Kind != SyntaxKind.EndOfFileToken) _skippedTokens.Add(token); return token; }

    private bool AtTypeArgumentClose => Current.Kind is SyntaxKind.GreaterToken or SyntaxKind.GreaterGreaterToken;

    private SyntaxToken ConsumeTypeArgumentClose()
    {
        if (Current.Kind == SyntaxKind.GreaterToken)
            return NextToken();
        if (Current.Kind == SyntaxKind.GreaterGreaterToken)
        {
            var original = Current;
            if (_splitGreaterPosition == _position)
            {
                _splitGreaterPosition = -1;
                _position = Math.Min(_position + 1, _tokens.Length - 1);
                return new SyntaxToken(SyntaxKind.GreaterToken, _source, new TextSpan(original.Span.Start + 1, 1), ">");
            }
            _splitGreaterPosition = _position;
            return new SyntaxToken(SyntaxKind.GreaterToken, _source, new TextSpan(original.Span.Start, 1), ">");
        }
        return Match(SyntaxKind.GreaterToken);
    }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var start = Current.Span.Start;
        var usings = ImmutableArray.CreateBuilder<UsingDirectiveSyntax>();
        while (Current.Kind == SyntaxKind.UsingKeyword)
            usings.Add(ParseUsing());

        NamespaceSyntax? namespaceSyntax = null;
        var types = ImmutableArray.CreateBuilder<TypeDeclarationSyntax>();
        if (Current.Kind == SyntaxKind.NamespaceKeyword)
        {
            var namespaceStart = NextToken().Span.Start;
            var name = ParseQualifiedName();
            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                var end = NextToken().Span.End;
                namespaceSyntax = new NamespaceSyntax(_source, TextSpan.FromBounds(namespaceStart, end), name, true);
                ParseTypes(types, SyntaxKind.EndOfFileToken);
            }
            else
            {
                Match(SyntaxKind.OpenBraceToken);
                namespaceSyntax = new NamespaceSyntax(_source, TextSpan.FromBounds(namespaceStart, Current.Span.Start), name, false);
                ParseTypes(types, SyntaxKind.CloseBraceToken);
                Match(SyntaxKind.CloseBraceToken);
                if (Current.Kind != SyntaxKind.EndOfFileToken)
                    Report("CT0101", "A file cannot contain declarations outside its block namespace.", Current);
            }
        }
        else
        {
            ParseTypes(types, SyntaxKind.EndOfFileToken);
        }

        var eof = Match(SyntaxKind.EndOfFileToken);
        return new CompilationUnitSyntax(_source, TextSpan.FromBounds(start, eof.Span.End), usings.ToImmutable(), namespaceSyntax, types.ToImmutable());
    }

    private UsingDirectiveSyntax ParseUsing()
    {
        var start = Match(SyntaxKind.UsingKeyword).Span.Start;
        var name = ParseQualifiedName();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new UsingDirectiveSyntax(_source, TextSpan.FromBounds(start, end), name);
    }

    private void ParseTypes(ImmutableArray<TypeDeclarationSyntax>.Builder types, SyntaxKind terminator)
    {
        while (Current.Kind != terminator && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            var before = _position;
            var attributes = ParseAttributes();
            var modifiers = ParseModifiers();
            if (Current.Kind is SyntaxKind.ClassKeyword or SyntaxKind.StructKeyword or SyntaxKind.InterfaceKeyword or SyntaxKind.EnumKeyword)
                types.Add(ParseTypeDeclaration(attributes, modifiers));
            else if (Current.Kind == SyntaxKind.DelegateKeyword)
                types.Add(ParseDelegateDeclaration(attributes, modifiers));
            else if (Current.Kind == SyntaxKind.OpaqueKeyword)
                types.Add(ParseOpaqueDeclaration(attributes, modifiers));
            else
            {
                Report("CT0102", "Expected a class, structure, interface, enumeration, delegate, or opaque declaration.", Current);
                Synchronize(SyntaxKind.ClassKeyword, SyntaxKind.StructKeyword, SyntaxKind.InterfaceKeyword, SyntaxKind.EnumKeyword, SyntaxKind.DelegateKeyword, SyntaxKind.OpaqueKeyword, terminator);
            }
            if (_position == before)
                SkipToken();
        }
    }

    private TypeDeclarationSyntax ParseOpaqueDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<string> modifiers)
    {
        var start = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;
        Match(SyntaxKind.OpaqueKeyword);
        var name = Match(SyntaxKind.IdentifierToken);
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new TypeDeclarationSyntax(
            _source, TextSpan.FromBounds(start, end), TypeDeclarationKind.Opaque, name.Text,
            modifiers, attributes, null, [], null, [], null, []);
    }

    private TypeDeclarationSyntax ParseDelegateDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<string> modifiers)
    {
        var start = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;
        Match(SyntaxKind.DelegateKeyword);
        var returnType = ParseType();
        var name = Match(SyntaxKind.IdentifierToken);
        var typeParameters = ParseTypeParameters();
        var parameters = ParseParameters();
        var constraints = ParseConstraintClauses();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new TypeDeclarationSyntax(
            _source,
            TextSpan.FromBounds(start, end),
            TypeDeclarationKind.Delegate,
            name.Text,
            modifiers,
            attributes,
            null,
            [],
            null,
            [],
            returnType,
            parameters,
            typeParameters,
            [],
            constraints);
    }

    private TypeDeclarationSyntax ParseTypeDeclaration(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<string> modifiers)
    {
        var start = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;
        var kindToken = NextToken();
        var kind = kindToken.Kind switch
        {
            SyntaxKind.StructKeyword => TypeDeclarationKind.Struct,
            SyntaxKind.InterfaceKeyword => TypeDeclarationKind.Interface,
            SyntaxKind.EnumKeyword => TypeDeclarationKind.Enum,
            _ => TypeDeclarationKind.Class,
        };
        var name = Match(SyntaxKind.IdentifierToken);
        var typeParameters = kind == TypeDeclarationKind.Enum ? ImmutableArray<TypeParameterSyntax>.Empty : ParseTypeParameters();
        TypeSyntax? underlying = null;
        TypeSyntax? baseType = null;
        var baseTypes = ImmutableArray.CreateBuilder<TypeSyntax>();
        if (kind == TypeDeclarationKind.Enum && Current.Kind == SyntaxKind.ColonToken)
        {
            NextToken();
            underlying = ParseType();
        }
        else if (kind is TypeDeclarationKind.Class or TypeDeclarationKind.Struct or TypeDeclarationKind.Interface && Current.Kind == SyntaxKind.ColonToken)
        {
            NextToken();
            while (Current.Kind is not SyntaxKind.OpenBraceToken and not SyntaxKind.WhereKeyword and not SyntaxKind.EndOfFileToken)
            {
                var candidate = ParseType();
                baseTypes.Add(candidate);
                if (Current.Kind != SyntaxKind.CommaToken)
                    break;
                NextToken();
            }
            if (kind == TypeDeclarationKind.Class && baseTypes.Count != 0)
                baseType = baseTypes[0];
        }
        var constraints = ParseConstraintClauses();
        Match(SyntaxKind.OpenBraceToken);

        var members = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();
        var enumMembers = ImmutableArray.CreateBuilder<EnumMemberSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            var before = _position;
            if (kind == TypeDeclarationKind.Enum)
                enumMembers.Add(ParseEnumMember());
            else
                members.Add(ParseMember(name.Text));
            if (_position == before)
                SkipToken();
        }
        var close = Match(SyntaxKind.CloseBraceToken);
        return new TypeDeclarationSyntax(_source, TextSpan.FromBounds(start, close.Span.End), kind, name.Text, modifiers, attributes, baseType, members.ToImmutable(), underlying, enumMembers.ToImmutable(), null, [], typeParameters, baseTypes.ToImmutable(), constraints);
    }

    private EnumMemberSyntax ParseEnumMember()
    {
        var name = Match(SyntaxKind.IdentifierToken);
        ExpressionSyntax? value = null;
        if (Current.Kind == SyntaxKind.EqualsToken)
        {
            NextToken();
            value = ParseExpression();
        }
        var end = value?.Span.End ?? name.Span.End;
        if (Current.Kind == SyntaxKind.CommaToken)
            end = NextToken().Span.End;
        else if (Current.Kind != SyntaxKind.CloseBraceToken)
            Match(SyntaxKind.CommaToken);
        return new EnumMemberSyntax(_source, TextSpan.FromBounds(name.Span.Start, end), name.Text, value);
    }

    private MemberDeclarationSyntax ParseMember(string containingTypeName)
    {
        var attributes = ParseAttributes();
        var modifiers = ParseModifiers();
        var start = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;

        if (Current.Kind == SyntaxKind.IdentifierToken && Current.Text == containingTypeName && Peek(1).Kind == SyntaxKind.OpenParenToken)
        {
            var name = NextToken();
            var parameters = ParseParameters();
            ConstructorInitializerSyntax? constructorInitializer = null;
            if (Current.Kind == SyntaxKind.ColonToken)
            {
                var initializerStart = NextToken().Span.Start;
                var initializerToken = Current;
                var initializerKind = initializerToken.Kind == SyntaxKind.ThisKeyword
                    ? ConstructorInitializerKind.This
                    : ConstructorInitializerKind.Base;
                if (initializerToken.Kind is SyntaxKind.ThisKeyword or SyntaxKind.BaseKeyword)
                    NextToken();
                else
                    Match(SyntaxKind.BaseKeyword);
                var initializerArguments = ParseCallArguments(out var initializerEnd);
                constructorInitializer = new ConstructorInitializerSyntax(_source, TextSpan.FromBounds(initializerStart, initializerEnd), initializerKind, initializerArguments);
            }
            var body = ParseBlock();
            return new ConstructorDeclarationSyntax(_source, TextSpan.FromBounds(start, body.Span.End), modifiers, attributes, name.Text, parameters, constructorInitializer, body);
        }

        var type = ParseType();
        if (Current.Kind == SyntaxKind.OperatorKeyword)
        {
            NextToken();
            SyntaxToken operatorToken;
            if (Current.Kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken)
                operatorToken = NextToken();
            else
            {
                Report("CT0108", "Expected one of +, -, *, or / in an operator declaration.", Current);
                operatorToken = new SyntaxToken(SyntaxKind.BadToken, _source, new TextSpan(Current.Span.Start, 0), string.Empty) { IsMissing = true };
                _missingTokens.Add(operatorToken);
                if (Current.Kind is not SyntaxKind.OpenParenToken and not SyntaxKind.EndOfFileToken)
                    SkipToken();
            }
            var parameters = ParseParameters();
            BlockStatementSyntax? body;
            int end;
            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                end = NextToken().Span.End;
                body = null;
            }
            else
            {
                body = ParseBlock();
                end = body.Span.End;
            }
            return new OperatorDeclarationSyntax(_source, TextSpan.FromBounds(start, end), modifiers, attributes, type, operatorToken, parameters, body);
        }
        var memberName = Match(SyntaxKind.IdentifierToken);
        var typeParameters = ParseTypeParameters();
        if (Current.Kind == SyntaxKind.OpenParenToken)
        {
            var parameters = ParseParameters();
            var constraints = ParseConstraintClauses();
            BlockStatementSyntax? body;
            int end;
            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                end = NextToken().Span.End;
                body = null;
            }
            else
            {
                body = ParseBlock();
                end = body.Span.End;
            }
            return new MethodDeclarationSyntax(_source, TextSpan.FromBounds(start, end), modifiers, attributes, type, memberName.Text, parameters, body, typeParameters, constraints);
        }

        if (!typeParameters.IsDefaultOrEmpty)
            Report("CT0111", "Only methods can declare member type parameters.", memberName);

        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            NextToken();
            AccessorSyntax? getter = null;
            AccessorSyntax? setter = null;
            while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
            {
                var accessorModifiers = ParseModifiers();
                var accessor = Current;
                if (accessor.Kind is not SyntaxKind.GetKeyword and not SyntaxKind.SetKeyword)
                {
                    Report("CT0103", "Expected a get or set accessor.", Current);
                    SkipToken();
                    continue;
                }
                NextToken();
                BlockStatementSyntax? accessorBody = null;
                int accessorEnd;
                if (Current.Kind == SyntaxKind.SemicolonToken)
                    accessorEnd = NextToken().Span.End;
                else
                {
                    accessorBody = ParseBlock();
                    accessorEnd = accessorBody.Span.End;
                }
                var syntax = new AccessorSyntax(_source, TextSpan.FromBounds(accessor.Span.Start, accessorEnd), accessor.Text, accessorModifiers, accessorBody);
                if (accessor.Kind == SyntaxKind.GetKeyword)
                {
                    if (getter is not null)
                        Report("CT0106", "A property can declare only one getter.", accessor);
                    getter = syntax;
                }
                else
                {
                    if (setter is not null)
                        Report("CT0107", "A property can declare only one setter.", accessor);
                    setter = syntax;
                }
            }
            var close = Match(SyntaxKind.CloseBraceToken);
            return new PropertyDeclarationSyntax(_source, TextSpan.FromBounds(start, close.Span.End), modifiers, attributes, type, memberName.Text, getter, setter);
        }

        ExpressionSyntax? initializer = null;
        if (Current.Kind == SyntaxKind.EqualsToken)
        {
            NextToken();
            initializer = ParseExpression();
        }
        var semicolon = Match(SyntaxKind.SemicolonToken);
        return new FieldDeclarationSyntax(_source, TextSpan.FromBounds(start, semicolon.Span.End), modifiers, attributes, type, memberName.Text, initializer);
    }

    private ImmutableArray<ParameterSyntax> ParseParameters()
    {
        Match(SyntaxKind.OpenParenToken);
        var parameters = ImmutableArray.CreateBuilder<ParameterSyntax>();
        while (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
        {
            var start = Current.Span.Start;
            var attributes = ParseAttributes();
            var passingKind = ParsePassingKind();
            var type = ParseType();
            var name = Match(SyntaxKind.IdentifierToken);
            parameters.Add(new ParameterSyntax(_source, TextSpan.FromBounds(start, name.Span.End), attributes, passingKind, type, name.Text));
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        Match(SyntaxKind.CloseParenToken);
        return parameters.ToImmutable();
    }

    private ImmutableArray<TypeParameterSyntax> ParseTypeParameters()
    {
        if (Current.Kind != SyntaxKind.LessToken)
            return [];
        NextToken();
        var parameters = ImmutableArray.CreateBuilder<TypeParameterSyntax>();
        while (!AtTypeArgumentClose && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            var name = Match(SyntaxKind.IdentifierToken);
            parameters.Add(new TypeParameterSyntax(_source, name.Span, name.Text));
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        ConsumeTypeArgumentClose();
        return parameters.ToImmutable();
    }

    private ImmutableArray<TypeParameterConstraintClauseSyntax> ParseConstraintClauses()
    {
        var clauses = ImmutableArray.CreateBuilder<TypeParameterConstraintClauseSyntax>();
        while (Current.Kind == SyntaxKind.WhereKeyword)
        {
            var start = NextToken().Span.Start;
            var name = Match(SyntaxKind.IdentifierToken);
            Match(SyntaxKind.ColonToken);
            var constraints = ImmutableArray.CreateBuilder<TypeParameterConstraintSyntax>();
            while (Current.Kind is not SyntaxKind.WhereKeyword and not SyntaxKind.OpenBraceToken and not SyntaxKind.SemicolonToken and not SyntaxKind.EndOfFileToken)
            {
                var constraintStart = Current.Span.Start;
                TypeParameterConstraintSyntax constraint;
                if (Current.Kind == SyntaxKind.ClassKeyword)
                {
                    var token = NextToken();
                    constraint = new TypeParameterConstraintSyntax(_source, token.Span, TypeParameterConstraintKind.Class);
                }
                else if (Current.Kind == SyntaxKind.StructKeyword)
                {
                    var token = NextToken();
                    constraint = new TypeParameterConstraintSyntax(_source, token.Span, TypeParameterConstraintKind.Struct);
                }
                else if (Current.Kind == SyntaxKind.UnmanagedKeyword)
                {
                    var token = NextToken();
                    constraint = new TypeParameterConstraintSyntax(_source, token.Span, TypeParameterConstraintKind.Unmanaged);
                }
                else if (Current.Kind == SyntaxKind.NewKeyword && Peek(1).Kind == SyntaxKind.OpenParenToken)
                {
                    NextToken();
                    NextToken();
                    var close = Match(SyntaxKind.CloseParenToken);
                    constraint = new TypeParameterConstraintSyntax(_source, TextSpan.FromBounds(constraintStart, close.Span.End), TypeParameterConstraintKind.Constructor);
                }
                else
                {
                    var type = ParseType();
                    constraint = new TypeParameterConstraintSyntax(_source, type.Span, TypeParameterConstraintKind.Type, type);
                }
                constraints.Add(constraint);
                if (Current.Kind != SyntaxKind.CommaToken)
                    break;
                NextToken();
            }
            var end = constraints.Count == 0 ? name.Span.End : constraints[^1].Span.End;
            clauses.Add(new TypeParameterConstraintClauseSyntax(_source, TextSpan.FromBounds(start, end), name.Text, constraints.ToImmutable()));
        }
        return clauses.ToImmutable();
    }

    private BlockStatementSyntax ParseBlock()
    {
        var open = Match(SyntaxKind.OpenBraceToken);
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            var before = _position;
            statements.Add(ParseStatement());
            if (before == _position)
                SkipToken();
        }
        var close = Match(SyntaxKind.CloseBraceToken);
        return new BlockStatementSyntax(_source, TextSpan.FromBounds(open.Span.Start, close.Span.End), statements.ToImmutable());
    }

    private StatementSyntax ParseStatement()
    {
        if (Current.Kind == SyntaxKind.OpenBracketToken)
        {
            var attributes = ParseAttributes();
            if (Current.Kind == SyntaxKind.AsmKeyword)
                return ParseInlineAssembly(attributes);
            Report("CT0109", "Statement attributes are supported only on asm statements.", Current);
        }
        return Current.Kind switch
        {
            SyntaxKind.OpenBraceToken => ParseBlock(),
            SyntaxKind.SemicolonToken => new EmptyStatementSyntax(_source, NextToken().Span),
            SyntaxKind.IfKeyword => ParseIf(),
            SyntaxKind.WhileKeyword => ParseWhile(),
            SyntaxKind.DoKeyword => ParseDo(),
            SyntaxKind.ForKeyword => ParseFor(),
            SyntaxKind.ForeachKeyword => ParseForeach(),
            SyntaxKind.SwitchKeyword => ParseSwitch(),
            SyntaxKind.BreakKeyword => ParseSimpleJump(true),
            SyntaxKind.ContinueKeyword => ParseSimpleJump(false),
            SyntaxKind.DeferKeyword => ParseDefer(),
            SyntaxKind.LockKeyword => ParseLock(),
            SyntaxKind.ReturnKeyword => ParseReturn(),
            SyntaxKind.ThrowKeyword => ParseThrow(),
            SyntaxKind.TryKeyword => ParseTry(),
            SyntaxKind.AsmKeyword => ParseInlineAssembly([]),
            SyntaxKind.UnsafeKeyword when Peek(1).Kind == SyntaxKind.OpenBraceToken => ParseUnsafe(),
            _ when LooksLikeLocalDeclaration() => ParseLocalDeclaration(true),
            _ => ParseExpressionStatement(),
        };
    }

    private LocalDeclarationStatementSyntax ParseLocalDeclaration(bool consumeSemicolon)
    {
        var start = Current.Span.Start;
        var isConst = Current.Kind == SyntaxKind.ConstKeyword;
        var isReadonly = Current.Kind == SyntaxKind.ReadonlyKeyword;
        if (isConst || isReadonly)
            NextToken();
        var type = ParseType(allowVar: true);
        var name = Match(SyntaxKind.IdentifierToken);
        ExpressionSyntax? initializer = null;
        if (Current.Kind == SyntaxKind.EqualsToken)
        {
            NextToken();
            initializer = ParseExpression();
        }
        var end = initializer?.Span.End ?? name.Span.End;
        if (consumeSemicolon)
            end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new LocalDeclarationStatementSyntax(_source, TextSpan.FromBounds(start, end), type, name.Text, initializer, isConst, isReadonly);
    }

    private StatementSyntax ParseExpressionStatement()
    {
        var expression = ParseExpression();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new ExpressionStatementSyntax(_source, TextSpan.FromBounds(expression.Span.Start, end), expression);
    }

    private IfStatementSyntax ParseIf()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var then = ParseStatement();
        StatementSyntax? @else = null;
        if (Current.Kind == SyntaxKind.ElseKeyword)
        {
            NextToken();
            @else = ParseStatement();
        }
        return new IfStatementSyntax(_source, TextSpan.FromBounds(start, (@else ?? then).Span.End), condition, then, @else);
    }

    private WhileStatementSyntax ParseWhile()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var body = ParseStatement();
        return new WhileStatementSyntax(_source, TextSpan.FromBounds(start, body.Span.End), condition, body);
    }

    private DoStatementSyntax ParseDo()
    {
        var start = NextToken().Span.Start;
        var body = ParseStatement();
        Match(SyntaxKind.WhileKeyword);
        Match(SyntaxKind.OpenParenToken);
        var condition = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new DoStatementSyntax(_source, TextSpan.FromBounds(start, end), body, condition);
    }

    private ForStatementSyntax ParseFor()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        StatementSyntax? initializer = null;
        if (Current.Kind != SyntaxKind.SemicolonToken)
            initializer = LooksLikeLocalDeclaration() ? ParseLocalDeclaration(false) : new ExpressionStatementSyntax(_source, Current.Span, ParseExpression());
        Match(SyntaxKind.SemicolonToken);
        ExpressionSyntax? condition = Current.Kind == SyntaxKind.SemicolonToken ? null : ParseExpression();
        Match(SyntaxKind.SemicolonToken);
        ExpressionSyntax? iterator = Current.Kind == SyntaxKind.CloseParenToken ? null : ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var body = ParseStatement();
        return new ForStatementSyntax(_source, TextSpan.FromBounds(start, body.Span.End), initializer, condition, iterator, body);
    }

    private ForeachStatementSyntax ParseForeach()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        var type = ParseType(allowVar: true);
        var name = Match(SyntaxKind.IdentifierToken);
        Match(SyntaxKind.InKeyword);
        var collection = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var body = ParseStatement();
        return new ForeachStatementSyntax(_source, TextSpan.FromBounds(start, body.Span.End), type, name.Text, collection, body);
    }

    private SwitchStatementSyntax ParseSwitch()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        var expression = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        Match(SyntaxKind.OpenBraceToken);
        var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            var sectionStart = Current.Span.Start;
            var labels = ImmutableArray.CreateBuilder<SwitchLabelSyntax>();
            while (Current.Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
            {
                var labelStart = NextToken();
                ExpressionSyntax? value = labelStart.Kind == SyntaxKind.CaseKeyword ? ParseExpression() : null;
                var colon = Match(SyntaxKind.ColonToken);
                labels.Add(new SwitchLabelSyntax(_source, TextSpan.FromBounds(labelStart.Span.Start, colon.Span.End), value));
            }
            if (labels.Count == 0)
            {
                Report("CT0104", "Expected a case or default label.", Current);
                NextToken();
                continue;
            }
            var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
            while (Current.Kind is not SyntaxKind.CaseKeyword and not SyntaxKind.DefaultKeyword and not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
                statements.Add(ParseStatement());
            var end = statements.Count == 0 ? labels[^1].Span.End : statements[^1].Span.End;
            sections.Add(new SwitchSectionSyntax(_source, TextSpan.FromBounds(sectionStart, end), labels.ToImmutable(), statements.ToImmutable()));
        }
        var close = Match(SyntaxKind.CloseBraceToken);
        return new SwitchStatementSyntax(_source, TextSpan.FromBounds(start, close.Span.End), expression, sections.ToImmutable());
    }

    private StatementSyntax ParseSimpleJump(bool isBreak)
    {
        var start = NextToken().Span.Start;
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return isBreak ? new BreakStatementSyntax(_source, TextSpan.FromBounds(start, end)) : new ContinueStatementSyntax(_source, TextSpan.FromBounds(start, end));
    }

    private ReturnStatementSyntax ParseReturn()
    {
        var start = NextToken().Span.Start;
        var expression = Current.Kind == SyntaxKind.SemicolonToken ? null : ParseExpression();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new ReturnStatementSyntax(_source, TextSpan.FromBounds(start, end), expression);
    }

    private DeferStatementSyntax ParseDefer()
    {
        var start = NextToken().Span.Start;
        var expression = ParseExpression();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new DeferStatementSyntax(_source, TextSpan.FromBounds(start, end), expression);
    }

    private LockStatementSyntax ParseLock()
    {
        var start = NextToken().Span.Start;
        Match(SyntaxKind.OpenParenToken);
        var expression = ParseExpression();
        Match(SyntaxKind.CloseParenToken);
        var body = ParseBlock();
        return new LockStatementSyntax(_source, TextSpan.FromBounds(start, body.Span.End), expression, body);
    }

    private ThrowStatementSyntax ParseThrow()
    {
        var start = NextToken().Span.Start;
        var expression = Current.Kind == SyntaxKind.SemicolonToken ? null : ParseExpression();
        var end = Match(SyntaxKind.SemicolonToken).Span.End;
        return new ThrowStatementSyntax(_source, TextSpan.FromBounds(start, end), expression);
    }

    private TryStatementSyntax ParseTry()
    {
        var start = NextToken().Span.Start;
        var body = ParseBlock();
        var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
        while (Current.Kind == SyntaxKind.CatchKeyword)
        {
            var catchStart = NextToken().Span.Start;
            TypeSyntax? type = null;
            string? name = null;
            if (Current.Kind == SyntaxKind.OpenParenToken)
            {
                NextToken();
                type = ParseType();
                if (Current.Kind == SyntaxKind.IdentifierToken)
                    name = NextToken().Text;
                Match(SyntaxKind.CloseParenToken);
            }
            var catchBody = ParseBlock();
            catches.Add(new CatchClauseSyntax(_source, TextSpan.FromBounds(catchStart, catchBody.Span.End), type, name, catchBody));
        }

        FinallyClauseSyntax? finallyClause = null;
        if (Current.Kind == SyntaxKind.FinallyKeyword)
        {
            var finallyStart = NextToken().Span.Start;
            var finallyBody = ParseBlock();
            finallyClause = new FinallyClauseSyntax(_source, TextSpan.FromBounds(finallyStart, finallyBody.Span.End), finallyBody);
        }
        if (catches.Count == 0 && finallyClause is null)
            _diagnostics.Add("CT0108", "A try statement requires a catch or finally clause.", _source, TextSpan.FromBounds(start, body.Span.End));
        var end = finallyClause?.Span.End ?? (catches.Count == 0 ? body.Span.End : catches[^1].Span.End);
        return new TryStatementSyntax(_source, TextSpan.FromBounds(start, end), body, catches.ToImmutable(), finallyClause);
    }

    private UnsafeStatementSyntax ParseUnsafe()
    {
        var start = NextToken().Span.Start;
        var body = ParseBlock();
        return new UnsafeStatementSyntax(_source, TextSpan.FromBounds(start, body.Span.End), body);
    }

    private ExpressionSyntax ParseExpression()
    {
        var left = ParseBinaryExpression();
        if (IsAssignment(Current.Kind))
        {
            var op = NextToken();
            var right = ParseExpression();
            return new AssignmentExpressionSyntax(_source, TextSpan.FromBounds(left.Span.Start, right.Span.End), left, op.Kind, right);
        }
        return left;
    }

    private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        var unaryPrecedence = GetUnaryPrecedence(Current.Kind);
        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            var op = NextToken();
            var operand = ParseBinaryExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(_source, TextSpan.FromBounds(op.Span.Start, operand.Span.End), op.Kind, operand);
        }
        else if (Current.Kind == SyntaxKind.OpenParenToken && LooksLikeCast())
        {
            var start = NextToken().Span.Start;
            var type = ParseType();
            Match(SyntaxKind.CloseParenToken);
            var expression = ParseBinaryExpression(12);
            left = new CastExpressionSyntax(_source, TextSpan.FromBounds(start, expression.Span.End), type, expression);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            if (Current.Kind is SyntaxKind.IsKeyword or SyntaxKind.AsKeyword)
            {
                const int typePrecedence = 8;
                if (typePrecedence <= parentPrecedence)
                    break;
                var typeOperator = NextToken();
                var type = ParseType();
                left = typeOperator.Kind == SyntaxKind.IsKeyword
                    ? new TypeTestExpressionSyntax(_source, TextSpan.FromBounds(left.Span.Start, type.Span.End), left, type)
                    : new SafeCastExpressionSyntax(_source, TextSpan.FromBounds(left.Span.Start, type.Span.End), left, type);
                continue;
            }
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;
            var op = NextToken();
            var right = ParseBinaryExpression(precedence);
            left = new BinaryExpressionSyntax(_source, TextSpan.FromBounds(left.Span.Start, right.Span.End), left, op.Kind, right);
        }
        return left;
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        ExpressionSyntax expression = ParsePrimaryExpression();
        while (true)
        {
            if (Current.Kind == SyntaxKind.DotToken)
            {
                NextToken();
                var name = Match(SyntaxKind.IdentifierToken);
                var typeArguments = LooksLikeInvocationTypeArguments() ? ParseInvocationTypeArguments() : [];
                var end = typeArguments.IsDefaultOrEmpty ? name.Span.End : typeArguments[^1].Span.End;
                expression = new MemberAccessExpressionSyntax(_source, TextSpan.FromBounds(expression.Span.Start, end), expression, name.Text, typeArguments);
            }
            else if (Current.Kind == SyntaxKind.OpenParenToken)
            {
                var arguments = ParseCallArguments(out var end);
                expression = new CallExpressionSyntax(_source, TextSpan.FromBounds(expression.Span.Start, end), expression, arguments);
            }
            else if (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                NextToken();
                var index = ParseExpression();
                var close = Match(SyntaxKind.CloseBracketToken);
                expression = new IndexExpressionSyntax(_source, TextSpan.FromBounds(expression.Span.Start, close.Span.End), expression, index);
            }
            else if (Current.Kind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
            {
                var op = NextToken();
                expression = new UnaryExpressionSyntax(_source, TextSpan.FromBounds(expression.Span.Start, op.Span.End), op.Kind, expression, true);
            }
            else
                return expression;
        }
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        var token = Current;
        switch (token.Kind)
        {
            case SyntaxKind.OpenParenToken:
                var open = NextToken();
                var nested = ParseExpression();
                var close = Match(SyntaxKind.CloseParenToken);
                return new ParenthesizedExpressionSyntax(_source, TextSpan.FromBounds(open.Span.Start, close.Span.End), nested);
            case SyntaxKind.NewKeyword:
                return ParseNew();
            case SyntaxKind.StackallocKeyword:
                return ParseStackAlloc();
            case SyntaxKind.ThisKeyword:
                NextToken();
                return new ThisExpressionSyntax(_source, token.Span);
            case SyntaxKind.BaseKeyword:
                NextToken();
                return new BaseExpressionSyntax(_source, token.Span);
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
                NextToken();
                return new LiteralExpressionSyntax(_source, token.Span, token.Kind == SyntaxKind.TrueKeyword, token.Kind);
            case SyntaxKind.NullKeyword:
                NextToken();
                return new LiteralExpressionSyntax(_source, token.Span, null, token.Kind);
            case SyntaxKind.NumberToken:
            case SyntaxKind.StringToken:
            case SyntaxKind.CharacterToken:
                NextToken();
                return new LiteralExpressionSyntax(_source, token.Span, token.Value, token.Kind);
            case SyntaxKind.IdentifierToken:
                NextToken();
                var typeArguments = LooksLikeInvocationTypeArguments() ? ParseInvocationTypeArguments() : [];
                return new NameExpressionSyntax(_source, TextSpan.FromBounds(token.Span.Start, typeArguments.IsDefaultOrEmpty ? token.Span.End : typeArguments[^1].Span.End), token.Text, typeArguments);
            default:
                Report("CT0105", "Expected an expression.", token);
                if (token.Kind != SyntaxKind.EndOfFileToken)
                    SkipToken();
                return new LiteralExpressionSyntax(_source, token.Span, new NumericLiteralValue(0, IntegerLiteralSuffix.None, null), SyntaxKind.NumberToken);
        }
    }

    private bool LooksLikeInvocationTypeArguments()
    {
        if (Current.Kind != SyntaxKind.LessToken)
            return false;
        var index = _position;
        var depth = 0;
        while (index < _tokens.Length)
        {
            depth += _tokens[index].Kind switch
            {
                SyntaxKind.LessToken => 1,
                SyntaxKind.GreaterToken => -1,
                SyntaxKind.GreaterGreaterToken => -2,
                _ => 0,
            };
            index++;
            if (depth == 0)
                return index < _tokens.Length && _tokens[index].Kind is SyntaxKind.OpenParenToken or SyntaxKind.DotToken;
            if (depth < 0 || _tokens[index - 1].Kind is SyntaxKind.SemicolonToken or SyntaxKind.EndOfFileToken)
                return false;
        }
        return false;
    }

    private ImmutableArray<TypeSyntax> ParseInvocationTypeArguments()
    {
        Match(SyntaxKind.LessToken);
        var arguments = ImmutableArray.CreateBuilder<TypeSyntax>();
        while (Current.Kind is not SyntaxKind.GreaterToken and not SyntaxKind.EndOfFileToken)
        {
            arguments.Add(ParseType());
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        Match(SyntaxKind.GreaterToken);
        return arguments.ToImmutable();
    }

    private NewExpressionSyntax ParseNew()
    {
        var start = NextToken().Span.Start;
        var type = ParseType();
        if (Current.Kind == SyntaxKind.OpenBracketToken)
        {
            NextToken();
            var length = ParseExpression();
            var close = Match(SyntaxKind.CloseBracketToken);
            type = type with { IsArray = true, Span = TextSpan.FromBounds(type.Span.Start, close.Span.End) };
            return new NewExpressionSyntax(_source, TextSpan.FromBounds(start, close.Span.End), type, [], length);
        }
        var arguments = ParseCallArguments(out var end);
        return new NewExpressionSyntax(_source, TextSpan.FromBounds(start, end), type, arguments, null);
    }

    private StackAllocExpressionSyntax ParseStackAlloc()
    {
        var start = NextToken().Span.Start;
        var elementType = ParseType();
        Match(SyntaxKind.OpenBracketToken);
        var count = ParseExpression();
        var close = Match(SyntaxKind.CloseBracketToken);
        return new StackAllocExpressionSyntax(_source, TextSpan.FromBounds(start, close.Span.End), elementType, count);
    }

    private ImmutableArray<ArgumentSyntax> ParseCallArguments(out int end)
    {
        Match(SyntaxKind.OpenParenToken);
        var arguments = ImmutableArray.CreateBuilder<ArgumentSyntax>();
        while (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
        {
            var start = Current.Span.Start;
            var passingKind = ParsePassingKind();
            var expression = ParseExpression();
            arguments.Add(new ArgumentSyntax(_source, TextSpan.FromBounds(start, expression.Span.End), passingKind, expression));
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        end = Match(SyntaxKind.CloseParenToken).Span.End;
        return arguments.ToImmutable();
    }

    private ParameterPassingKind ParsePassingKind()
    {
        var kind = Current.Kind switch
        {
            SyntaxKind.RefKeyword => ParameterPassingKind.Ref,
            SyntaxKind.InKeyword => ParameterPassingKind.In,
            SyntaxKind.OutKeyword => ParameterPassingKind.Out,
            _ => ParameterPassingKind.Value,
        };
        if (kind != ParameterPassingKind.Value)
            NextToken();
        return kind;
    }

    private ImmutableArray<ExpressionSyntax> ParseAttributeArguments(out int end)
    {
        Match(SyntaxKind.OpenParenToken);
        var arguments = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        while (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
        {
            arguments.Add(ParseExpression());
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        end = Match(SyntaxKind.CloseParenToken).Span.End;
        return arguments.ToImmutable();
    }

    private ImmutableArray<AttributeSyntax> ParseAttributes()
    {
        var attributes = ImmutableArray.CreateBuilder<AttributeSyntax>();
        while (Current.Kind == SyntaxKind.OpenBracketToken)
        {
            var start = NextToken().Span.Start;
            var name = ParseQualifiedName();
            ImmutableArray<ExpressionSyntax> arguments = [];
            if (Current.Kind == SyntaxKind.OpenParenToken)
                arguments = ParseAttributeArguments(out _);
            var close = Match(SyntaxKind.CloseBracketToken);
            attributes.Add(new AttributeSyntax(_source, TextSpan.FromBounds(start, close.Span.End), name, arguments));
        }
        return attributes.ToImmutable();
    }

    private ImmutableArray<string> ParseModifiers()
    {
        var modifiers = ImmutableArray.CreateBuilder<string>();
        while (ModifierKinds.Contains(Current.Kind))
            modifiers.Add(NextToken().Text);
        return modifiers.ToImmutable();
    }

    private static bool IsBuiltInType(SyntaxKind kind) => kind is SyntaxKind.BoolKeyword or SyntaxKind.ByteKeyword or SyntaxKind.SbyteKeyword or SyntaxKind.ShortKeyword or SyntaxKind.UshortKeyword or SyntaxKind.CharKeyword or SyntaxKind.IntKeyword or SyntaxKind.UintKeyword or SyntaxKind.LongKeyword or SyntaxKind.UlongKeyword or SyntaxKind.NintKeyword or SyntaxKind.NuintKeyword or SyntaxKind.FloatKeyword or SyntaxKind.StringKeyword or SyntaxKind.ObjectKeyword or SyntaxKind.VoidKeyword;
    private static bool IsAssignment(SyntaxKind kind) => kind is SyntaxKind.EqualsToken or SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken or SyntaxKind.StarEqualsToken or SyntaxKind.SlashEqualsToken or SyntaxKind.PercentEqualsToken;
    private static int GetUnaryPrecedence(SyntaxKind kind) => kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.BangToken or SyntaxKind.TildeToken or SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken or SyntaxKind.StarToken or SyntaxKind.AmpersandToken ? 12 : 0;
    private static int GetBinaryPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 11,
        SyntaxKind.PlusToken or SyntaxKind.MinusToken => 10,
        SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken => 9,
        SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken => 8,
        SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 7,
        SyntaxKind.AmpersandToken => 6,
        SyntaxKind.HatToken => 5,
        SyntaxKind.PipeToken => 4,
        SyntaxKind.AmpersandAmpersandToken => 3,
        SyntaxKind.PipePipeToken => 2,
        _ => 0,
    };

    private SyntaxToken Match(SyntaxKind kind)
    {
        if (Current.Kind == kind)
            return NextToken();
        Report("CT0100", $"Expected {Display(kind)}, but found {Display(Current.Kind)}.", Current);
        var missing = new SyntaxToken(kind, _source, new TextSpan(Current.Span.Start, 0), string.Empty) { IsMissing = true };
        _missingTokens.Add(missing);
        return missing;
    }

    private void Synchronize(params SyntaxKind[] kinds)
    {
        while (Current.Kind != SyntaxKind.EndOfFileToken && !kinds.Contains(Current.Kind))
            SkipToken();
    }

    private void Report(string code, string message, SyntaxToken token) => _diagnostics.Add(code, message, _source, token.Span);
    private static string Display(SyntaxKind kind) => kind.ToString().Replace("Keyword", string.Empty, StringComparison.Ordinal).Replace("Token", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
