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
    BreakKeyword,
    ByteKeyword,
    CaseKeyword,
    CharKeyword,
    ClassKeyword,
    ConstKeyword,
    ContinueKeyword,
    DefaultKeyword,
    DoKeyword,
    ElseKeyword,
    EnumKeyword,
    FalseKeyword,
    FloatKeyword,
    ForKeyword,
    ForeachKeyword,
    IfKeyword,
    InKeyword,
    IntKeyword,
    InternalKeyword,
    NamespaceKeyword,
    NewKeyword,
    NullKeyword,
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
    TrueKeyword,
    UintKeyword,
    UnsafeKeyword,
    UshortKeyword,
    UsingKeyword,
    VarKeyword,
    VoidKeyword,
    WhileKeyword,
    GetKeyword,
    SetKeyword,
}

public sealed record SyntaxToken(SyntaxKind Kind, SourceText Source, TextSpan Span, string Text, object? Value = null)
{
    public SourceLocation Location => Source.GetLocation(Span);
}

public abstract record SyntaxNode(SourceText Source, TextSpan Span);

public sealed record SyntaxTree
{
    private SyntaxTree(SourceText text, CompilationUnitSyntax root, ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public SourceText Text { get; }
    public CompilationUnitSyntax Root { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public static SyntaxTree Parse(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, diagnostics).Lex();
        var root = new Parser(text, tokens, diagnostics).ParseCompilationUnit();
        return new SyntaxTree(text, root, diagnostics.ToImmutable());
    }

    public static SyntaxTree ParseText(string text, string filePath = "<memory>") => Parse(SourceText.From(text, filePath));
}

public sealed record CompilationUnitSyntax(
    SourceText Source,
    TextSpan Span,
    ImmutableArray<UsingDirectiveSyntax> Usings,
    NamespaceSyntax? Namespace,
    ImmutableArray<TypeDeclarationSyntax> Types) : SyntaxNode(Source, Span);

public sealed record UsingDirectiveSyntax(SourceText Source, TextSpan Span, string Name) : SyntaxNode(Source, Span);

public sealed record NamespaceSyntax(SourceText Source, TextSpan Span, string Name, bool IsFileScoped) : SyntaxNode(Source, Span);

public enum TypeDeclarationKind { Class, Struct, Enum }

public sealed record TypeDeclarationSyntax(
    SourceText Source,
    TextSpan Span,
    TypeDeclarationKind Kind,
    string Name,
    ImmutableArray<string> Modifiers,
    ImmutableArray<AttributeSyntax> Attributes,
    ImmutableArray<MemberDeclarationSyntax> Members,
    TypeSyntax? EnumUnderlyingType,
    ImmutableArray<EnumMemberSyntax> EnumMembers) : SyntaxNode(Source, Span);

public sealed record EnumMemberSyntax(SourceText Source, TextSpan Span, string Name, ExpressionSyntax? Value) : SyntaxNode(Source, Span);

public sealed record AttributeSyntax(SourceText Source, TextSpan Span, string Name, ImmutableArray<ExpressionSyntax> Arguments) : SyntaxNode(Source, Span);

public sealed record TypeSyntax(SourceText Source, TextSpan Span, string Name, int PointerDepth = 0, bool IsArray = false) : SyntaxNode(Source, Span)
{
    public override string ToString() => Name + new string('*', PointerDepth) + (IsArray ? "[]" : string.Empty);
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

public sealed record ParameterSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, string Name) : SyntaxNode(Source, Span);

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
    BlockStatementSyntax Body) : MemberDeclarationSyntax(Source, Span, Modifiers, Attributes);

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
public sealed record ReturnStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax? Expression) : StatementSyntax(Source, Span);
public sealed record UnsafeStatementSyntax(SourceText Source, TextSpan Span, BlockStatementSyntax Body) : StatementSyntax(Source, Span);
public sealed record SwitchStatementSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression, ImmutableArray<SwitchSectionSyntax> Sections) : StatementSyntax(Source, Span);
public sealed record SwitchSectionSyntax(SourceText Source, TextSpan Span, ImmutableArray<SwitchLabelSyntax> Labels, ImmutableArray<StatementSyntax> Statements) : SyntaxNode(Source, Span);
public sealed record SwitchLabelSyntax(SourceText Source, TextSpan Span, ExpressionSyntax? Value) : SyntaxNode(Source, Span);

public abstract record ExpressionSyntax(SourceText Source, TextSpan Span) : SyntaxNode(Source, Span);
public sealed record LiteralExpressionSyntax(SourceText Source, TextSpan Span, object? Value, SyntaxKind LiteralKind) : ExpressionSyntax(Source, Span);
public sealed record NameExpressionSyntax(SourceText Source, TextSpan Span, string Name) : ExpressionSyntax(Source, Span);
public sealed record ThisExpressionSyntax(SourceText Source, TextSpan Span) : ExpressionSyntax(Source, Span);
public sealed record ParenthesizedExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Expression) : ExpressionSyntax(Source, Span);
public sealed record UnaryExpressionSyntax(SourceText Source, TextSpan Span, SyntaxKind OperatorKind, ExpressionSyntax Operand, bool IsPostfix = false) : ExpressionSyntax(Source, Span);
public sealed record BinaryExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Left, SyntaxKind OperatorKind, ExpressionSyntax Right) : ExpressionSyntax(Source, Span);
public sealed record AssignmentExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Left, SyntaxKind OperatorKind, ExpressionSyntax Right) : ExpressionSyntax(Source, Span);
public sealed record MemberAccessExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Receiver, string Name) : ExpressionSyntax(Source, Span);
public sealed record CallExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Target, ImmutableArray<ExpressionSyntax> Arguments) : ExpressionSyntax(Source, Span);
public sealed record IndexExpressionSyntax(SourceText Source, TextSpan Span, ExpressionSyntax Receiver, ExpressionSyntax Index) : ExpressionSyntax(Source, Span);
public sealed record NewExpressionSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, ImmutableArray<ExpressionSyntax> Arguments, ExpressionSyntax? ArrayLength) : ExpressionSyntax(Source, Span);
public sealed record CastExpressionSyntax(SourceText Source, TextSpan Span, TypeSyntax Type, ExpressionSyntax Expression) : ExpressionSyntax(Source, Span);
