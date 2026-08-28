using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private bool TryFoldUnary(UnaryExpressionSyntax syntax, IrExpressionValue operand, out IrExpressionValue result)
    {
        result = ErrorExpression();
        try
        {
            switch (syntax.OperatorKind)
            {
                case SyntaxKind.PlusToken:
                    result = operand;
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Int:
                    var signed = unchecked(-(int)operand.ConstantValue!);
                    result = Constant(CType.Int, signed, FormatInt32(signed));
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Long:
                    var signed64 = unchecked(-(long)operand.ConstantValue!);
                    result = Constant(CType.Long, signed64, FormatInt64(signed64));
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Float:
                    var floating = -(float)operand.ConstantValue!;
                    result = Constant(CType.Float, floating, FormatFloat(floating));
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Double:
                    var binary64 = -(double)operand.ConstantValue!;
                    result = Constant(CType.Double, binary64, FormatDouble(binary64));
                    return true;
                case SyntaxKind.BangToken when operand.Type == CType.Bool:
                    var boolean = !(bool)operand.ConstantValue!;
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Int:
                    var complemented = ~(int)operand.ConstantValue!;
                    result = Constant(CType.Int, complemented, FormatInt32(complemented));
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Uint:
                    var unsigned = ~(uint)operand.ConstantValue!;
                    result = Constant(CType.Uint, unsigned, $"UINT32_C({unsigned.ToString(CultureInfo.InvariantCulture)})");
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Long:
                    var complemented64 = ~(long)operand.ConstantValue!;
                    result = Constant(CType.Long, complemented64, FormatInt64(complemented64));
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Ulong:
                    var unsigned64 = ~(ulong)operand.ConstantValue!;
                    result = Constant(CType.Ulong, unsigned64, FormatUInt64(unsigned64));
                    return true;
            }
        }
        catch (InvalidCastException)
        {
            return false;
        }
        return false;
    }

    private bool TryFoldBinary(BinaryExpressionSyntax syntax, IrExpressionValue left, IrExpressionValue right, out IrExpressionValue result)
    {
        result = ErrorExpression();
        if (syntax.OperatorKind == SyntaxKind.PlusToken && left.Type == CType.String && right.Type == CType.String)
        {
            var text = (string?)left.ConstantValue + (string?)right.ConstantValue;
            result = Constant(CType.String, text, _emitter.RegisterString(text));
            return true;
        }
        if (left.Type == CType.Bool && right.Type == CType.Bool)
        {
            var l = (bool)left.ConstantValue!;
            var r = (bool)right.ConstantValue!;
            var value = syntax.OperatorKind switch
            {
                SyntaxKind.AmpersandAmpersandToken => l && r,
                SyntaxKind.PipePipeToken => l || r,
                SyntaxKind.EqualsEqualsToken => l == r,
                SyntaxKind.BangEqualsToken => l != r,
                _ => false,
            };
            if (syntax.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken or SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            {
                result = Constant(CType.Bool, value, value ? "true" : "false");
                return true;
            }
        }
        if (left.Type.Kind == CTypeKind.Enum && left.Type == right.Type && syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
        {
            var equal = BigInteger.Parse(left.ConstantValue!.ToString()!, CultureInfo.InvariantCulture) ==
                BigInteger.Parse(right.ConstantValue!.ToString()!, CultureInfo.InvariantCulture);
            var enumComparison = syntax.OperatorKind == SyntaxKind.EqualsEqualsToken ? equal : !equal;
            result = Constant(CType.Bool, enumComparison, enumComparison ? "true" : "false");
            return true;
        }
        if (!left.Type.IsNumeric || !right.Type.IsNumeric)
            return false;
        var common = TypeFacts.PromoteNumeric(left.Type, right.Type);
        if (common.Kind is CTypeKind.Nint or CTypeKind.Nuint)
            return false;
        if (!TryConvertConstant(left, common, out left) || !TryConvertConstant(right, common, out right))
            return false;
        var comparison = syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken;
        try
        {
            if (common == CType.Double)
            {
                var l = (double)left.ConstantValue!; var r = (double)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = CompareDouble(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (syntax.OperatorKind is not (SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken))
                    return false;
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => l + r,
                    SyntaxKind.MinusToken => l - r,
                    SyntaxKind.StarToken => l * r,
                    SyntaxKind.SlashToken => l / r,
                    _ => double.NaN,
                };
                result = Constant(CType.Double, value, FormatDouble(value));
                return true;
            }
            if (common == CType.Float)
            {
                var l = (float)left.ConstantValue!; var r = (float)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = CompareFloat(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (syntax.OperatorKind is not (SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken))
                    return false;
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => l + r,
                    SyntaxKind.MinusToken => l - r,
                    SyntaxKind.StarToken => l * r,
                    SyntaxKind.SlashToken => l / r,
                    _ => float.NaN,
                };
                result = Constant(CType.Float, value, FormatFloat(value));
                return true;
            }
            else if (common == CType.Ulong)
            {
                var l = (ulong)left.ConstantValue!; var r = (ulong)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Ulong, 0ul, "UINT64_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l / r,
                    SyntaxKind.PercentToken => l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => l << ((int)r & 63),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 63),
                    _ => ulong.MaxValue,
                };
                result = Constant(CType.Ulong, value, FormatUInt64(value));
                return true;
            }
            else if (common == CType.Long)
            {
                var l = (long)left.ConstantValue!; var r = (long)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Long, 0L, "INT64_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l == long.MinValue && r == -1 ? long.MinValue : l / r,
                    SyntaxKind.PercentToken => l == long.MinValue && r == -1 ? 0 : l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => unchecked(l << ((int)r & 63)),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 63),
                    _ => long.MinValue,
                };
                result = Constant(CType.Long, value, FormatInt64(value));
                return true;
            }
            else if (common == CType.Uint)
            {
                var l = (uint)left.ConstantValue!; var r = (uint)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Uint, 0u, "UINT32_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l / r,
                    SyntaxKind.PercentToken => l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => l << ((int)r & 31),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 31),
                    _ => uint.MaxValue,
                };
                result = Constant(CType.Uint, value, $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})");
                return true;
            }
            else
            {
                var l = (int)left.ConstantValue!; var r = (int)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Int, 0, "0");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l == int.MinValue && r == -1 ? int.MinValue : l / r,
                    SyntaxKind.PercentToken => l == int.MinValue && r == -1 ? 0 : l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => unchecked(l << (r & 31)),
                    SyntaxKind.GreaterGreaterToken => l >> (r & 31),
                    _ => int.MinValue,
                };
                result = Constant(CType.Int, value, FormatInt32(value));
                return true;
            }
        }
        catch (DivideByZeroException)
        {
            Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
            result = common.Kind switch
            {
                CTypeKind.Uint => Constant(common, 0u, "UINT32_C(0)"),
                CTypeKind.Long => Constant(common, 0L, "INT64_C(0)"),
                CTypeKind.Ulong => Constant(common, 0ul, "UINT64_C(0)"),
                _ => Constant(common, 0, "0"),
            };
            return true;
        }
    }

    private static bool Compare<T>(SyntaxKind operation, T left, T right) where T : IComparable<T>
    {
        var comparison = left.CompareTo(right);
        return operation switch
        {
            SyntaxKind.EqualsEqualsToken => comparison == 0,
            SyntaxKind.BangEqualsToken => comparison != 0,
            SyntaxKind.LessToken => comparison < 0,
            SyntaxKind.LessEqualsToken => comparison <= 0,
            SyntaxKind.GreaterToken => comparison > 0,
            SyntaxKind.GreaterEqualsToken => comparison >= 0,
            _ => false,
        };
    }

    private bool TryConvertConstant(IrExpressionValue expression, CType target, out IrExpressionValue result)
    {
        result = expression;
        if (!expression.IsConstant)
            return false;
        if (expression.Type == target)
            return true;
        if (expression.Type.Kind == CTypeKind.Null && target.IsPointerLike)
        {
            result = Constant(target, null, $"({_emitter.CCastType(target)})NULL");
            return true;
        }
        if (!expression.Type.IsNumeric || !target.IsNumeric)
            return false;
        try
        {
            if (target == CType.Float)
            {
                var value = System.Convert.ToSingle(expression.ConstantValue, CultureInfo.InvariantCulture);
                result = Constant(target, value, FormatFloat(value));
                return true;
            }
            if (target == CType.Double)
            {
                var value = System.Convert.ToDouble(expression.ConstantValue, CultureInfo.InvariantCulture);
                result = Constant(target, value, FormatDouble(value));
                return true;
            }
            if (target == CType.Uint)
            {
                var value = expression.ConstantValue switch
                {
                    uint unsigned => unsigned,
                    int signed => unchecked((uint)signed),
                    float floating => unchecked((uint)floating),
                    double floating => unchecked((uint)floating),
                    _ => unchecked((uint)System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})");
                return true;
            }
            if (target == CType.Long)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unchecked((long)unsigned),
                    float floating => unchecked((long)floating),
                    double floating => unchecked((long)floating),
                    _ => unchecked(System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, FormatInt64(value));
                return true;
            }
            if (target == CType.Ulong)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unsigned,
                    long signed => unchecked((ulong)signed),
                    int signed => unchecked((ulong)signed),
                    float floating => unchecked((ulong)floating),
                    double floating => unchecked((ulong)floating),
                    _ => unchecked(System.Convert.ToUInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, FormatUInt64(value));
                return true;
            }
            if (target == CType.Nint)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unchecked((long)unsigned),
                    float floating => unchecked((long)floating),
                    double floating => unchecked((long)floating),
                    _ => unchecked(System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, $"((intptr_t){FormatInt64(value)})");
                return true;
            }
            if (target == CType.Nuint)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unsigned,
                    long signed => unchecked((ulong)signed),
                    int signed => unchecked((ulong)signed),
                    float floating => unchecked((ulong)floating),
                    double floating => unchecked((ulong)floating),
                    _ => unchecked(System.Convert.ToUInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, $"((uintptr_t){FormatUInt64(value)})");
                return true;
            }
            var signedValue = expression.ConstantValue switch
            {
                int signed => signed,
                uint unsigned => unchecked((int)unsigned),
                long signed => unchecked((int)signed),
                ulong unsigned => unchecked((int)unsigned),
                float floating => unchecked((int)floating),
                double floating => unchecked((int)floating),
                _ => unchecked((int)System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
            };
            if (target == CType.Int)
                result = Constant(target, signedValue, FormatInt32(signedValue));
            else
            {
                var narrowed = target.Kind switch
                {
                    CTypeKind.Byte or CTypeKind.Char => unchecked((byte)signedValue),
                    CTypeKind.Sbyte => unchecked((sbyte)signedValue),
                    CTypeKind.Short => unchecked((short)signedValue),
                    CTypeKind.Ushort => unchecked((ushort)signedValue),
                    _ => signedValue,
                };
                result = Constant(target, narrowed, $"({_emitter.CTypeName(target)}){FormatInt32(signedValue)}");
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string OperatorText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.PercentToken => "%",
        SyntaxKind.AmpersandToken => "&",
        SyntaxKind.PipeToken => "|",
        SyntaxKind.HatToken => "^",
        SyntaxKind.LessToken => "<",
        SyntaxKind.LessEqualsToken => "<=",
        SyntaxKind.GreaterToken => ">",
        SyntaxKind.GreaterEqualsToken => ">=",
        SyntaxKind.EqualsEqualsToken => "==",
        SyntaxKind.BangEqualsToken => "!=",
        SyntaxKind.LessLessToken => "<<",
        SyntaxKind.GreaterGreaterToken => ">>",
        _ => "+",
    };

    private static string FormatInt32(int value) => value == int.MinValue ? "INT32_MIN" : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatInt64(long value) => value switch
    {
        long.MinValue => "INT64_MIN",
        < 0 => $"(-INT64_C({(-value).ToString(CultureInfo.InvariantCulture)}))",
        _ => $"INT64_C({value.ToString(CultureInfo.InvariantCulture)})",
    };

    private static string FormatUInt64(ulong value) => $"UINT64_C({value.ToString(CultureInfo.InvariantCulture)})";

    private static string FormatFloat(float value)
    {
        if (float.IsNaN(value))
            return "NAN";
        if (float.IsPositiveInfinity(value))
            return "INFINITY";
        if (float.IsNegativeInfinity(value))
            return "(-INFINITY)";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
            text += ".0";
        return text + "f";
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            return "NAN";
        if (double.IsPositiveInfinity(value))
            return "INFINITY";
        if (double.IsNegativeInfinity(value))
            return "(-INFINITY)";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
            text += ".0";
        return text;
    }

    private static bool CompareFloat(SyntaxKind operation, float left, float right) => operation switch
    {
        SyntaxKind.EqualsEqualsToken => left == right,
        SyntaxKind.BangEqualsToken => left != right,
        SyntaxKind.LessToken => left < right,
        SyntaxKind.LessEqualsToken => left <= right,
        SyntaxKind.GreaterToken => left > right,
        SyntaxKind.GreaterEqualsToken => left >= right,
        _ => false,
    };

    private static bool CompareDouble(SyntaxKind operation, double left, double right) => operation switch
    {
        SyntaxKind.EqualsEqualsToken => left == right,
        SyntaxKind.BangEqualsToken => left != right,
        SyntaxKind.LessToken => left < right,
        SyntaxKind.LessEqualsToken => left <= right,
        SyntaxKind.GreaterToken => left > right,
        SyntaxKind.GreaterEqualsToken => left >= right,
        _ => false,
    };

    private static string FormatCondition(string code) => code.StartsWith('(') && code.EndsWith(')') ? code : $"({code})";

    private static IrExpressionValue Constant(CType type, object? value, string code, object? symbol = null) => new() { Type = type, Code = code, IsConstant = true, ConstantValue = value, Symbol = symbol };
    private static IrExpressionValue ErrorExpression(IEnumerable<string>? prelude = null) => new() { Type = CType.Error, Code = "0", Prelude = prelude?.ToList() ?? [] };
}
