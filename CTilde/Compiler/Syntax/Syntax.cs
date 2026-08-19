using System.Collections.Immutable;

namespace CTilde;

public enum SyntaxKind
{
    BadToken,
    EndOfFileToken,
    IdentifierToken,
    NumberToken,
    StringToken,
    CharacterToken,

    OpenParenToken,
    CloseParenToken,
    OpenBraceToken,
    CloseBraceToken,
    OpenBracketToken,
    CloseBracketToken,
    SemicolonToken,
    ColonToken,
    CommaToken,
    DotToken,
    PlusToken,
    MinusToken,
    StarToken,
    SlashToken,
    PercentToken,
    AmpersandToken,
    PipeToken,
    HatToken,
    TildeToken,
    BangToken,
    EqualsToken,
    LessToken,
    GreaterToken,
    PlusPlusToken,
    MinusMinusToken,
    PlusEqualsToken,
    MinusEqualsToken,
    StarEqualsToken,
    SlashEqualsToken,
    PercentEqualsToken,
    AmpersandAmpersandToken,
    PipePipeToken,
    EqualsEqualsToken,
    BangEqualsToken,
    LessEqualsToken,
    GreaterEqualsToken,
    LessLessToken,
    GreaterGreaterToken,

    BoolKeyword,
    AsKeyword,
    BaseKeyword,
    BreakKeyword,
    ByteKeyword,
    CaseKeyword,
    CatchKeyword,
    CharKeyword,
    ClassKeyword,
    ConstKeyword,
    ContinueKeyword,
    DefaultKeyword,
    DeferKeyword,
    DelegateKeyword,
    DoKeyword,
    ElseKeyword,
    EnumKeyword,
    FalseKeyword,
    FinallyKeyword,
    FloatKeyword,
    ForKeyword,
    ForeachKeyword,
    IfKeyword,
    InKeyword,
    IntKeyword,
    InternalKeyword,
    IsKeyword,
    LongKeyword,
    NamespaceKeyword,
    NewKeyword,
    NullKeyword,
    ObjectKeyword,
    OverrideKeyword,
    PrivateKeyword,
    ProtectedKeyword,
    PublicKeyword,
    ReadonlyKeyword,
    ReturnKeyword,
    SbyteKeyword,
    SealedKeyword,
    ShortKeyword,
    StaticKeyword,
    StringKeyword,
    StructKeyword,
    SwitchKeyword,
    ThisKeyword,
    ThrowKeyword,
    TrueKeyword,
    TryKeyword,
    UintKeyword,
    UlongKeyword,
    UnmanagedKeyword,
    UnsafeKeyword,
    UshortKeyword,
    UsingKeyword,
    VarKeyword,
    VirtualKeyword,
    VoidKeyword,
    WhileKeyword,
    GetKeyword,
    SetKeyword,
}

public enum SyntaxTriviaKind
{
    Whitespace,
    EndOfLine,
    SingleLineComment,
    BlockComment,
    SkippedTokens,
}

public sealed record SyntaxTrivia(
    SyntaxTriviaKind Kind,
    SourceText Source,
    TextSpan Span,
    string Text,
    ImmutableArray<SyntaxToken> SkippedTokens = default);

public sealed record SyntaxToken(SyntaxKind Kind, SourceText Source, TextSpan Span, string Text, object? Value = null)
{
    public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; init; } = [];
    public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; init; } = [];
    public bool IsMissing { get; init; }
    public TextSpan FullSpan
    {
        get
        {
            var start = LeadingTrivia.IsDefaultOrEmpty ? Span.Start : LeadingTrivia[0].Span.Start;
            var end = TrailingTrivia.IsDefaultOrEmpty ? Span.End : TrailingTrivia[^1].Span.End;
            return TextSpan.FromBounds(start, end);
        }
    }
    public SourceLocation Location => Source.GetLocation(Span);
    public string ToFullString() => string.Concat(LeadingTrivia.Select(trivia => trivia.Text)) + Text + string.Concat(TrailingTrivia.Select(trivia => trivia.Text));
}

public readonly record struct SyntaxNodeOrToken(SyntaxNode? Node, SyntaxToken? Token)
{
    public bool IsNode => Node is not null;
    public bool IsToken => Token is not null;
    public TextSpan FullSpan => Node?.FullSpan ?? Token?.FullSpan ?? default;
}

public abstract record SyntaxNode(SourceText Source, TextSpan Span)
{
    private ImmutableArray<SyntaxToken> _tokens = [];

