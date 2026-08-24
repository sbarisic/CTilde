using System.Globalization;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private bool IsKnownStringConcat(BinaryExpressionSyntax syntax) =>
        syntax.OperatorKind == SyntaxKind.PlusToken &&
        _semanticHints?.GetValueOrDefault(syntax)?.Type == CType.String;

    private IrExpressionValue LowerStringBuild(BinaryExpressionSyntax syntax)
    {
        var segments = new List<ExpressionSyntax>();
        Flatten(syntax);
        var name = NewTemp();
        var prelude = new List<string>
        {
            $"const uint8_t* {name}_parts[{segments.Count}] = {{0}};",
            $"int32_t {name}_lengths[{segments.Count}] = {{0}};",
        };

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (TryLowerScalarStringSegment(segment, name, index, prelude))
                continue;

            var value = Materialize(Convert(LowerExpression(segment), CType.String, segment, false), segment);
            prelude.AddRange(value.Prelude);
            if (value.IsKnownNonNull)
            {
                prelude.Add($"{name}_parts[{index}] = ({value.Code})->Data;");
                prelude.Add($"{name}_lengths[{index}] = ({value.Code})->Length;");
            }
            else
            {
                prelude.Add($"{name}_parts[{index}] = {value.Code} == NULL ? NULL : ({value.Code})->Data;");
                prelude.Add($"{name}_lengths[{index}] = {value.Code} == NULL ? 0 : ({value.Code})->Length;");
            }
        }

        _emitter.AllocationEffects.RecordDirect(_method, syntax, "fused string construction");
        return OwnResult(CType.String,
            $"ct_string_build({name}_parts, {name}_lengths, {segments.Count.ToString(CultureInfo.InvariantCulture)}, {_emitter.SourceArgument(syntax)})",
            prelude);

        void Flatten(ExpressionSyntax expression)
        {
            if (expression is BinaryExpressionSyntax binary && IsKnownStringConcat(binary))
            {
                Flatten(binary.Left);
                Flatten(binary.Right);
            }
            else
                segments.Add(expression);
        }
    }

    private bool TryLowerScalarStringSegment(ExpressionSyntax syntax, string buildName, int index, List<string> prelude)
    {
        if (syntax is not CallExpressionSyntax { Arguments.Length: 0, Target: MemberAccessExpressionSyntax member } ||
            member.Name != "ToString")
            return false;
        var receiverType = _semanticHints?.GetValueOrDefault(member.Receiver)?.Type;
        if (receiverType is null || receiverType.Kind is CTypeKind.String || !SupportsBuiltInToString(receiverType))
            return false;

        var receiver = Materialize(LowerExpression(member.Receiver), member.Receiver);
        prelude.AddRange(receiver.Prelude);
        var value = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort => $"(uint32_t){receiver.Code}",
            CTypeKind.Sbyte or CTypeKind.Short => $"(int32_t){receiver.Code}",
            _ => receiver.Code,
        };
        var buffer = $"{buildName}_buffer_{index}";
        var length = $"{buildName}_segment_length_{index}";
        switch (receiver.Type.Kind)
        {
            case CTypeKind.Bool:
                prelude.Add($"{buildName}_parts[{index}] = (const uint8_t*)({value} ? \"True\" : \"False\");");
                prelude.Add($"{buildName}_lengths[{index}] = {value} ? 4 : 5;");
                return true;
            case CTypeKind.Char:
                prelude.Add($"uint8_t {buffer}[1] = {{ {value} }};");
                prelude.Add($"{buildName}_parts[{index}] = {buffer};");
                prelude.Add($"{buildName}_lengths[{index}] = 1;");
                return true;
        }

        var (capacity, format, argument) = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => ("11", "\"%\" PRIu32", value),
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => ("12", "\"%\" PRId32", value),
            CTypeKind.Long => ("21", "\"%\" PRId64", value),
            CTypeKind.Ulong => ("21", "\"%\" PRIu64", value),
            CTypeKind.Nint => ("3 * sizeof(intptr_t) + 2", "\"%\" PRIdPTR", value),
            CTypeKind.Nuint => ("3 * sizeof(uintptr_t) + 1", "\"%\" PRIuPTR", value),
            CTypeKind.Float => ("32", "\"%.9g\"", $"(double){value}"),
            _ => throw new InvalidOperationException($"Unsupported scalar string segment '{receiver.Type.DisplayName}'."),
        };
        prelude.Add($"char {buffer}[{capacity}];");
        prelude.Add($"int {length} = snprintf({buffer}, sizeof({buffer}), {format}, {argument});");
        prelude.Add($"if ({length} < 0 || (size_t){length} >= sizeof({buffer})) ct_raise_runtime_fault(CT_FAULT_OVERFLOW, \"CTS0002\", {_emitter.SourceArgument(syntax)});");
        prelude.Add($"{buildName}_parts[{index}] = (const uint8_t*){buffer};");
        prelude.Add($"{buildName}_lengths[{index}] = (int32_t){length};");
        return true;
    }
}
