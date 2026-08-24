using System.Collections.Immutable;

namespace CTilde;

internal static class CompileTimeEvaluator
{
    public static void EvaluateAssertions(CompilationModel model, AnalysisServices services)
    {
        foreach (var tree in model.UserSyntaxTrees.OrderBy(tree => tree.Text.FilePath, StringComparer.Ordinal))
        {
            var fallback = model.UserTypes.FirstOrDefault(type => ReferenceEquals(type.Syntax?.Source, tree.Text)) ??
                model.Types["System.Runtime.Target"];
            foreach (var assertion in tree.Root.Assertions.OrderBy(assertion => assertion.Span.Start))
                Evaluate(model, services, fallback, assertion, ImmutableDictionary<string, CType>.Empty);
        }

        foreach (var type in model.UserTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var syntax = type.Syntax;
            if (syntax is null || syntax.Assertions.IsDefaultOrEmpty || type.IsGenericDefinition)
                continue;
            var substitutions = type.GenericDefinition is null
                ? ImmutableDictionary<string, CType>.Empty
                : type.GenericDefinition.TypeParameters.Select((parameter, index) => (parameter.Name, type.TypeArguments[index]))
                    .ToImmutableDictionary(pair => pair.Name, pair => pair.Item2, StringComparer.Ordinal);
            foreach (var assertion in syntax.Assertions.OrderBy(assertion => assertion.Span.Start))
                Evaluate(model, services, type, assertion, substitutions);
        }
    }

    private static void Evaluate(CompilationModel model, AnalysisServices services, TypeSymbol owner,
        StaticAssertDeclarationSyntax syntax, ImmutableDictionary<string, CType> substitutions)
    {
        var tree = model.SyntaxTrees.First(candidate => ReferenceEquals(candidate.Text, syntax.Source));
        foreach (var layoutType in Descendants(syntax.Condition).Select(node => node switch
                 {
                     SizeOfExpressionSyntax size => size.Type,
                     AlignOfExpressionSyntax align => align.Type,
                     OffsetOfExpressionSyntax offset => offset.Type,
                     _ => null,
                 }).Where(type => type is not null).Cast<TypeSyntax>())
        {
            var resolved = model.ResolveType(layoutType, tree, substitutions, report: false);
            if (resolved.Symbol is not null)
                model.StaticAssertionLayoutTypes.Add(resolved.Symbol);
        }
        var method = new MethodSymbol
        {
            Name = "<static_assert>",
            ContainingType = owner,
            Accessibility = Accessibility.Private,
            IsStatic = true,
            Syntax = null,
            ReturnType = CType.Void,
            Parameters = [],
            Body = null,
            IsNoAlloc = true,
            TypeSubstitutions = substitutions,
        };
        var evaluator = new TypedIrBodyLowerer(services, method, analysisOnly: true);
        var condition = evaluator.LowerStandalone(syntax.Condition);
        var message = syntax.Message switch
        {
            null => "static assertion failed",
            LiteralExpressionSyntax { Value: string text } => text,
            _ => string.Empty,
        };
        if (syntax.Message is not null && message.Length == 0)
        {
            model.Diagnostics.Add("CT2200", "A static assertion message must be a non-empty string literal.", syntax.Message.Source, syntax.Message.Span);
            return;
        }
        if (condition.Type != CType.Bool || !condition.IsConstant)
        {
            model.Diagnostics.Add("CT2200", "A static assertion requires a compile-time Boolean expression.", syntax.Condition.Source, syntax.Condition.Span);
            return;
        }
        if (condition.ConstantValue is bool known)
        {
            if (!known)
                model.Diagnostics.Add("CT2201", $"Static assertion failed: {message}", syntax.Source, syntax.Span);
            return;
        }
        if (condition.ConstantValue is LayoutConstantValue)
            model.StaticAssertions.Add(new BoundStaticAssertion(syntax, condition.Code, message));
        else
            model.Diagnostics.Add("CT2200", "A static assertion condition is not a supported constant expression.", syntax.Condition.Source, syntax.Condition.Span);
    }

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
    {
        yield return root;
        foreach (var child in root.ChildNodesAndTokens().Where(child => child.IsNode).Select(child => child.Node!))
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
