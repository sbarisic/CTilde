namespace CTilde;

internal sealed partial class CEmitter
{
    private static readonly (string RuntimeName, string NativeName, string CType, int ParameterCount)[] MathFunctions =
    [
        ("ct_math_sqrt", "sqrtf", "float", 1),
        ("ct_math_abs", "fabsf", "float", 1),
        ("ct_math_tan", "tanf", "float", 1),
        ("ct_math_min", "fminf", "float", 2),
        ("ct_math_max", "fmaxf", "float", 2),
        ("ct_math_sin", "sinf", "float", 1),
        ("ct_math_cos", "cosf", "float", 1),
        ("ct_math_acos", "acosf", "float", 1),
        ("ct_math_floor", "floorf", "float", 1),
        ("ct_math_ceiling", "ceilf", "float", 1),
        ("ct_math_asin", "asinf", "float", 1),
        ("ct_math_atan", "atanf", "float", 1),
        ("ct_math_atan2", "atan2f", "float", 2),
        ("ct_math_exp", "expf", "float", 1),
        ("ct_math_log", "logf", "float", 1),
        ("ct_math_log2", "log2f", "float", 1),
        ("ct_math_log10", "log10f", "float", 1),
        ("ct_math_pow", "powf", "float", 2),
        ("ct_math_round", "roundf", "float", 1),
        ("ct_math_truncate", "truncf", "float", 1),
        ("ct_math_sqrt_double", "sqrt", "double", 1),
        ("ct_math_abs_double", "fabs", "double", 1),
        ("ct_math_tan_double", "tan", "double", 1),
        ("ct_math_min_double", "fmin", "double", 2),
        ("ct_math_max_double", "fmax", "double", 2),
        ("ct_math_sin_double", "sin", "double", 1),
        ("ct_math_cos_double", "cos", "double", 1),
        ("ct_math_acos_double", "acos", "double", 1),
        ("ct_math_floor_double", "floor", "double", 1),
        ("ct_math_ceiling_double", "ceil", "double", 1),
        ("ct_math_asin_double", "asin", "double", 1),
        ("ct_math_atan_double", "atan", "double", 1),
        ("ct_math_atan2_double", "atan2", "double", 2),
        ("ct_math_exp_double", "exp", "double", 1),
        ("ct_math_log_double", "log", "double", 1),
        ("ct_math_log2_double", "log2", "double", 1),
        ("ct_math_log10_double", "log10", "double", 1),
        ("ct_math_pow_double", "pow", "double", 2),
        ("ct_math_round_double", "round", "double", 1),
        ("ct_math_truncate_double", "trunc", "double", 1),
    ];

    private static bool IsMathSymbol(string name) =>
        MathFunctions.Any(function => function.RuntimeName == name);

    private void EmitMathSupport(CWriter writer)
    {
        foreach (var function in MathFunctions.Where(function => _usedMathSymbols.Contains(function.RuntimeName)))
        {
            var parameters = function.ParameterCount == 1 ? $"{function.CType} value" : $"{function.CType} left, {function.CType} right";
            var arguments = function.ParameterCount == 1 ? "value" : "left, right";
            var role = (function.CType, function.ParameterCount) switch
            {
                ("float", 1) => RuntimeImplementationRole.MathFloatUnary,
                ("float", 2) => RuntimeImplementationRole.MathFloatBinary,
                ("double", 1) => RuntimeImplementationRole.MathDoubleUnary,
                _ => RuntimeImplementationRole.MathDoubleBinary,
            };
            if (Model.RuntimeImplementations.TryGetValue(role, out var implementation))
            {
                var operation = function.ParameterCount == 1 ? UnaryMathOperation(function.RuntimeName) : BinaryMathOperation(function.RuntimeName);
                var operationType = CTypeName(implementation.Parameters[0].Type);
                writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ return {implementation.CName}(({operationType})UINT8_C({operation}), {arguments}); }}");
                continue;
            }
            if (IsFreestanding)
            {
                writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ (void){(function.ParameterCount == 1 ? "value" : "left; (void)right")}; ct_runtime_service_fail(\"CTK0002\", UINT8_C(7), 0); return ({function.CType})0; }}");
                continue;
            }
            if (function.RuntimeName is "ct_math_min" or "ct_math_min_double")
            {
                var zero = function.CType == "float" ? "0.0f" : "0.0";
                writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ {function.CType} value = {function.NativeName}({arguments}); if (left == {zero} && right == {zero}) return signbit(left) || signbit(right) ? -{zero} : {zero}; return value; }}");
                continue;
            }
            if (function.RuntimeName is "ct_math_max" or "ct_math_max_double")
            {
                var zero = function.CType == "float" ? "0.0f" : "0.0";
                writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ {function.CType} value = {function.NativeName}({arguments}); if (left == {zero} && right == {zero}) return signbit(left) && signbit(right) ? -{zero} : {zero}; return value; }}");
                continue;
            }
            writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ return {function.NativeName}({arguments}); }}");
        }
        if (_usedMathSymbols.Count != 0)
            writer.WriteLine();
    }

    private static int UnaryMathOperation(string name) => name switch
    {
        "ct_math_sqrt" or "ct_math_sqrt_double" => 0,
        "ct_math_abs" or "ct_math_abs_double" => 1,
        "ct_math_tan" or "ct_math_tan_double" => 2,
        "ct_math_sin" or "ct_math_sin_double" => 3,
        "ct_math_cos" or "ct_math_cos_double" => 4,
        "ct_math_acos" or "ct_math_acos_double" => 5,
        "ct_math_floor" or "ct_math_floor_double" => 6,
        "ct_math_ceiling" or "ct_math_ceiling_double" => 7,
        "ct_math_asin" or "ct_math_asin_double" => 8,
        "ct_math_atan" or "ct_math_atan_double" => 9,
        "ct_math_exp" or "ct_math_exp_double" => 10,
        "ct_math_log" or "ct_math_log_double" => 11,
        "ct_math_log2" or "ct_math_log2_double" => 12,
        "ct_math_log10" or "ct_math_log10_double" => 13,
        "ct_math_round" or "ct_math_round_double" => 14,
        "ct_math_truncate" or "ct_math_truncate_double" => 15,
        _ => throw new InvalidOperationException($"Unknown unary math service '{name}'."),
    };

    private static int BinaryMathOperation(string name) => name switch
    {
        "ct_math_min" or "ct_math_min_double" => 0,
        "ct_math_max" or "ct_math_max_double" => 1,
        "ct_math_atan2" or "ct_math_atan2_double" => 2,
        "ct_math_pow" or "ct_math_pow_double" => 3,
        _ => throw new InvalidOperationException($"Unknown binary math service '{name}'."),
    };
}
