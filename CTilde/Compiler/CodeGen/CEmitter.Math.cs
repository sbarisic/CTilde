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
        ("ct_math_floor", "floorf", "float", 1),
        ("ct_math_ceiling", "ceilf", "float", 1),
        ("ct_math_sqrt_double", "sqrt", "double", 1),
        ("ct_math_abs_double", "fabs", "double", 1),
        ("ct_math_tan_double", "tan", "double", 1),
        ("ct_math_min_double", "fmin", "double", 2),
        ("ct_math_max_double", "fmax", "double", 2),
        ("ct_math_sin_double", "sin", "double", 1),
        ("ct_math_cos_double", "cos", "double", 1),
        ("ct_math_floor_double", "floor", "double", 1),
        ("ct_math_ceiling_double", "ceil", "double", 1),
    ];

    private static bool IsMathSymbol(string name) =>
        MathFunctions.Any(function => function.RuntimeName == name);

    private void EmitMathSupport(CWriter writer)
    {
        foreach (var function in MathFunctions.Where(function => _usedMathSymbols.Contains(function.RuntimeName)))
        {
            var parameters = function.ParameterCount == 1 ? $"{function.CType} value" : $"{function.CType} left, {function.CType} right";
            var arguments = function.ParameterCount == 1 ? "value" : "left, right";
            writer.WriteLine($"{function.CType} {function.RuntimeName}({parameters}) {{ return {function.NativeName}({arguments}); }}");
        }
        if (_usedMathSymbols.Count != 0)
            writer.WriteLine();
    }
}
