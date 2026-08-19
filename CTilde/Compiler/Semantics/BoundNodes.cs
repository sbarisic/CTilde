using System.Collections.Immutable;

namespace CTilde;

internal enum BoundValueCategory
{
    Value,
    Variable,
    Type,
    MethodGroup,
    Error,
}

internal enum BoundExpressionKind
{
    Error,
    Literal,
    Name,
    This,
    Base,
    MemberAccess,
    Call,
    Index,
    ObjectCreation,
    ArrayCreation,
    Unary,
    Binary,
    Assignment,
    Conversion,
    TypeTest,
    SafeCast,
}

internal enum BoundStatementKind
{
    Block,
    Empty,
    LocalDeclaration,
    Expression,
    If,
    While,
    Do,
    For,
    Foreach,
    Switch,
    Break,
    Continue,
    Return,
    Throw,
    Try,
    Catch,
    Finally,
    Defer,
    Unsafe,
}

internal sealed record BoundSemanticEntry(
    SyntaxNode Syntax,
    CType Type,
    object? Symbol,
    object? ConstantValue,
    OwnershipKind Ownership,
    BoundValueCategory ValueCategory);

internal sealed record BoundExpression(
    ExpressionSyntax Syntax,
    BoundExpressionKind Kind,
    CType Type,
    object? Symbol,
    object? ConstantValue,
    OwnershipKind Ownership,
    BoundValueCategory ValueCategory,
    ImmutableArray<BoundExpression> Children);

internal sealed record BoundStatement(
    SyntaxNode Syntax,
    BoundStatementKind Kind,
    ImmutableArray<BoundExpression> Expressions,
    ImmutableArray<BoundStatement> Children,
    bool CreatesLexicalScope,
    bool IsCleanupBoundary);

internal sealed record BoundFlowSummary(
    bool ContainsReturn,
    bool ContainsThrow,
    bool ContainsBreak,
    bool ContainsContinue,
    bool ContainsExceptionRegion,
    bool ContainsDefer);

internal sealed record BoundBody(
    MethodSymbol Method,
    BoundStatement Root,
    ImmutableDictionary<SyntaxNode, BoundSemanticEntry> Semantics,
    BoundFlowSummary Flow,
    ImmutableArray<AllocationOperation> AllocationEffects,
    ImmutableArray<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses,
    ImmutableArray<MethodSymbol> DeferredCalls);

internal sealed record BoundProgram(
    CompilationModel Model,
    ImmutableArray<BoundBody> Bodies,
    ImmutableDictionary<SyntaxNode, BoundSemanticEntry> SemanticMap,
    ImmutableArray<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses,
    ImmutableHashSet<string> DynamicGeneratedSymbols,
    bool UsesExceptions);

internal static class BoundTreeFactory
{
    public static BoundStatement CreateRoot(MethodSymbol method, ImmutableDictionary<SyntaxNode, BoundSemanticEntry> semantics)
    {
        var root = method.Body ?? new BlockStatementSyntax(
            (method.Syntax ?? method.ContainingType.Syntax!).Source,
            method.Syntax?.Span ?? method.ContainingType.Syntax!.Span,
            []);
        return CreateStatement(root, semantics);
    }

    public static BoundFlowSummary Summarize(BoundStatement root)
    {
        var nodes = DescendantsAndSelf(root).ToArray();
        return new BoundFlowSummary(
            nodes.Any(node => node.Kind == BoundStatementKind.Return),
            nodes.Any(node => node.Kind == BoundStatementKind.Throw),
            nodes.Any(node => node.Kind == BoundStatementKind.Break),
            nodes.Any(node => node.Kind == BoundStatementKind.Continue),
            nodes.Any(node => node.Kind is BoundStatementKind.Try or BoundStatementKind.Catch or BoundStatementKind.Finally),
            nodes.Any(node => node.Kind == BoundStatementKind.Defer));
    }

