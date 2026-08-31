namespace CTilde;

internal sealed partial class CompilationModel
{
    private void DeclareOperator(TypeSymbol type, OperatorDeclarationSyntax syntax, SyntaxTree tree, Accessibility accessibility, bool isStatic)
    {
        ValidateAllowedModifiers(syntax.Modifiers, ["public", "static", "unsafe"], syntax);
        ValidateAttributes(syntax.Attributes, syntax, ["NoAlloc", "NoThrow", "NoBlock", "NoRuntime", "NoRecursion", "Section"]);
        var effects = ParseEffectContracts(syntax.Attributes);
        var noRecursion = FindAttribute(syntax.Attributes, "NoRecursion");
        var section = FindAttribute(syntax.Attributes, "Section");
        _ = ParseSectionName(section);
        if (section is not null)
            Diagnostics.Add("CT1287", "Section is not valid on an operator.", section.Source, section.Span);
        if (noRecursion is not null && noRecursion.Arguments.Length != 0)
            Diagnostics.Add("CT1294", "NoRecursion does not accept arguments.", noRecursion.Source, noRecursion.Span);

        var returnType = ResolveType(syntax.ReturnType, tree);
        var parameters = DeclareParameters(syntax.Parameters, tree, isExtern: false);
        var operatorKind = syntax.OperatorToken.Kind;
        var arity = parameters.Length;
        var validArity = operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken
            ? arity is 1 or 2
            : arity == 2 && (operatorKind is SyntaxKind.StarToken or SyntaxKind.SlashToken || OperatorFacts.IsComparison(operatorKind));
        var mentionsContainingType = arity == 1
            ? parameters[0].Type == type.Type
            : parameters.Any(parameter => parameter.Type == type.Type);
        var invalid = type.Kind is not DeclaredTypeKind.Class and not DeclaredTypeKind.Struct ||
            type.IsStatic || accessibility != Accessibility.Public || !isStatic ||
            syntax.Modifiers.Any(modifier => modifier is not "public" and not "static" and not "unsafe") ||
            syntax.Attributes.Any(attribute => attribute.Name is not "NoAlloc" and not "NoThrow" and not "NoBlock" and not "NoRuntime" and not "NoRecursion" and not "Section") ||
            syntax.Parameters.Any(parameter => !parameter.Attributes.IsDefaultOrEmpty) ||
            !OperatorFacts.IsSupported(operatorKind) || !validArity ||
            parameters.Any(parameter => parameter.PassingKind != ParameterPassingKind.Value) ||
            !mentionsContainingType || returnType == CType.Void || OperatorFacts.IsComparison(operatorKind) && returnType != CType.Bool || syntax.Body is null;
        if (invalid)
        {
            Diagnostics.Add(
                "CT1269",
                "An operator must be a public static body-bearing class or structure member, use one or two value parameters of the required arity, mention its containing type, and return a value.",
                syntax.Source,
                syntax.Span);
        }
        if (returnType.IsNativeBuffer)
            Diagnostics.Add("CT2186", "Native-buffer views cannot be returned.", syntax.ReturnType.Source, syntax.ReturnType.Span);
        if (returnType.IsNativeUtf8String && UserSyntaxTrees.Contains(tree))
            Diagnostics.Add("CT1266", "NativeUtf8String is scoped and cannot be returned.", syntax.ReturnType.Source, syntax.ReturnType.Span);

        var symbol = new MethodSymbol
        {
            Name = $"<operator:{OperatorFacts.MetadataName(operatorKind, arity)}>",
            ContainingType = type,
            Accessibility = accessibility,
            IsStatic = isStatic,
            Syntax = syntax,
            ReturnType = returnType,
            Parameters = parameters,
            Body = syntax.Body,
            DeclaredEffects = effects,
            IsNoRecursion = noRecursion is not null,
            IsUnsafe = syntax.Modifiers.Contains("unsafe", StringComparer.Ordinal),
            IsOperator = true,
            OperatorKind = operatorKind,
        };
        AddMethod(type.Methods, symbol);
    }
}