    public TextSpan FullSpan { get; private set; } = Span;

    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        var children = DirectChildren().OrderBy(child => child.FullSpan.Start).ThenBy(child => child.FullSpan.Length).ToArray();
        var items = new List<SyntaxNodeOrToken>();
        items.AddRange(children.Select(child => new SyntaxNodeOrToken(child, null)));
        items.AddRange(_tokens
            .Where(token => !children.Any(child => Contains(child.FullSpan, token.Span)))
            .Select(token => new SyntaxNodeOrToken(null, token)));
        return items.OrderBy(item => item.FullSpan.Start).ThenBy(item => item.IsToken ? 0 : 1);
    }

    public string ToFullString() => Source.Text.Substring(FullSpan.Start, FullSpan.Length);

    internal void AttachTokens(ImmutableArray<SyntaxToken> tokens)
    {
        _tokens = [.. tokens.Where(token => Contains(Span, token.Span))];
        foreach (var child in DirectChildren())
            child.AttachTokens(tokens);
        if (!_tokens.IsDefaultOrEmpty)
            FullSpan = TextSpan.FromBounds(_tokens[0].FullSpan.Start, _tokens[^1].FullSpan.End);
    }

    private IEnumerable<SyntaxNode> DirectChildren()
    {
        foreach (var property in GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.Name is nameof(Source) or nameof(Span) or nameof(FullSpan))
                continue;
            var value = property.GetValue(this);
            if (value is SyntaxNode node)
                yield return node;
            else if (value is System.Collections.IEnumerable sequence && value is not string)
            {
                foreach (var item in sequence)
                    if (item is SyntaxNode child)
                        yield return child;
            }
        }
    }

    private static bool Contains(TextSpan outer, TextSpan inner) =>
        inner.Length == 0 ? inner.Start >= outer.Start && inner.Start <= outer.End : inner.Start >= outer.Start && inner.End <= outer.End;
}

public sealed record SyntaxTree
{
    private SyntaxTree(SourceText text, CompilationUnitSyntax root, ImmutableArray<SyntaxToken> tokens, ImmutableArray<SyntaxToken> skippedTokens, ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Tokens = tokens;
        SkippedTokens = skippedTokens;
        Diagnostics = diagnostics;
        Root.AttachTokens(tokens);
    }

    public SourceText Text { get; }
    public CompilationUnitSyntax Root { get; }
    public ImmutableArray<SyntaxToken> Tokens { get; }
    public ImmutableArray<SyntaxToken> SkippedTokens { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public string ToFullString() => Text.Text;

    public static SyntaxTree Parse(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var diagnostics = new DiagnosticBag();
        var lexicalTokens = new Lexer(text, diagnostics).Lex();
        var parser = new Parser(text, lexicalTokens, diagnostics);
        var root = parser.ParseCompilationUnit();
        var tokens = MergeTokens(lexicalTokens, parser.MissingTokens, parser.SkippedTokens);
        return new SyntaxTree(text, root, tokens, parser.SkippedTokens, diagnostics.ToImmutable());
    }

    public static SyntaxTree ParseText(string text, string filePath = "<memory>") => Parse(SourceText.From(text, filePath));

    private static ImmutableArray<SyntaxToken> MergeTokens(ImmutableArray<SyntaxToken> lexicalTokens, ImmutableArray<SyntaxToken> missingTokens, ImmutableArray<SyntaxToken> skippedTokens)
    {
        var skipped = skippedTokens.ToHashSet(ReferenceEqualityComparer.Instance);
        var result = ImmutableArray.CreateBuilder<SyntaxToken>();
        var pending = ImmutableArray.CreateBuilder<SyntaxToken>();
        foreach (var token in lexicalTokens)
        {
            if (skipped.Contains(token))
            {
                pending.Add(token);
                continue;
            }
            var current = token;
            if (pending.Count > 0)
            {
                var start = pending[0].FullSpan.Start;
                var end = pending[^1].FullSpan.End;
                var text = string.Concat(pending.Select(item => item.ToFullString()));
                var trivia = new SyntaxTrivia(SyntaxTriviaKind.SkippedTokens, token.Source, TextSpan.FromBounds(start, end), text, pending.ToImmutable());
                current = current with { LeadingTrivia = [trivia, .. current.LeadingTrivia] };
                pending.Clear();
            }
            result.Add(current);
        }
        result.AddRange(missingTokens);
        return [.. result.OrderBy(token => token.FullSpan.Start).ThenBy(token => token.IsMissing ? 0 : 1)];
    }
}

public sealed record CompilationUnitSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<UsingDirectiveSyntax> Usings,
    NamespaceSyntax? Namespace,
    ImmutableArray<TypeDeclarationSyntax> Types) : SyntaxNode(Source, Span);

