namespace CTilde;

internal sealed partial class CEmitter
{
    private static readonly (string RuntimeName, string NativeName, int ParameterCount)[] MathFunctions =
    [
        ("ct_math_sqrt", "sqrtf", 1),
        ("ct_math_abs", "fabsf", 1),
        ("ct_math_tan", "tanf", 1),
        ("ct_math_min", "fminf", 2),
        ("ct_math_max", "fmaxf", 2),
        ("ct_math_sin", "sinf", 1),
        ("ct_math_cos", "cosf", 1),
        ("ct_math_floor", "floorf", 1),
        ("ct_math_ceiling", "ceilf", 1),
    ];

    private static bool IsMathSymbol(string name) =>
        MathFunctions.Any(function => function.RuntimeName == name);

    private void EmitMathSupport(CWriter writer)
    {
        foreach (var function in MathFunctions.Where(function => _usedMathSymbols.Contains(function.RuntimeName)))
        {
            var parameters = function.ParameterCount == 1 ? "float value" : "float left, float right";
            var arguments = function.ParameterCount == 1 ? "value" : "left, right";
            writer.WriteLine($"float {function.RuntimeName}({parameters}) {{ return {function.NativeName}({arguments}); }}");
        }
        if (_usedMathSymbols.Count != 0)
            writer.WriteLine();
    }
}