    private static BoundStatement CreateStatement(SyntaxNode syntax, ImmutableDictionary<SyntaxNode, BoundSemanticEntry> semantics)
    {
        var expressions = syntax.ChildNodesAndTokens()
            .Where(child => child.IsNode)
            .Select(child => child.Node)
            .OfType<ExpressionSyntax>()
            .Select(expression => CreateExpression(expression, semantics))
            .ToImmutableArray();
        var children = syntax switch
        {
            TryStatementSyntax @try => new SyntaxNode[] { @try.Body }
                .Concat(@try.Catches)
                .Concat(@try.Finally is null ? [] : [@try.Finally])
                .Select(child => CreateStatement(child, semantics)).ToImmutableArray(),
            CatchClauseSyntax @catch => [CreateStatement(@catch.Body, semantics)],
            FinallyClauseSyntax @finally => [CreateStatement(@finally.Body, semantics)],
            _ => syntax.ChildNodesAndTokens()
                .Where(child => child.IsNode)
                .Select(child => child.Node)
                .OfType<StatementSyntax>()
                .Select(child => CreateStatement(child, semantics)).ToImmutableArray(),
        };
        var kind = syntax switch
        {
            BlockStatementSyntax => BoundStatementKind.Block,
            EmptyStatementSyntax => BoundStatementKind.Empty,
            LocalDeclarationStatementSyntax => BoundStatementKind.LocalDeclaration,
            ExpressionStatementSyntax => BoundStatementKind.Expression,
            IfStatementSyntax => BoundStatementKind.If,
            WhileStatementSyntax => BoundStatementKind.While,
            DoStatementSyntax => BoundStatementKind.Do,
            ForStatementSyntax => BoundStatementKind.For,
            ForeachStatementSyntax => BoundStatementKind.Foreach,
            SwitchStatementSyntax => BoundStatementKind.Switch,
            BreakStatementSyntax => BoundStatementKind.Break,
            ContinueStatementSyntax => BoundStatementKind.Continue,
            ReturnStatementSyntax => BoundStatementKind.Return,
            ThrowStatementSyntax => BoundStatementKind.Throw,
            TryStatementSyntax => BoundStatementKind.Try,
            CatchClauseSyntax => BoundStatementKind.Catch,
            FinallyClauseSyntax => BoundStatementKind.Finally,
            DeferStatementSyntax => BoundStatementKind.Defer,
            UnsafeStatementSyntax => BoundStatementKind.Unsafe,
            _ => BoundStatementKind.Block,
        };
        return new BoundStatement(
            syntax,
            kind,
            expressions,
            children,
            kind is BoundStatementKind.Block or BoundStatementKind.For or BoundStatementKind.Foreach or BoundStatementKind.Catch,
            kind is BoundStatementKind.Try or BoundStatementKind.Finally or BoundStatementKind.Defer);
    }

    private static BoundExpression CreateExpression(ExpressionSyntax syntax, ImmutableDictionary<SyntaxNode, BoundSemanticEntry> semantics)
    {
        var semantic = semantics.GetValueOrDefault(syntax) ?? new BoundSemanticEntry(
            syntax, CType.Error, null, null, OwnershipKind.None, BoundValueCategory.Error);
        var children = syntax.ChildNodesAndTokens()
            .Where(child => child.IsNode)
            .Select(child => child.Node)
            .OfType<ExpressionSyntax>()
            .Select(expression => CreateExpression(expression, semantics))
            .ToImmutableArray();
        var kind = syntax switch
        {
            LiteralExpressionSyntax => BoundExpressionKind.Literal,
            NameExpressionSyntax => BoundExpressionKind.Name,
            ThisExpressionSyntax => BoundExpressionKind.This,
            BaseExpressionSyntax => BoundExpressionKind.Base,
            MemberAccessExpressionSyntax => BoundExpressionKind.MemberAccess,
            CallExpressionSyntax => BoundExpressionKind.Call,
            IndexExpressionSyntax => BoundExpressionKind.Index,
            NewExpressionSyntax @new when @new.ArrayLength is not null => BoundExpressionKind.ArrayCreation,
            NewExpressionSyntax => BoundExpressionKind.ObjectCreation,
            UnaryExpressionSyntax => BoundExpressionKind.Unary,
            BinaryExpressionSyntax => BoundExpressionKind.Binary,
            AssignmentExpressionSyntax => BoundExpressionKind.Assignment,
            CastExpressionSyntax or ParenthesizedExpressionSyntax => BoundExpressionKind.Conversion,
            TypeTestExpressionSyntax => BoundExpressionKind.TypeTest,
            SafeCastExpressionSyntax => BoundExpressionKind.SafeCast,
            _ => BoundExpressionKind.Error,
        };
        return new BoundExpression(
            syntax,
            kind,
            semantic.Type,
            semantic.Symbol,
            semantic.ConstantValue,
            semantic.Ownership,
            semantic.ValueCategory,
            children);
    }

    private static IEnumerable<BoundStatement> DescendantsAndSelf(BoundStatement root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }
}