public sealed record UsingDirectiveSyntax(SourceText Source, TextSpan Span, string Name) : SyntaxNode(Source, Span);

public sealed record NamespaceSyntax(SourceText Source, TextSpan Span, string Name, bool IsFileScoped) : SyntaxNode(Source, Span);

public enum TypeDeclarationKind { Class, Struct, Enum, Delegate }

public sealed record TypeDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    TypeDeclarationKind Kind,
    string Name,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    TypeSyntax? BaseType,
    ImmutableArray<MemberDeclarationSyntax> Members,
    TypeSyntax? EnumUnderlyingType,
    ImmutableArray<EnumMemberSyntax> EnumMembers,
    TypeSyntax? DelegateReturnType,
    ImmutableArray<ParameterSyntax> DelegateParameters) : SyntaxNode(Source, Span);

public sealed record EnumMemberSyntax(SourceText Source, TextSpan Span, string Name, ExpressionSyntax? Value) : SyntaxNode(Source, Span);

public sealed record AttributeSyntax(SourceText Source, TextSpan Span, string Name, ImmutableArray<ExpressionSyntax> Arguments) : SyntaxNode(Source, Span);

public sealed record FunctionPointerSignatureSyntax(SourceText Source, TextSpan Span, ImmutableArray<TypeSyntax> Elements) : SyntaxNode(Source, Span);

public sealed record TypeSyntax(SourceText Source, TextSpan Span, string Name, int PointerDepth = 0, bool IsArray = false, FunctionPointerSignatureSyntax? FunctionPointer = null) : SyntaxNode(Source, Span)
{
    public override string ToString() => FunctionPointer is null
        ? Name + new string('*', PointerDepth) + (IsArray ? "[]" : string.Empty)
        : $"delegate* unmanaged<{string.Join(", ", FunctionPointer.Elements)}>";
}

public abstract record MemberDeclarationSyntax(SourceText Source, TextSpan Span, ImmutableArray<string> Modifiers, ImmutableArray<AttributeSyntax> Attributes) : SyntaxNode(Source, Span);

public sealed record FieldDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    TypeSyntax Type,
    string Name,
    ExpressionSyntax? Initializer) : MemberDeclarationSyntax(Source, Span, Modifiers, Attributes);

public sealed record ParameterSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<AttributeSyntax> Attributes,
    TypeSyntax Type,
    string Name) : SyntaxNode(Source, Span);

public sealed record MethodDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    TypeSyntax ReturnType,
    string Name,
    ImmutableArray<ParameterSyntax> Parameters,
    BlockStatementSyntax? Body) : MemberDeclarationSyntax(Source, Span, Modifiers, Attributes);

public sealed record ConstructorDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    string Name,
    ImmutableArray<ParameterSyntax> Parameters,
    ConstructorInitializerSyntax? Initializer,
    BlockStatementSyntax Body) : MemberDeclarationSyntax(Source, Span, Modifiers, Attributes);

public enum ConstructorInitializerKind { Base, This }

public sealed record ConstructorInitializerSyntax(
    SourceText Source,
    TextSpan Span,
    ConstructorInitializerKind Kind,
    ImmutableArray<ExpressionSyntax> Arguments) : SyntaxNode(Source, Span);

public sealed record AccessorSyntax(
    SourceText Source,
    TextSpan Span,
    string Kind,
    ImmutableArray<string> Modifiers,
    BlockStatementSyntax? Body) : SyntaxNode(Source, Span);

public sealed record PropertyDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    TypeSyntax Type,
    string Name,
    AccessorSyntax? Getter,
    AccessorSyntax? Setter) : MemberDeclarationSyntax(Source, Span, Modifiers, Attributes);

public abstract record StatementSyntax(SourceText Source, TextSpan Span) : SyntaxNode(Source, Span);

