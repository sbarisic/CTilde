using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private IrExpressionValue LowerCast(CastExpressionSyntax syntax)
    {
        var target = ResolveType(syntax.Type);
        var expression = LowerExpression(syntax.Expression);
        if (target.ContainsPointer || expression.Type.ContainsPointer)
            RequireUnsafe(syntax);
        return Convert(expression, target, syntax, true);
    }

    private IrExpressionValue LowerTypeTest(TypeTestExpressionSyntax syntax)
    {
        var target = ResolveType(syntax.Type);
        if (target.Kind is CTypeKind.Void or CTypeKind.Null or CTypeKind.Error)
        {
            Report("CT2147", $"Type '{target.DisplayName}' is not valid in an is expression.", syntax.Type);
            return ErrorExpression();
        }
        if (target.ContainsPointer)
            RequireUnsafe(syntax);
        _emitter.RegisterType(target);
        if (!target.IsReference)
            _emitter.RegisterBox(target);
        var objectType = _model.Types["System.Object"].Type;
        var value = Materialize(Convert(LowerExpression(syntax.Expression), objectType, syntax.Expression, false), syntax.Expression);
        var code = $"({value.Code} != NULL && ct_type_is_assignable(((ct_object*)(void*){value.Code})->Type, {_emitter.DescriptorExpression(target)}))";
        return new IrExpressionValue { Type = CType.Bool, Code = code, Prelude = value.Prelude };
    }

    private IrExpressionValue LowerSafeCast(SafeCastExpressionSyntax syntax)
    {
        var target = ResolveType(syntax.Type);
        if (!target.IsReference)
        {
            Report("CT2147", "The as operator requires a reference target type.", syntax.Type);
            return ErrorExpression();
        }
        var source = LowerExpression(syntax.Expression);
        if (!source.Type.IsReference && source.Type.Kind != CTypeKind.Null && !source.Type.IsError)
        {
            Report("CT2147", "The as operator requires a reference source expression.", syntax.Expression);
            return ErrorExpression(source.Prelude);
        }
        _emitter.RegisterType(target);
        var objectType = _model.Types["System.Object"].Type;
        var value = Materialize(Convert(source, objectType, syntax.Expression, false), syntax.Expression);
        var code = $"({_emitter.CTypeName(target)})(void*)ct_safe_cast((ct_object*)(void*){value.Code}, {_emitter.DescriptorExpression(target)})";
        return new IrExpressionValue { Type = target, Code = code, Prelude = value.Prelude };
    }

    private IrExpressionValue LowerUnary(UnaryExpressionSyntax syntax)
    {
        if (syntax.OperatorKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
            return LowerIncrement(syntax);
        if (syntax.OperatorKind == SyntaxKind.AmpersandToken)
        {
            RequireUnsafe(syntax);
            var methodGroup = LowerExpression(syntax.Operand);
            if (methodGroup.MethodGroup is not null)
                return new IrExpressionValue { Type = CType.Error, Code = string.Empty, Prelude = methodGroup.Prelude, MethodGroup = methodGroup.MethodGroup, IsFunctionAddress = true };
            var operand = LowerAssignable(syntax.Operand);
            if (operand.LValue?.Field is { ContainingType.HasNonNaturalLayout: true })
            {
                Report("CT2190", "A field in a packed or explicit-layout aggregate cannot have its address taken.", syntax.Operand);
                return ErrorExpression(operand.Prelude);
            }
            if (operand.LValue?.Address is null)
            {
                Report("CT2124", "The address-of operator requires an addressable value.", syntax.Operand);
                return ErrorExpression(operand.Prelude);
            }
            return new IrExpressionValue { Type = new CType(CTypeKind.Pointer, ElementType: operand.Type), Code = operand.LValue.Address, Prelude = operand.Prelude };
        }
        if (syntax.OperatorKind == SyntaxKind.StarToken)
        {
            RequireUnsafe(syntax);
            var pointer = Materialize(LowerExpression(syntax.Operand), syntax.Operand);
            if (pointer.Type.Kind != CTypeKind.Pointer)
            {
                Report("CT2125", "The dereference operator requires a pointer.", syntax.Operand);
                return ErrorExpression(pointer.Prelude);
            }
            if (pointer.Type.ElementType == CType.Void)
            {
                Report("CT2180", "void* cannot be dereferenced.", syntax);
                return ErrorExpression(pointer.Prelude);
            }
            var dereferenceCode = $"*({pointer.Code})";
            return new IrExpressionValue
            {
                Type = pointer.Type.ElementType!,
                Code = dereferenceCode,
                Prelude = pointer.Prelude,
                LValue = new IrValueStorage { Store = value => $"{dereferenceCode} = {value}", Address = pointer.Code },
            };
        }

        if (syntax.OperatorKind == SyntaxKind.MinusToken && syntax.Operand is LiteralExpressionSyntax
            {
                LiteralKind: SyntaxKind.NumberToken,
                Value: NumericLiteralValue { FloatingPoint: null } numeric,
            })
        {
            if (numeric.Suffix == IntegerLiteralSuffix.None && numeric.Integer == (BigInteger)int.MaxValue + 1)
                return Constant(CType.Int, int.MinValue, "INT32_MIN");
            if (numeric.Suffix == IntegerLiteralSuffix.Long && numeric.Integer == (BigInteger)long.MaxValue + 1)
                return Constant(CType.Long, long.MinValue, "INT64_MIN");
        }

        var operandExpression = LowerExpression(syntax.Operand);
        if (syntax.OperatorKind == SyntaxKind.TildeToken && !operandExpression.Type.IsIntegral && !operandExpression.Type.IsError)
        {
            Report("CT2148", "The bitwise complement operator requires an integral operand.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        if (operandExpression.IsConstant && TryFoldUnary(syntax, operandExpression, out var foldedUnary))
            return foldedUnary;
        if (syntax.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken && HasUserDefinedOperatorOperand(operandExpression))
            return LowerOperatorCall(syntax.OperatorKind, [operandExpression], [syntax.Operand], syntax);
        if (syntax.OperatorKind == SyntaxKind.BangToken)
        {
            var operand = RequireBoolean(operandExpression, syntax.Operand);
            return new IrExpressionValue { Type = CType.Bool, Code = $"!({operand.Code})", Prelude = operand.Prelude, IsConstant = operand.IsConstant, ConstantValue = SymbolicConstant(operand) };
        }
        if (!operandExpression.Type.IsNumeric && !operandExpression.Type.IsIntegral)
        {
            Report("CT2126", $"Unary operator cannot be applied to '{operandExpression.Type.DisplayName}'.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        var promoted = operandExpression.Type.Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char ? CType.Int : operandExpression.Type;
        if (syntax.OperatorKind == SyntaxKind.MinusToken && promoted.Kind is CTypeKind.Uint or CTypeKind.Ulong or CTypeKind.Nuint)
        {
            Report("CT2145", "Unary minus requires a signed numeric operand.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        var operandValue = Convert(operandExpression, promoted, syntax.Operand, false);
        string code = syntax.OperatorKind switch
        {
            SyntaxKind.PlusToken => operandValue.Code,
            SyntaxKind.MinusToken when IsSymbolicConstant(operandValue) => $"-({operandValue.Code})",
            SyntaxKind.MinusToken when promoted == CType.Int => $"ct_i32_neg({operandValue.Code})",
            SyntaxKind.MinusToken when promoted == CType.Long => $"ct_i64_neg({operandValue.Code})",
            SyntaxKind.MinusToken when promoted == CType.Nint => $"ct_ni_neg({operandValue.Code})",
            SyntaxKind.MinusToken => $"-({operandValue.Code})",
            SyntaxKind.TildeToken => $"~({operandValue.Code})",
            _ => operandValue.Code,
        };
        return new IrExpressionValue { Type = promoted, Code = code, Prelude = operandValue.Prelude, IsConstant = operandValue.IsConstant, ConstantValue = SymbolicConstant(operandValue) };
    }

    private IrExpressionValue LowerIncrement(UnaryExpressionSyntax syntax)
    {
        var target = LowerAssignable(syntax.Operand);
        if (target.LValue is null || !target.Type.IsNumeric)
        {
            Report("CT2127", "Increment and decrement require an assignable numeric operand.", syntax.Operand);
            return ErrorExpression(target.Prelude);
        }
        ValidateAssignmentTarget(target.LValue, syntax);
        if (target.LValue.Property is { Getter: not null } property)
        {
            var getter = _emitter.GetAccessorMethod(property, getter: true);
            _emitter.AllocationEffects.RecordCall(_method, getter, syntax.Operand, property.IsVirtual && !target.LValue.IsBaseReceiver);
        }
        var prelude = new List<string>(target.Prelude);
        var old = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, old)} = {target.Code};");
        var one = target.Type == CType.Float ? "1.0f" : "1";
        var nextCode = NumericOperation(syntax.OperatorKind == SyntaxKind.PlusPlusToken ? SyntaxKind.PlusToken : SyntaxKind.MinusToken, target.Type, old, one, syntax);
        var next = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, next)} = {nextCode};");
        prelude.Add(target.LValue.Store(next) + ";");
        MarkAssigned(target.LValue);
        return new IrExpressionValue { Type = target.Type, Code = syntax.IsPostfix ? old : next, Prelude = prelude };
    }

    private IrExpressionValue LowerBinary(BinaryExpressionSyntax syntax)
    {
        if (syntax.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
            return LowerShortCircuit(syntax);
        if (IsKnownStringConcat(syntax))
            return LowerStringBuild(syntax);
        var left = LowerExpression(syntax.Left);
        var right = LowerExpression(syntax.Right);
        if (syntax.OperatorKind == SyntaxKind.PercentToken &&
            (!left.Type.IsIntegral || !right.Type.IsIntegral) &&
            !left.Type.IsError && !right.Type.IsError)
        {
            Report("CT2149", "The remainder operator requires integral operands.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        if (left.IsConstant && right.IsConstant && TryFoldBinary(syntax, left, right, out var foldedBinary))
            return foldedBinary;

        if (syntax.OperatorKind == SyntaxKind.PlusToken &&
            (left.Type == CType.String && right.Type.Kind is CTypeKind.String or CTypeKind.Null || right.Type == CType.String && left.Type.Kind is CTypeKind.String or CTypeKind.Null))
        {
            left = Convert(left, CType.String, syntax.Left, false);
            right = Convert(right, CType.String, syntax.Right, false);
            left = Materialize(left, syntax.Left);
            right = Materialize(right, syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "nonconstant string concatenation");
            return OwnResult(CType.String, $"ct_string_concat({left.Code}, {right.Code}, {_emitter.SourceArgument(syntax)})", prelude);
        }

        if (syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            return LowerEquality(syntax, left, right);

        if (syntax.OperatorKind is SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken)
        {
            if (!(left.Type.IsNumeric && right.Type.IsNumeric) && !(left.Type.Kind == CTypeKind.Enum && left.Type == right.Type))
                Report("CT2128", "Ordered comparison requires numeric operands or the same enum type.", syntax);
            var common = left.Type.Kind == CTypeKind.Enum ? left.Type : TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError && !left.Type.IsError && !right.Type.IsError)
            {
                Report("CT2128", "Ordered comparison has no valid common numeric type.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            return new IrExpressionValue { Type = CType.Bool, Code = $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})", Prelude = prelude, IsConstant = left.IsConstant && right.IsConstant, ConstantValue = SymbolicConstant(left, right) };
        }

        if (syntax.OperatorKind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.HatToken or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
        {
            if (!left.Type.IsIntegral || !right.Type.IsIntegral)
                Report("CT2129", "Bitwise and shift operators require integral operands.", syntax);
            if (left.Type.Kind == CTypeKind.Enum)
            {
                var enumType = left.Type;
                if (syntax.OperatorKind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.HatToken && right.Type != enumType)
                    Report("CT2143", "Binary enum bitwise operands must have the same enum type.", syntax);
                var underlying = enumType.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type;
                left = Materialize(Convert(left, underlying, syntax.Left, true), syntax.Left);
                right = Materialize(Convert(right, syntax.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken ? CType.Int : underlying, syntax.Right, true), syntax.Right);
                var enumPrelude = new List<string>(left.Prelude); enumPrelude.AddRange(right.Prelude);
                var enumCode = syntax.OperatorKind switch
                {
                    SyntaxKind.LessLessToken when underlying == CType.Int => $"ct_i32_shl({left.Code}, {right.Code})",
                    SyntaxKind.GreaterGreaterToken when underlying == CType.Int => $"ct_i32_shr({left.Code}, {right.Code})",
                    _ => $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})",
                };
                return new IrExpressionValue { Type = enumType, Code = $"({_emitter.CTypeName(enumType)})({enumCode})", Prelude = enumPrelude };
            }
            var shifting = syntax.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
            var common = shifting
                ? left.Type.Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char ? CType.Int : left.Type
                : TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError && !left.Type.IsError && !right.Type.IsError)
            {
                Report("CT2129", "Bitwise operands have no valid common integral type.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, shifting ? CType.Int : common, syntax.Right, shifting), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            var code = syntax.OperatorKind switch
            {
                SyntaxKind.LessLessToken when common == CType.Int => $"ct_i32_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Int => $"ct_i32_shr({left.Code}, {right.Code})",
                SyntaxKind.LessLessToken when common == CType.Long => $"ct_i64_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Long => $"ct_i64_shr({left.Code}, {right.Code})",
                SyntaxKind.LessLessToken when common == CType.Ulong => $"({left.Code} << ((uint32_t){right.Code} & 63u))",
                SyntaxKind.GreaterGreaterToken when common == CType.Ulong => $"({left.Code} >> ((uint32_t){right.Code} & 63u))",
                SyntaxKind.LessLessToken when common == CType.Nint => $"ct_ni_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Nint => $"ct_ni_shr({left.Code}, {right.Code})",
                SyntaxKind.LessLessToken when common == CType.Nuint => $"({left.Code} << ((uint32_t){right.Code} & (uint32_t)(sizeof(uintptr_t) * CHAR_BIT - 1u)))",
                SyntaxKind.GreaterGreaterToken when common == CType.Nuint => $"({left.Code} >> ((uint32_t){right.Code} & (uint32_t)(sizeof(uintptr_t) * CHAR_BIT - 1u)))",
                SyntaxKind.LessLessToken => $"({left.Code} << ((uint32_t){right.Code} & 31u))",
                SyntaxKind.GreaterGreaterToken => $"({left.Code} >> ((uint32_t){right.Code} & 31u))",
                _ => $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})",
            };
            return new IrExpressionValue { Type = common, Code = code, Prelude = prelude, IsConstant = left.IsConstant && right.IsConstant, ConstantValue = SymbolicConstant(left, right) };
        }

        if (left.Type.Kind == CTypeKind.Pointer && right.Type.Kind is CTypeKind.Int or CTypeKind.Nint && syntax.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
        {
            RequireUnsafe(syntax);
            if (left.Type.ElementType == CType.Void)
            {
                Report("CT2180", "void* does not support pointer arithmetic.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(left, syntax.Left);
            right = Materialize(Convert(right, right.Type, syntax.Right, false), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            return new IrExpressionValue { Type = left.Type, Code = $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})", Prelude = prelude };
        }
        if (left.Type.Kind == CTypeKind.Pointer && right.Type == left.Type && syntax.OperatorKind == SyntaxKind.MinusToken)
        {
            RequireUnsafe(syntax);
            left = Materialize(left, syntax.Left);
            right = Materialize(right, syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            return new IrExpressionValue { Type = CType.Nint, Code = $"(intptr_t)({left.Code} - {right.Code})", Prelude = prelude };
        }

        if (syntax.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken &&
            HasUserDefinedOperatorOperand(left, right))
        {
            return LowerOperatorCall(syntax.OperatorKind, [left, right], [syntax.Left, syntax.Right], syntax);
        }

        if (!left.Type.IsNumeric || !right.Type.IsNumeric)
        {
            Report("CT2130", "Arithmetic operators require numeric operands.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        var resultType = TypeFacts.PromoteNumeric(left.Type, right.Type);
        if (resultType.IsError)
        {
            Report("CT2130", "Arithmetic operands have no valid common numeric type.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        left = Materialize(Convert(left, resultType, syntax.Left, false), syntax.Left);
        right = Materialize(Convert(right, resultType, syntax.Right, false), syntax.Right);
        var symbolicConstant = SymbolicConstant(left, right);
        var arithmeticPrelude = new List<string>(left.Prelude); arithmeticPrelude.AddRange(right.Prelude);
        return new IrExpressionValue
        {
            Type = resultType,
            Code = symbolicConstant is not null
                ? $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})"
                : NumericOperation(syntax.OperatorKind, resultType, left.Code, right.Code, syntax),
            Prelude = arithmeticPrelude,
            IsConstant = left.IsConstant && right.IsConstant,
            ConstantValue = symbolicConstant,
        };
    }

    private IrExpressionValue LowerShortCircuit(BinaryExpressionSyntax syntax)
    {
        var rawLeft = RequireBoolean(LowerExpression(syntax.Left), syntax.Left);
        var right = RequireBoolean(LowerExpression(syntax.Right), syntax.Right);
        if (rawLeft.IsConstant && right.IsConstant && rawLeft.ConstantValue is bool && right.ConstantValue is bool && TryFoldBinary(syntax, rawLeft, right, out var folded))
            return folded;
        if (rawLeft.IsConstant && right.IsConstant && SymbolicConstant(rawLeft, right) is { } symbolic)
            return new IrExpressionValue
            {
                Type = CType.Bool,
                Code = $"({rawLeft.Code} {OperatorText(syntax.OperatorKind)} {right.Code})",
                IsConstant = true,
                ConstantValue = symbolic,
            };
        var left = Materialize(rawLeft, syntax.Left);
        var prelude = new List<string>(left.Prelude);
        var result = NewTemp();
        prelude.Add($"bool {result} = {left.Code};");
        var condition = syntax.OperatorKind == SyntaxKind.AmpersandAmpersandToken ? result : $"!{result}";
        prelude.Add($"if ({condition}) {{");
        prelude.AddRange(right.Prelude.Select(line => "    " + line));
        prelude.Add($"    {result} = {right.Code};");
        prelude.Add("}");
        return new IrExpressionValue { Type = CType.Bool, Code = result, Prelude = prelude };
    }

    private IrExpressionValue LowerEquality(BinaryExpressionSyntax syntax, IrExpressionValue left, IrExpressionValue right)
    {
        string code;
        CType common;
        if (left.Type == CType.String && right.Type.Kind is CTypeKind.String or CTypeKind.Null || right.Type == CType.String && left.Type.Kind is CTypeKind.String or CTypeKind.Null)
        {
            left = Convert(left, CType.String, syntax.Left, false);
            right = Convert(right, CType.String, syntax.Right, false);
            left = Materialize(left, syntax.Left); right = Materialize(right, syntax.Right);
            code = $"ct_string_equal({left.Code}, {right.Code})";
        }
        else if (left.Type.IsNumeric && right.Type.IsNumeric)
        {
            common = TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError)
            {
                Report("CT2131", $"Types '{left.Type.DisplayName}' and '{right.Type.DisplayName}' cannot be compared for equality.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            code = $"({left.Code} == {right.Code})";
        }
        else if (left.Type == right.Type || left.Type.Kind == CTypeKind.Null && right.Type.IsPointerLike || right.Type.Kind == CTypeKind.Null && left.Type.IsPointerLike)
        {
            common = left.Type.Kind == CTypeKind.Null ? right.Type : left.Type;
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            code = $"({left.Code} == {right.Code})";
        }
        else
        {
            Report("CT2131", $"Types '{left.Type.DisplayName}' and '{right.Type.DisplayName}' cannot be compared for equality.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        if (syntax.OperatorKind == SyntaxKind.BangEqualsToken)
            code = $"!({code})";
        var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
        return new IrExpressionValue { Type = CType.Bool, Code = code, Prelude = prelude, IsConstant = left.IsConstant && right.IsConstant, ConstantValue = SymbolicConstant(left, right) };
    }

    private IrExpressionValue LowerAssignment(AssignmentExpressionSyntax syntax)
    {
        var target = LowerAssignable(syntax.Left);
        if (target.LValue is null)
        {
            Report("CT2132", "The left side of an assignment must be assignable.", syntax.Left);
            return ErrorExpression(target.Prelude);
        }
        if (target.Type.ContainsAtomic)
        {
            Report("CT1278", "Atomic<T> values are non-copyable and cannot be assigned.", syntax);
            return ErrorExpression(target.Prelude);
        }
        if (target.LValue.Field?.IsVolatile == true && syntax.OperatorKind != SyntaxKind.EqualsToken)
        {
            Report("CT1274", "Compound assignment is not permitted on a volatile field; use Atomic<T> for read-modify-write operations.", syntax);
            return ErrorExpression(target.Prelude);
        }
        ValidateAssignmentTarget(target.LValue, syntax);
        var prelude = new List<string>(target.Prelude);
        if (syntax.OperatorKind == SyntaxKind.EqualsToken)
        {
            var value = Convert(LowerExpression(syntax.Right), target.Type, syntax.Right, false);
            if (target.Type.Kind is CTypeKind.Opaque or CTypeKind.Pointer && target.LValue.Local is { } resourceTarget)
            {
                if (resourceTarget.NativeResourceState is NativeResourceState.Owned or NativeResourceState.Deferred)
                    Report("CT1262", $"Owned native resource '{resourceTarget.Name}' cannot be overwritten before its ownership is discharged.", syntax.Left);
                if (value.Ownership == OwnershipKind.Owned)
                {
                    ConsumeOwnedExpression(value, syntax.Right);
                    resourceTarget.NativeResourceState = NativeResourceState.Owned;
                }
                else
                    resourceTarget.NativeResourceState = value.Type.Kind == CTypeKind.Null ? NativeResourceState.None : NativeResourceState.Borrowed;
            }
            prelude.AddRange(value.Prelude);
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(target.Type, temp)} = {value.Code};");
            if (target.Type.ContainsManagedReferences)
            {
                if (IsUninitializedOut(target.LValue))
                    AddConstructStore(prelude, target, temp, value);
                else
                    AddStrongStore(prelude, target, temp, value);
            }
            else
                prelude.Add(target.LValue.Store(temp) + ";");
            MarkAssigned(target.LValue);
            if (target.LValue.Local is { } assignedLocal)
            {
                assignedLocal.IsKnownNonNull = value.IsKnownNonNull;
                assignedLocal.KnownLength = value.KnownLength;
            }
            return new IrExpressionValue { Type = target.Type, Code = temp, Prelude = prelude };
        }

        var rawRight = LowerExpression(syntax.Right);
        var operation = syntax.OperatorKind switch
        {
            SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
            SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
            SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
            SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
            SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
            _ => SyntaxKind.PlusToken,
        };
        if (operation is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken &&
            HasUserDefinedOperatorOperand(target, rawRight))
        {
            if (target.LValue.Property is { Getter: not null } overloadedProperty)
            {
                var getter = _emitter.GetAccessorMethod(overloadedProperty, getter: true);
                _emitter.AllocationEffects.RecordCall(_method, getter, syntax.Left, overloadedProperty.IsVirtual && !target.LValue.IsBaseReceiver);
            }
            var oldOperand = OwnResult(target.Type, target.Code, prelude, borrowed: target.Type.ContainsManagedReferences);
            var operatorResult = LowerOperatorCall(operation, [oldOperand, rawRight], [syntax.Left, syntax.Right], syntax);
            var convertedResult = Convert(operatorResult, target.Type, syntax, false);
            var overloadedPrelude = new List<string>(convertedResult.Prelude);
            var overloadedResult = NewTemp();
            overloadedPrelude.Add($"{_emitter.CDeclaration(target.Type, overloadedResult)} = {convertedResult.Code};");
            if (target.Type.ContainsManagedReferences)
                AddStrongStore(overloadedPrelude, target, overloadedResult, convertedResult);
            else
                overloadedPrelude.Add(target.LValue.Store(overloadedResult) + ";");
            MarkAssigned(target.LValue);
            return new IrExpressionValue { Type = target.Type, Code = overloadedResult, Prelude = overloadedPrelude };
        }

        if (!target.Type.IsNumeric)
            Report("CT2133", "Compound assignment requires a numeric target or applicable user-defined operator.", syntax.Left);
        var old = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, old)} = {target.Code};");
        if (syntax.OperatorKind == SyntaxKind.PercentEqualsToken &&
            (!target.Type.IsIntegral || !rawRight.Type.IsIntegral) &&
            !target.Type.IsError && !rawRight.Type.IsError)
        {
            Report("CT2149", "The remainder operator requires integral operands.", syntax);
            return ErrorExpression(prelude.Concat(rawRight.Prelude));
        }

        if (target.LValue.Property is { Getter: not null } property)
        {
            var getter = _emitter.GetAccessorMethod(property, getter: true);
            _emitter.AllocationEffects.RecordCall(_method, getter, syntax.Left, property.IsVirtual && !target.LValue.IsBaseReceiver);
        }
        var operationType = TypeFacts.PromoteNumeric(target.Type, rawRight.Type);
        if (operationType.IsError)
        {
            Report("CT2133", "Compound-assignment operands have no valid common numeric type.", syntax);
            return ErrorExpression(prelude.Concat(rawRight.Prelude));
        }
        var right = Convert(rawRight, operationType, syntax.Right, true);
        prelude.AddRange(right.Prelude);
        var rightTemp = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(operationType, rightTemp)} = {right.Code};");
        var operationResult = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(operationType, operationResult)} = {NumericOperation(operation, operationType, $"({_emitter.CCastType(operationType)})({old})", rightTemp, syntax)};");
        var result = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, result)} = ({_emitter.CCastType(target.Type)})({operationResult});");
        prelude.Add(target.LValue.Store(result) + ";");
        MarkAssigned(target.LValue);
        return new IrExpressionValue { Type = target.Type, Code = result, Prelude = prelude };
    }

    private IrExpressionValue LowerAssignable(ExpressionSyntax syntax)
    {
        var expression = syntax switch
        {
            NameExpressionSyntax name => LowerName(name, true),
            MemberAccessExpressionSyntax member => LowerMember(member, true),
            IndexExpressionSyntax index => LowerIndex(index, true),
            UnaryExpressionSyntax { OperatorKind: SyntaxKind.StarToken } unary => LowerUnary(unary),
            _ => LowerExpression(syntax),
        };
        return RecordSemantic(syntax, expression);
    }

    private void ValidateAssignmentTarget(IrValueStorage lvalue, SyntaxNode syntax)
    {
        if (lvalue.Parameter?.PassingKind == ParameterPassingKind.In)
            Report("CT2173", $"In parameter '{lvalue.Parameter.Name}' is read-only.", syntax);
        if (lvalue.Local is { IsConst: true })
            Report("CT2134", $"Const local '{lvalue.Local.Name}' cannot be assigned.", syntax);
        if (lvalue.Local is { IsReadonly: true } readonlyLocal &&
            (readonlyLocal.AssignmentCount > 0 || _repeatableLoopDepth > readonlyLocal.LoopDepthAtDeclaration))
            Report("CT3130", $"Readonly local '{lvalue.Local.Name}' can be assigned only once.", syntax);
        if (lvalue.Field is { IsConst: true })
            Report("CT2135", $"Const field '{lvalue.Field.Name}' cannot be assigned.", syntax);
        if (lvalue.Field is { IsReadonly: true } field && (!_method.IsConstructor || field.ContainingType != _method.ContainingType))
            Report("CT2136", $"Readonly field '{field.Name}' can be assigned only by its constructor.", syntax);
        else if (lvalue.Field is { IsReadonly: true } readonlyField &&
                 (_fieldAssignmentCounts.GetValueOrDefault(readonlyField) > 0 || _repeatableLoopDepth > 0))
            Report("CT3131", $"Readonly field '{readonlyField.Name}' can be assigned only once.", syntax);
    }

    private void MarkAssigned(IrValueStorage lvalue)
    {
        if (lvalue.Local is not null)
        {
            lvalue.Local.IsAssigned = true;
            lvalue.Local.AssignmentCount++;
            lvalue.Local.IsKnownNonNull = false;
            lvalue.Local.KnownLength = null;
        }
        if (lvalue.Field is not null)
        {
            _assignedFields.Add(lvalue.Field);
            _fieldAssignmentCounts[lvalue.Field] = _fieldAssignmentCounts.GetValueOrDefault(lvalue.Field) + 1;
        }
        if (lvalue.Parameter?.PassingKind == ParameterPassingKind.Out)
            _assignedOutParameters.Add(lvalue.Parameter);
    }

    private bool IsUninitializedOut(IrValueStorage lvalue) =>
        lvalue.Parameter is { PassingKind: ParameterPassingKind.Out } parameter &&
        !_assignedOutParameters.Contains(parameter);

    private void ValidateOutParameters(SyntaxNode syntax)
    {
        foreach (var parameter in _method.Parameters.Where(parameter => parameter.PassingKind == ParameterPassingKind.Out && !_assignedOutParameters.Contains(parameter)))
            Report("CT2175", $"Out parameter '{parameter.Name}' must be assigned on every normal return.", syntax);
    }

    private string NumericOperation(SyntaxKind operation, CType type, string left, string right, SyntaxNode syntax)
    {
        if (type == CType.Int)
        {
            return operation switch
            {
                SyntaxKind.PlusToken => $"ct_i32_add({left}, {right})",
                SyntaxKind.MinusToken => $"ct_i32_sub({left}, {right})",
                SyntaxKind.StarToken => $"ct_i32_mul({left}, {right})",
                SyntaxKind.SlashToken => $"ct_i32_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_i32_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Uint)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_u32_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_u32_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Long)
        {
            return operation switch
            {
                SyntaxKind.PlusToken => $"ct_i64_add({left}, {right})",
                SyntaxKind.MinusToken => $"ct_i64_sub({left}, {right})",
                SyntaxKind.StarToken => $"ct_i64_mul({left}, {right})",
                SyntaxKind.SlashToken => $"ct_i64_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_i64_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Ulong)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_u64_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_u64_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Nint)
        {
            return operation switch
            {
                SyntaxKind.PlusToken => $"ct_ni_add({left}, {right})",
                SyntaxKind.MinusToken => $"ct_ni_sub({left}, {right})",
                SyntaxKind.StarToken => $"ct_ni_mul({left}, {right})",
                SyntaxKind.SlashToken => $"ct_ni_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_ni_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Nuint)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_nu_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_nu_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        return $"({left} {OperatorText(operation)} {right})";
    }

    private IrExpressionValue Convert(IrExpressionValue expression, CType target, SyntaxNode syntax, bool explicitConversion)
    {
        if (expression.IsFunctionAddress)
            return ConvertFunctionAddress(expression, target, syntax);
        if (expression.MethodGroup is not null)
            return ConvertMethodGroup(expression, target, syntax);
        if (expression.Type == target || expression.Type.IsError || target.IsError)
            return new IrExpressionValue
            {
                Type = target,
                Code = expression.Code,
                Prelude = expression.Prelude,
                LValue = expression.LValue,
                IsConstant = expression.IsConstant,
                ConstantValue = expression.ConstantValue,
                Ownership = expression.Ownership,
                Symbol = expression.Symbol,
                IsKnownNonNull = expression.IsKnownNonNull,
                KnownLength = expression.KnownLength,
                OwnedCleanupRecord = expression.OwnedCleanupRecord,
            };
        if (expression.Type.Kind == CTypeKind.NativeBuffer && target.Kind == CTypeKind.ReadOnlyNativeBuffer && expression.Type.ElementType == target.ElementType)
            return new IrExpressionValue
            {
                Type = target,
                Code = $"({_emitter.CTypeName(target)}){{ ({expression.Code}).Data, ({expression.Code}).Length }}",
                Prelude = expression.Prelude,
            };
        var sourceType = expression.Type;
        var valid = explicitConversion ? TypeFacts.CanExplicitlyConvert(sourceType, target) : TypeFacts.CanImplicitlyConvert(sourceType, target) || CanImplicitNativeConstant(expression, target);
        if (!valid)
        {
            Report("CT2137", $"Cannot {(explicitConversion ? "cast" : "implicitly convert")} '{expression.Type.DisplayName}' to '{target.DisplayName}'.", syntax);
            return new IrExpressionValue { Type = target, Code = _emitter.DefaultValue(target), Prelude = expression.Prelude };
        }
        if (expression.IsConstant && TryConvertConstant(expression, target, out var constant))
            return constant;
        var objectType = _model.Types.GetValueOrDefault("System.Object")?.Type;
        if (sourceType.ContainsAtomic && target != sourceType)
        {
            Report("CT1278", "Atomic<T> values cannot be boxed or converted by copying.", syntax);
            return new IrExpressionValue { Type = target, Code = _emitter.DefaultValue(target), Prelude = expression.Prelude };
        }
        if (objectType is not null && target == objectType && !sourceType.IsReference && sourceType.Kind is not CTypeKind.Null)
        {
            if (sourceType.ContainsPointer)
                RequireUnsafe(syntax);
            _emitter.RegisterBox(sourceType);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, $"boxing of '{sourceType.DisplayName}'");
            var boxCode = $"{CEmitter.BoxFunctionName(sourceType)}({expression.Code}, {_emitter.SourceArgument(syntax)})";
            return OwnResult(target, boxCode, expression.Prelude);
        }
        if (target.Kind == CTypeKind.Interface && !sourceType.IsReference && sourceType.Kind is not CTypeKind.Null)
        {
            _emitter.RegisterBox(sourceType);
            _emitter.RegisterType(target);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, $"boxing of '{sourceType.DisplayName}'");
            var boxCode = $"({_emitter.CTypeName(target)})(void*){CEmitter.BoxFunctionName(sourceType)}({expression.Code}, {_emitter.SourceArgument(syntax)})";
            return OwnResult(target, boxCode, expression.Prelude);
        }
        if (objectType is not null && sourceType.Kind is CTypeKind.Class or CTypeKind.Interface && target != objectType && target.Kind is not CTypeKind.Class and not CTypeKind.Interface and not CTypeKind.String and not CTypeKind.Array)
        {
            if (target.ContainsPointer)
                RequireUnsafe(syntax);
            _emitter.RegisterBox(target);
            var unboxCode = $"{CEmitter.UnboxFunctionName(target)}({expression.Code}, {_emitter.SourceArgument(syntax)})";
            return target.ContainsManagedReferences ? OwnResult(target, unboxCode, expression.Prelude) : new IrExpressionValue { Type = target, Code = unboxCode, Prelude = expression.Prelude };
        }
        if (explicitConversion && sourceType.IsReference && target.IsReference && sourceType != target &&
            !(sourceType.Kind == CTypeKind.Class && target.Kind == CTypeKind.Class && sourceType.Symbol?.DerivesFrom(target.Symbol!) == true))
        {
            _emitter.RegisterType(target);
            var castCode = $"({_emitter.CTypeName(target)})(void*)ct_checked_cast((ct_object*)(void*){expression.Code}, {_emitter.DescriptorExpression(target)}, {_emitter.SourceArgument(syntax)})";
            return new IrExpressionValue { Type = target, Code = castCode, Prelude = expression.Prelude };
        }
        var code = sourceType.Kind == CTypeKind.Null
            ? target.Kind == CTypeKind.Opaque ? $"({_emitter.CCastType(target)})0" : $"({_emitter.CCastType(target)})NULL"
            : sourceType.IsPointerLike || target.IsPointerLike
                ? $"({_emitter.CCastType(target)})(void*)({expression.Code})"
                : $"({_emitter.CCastType(target)})({expression.Code})";
        return new IrExpressionValue
        {
            Type = target,
            Code = code,
            Prelude = expression.Prelude,
            IsConstant = expression.IsConstant,
            ConstantValue = expression.ConstantValue,
            Ownership = expression.Ownership,
            Symbol = expression.Symbol,
            IsKnownNonNull = expression.IsKnownNonNull,
            KnownLength = target.Kind == CTypeKind.Array ? expression.KnownLength : null,
            OwnedCleanupRecord = expression.OwnedCleanupRecord,
        };
    }

    private IrExpressionValue ConvertFunctionAddress(IrExpressionValue expression, CType target, SyntaxNode syntax)
    {
        RequireUnsafe(syntax);
        if (target.Kind != CTypeKind.FunctionPointer)
        {
            Report("CT2163", $"A method address can convert only to a compatible unmanaged function pointer, not '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var signature = target.FunctionPointer!;
        var matches = expression.MethodGroup!.Candidates.Where(candidate =>
            candidate.IsStatic && candidate.ReturnType == signature.ReturnType &&
            candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(signature.ParameterTypes) &&
            candidate.Parameters.Select(parameter => parameter.PassingKind).SequenceEqual(signature.PassingKinds)).ToArray();
        if (matches.Length != 1)
        {
            Report("CT2163", "Method address is not uniquely compatible with the unmanaged function-pointer signature.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var selected = matches[0];
        CheckAccess(selected, syntax);
        if (selected.ExternName is not null)
        {
            _emitter.RegisterExternUse(selected, syntax);
            return new IrExpressionValue { Type = target, Code = $"&{selected.CName}", Prelude = expression.Prelude };
        }
        var trampoline = _emitter.RegisterFunctionPointerTrampoline(target, selected);
        return new IrExpressionValue { Type = target, Code = $"&{trampoline}", Prelude = expression.Prelude };
    }

    private IrExpressionValue ConvertMethodGroup(IrExpressionValue expression, CType target, SyntaxNode syntax)
    {
        if (target.Kind != CTypeKind.Delegate)
        {
            Report("CT2158", $"A method group can convert only to a compatible named delegate, not '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var group = expression.MethodGroup!;
        var selected = FindDelegateMethod(group, target.Symbol!);
        if (selected is null)
        {
            Report("CT2159", $"Method group is not uniquely compatible with delegate '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        CheckAccess(selected, syntax);
        if (selected.IsUnsafe)
            RequireUnsafe(syntax);
        IrExpressionValue? receiver = group.Receiver;
        if (!selected.IsStatic && receiver is null)
        {
            if (_method.IsStatic)
            {
                Report("CT2115", $"Instance method '{selected.Name}' requires an object.", syntax);
                return ErrorExpression(expression.Prelude);
            }
            receiver = new IrExpressionValue { Type = _method.ContainingType.Type, Code = "ct_self" };
        }
        var prelude = new List<string>(expression.Prelude);
        var targetCode = "NULL";
        if (receiver is not null)
        {
            if (receiver.Type.Kind != CTypeKind.Class)
            {
                Report("CT2161", "Instance delegates require a managed class receiver.", syntax);
                return ErrorExpression(prelude);
            }
            receiver = Materialize(receiver, syntax);
            prelude = new List<string>(receiver.Prelude);
            targetCode = $"(ct_object*)(void*){receiver.Code}";
        }
        var virtualDispatch = !selected.IsStatic && selected.IsVirtual && !group.IsBaseReceiver;
        var thunk = _emitter.RegisterDelegateThunk(target.Symbol!, selected, virtualDispatch);
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"creation of delegate '{target.DisplayName}'");
        var creation = $"{CEmitter.DelegateFactoryName(target.Symbol!)}({targetCode}, &{thunk}, {_emitter.SourceArgument(syntax)})";
        return OwnResult(target, creation, prelude);
    }

    private IrExpressionValue RequireBoolean(IrExpressionValue expression, SyntaxNode syntax)
    {
        if (expression.Type != CType.Bool && !expression.Type.IsError)
            Report("CT2138", $"Condition requires bool, not '{expression.Type.DisplayName}'.", syntax);
        return expression.Type == CType.Bool ? expression : new IrExpressionValue { Type = CType.Bool, Code = "false", Prelude = expression.Prelude };
    }

    private IrExpressionValue Materialize(IrExpressionValue expression, SyntaxNode syntax)
    {
        if (expression.Type.Kind is CTypeKind.Void or CTypeKind.Error || expression.TypeReceiver is not null)
            return expression;
        if (expression.IsConstant && expression.Prelude.Count == 0)
            return expression;
        var prelude = new List<string>(expression.Prelude);
        var temp = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(expression.Type, temp)} = {expression.Code};");
        return new IrExpressionValue
        {
            Type = expression.Type,
            Code = temp,
            Prelude = prelude,
            IsConstant = expression.IsConstant,
            ConstantValue = expression.ConstantValue,
            Ownership = expression.Ownership,
            Symbol = expression.Symbol,
            IsKnownNonNull = expression.IsKnownNonNull,
            KnownLength = expression.KnownLength,
            OwnedCleanupRecord = expression.OwnedCleanupRecord,
        };
    }

    private static bool IsSymbolicConstant(IrExpressionValue expression) => expression.ConstantValue is LayoutConstantValue;

    private static object? SymbolicConstant(params IrExpressionValue[] expressions) =>
        expressions.Any(IsSymbolicConstant) ? new LayoutConstantValue() : null;

    private IrExpressionValue MaterializeReceiver(IrExpressionValue receiver, SyntaxNode syntax)
    {
        var prelude = new List<string>(receiver.Prelude);
        if (receiver.Type.Kind == CTypeKind.Class)
        {
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(receiver.Type, temp)} = {receiver.Code};");
            if (!receiver.IsKnownNonNull)
                prelude.Add($"(void)ct_require_nonnull({temp}, {_emitter.SourceArgument(syntax)});");
            return new IrExpressionValue { Type = receiver.Type, Code = temp, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver, IsKnownNonNull = true, KnownLength = receiver.KnownLength };
        }
        if (receiver.Type.Kind == CTypeKind.Struct)
        {
            if (receiver.LValue?.Address is string address)
                return new IrExpressionValue { Type = receiver.Type, Code = address, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(receiver.Type, temp)} = {receiver.Code};");
            return new IrExpressionValue { Type = receiver.Type, Code = $"&{temp}", Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
        }
        return receiver;
    }

    private void RequireUnsafe(SyntaxNode syntax)
    {
        if (_unsafeDepth == 0)
            Report("CT2139", "Pointer operations require an unsafe method or block.", syntax);
    }

    private void CheckAccess(MemberSymbol member, SyntaxNode syntax)
    {
        CheckAccessibility(member.Accessibility, member, syntax);
    }

    private void CheckAccessibility(Accessibility accessibility, MemberSymbol member, SyntaxNode syntax)
    {
        if (accessibility == Accessibility.Private && member.ContainingType != _method.ContainingType)
            Report("CT1110", $"Member '{member.Name}' is private.", syntax);
        if (accessibility == Accessibility.Protected && member.ContainingType != _method.ContainingType && !_method.ContainingType.DerivesFrom(member.ContainingType))
            Report("CT1113", $"Member '{member.Name}' is protected.", syntax);
    }

    private static IEnumerable<TypeSymbol> Hierarchy(TypeSymbol type)
    {
        var pending = new Stack<TypeSymbol>();
        pending.Push(type);
        var visited = new HashSet<TypeSymbol>();
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            yield return current;
            foreach (var contract in current.Interfaces.AsEnumerable().Reverse())
                pending.Push(contract);
            if (current.BaseType is not null)
                pending.Push(current.BaseType);
        }
    }
    private static string MethodSignatureKey(MethodSymbol method) => $"{method.Name}:{string.Join(',', method.Parameters.Select(parameter => $"{parameter.PassingKind}:{NameMangler.TypeCode(parameter.Type)}"))}:{method.IsStatic}";
}
