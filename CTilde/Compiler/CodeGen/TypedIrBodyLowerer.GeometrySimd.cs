namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private bool TryEmitExtendedX64GeometryKernel(out string definition)
    {
        definition = string.Empty;
        var type = _method.ContainingType.FullName;
        if (type is "System.Vec2" or "System.Vec3" or "System.Vec4")
            return TryEmitX64VectorKernel(type[^1] - '0', out definition);
        if (type == "System.Matrix3x2")
            return TryEmitX64Matrix3x2Kernel(out definition);
        if (type == "System.Matrix4x4")
            return TryEmitX64Matrix4x4ElementwiseKernel(out definition);
        if (type == "System.Quaternion")
            return TryEmitX64QuaternionElementwiseKernel(out definition);
        return false;
    }

    private bool TryEmitX64VectorKernel(int lanes, out string definition)
    {
        definition = string.Empty;
        var resultType = _emitter.CTypeName(_method.ReturnType);
        if (_method.IsOperator && _method.IsStatic)
        {
            var parameters = _method.Parameters;
            if (parameters.Length == 1 && _method.OperatorKind == SyntaxKind.MinusToken)
            {
                var value = NameMangler.Identifier(parameters[0].Name);
                definition = VectorResultDefinition(resultType, lanes,
                    $"_mm_xor_ps({VectorLoad(value, lanes)}, _mm_set1_ps(-0.0f))");
                return true;
            }
            if (parameters.Length != 2)
                return false;
            var left = NameMangler.Identifier(parameters[0].Name);
            var right = NameMangler.Identifier(parameters[1].Name);
            var containing = _method.ContainingType.FullName;
            var leftVector = parameters[0].Type.Symbol?.FullName == containing;
            var rightVector = parameters[1].Type.Symbol?.FullName == containing;
            string? intrinsic = _method.OperatorKind switch
            {
                SyntaxKind.PlusToken => "_mm_add_ps",
                SyntaxKind.MinusToken => "_mm_sub_ps",
                SyntaxKind.StarToken => "_mm_mul_ps",
                SyntaxKind.SlashToken => "_mm_div_ps",
                _ => null,
            };
            if (intrinsic is null)
                return false;
            string expression;
            if (leftVector && rightVector)
                expression = $"{intrinsic}({VectorLoad(left, lanes)}, {VectorLoad(right, lanes)})";
            else if (leftVector && parameters[1].Type.Kind == CTypeKind.Float)
                expression = $"{intrinsic}({VectorLoad(left, lanes)}, _mm_set1_ps({right}))";
            else if (rightVector && parameters[0].Type.Kind == CTypeKind.Float && _method.OperatorKind == SyntaxKind.StarToken)
                expression = $"_mm_mul_ps(_mm_set1_ps({left}), {VectorLoad(right, lanes)})";
            else
                return false;
            definition = VectorResultDefinition(resultType, lanes, expression);
            return true;
        }

        if (!_method.IsStatic && _method.Name is "Dot" or "LengthSquared")
        {
            var right = _method.Name == "Dot" ? NameMangler.Identifier(_method.Parameters[0].Name) : null;
            var product = right is null
                ? $"_mm_mul_ps({VectorLoad("ct_self", lanes, pointer: true)}, {VectorLoad("ct_self", lanes, pointer: true)})"
                : $"_mm_mul_ps({VectorLoad("ct_self", lanes, pointer: true)}, {VectorLoad(right, lanes)})";
            definition = $"{_emitter.MethodSignature(_method)}\n{{\n    float ct_lanes[4];\n    _mm_storeu_ps(ct_lanes, {product});\n    return {OrderedSum(lanes)};\n}}";
            return true;
        }

        if (_method.IsStatic && _method.Name == "Abs" && _method.Parameters.Length == 1)
        {
            var value = NameMangler.Identifier(_method.Parameters[0].Name);
            definition = VectorResultDefinition(resultType, lanes,
                $"_mm_andnot_ps(_mm_set1_ps(-0.0f), {VectorLoad(value, lanes)})");
            return true;
        }

        if (_method.IsStatic && _method.Name is "Min" or "Max" && _method.Parameters.Length == 2)
        {
            var left = NameMangler.Identifier(_method.Parameters[0].Name);
            var right = NameMangler.Identifier(_method.Parameters[1].Name);
            var helper = _method.Name == "Min" ? "ct_math_min" : "ct_math_max";
            _emitter.RequireMathSymbol(helper);
            var fields = new[] { "X", "Y", "Z", "W" };
            var laneExpressions = fields.Select((field, index) => index < lanes
                ? $"{helper}({left}.u_1_{field}, {right}.u_1_{field})"
                : "0.0f");
            definition = VectorResultDefinition(resultType, lanes,
                $"_mm_setr_ps({string.Join(", ", laneExpressions)})");
            return true;
        }

        if (!_method.IsStatic && _method.Name == "Cross" && lanes == 3 && _method.Parameters.Length == 1)
        {
            var right = NameMangler.Identifier(_method.Parameters[0].Name);
            var body = $"""
                __m128 ct_left = {VectorLoad("ct_self", 3, pointer: true)};
                __m128 ct_right = {VectorLoad(right, 3)};
                __m128 ct_left_yzx = _mm_shuffle_ps(ct_left, ct_left, _MM_SHUFFLE(3, 0, 2, 1));
                __m128 ct_right_zxy = _mm_shuffle_ps(ct_right, ct_right, _MM_SHUFFLE(3, 1, 0, 2));
                __m128 ct_left_zxy = _mm_shuffle_ps(ct_left, ct_left, _MM_SHUFFLE(3, 1, 0, 2));
                __m128 ct_right_yzx = _mm_shuffle_ps(ct_right, ct_right, _MM_SHUFFLE(3, 0, 2, 1));
                __m128 ct_value = _mm_sub_ps(_mm_mul_ps(ct_left_yzx, ct_right_zxy), _mm_mul_ps(ct_left_zxy, ct_right_yzx));
                """;
            definition = VectorResultDefinition(resultType, 3, "ct_value", body);
            return true;
        }
        return false;
    }

    private bool TryEmitX64Matrix3x2Kernel(out string definition)
    {
        definition = string.Empty;
        var resultType = _emitter.CTypeName(_method.ReturnType);
        if (_method.IsOperator && _method.IsStatic && _method.Parameters.Length == 2)
        {
            var left = NameMangler.Identifier(_method.Parameters[0].Name);
            var right = NameMangler.Identifier(_method.Parameters[1].Name);
            var bothMatrices = _method.Parameters.All(parameter => parameter.Type.Symbol?.FullName == "System.Matrix3x2");
            if (bothMatrices && _method.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
            {
                var intrinsic = _method.OperatorKind == SyntaxKind.PlusToken ? "_mm_add_ps" : "_mm_sub_ps";
                definition = Matrix3x2ResultDefinition(resultType,
                    $"{intrinsic}(_mm_loadu_ps(&{left}.u_3_M11), _mm_loadu_ps(&{right}.u_3_M11))",
                    $"{intrinsic}(_mm_setr_ps({left}.u_3_M31, {left}.u_3_M32, 0.0f, 0.0f), _mm_setr_ps({right}.u_3_M31, {right}.u_3_M32, 0.0f, 0.0f))");
                return true;
            }
            var matrixParameter = _method.Parameters[0].Type.Symbol?.FullName == "System.Matrix3x2" ? left : right;
            var scalarParameter = matrixParameter == left ? right : left;
            if (_method.OperatorKind == SyntaxKind.StarToken && _method.Parameters.Any(parameter => parameter.Type.Kind == CTypeKind.Float))
            {
                definition = Matrix3x2ResultDefinition(resultType,
                    $"_mm_mul_ps(_mm_loadu_ps(&{matrixParameter}.u_3_M11), _mm_set1_ps({scalarParameter}))",
                    $"_mm_mul_ps(_mm_setr_ps({matrixParameter}.u_3_M31, {matrixParameter}.u_3_M32, 0.0f, 0.0f), _mm_set1_ps({scalarParameter}))");
                return true;
            }
            if (bothMatrices && _method.OperatorKind == SyntaxKind.StarToken)
            {
                var body = $"""
                    __m128 ct_b0 = _mm_setr_ps({right}.u_3_M11, {right}.u_3_M12, {right}.u_3_M11, {right}.u_3_M12);
                    __m128 ct_b1 = _mm_setr_ps({right}.u_3_M21, {right}.u_3_M22, {right}.u_3_M21, {right}.u_3_M22);
                    __m128 ct_rows = _mm_add_ps(_mm_mul_ps(_mm_setr_ps({left}.u_3_M11, {left}.u_3_M11, {left}.u_3_M21, {left}.u_3_M21), ct_b0), _mm_mul_ps(_mm_setr_ps({left}.u_3_M12, {left}.u_3_M12, {left}.u_3_M22, {left}.u_3_M22), ct_b1));
                    __m128 ct_translation = _mm_add_ps(_mm_add_ps(_mm_mul_ps(_mm_set1_ps({left}.u_3_M31), _mm_setr_ps({right}.u_3_M11, {right}.u_3_M12, 0.0f, 0.0f)), _mm_mul_ps(_mm_set1_ps({left}.u_3_M32), _mm_setr_ps({right}.u_3_M21, {right}.u_3_M22, 0.0f, 0.0f))), _mm_setr_ps({right}.u_3_M31, {right}.u_3_M32, 0.0f, 0.0f));
                    """;
                definition = Matrix3x2ResultDefinition(resultType, "ct_rows", "ct_translation", body);
                return true;
            }
        }
        if (!_method.IsStatic && _method.Name is "TransformPoint" or "TransformVector" && _method.Parameters.Length == 1)
        {
            var value = NameMangler.Identifier(_method.Parameters[0].Name);
            var translation = _method.Name == "TransformPoint" ? "_mm_setr_ps(ct_self->u_3_M31, ct_self->u_3_M32, 0.0f, 0.0f)" : "_mm_setzero_ps()";
            var expression = $"_mm_add_ps(_mm_add_ps(_mm_mul_ps(_mm_set1_ps({value}.u_1_X), _mm_setr_ps(ct_self->u_3_M11, ct_self->u_3_M12, 0.0f, 0.0f)), _mm_mul_ps(_mm_set1_ps({value}.u_1_Y), _mm_setr_ps(ct_self->u_3_M21, ct_self->u_3_M22, 0.0f, 0.0f))), {translation})";
            definition = VectorResultDefinition(resultType, 2, expression);
            return true;
        }
        return false;
    }

    private bool TryEmitX64Matrix4x4ElementwiseKernel(out string definition)
    {
        definition = string.Empty;
        if (_method.IsStatic && _method.Name == "Transpose" && _method.Parameters.Length == 1)
        {
            var value = NameMangler.Identifier(_method.Parameters[0].Name);
            var resultType = _emitter.CTypeName(_method.ReturnType);
            definition = $"{_emitter.MethodSignature(_method)}\n{{\n"
                + $"    {resultType} ct_result;\n"
                + $"    __m128 ct_row0 = _mm_loadu_ps(&{value}.u_3_M11);\n"
                + $"    __m128 ct_row1 = _mm_loadu_ps(&{value}.u_3_M21);\n"
                + $"    __m128 ct_row2 = _mm_loadu_ps(&{value}.u_3_M31);\n"
                + $"    __m128 ct_row3 = _mm_loadu_ps(&{value}.u_3_M41);\n"
                + "    _MM_TRANSPOSE4_PS(ct_row0, ct_row1, ct_row2, ct_row3);\n"
                + "    _mm_storeu_ps(&ct_result.u_3_M11, ct_row0);\n"
                + "    _mm_storeu_ps(&ct_result.u_3_M21, ct_row1);\n"
                + "    _mm_storeu_ps(&ct_result.u_3_M31, ct_row2);\n"
                + "    _mm_storeu_ps(&ct_result.u_3_M41, ct_row3);\n"
                + "    return ct_result;\n}";
            return true;
        }
        if (!_method.IsOperator || !_method.IsStatic || _method.Parameters.Length != 2)
            return false;
        var left = NameMangler.Identifier(_method.Parameters[0].Name);
        var right = NameMangler.Identifier(_method.Parameters[1].Name);
        var bothMatrices = _method.Parameters.All(parameter => parameter.Type.Symbol?.FullName == "System.Matrix4x4");
        string? intrinsic = bothMatrices ? _method.OperatorKind switch
        {
            SyntaxKind.PlusToken => "_mm_add_ps",
            SyntaxKind.MinusToken => "_mm_sub_ps",
            _ => null,
        } : null;
        string? scalar = null;
        string? matrix = null;
        if (_method.OperatorKind == SyntaxKind.StarToken && _method.Parameters.Any(parameter => parameter.Type.Kind == CTypeKind.Float))
        {
            intrinsic = "_mm_mul_ps";
            matrix = _method.Parameters[0].Type.Symbol?.FullName == "System.Matrix4x4" ? left : right;
            scalar = matrix == left ? right : left;
        }
        if (intrinsic is null)
            return false;
        matrix ??= left;
        var body = new System.Text.StringBuilder();
        for (var row = 1; row <= 4; row++)
        {
            var second = scalar is null ? $"_mm_loadu_ps(&{right}.u_3_M{row}1)" : $"_mm_set1_ps({scalar})";
            body.Append("    _mm_storeu_ps(&ct_result.u_3_M").Append(row).Append("1, ").Append(intrinsic)
                .Append("(_mm_loadu_ps(&").Append(matrix).Append(".u_3_M").Append(row).Append("1), ").Append(second).AppendLine("));");
        }
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {_emitter.CTypeName(_method.ReturnType)} ct_result;\n{body}    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitX64QuaternionElementwiseKernel(out string definition)
    {
        definition = string.Empty;
        var resultType = _emitter.CTypeName(_method.ReturnType);
        if (_method.IsStatic && _method.Name == "Dot" && _method.Parameters.Length == 2)
        {
            var dotLeft = NameMangler.Identifier(_method.Parameters[0].Name);
            var dotRight = NameMangler.Identifier(_method.Parameters[1].Name);
            definition = QuaternionReductionDefinition(
                $"_mm_mul_ps(_mm_loadu_ps(&{dotLeft}.u_1_X), _mm_loadu_ps(&{dotRight}.u_1_X))");
            return true;
        }
        if (!_method.IsStatic && _method.Name == "LengthSquared")
        {
            definition = QuaternionReductionDefinition(
                "_mm_mul_ps(_mm_loadu_ps(&ct_self->u_1_X), _mm_loadu_ps(&ct_self->u_1_X))");
            return true;
        }
        if (!_method.IsStatic && _method.Name == "Conjugate")
        {
            definition = VectorResultDefinition(resultType, 4,
                "_mm_xor_ps(_mm_loadu_ps(&ct_self->u_1_X), _mm_setr_ps(-0.0f, -0.0f, -0.0f, 0.0f))",
                fieldPrefix: "u_1_");
            return true;
        }
        if (!_method.IsOperator || !_method.IsStatic)
            return false;
        if (_method.Parameters.Length == 1 && _method.OperatorKind == SyntaxKind.MinusToken)
        {
            var value = NameMangler.Identifier(_method.Parameters[0].Name);
            definition = VectorResultDefinition(resultType, 4,
                $"_mm_xor_ps(_mm_loadu_ps(&{value}.u_1_X), _mm_set1_ps(-0.0f))", fieldPrefix: "u_1_");
            return true;
        }
        if (_method.Parameters.Length != 2)
            return false;
        var left = NameMangler.Identifier(_method.Parameters[0].Name);
        var right = NameMangler.Identifier(_method.Parameters[1].Name);
        var both = _method.Parameters.All(parameter => parameter.Type.Symbol?.FullName == "System.Quaternion");
        var intrinsic = both ? _method.OperatorKind switch
        {
            SyntaxKind.PlusToken => "_mm_add_ps",
            SyntaxKind.MinusToken => "_mm_sub_ps",
            _ => null,
        } : null;
        if (intrinsic is not null)
        {
            definition = VectorResultDefinition(resultType, 4,
                $"{intrinsic}(_mm_loadu_ps(&{left}.u_1_X), _mm_loadu_ps(&{right}.u_1_X))", fieldPrefix: "u_1_");
            return true;
        }
        if (_method.OperatorKind == SyntaxKind.StarToken && _method.Parameters.Any(parameter => parameter.Type.Kind == CTypeKind.Float))
        {
            var value = _method.Parameters[0].Type.Symbol?.FullName == "System.Quaternion" ? left : right;
            var scale = value == left ? right : left;
            definition = VectorResultDefinition(resultType, 4,
                $"_mm_mul_ps(_mm_loadu_ps(&{value}.u_1_X), _mm_set1_ps({scale}))", fieldPrefix: "u_1_");
            return true;
        }
        return false;
    }

    private string QuaternionReductionDefinition(string product)
        => $"{_emitter.MethodSignature(_method)}\n{{\n    float ct_lanes[4];\n    _mm_storeu_ps(ct_lanes, {product});\n    return ((ct_lanes[0] + ct_lanes[1]) + ct_lanes[2]) + ct_lanes[3];\n}}";

    private string VectorResultDefinition(string resultType, int lanes, string expression, string prelude = "", string fieldPrefix = "u_1_")
    {
        var body = new System.Text.StringBuilder();
        if (prelude.Length != 0)
            foreach (var line in prelude.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(line => line.Length != 0))
                body.Append("    ").AppendLine(line.TrimStart());
        body.Append("    __m128 ct_vector = ").Append(expression).AppendLine(";");
        if (lanes == 4)
            body.Append("    _mm_storeu_ps(&ct_result.").Append(fieldPrefix).AppendLine("X, ct_vector);");
        else
        {
            body.AppendLine("    float ct_lanes[4];");
            body.AppendLine("    _mm_storeu_ps(ct_lanes, ct_vector);");
            body.Append("    ct_result.").Append(fieldPrefix).AppendLine("X = ct_lanes[0];");
            body.Append("    ct_result.").Append(fieldPrefix).AppendLine("Y = ct_lanes[1];");
            if (lanes == 3)
                body.Append("    ct_result.").Append(fieldPrefix).AppendLine("Z = ct_lanes[2];");
        }
        return $"{_emitter.MethodSignature(_method)}\n{{\n    {resultType} ct_result;\n{body}    return ct_result;\n}}";
    }

    private string Matrix3x2ResultDefinition(string resultType, string first, string second, string prelude = "")
    {
        var body = new System.Text.StringBuilder();
        if (prelude.Length != 0)
            foreach (var line in prelude.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(line => line.Length != 0))
                body.Append("    ").AppendLine(line.TrimStart());
        body.AppendLine($"    __m128 ct_first = {first};");
        body.AppendLine($"    __m128 ct_second = {second};");
        body.AppendLine("    float ct_tail[4];");
        body.AppendLine("    _mm_storeu_ps(&ct_result.u_3_M11, ct_first);");
        body.AppendLine("    _mm_storeu_ps(ct_tail, ct_second);");
        body.AppendLine("    ct_result.u_3_M31 = ct_tail[0];");
        body.AppendLine("    ct_result.u_3_M32 = ct_tail[1];");
        return $"{_emitter.MethodSignature(_method)}\n{{\n    {resultType} ct_result;\n{body}    return ct_result;\n}}";
    }

    private static string VectorLoad(string value, int lanes, bool pointer = false)
    {
        var access = pointer ? value + "->" : value + ".";
        return lanes switch
        {
            4 => $"_mm_loadu_ps(&{access}u_1_X)",
            3 => $"_mm_setr_ps({access}u_1_X, {access}u_1_Y, {access}u_1_Z, 0.0f)",
            _ => $"_mm_setr_ps({access}u_1_X, {access}u_1_Y, 0.0f, 0.0f)",
        };
    }

    private static string OrderedSum(int lanes) => lanes switch
    {
        4 => "((ct_lanes[0] + ct_lanes[1]) + ct_lanes[2]) + ct_lanes[3]",
        3 => "(ct_lanes[0] + ct_lanes[1]) + ct_lanes[2]",
        _ => "ct_lanes[0] + ct_lanes[1]",
    };
}