public sealed record BlockStatementSyntax(SourceText Source, TextSpan Span, ImmutableArray<StatementSyntax> Statements) : StatementSyntax(Source, Span);
public sealed record EmptyStatementSyntax(SourceText Source, TextSpan Span) : StatementSyntax(Source, Span);
public sealed record ExpressionStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression) : StatementSyntax(Source, Span);
public sealed record LocalDeclarationStatementSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, string Name, ExpressionSyntax? Initializer, bool IsConst, bool IsReadonly) : StatementSyntax(Source, Span);
public sealed record IfStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Condition, StatementSyntax Then, StatementSyntax? Else) : StatementSyntax(Source, Span);
public sealed record WhileStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Condition, StatementSyntax Body) : StatementSyntax(Source, Span);
public sealed record DoStatementSyntax(SourceText Source, TextSpan Span, StatementSyntax Body, ExpressionSyntax Condition) : StatementSyntax(Source, Span);
public sealed record ForStatementSyntax(SourceText Source, TextSpan Span, StatementSyntax? Initializer, ExpressionSyntax? Condition, ExpressionSyntax? Iterator, StatementSyntax Body) : StatementSyntax(Source, Span);
public sealed record ForeachStatementSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, string Name, ExpressionSyntax Collection, StatementSyntax Body) : StatementSyntax(Source, Span);
public sealed record BreakStatementSyntax(SourceText Source, TextSpan Span) : StatementSyntax(Source, Span);
public sealed record ContinueStatementSyntax(SourceText Source, TextSpan Span) : StatementSyntax(Source, Span);
public sealed record DeferStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression) : StatementSyntax(Source, Span);
public sealed record ReturnStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax? Expression) : StatementSyntax(Source, Span);
public sealed record ThrowStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax? Expression) : StatementSyntax(Source, Span);
public sealed record TryStatementSyntax(SourceText Source, TextSpan Span, BlockStatementSyntax Body, ImmutableArray<CatchClauseSyntax> Catches, FinallyClauseSyntax? Finally) : StatementSyntax(Source, Span);
public sealed record CatchClauseSyntax(SourceText Source, TextSpan Span, TypeSyntax? Type, string? Name, BlockStatementSyntax Body) : SyntaxNode(Source, Span);
public sealed record FinallyClauseSyntax(SourceText Source, TextSpan Span, BlockStatementSyntax Body) : SyntaxNode(Source, Span);
public sealed record UnsafeStatementSyntax(SourceText Source, TextSpan Span, BlockStatementSyntax Body) : StatementSyntax(Source, Span);
public sealed record SwitchStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression, ImmutableArray<SwitchSectionSyntax> Sections) : StatementSyntax(Source, Span);
public sealed record SwitchSectionSyntax(SourceText Source, TextSpan Span, ImmutableArray<SwitchLabelSyntax> Labels, ImmutableArray<StatementSyntax> Statements) : SyntaxNode(Source, Span);
public sealed record SwitchLabelSyntax(SourceText Source, TextSpan Span, ExpressionSyntax? Value) : SyntaxNode(Source, Span);

public abstract record ExpressionSyntax(SourceText Source, TextSpan Span) : SyntaxNode(Source, Span);
public sealed record LiteralExpressionSyntax(SourceText Source, TextSpan Span, object? Value, SyntaxKind LiteralKind) : ExpressionSyntax(Source, Span);
public sealed record NameExpressionSyntax(SourceText Source, TextSpan Span, string Name) : ExpressionSyntax(Source, Span);
public sealed record ThisExpressionSyntax(SourceText Source, TextSpan Span) : ExpressionSyntax(Source, Span);
public sealed record BaseExpressionSyntax(SourceText Source, TextSpan Span) : ExpressionSyntax(Source, Span);
public sealed record ParenthesizedExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression) : ExpressionSyntax(Source, Span);
public sealed record UnaryExpressionSyntax(SourceText Source, TextSpan Span, SyntaxKind OperatorKind, ExpressionSyntax Operand, bool IsPostfix = false) : ExpressionSyntax(Source, Span);
public sealed record BinaryExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Left, SyntaxKind OperatorKind, ExpressionSyntax Right) : ExpressionSyntax(Source, Span);
public sealed record AssignmentExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Left, SyntaxKind OperatorKind, ExpressionSyntax Right) : ExpressionSyntax(Source, Span);
public sealed record MemberAccessExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Receiver, string Name) : ExpressionSyntax(Source, Span);
public sealed record CallExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Target, ImmutableArray<ExpressionSyntax> Arguments) : ExpressionSyntax(Source, Span);
public sealed record IndexExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Receiver, ExpressionSyntax Index) : ExpressionSyntax(Source, Span);
public sealed record NewExpressionSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, ImmutableArray<ExpressionSyntax> Arguments, ExpressionSyntax? ArrayLength) : ExpressionSyntax(Source, Span);
public sealed record CastExpressionSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, ExpressionSyntax Expression) : ExpressionSyntax(Source, Span);
public sealed record TypeTestExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression, TypeSyntax Type) : ExpressionSyntax(Source, Span);
public sealed record SafeCastExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression, TypeSyntax Type) : ExpressionSyntax(Source, Span);
