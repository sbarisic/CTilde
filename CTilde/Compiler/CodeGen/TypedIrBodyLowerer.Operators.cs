using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private IrExpressionValue LowerOperatorCall(
        SyntaxKind operatorKind,
        IReadOnlyList<IrExpressionValue> operands,
        IReadOnlyList<ExpressionSyntax> operandSyntax,
        SyntaxNode syntax)
    {
        var arguments = operandSyntax
            .Select(expression => new ArgumentSyntax(expression.Source, expression.Span, ParameterPassingKind.Value, expression))
            .ToImmutableArray();
        var candidates = operands
            .Select(operand => operand.Type.Symbol)
            .Where(type => type?.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Struct)
            .SelectMany(type => Hierarchy(type!))
            .SelectMany(type => type.Methods)
            .Where(method => method.IsOperator && method.OperatorKind == operatorKind && method.Parameters.Length == operands.Count)
            .Distinct()
            .ToArray();
        var selected = SelectOperatorOverload(candidates, operatorKind, operands, arguments, syntax);
        if (selected is null)
            return ErrorExpression(operands.SelectMany(operand => operand.Prelude));

        if (selected.ReturnType.ContainsPointer || selected.Parameters.Any(parameter => parameter.Type.ContainsPointer) || selected.IsUnsafe)
            RequireUnsafe(syntax);
        CheckAccess(selected, syntax);
        _emitter.AllocationEffects.RecordCall(_method, selected, syntax, false);

        var loweredArguments = LowerOperatorArguments(operands, selected.Parameters, arguments);
        var prelude = new List<string>(loweredArguments.Prelude);
        var call = $"{selected.CName}({string.Join(", ", loweredArguments.Codes)})";
        if (selected.ReturnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
            return new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Ownership = OwnershipKind.Borrowed, Symbol = selected };
        return selected.ReturnType.ContainsManagedReferences
            ? OwnResult(selected.ReturnType, call, prelude, symbol: selected)
            : new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Symbol = selected };
    }

    private (List<string> Prelude, List<string> Codes) LowerOperatorArguments(
        IReadOnlyList<IrExpressionValue> operands,
        ImmutableArray<ParameterSymbol> parameters,
        ImmutableArray<ArgumentSyntax> syntax)
    {
        var prelude = new List<string>();
        var codes = new List<string>();
        for (var index = 0; index < operands.Count; index++)
        {
            var converted = Convert(operands[index], parameters[index].Type, syntax[index].Expression, false);
            if (converted.Type.ContainsManagedReferences)
            {
                var captured = OwnResult(converted.Type, converted.Code, converted.Prelude, borrowed: true);
                prelude.AddRange(captured.Prelude);
                codes.Add(captured.Code);
                continue;
            }
            prelude.AddRange(converted.Prelude);
            var temporary = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(converted.Type, temporary)} = {converted.Code};");
            codes.Add(temporary);
        }
        return (prelude, codes);
    }

    private static bool HasUserDefinedOperatorOperand(params IrExpressionValue[] operands) =>
        operands.Any(operand => operand.Type.Symbol?.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Struct);
}
