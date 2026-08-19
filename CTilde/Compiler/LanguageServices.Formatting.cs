namespace CTilde;

public sealed partial class LanguageServiceSnapshot
{
    private static string FormatSymbol(object symbol) => symbol switch
    {
        TypeSymbol { Kind: DeclaredTypeKind.Delegate } type => $"delegate {type.DelegateReturnType!.DisplayName} {type.FullName}({string.Join(", ", type.DelegateParameters.Select(FormatParameter))})",
        TypeSymbol type => $"{type.Kind.ToString().ToLowerInvariant()} {type.FullName}",
        FieldSymbol field => $"{AccessibilityText(field.Accessibility)}{(field.IsStatic ? "static " : string.Empty)}{field.Type.DisplayName} {field.ContainingType.FullName}.{field.Name}",
        PropertySymbol property => $"{AccessibilityText(property.Accessibility)}{(property.IsStatic ? "static " : string.Empty)}{property.Type.DisplayName} {property.ContainingType.FullName}.{property.Name}",
        MethodSymbol method => FormatMethod(method),
        ParameterSymbol parameter => FormatParameter(parameter),
        LocalSymbol local => $"{local.Type.DisplayName} {local.Name}",
        ParameterSyntax parameter => FormatParameter(parameter),
        LocalDeclarationStatementSyntax local => $"{local.Type} {local.Name}",
        LocalSemanticSymbol local => $"{local.Type} {local.Name}",
        EnumValueSymbol value => $"{value.Name} = {value.Value}",
        _ => string.Empty,
    };

    private static string FormatMethod(MethodSymbol method) => method.IsOperator
        ? $"{AccessibilityText(method.Accessibility)}static {method.ReturnType.DisplayName} {method.ContainingType.FullName}.{OperatorFacts.DisplayName(method.OperatorKind)}({string.Join(", ", method.Parameters.Select(FormatParameter))})"
        : $"{AccessibilityText(method.Accessibility)}{(method.IsStatic ? "static " : string.Empty)}{(method.IsConstructor ? string.Empty : method.ReturnType.DisplayName + " ")}{method.ContainingType.FullName}.{method.Name}({string.Join(", ", method.Parameters.Select(FormatParameter))})";

    private static string FormatParameter(ParameterSymbol parameter) => $"{PassingPrefix(parameter.PassingKind)}{parameter.Type.DisplayName} {parameter.Name}";
    private static string FormatParameter(ParameterSyntax parameter) => $"{PassingPrefix(parameter.PassingKind)}{parameter.Type} {parameter.Name}";
    private static string PassingPrefix(ParameterPassingKind kind) => kind == ParameterPassingKind.Value ? string.Empty : kind.ToString().ToLowerInvariant() + " ";

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
        MethodSymbol { IsOperator: true } method => OperatorFacts.DisplayName(method.OperatorKind),
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
        MethodDeclarationSyntax method => $"{method.ReturnType}({string.Join(", ", method.Parameters.Select(FormatParameter))})",
        OperatorDeclarationSyntax @operator => $"{@operator.ReturnType}({string.Join(", ", @operator.Parameters.Select(FormatParameter))})",
        ConstructorDeclarationSyntax constructor => $"({string.Join(", ", constructor.Parameters.Select(FormatParameter))})",
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

    private static TextSpan SelectionSpan(SyntaxNode syntax, string name) =>
        syntax is OperatorDeclarationSyntax @operator ? @operator.OperatorToken.Span : NameSpan(syntax, name);
}
