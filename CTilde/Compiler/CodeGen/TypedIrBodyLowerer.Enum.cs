namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private IrExpressionValue LowerEnumParseIntrinsic(MethodSymbol method, IReadOnlyList<string> arguments,
        List<string> prelude, CallExpressionSyntax syntax)
    {
        if (method.TypeArguments is not [{ Kind: CTypeKind.Enum, Symbol: { } enumType }])
        {
            Report("CT1322", "Enum.Parse and Enum.TryParse require an enum type argument.", syntax);
            return ErrorExpression(prelude);
        }
        var helper = _emitter.RegisterEnumParser(enumType);
        var ignoreCase = method.ExternName!.EndsWith("ignore_case", StringComparison.Ordinal);
        var ignore = ignoreCase ? arguments[1] : "false";
        if (method.Name == "TryParse")
        {
            var output = arguments[ignoreCase ? 2 : 1];
            return new IrExpressionValue
            {
                Type = CType.Bool,
                Code = $"{helper}({arguments[0]}, {ignore}, false, {output})",
                Prelude = prelude,
                Symbol = method,
            };
        }

        var temporary = NewTemp();
        prelude.Add($"{_emitter.CTypeName(enumType.Type)} {temporary} = ({_emitter.CTypeName(enumType.Type)})0;");
        prelude.Add($"(void){helper}({arguments[0]}, {ignore}, true, &{temporary});");
        return new IrExpressionValue
        {
            Type = enumType.Type,
            Code = temporary,
            Prelude = prelude,
            Symbol = method,
        };
    }
}
