using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private IrExpressionValue LowerExpression(ExpressionSyntax syntax)
    {
        var result = syntax switch
        {
            LiteralExpressionSyntax literal => LowerLiteral(literal),
            DefaultExpressionSyntax @default => LowerDefault(@default),
            LambdaExpressionSyntax lambda => new IrExpressionValue { Type = CType.Error, Code = string.Empty, Lambda = lambda, Symbol = lambda },
            NameExpressionSyntax name => LowerName(name, false),
            ThisExpressionSyntax @this => LowerThis(@this),
            BaseExpressionSyntax @base => LowerBase(@base),
            ParenthesizedExpressionSyntax parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionSyntax unary => LowerUnary(unary),
            BinaryExpressionSyntax binary => LowerBinary(binary),
            AssignmentExpressionSyntax assignment => LowerAssignment(assignment),
            MemberAccessExpressionSyntax member => LowerMember(member, false),
            CallExpressionSyntax call => LowerCall(call),
            IndexExpressionSyntax index => LowerIndex(index, false),
            NewExpressionSyntax @new => LowerNew(@new),
            CastExpressionSyntax cast => LowerCast(cast),
            TypeTestExpressionSyntax typeTest => LowerTypeTest(typeTest),
            SafeCastExpressionSyntax safeCast => LowerSafeCast(safeCast),
            StackAllocExpressionSyntax stackAlloc => LowerStackAlloc(stackAlloc),
            SizeOfExpressionSyntax sizeOf => LowerSizeOf(sizeOf),
            AlignOfExpressionSyntax alignOf => LowerAlignOf(alignOf),
            OffsetOfExpressionSyntax offsetOf => LowerOffsetOf(offsetOf),
            _ => ErrorExpression(),
        };
        if (syntax is CallExpressionSyntax && EmitDebugInstrumentation && !_analysisOnly)
            result.Prelude.Insert(0, $"ct_debug_site(UINT32_C({_emitter.RegisterDebugSite(_method, syntax, "call")}));");
        if (_optimizationFacts.KnownNonNullExpressions.Contains(syntax))
            result.IsKnownNonNull = true;
        return RecordSemantic(syntax, result);
    }

    private IrExpressionValue LowerDefault(DefaultExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        if (type.Kind == CTypeKind.Void || type.IsError)
        {
            Report("CT2211", "default(T) requires a complete non-void type.", syntax);
            return ErrorExpression();
        }
        if (type.ContainsPointer)
            RequireUnsafe(syntax);
        _emitter.RegisterType(type);
        return new IrExpressionValue
        {
            Type = type,
            Code = _emitter.DefaultValue(type),
            Ownership = type.ContainsManagedReferences ? OwnershipKind.Owned : OwnershipKind.None,
        };
    }

    private IrExpressionValue LowerSizeOf(SizeOfExpressionSyntax syntax)
    {
        var type = ResolveLayoutOperatorType(syntax.Type, syntax);
        return type.IsError ? ErrorExpression() : LayoutConstant(type, $"((uintptr_t)sizeof({_emitter.CCastType(type)}))", syntax);
    }

    private IrExpressionValue LowerAlignOf(AlignOfExpressionSyntax syntax)
    {
        var type = ResolveLayoutOperatorType(syntax.Type, syntax);
        return type.IsError ? ErrorExpression() : LayoutConstant(type, $"((uintptr_t)CT_ALIGNOF({_emitter.CCastType(type)}))", syntax);
    }

    private IrExpressionValue LowerOffsetOf(OffsetOfExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        if (type.Kind != CTypeKind.Struct || type.Symbol is null)
        {
            Report("CT2189", "offsetof requires a struct or union type.", syntax);
            return ErrorExpression();
        }
        var field = type.Symbol.Fields.FirstOrDefault(candidate => !candidate.IsStatic && candidate.Name == syntax.FieldName);
        if (field is null)
        {
            Report("CT1109", $"Type '{type.DisplayName}' has no directly declared instance field named '{syntax.FieldName}'.", syntax);
            return ErrorExpression();
        }
        CheckAccess(field, syntax);
        if (type.ContainsPointer || field.Type.ContainsPointer)
            RequireUnsafe(syntax);
        _emitter.RegisterType(type);
        _emitter.RegisterType(CType.Nuint);
        return new IrExpressionValue
        {
            Type = CType.Nuint,
            Code = $"((uintptr_t){AggregateLayout.OffsetExpression(type.Symbol, field)})",
            IsConstant = true,
            ConstantValue = new LayoutConstantValue(),
            Symbol = field,
        };
    }

    private CType ResolveLayoutOperatorType(TypeSyntax syntax, SyntaxNode expression)
    {
        var type = ResolveType(syntax);
        var valid = type.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Rune or
            CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Double or
            CTypeKind.Enum or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.FunctionPointer ||
            type.Kind == CTypeKind.Struct && !type.ContainsManagedReferences && !type.ContainsAtomic;
        if (!valid)
        {
            Report("CT2189", $"Layout operators require a complete unmanaged type, not '{type.DisplayName}'.", expression);
            return CType.Error;
        }
        if (type.ContainsPointer)
            RequireUnsafe(expression);
        _emitter.RegisterType(type);
        return type;
    }

    private IrExpressionValue LayoutConstant(CType measuredType, string code, SyntaxNode syntax)
    {
        _emitter.RegisterType(CType.Nuint);
        return new IrExpressionValue
        {
            Type = CType.Nuint,
            Code = code,
            IsConstant = true,
            ConstantValue = new LayoutConstantValue(),
            Symbol = measuredType.Symbol,
        };
    }

    private IrExpressionValue RecordSemantic(ExpressionSyntax syntax, IrExpressionValue expression)
    {
        var symbol = expression.Symbol ?? (object?)expression.LValue?.Local ?? expression.LValue?.Parameter ??
            expression.LValue?.Field ?? expression.LValue?.Property ?? expression.TypeReceiver ?? (object?)expression.MethodGroup;
        var valueCategory = expression.Type.IsError ? BoundValueCategory.Error :
            expression.TypeReceiver is not null ? BoundValueCategory.Type :
            expression.MethodGroup is not null ? BoundValueCategory.MethodGroup :
            expression.LValue is not null ? BoundValueCategory.Variable : BoundValueCategory.Value;
        _semanticEntries[syntax] = new BoundSemanticEntry(
            syntax,
            expression.Type,
            symbol,
            expression.IsConstant ? expression.ConstantValue : null,
            expression.Ownership,
            valueCategory);
        return expression;
    }

    private IrExpressionValue LowerArgument(ArgumentSyntax argument) => argument.PassingKind == ParameterPassingKind.Out
        ? LowerAssignable(argument.Expression)
        : LowerExpression(argument.Expression);

    private IrExpressionValue LowerLiteral(LiteralExpressionSyntax syntax)
    {
        if (syntax.LiteralKind == SyntaxKind.TrueKeyword || syntax.LiteralKind == SyntaxKind.FalseKeyword)
            return Constant(CType.Bool, (bool)syntax.Value!, (bool)syntax.Value! ? "true" : "false");
        if (syntax.LiteralKind == SyntaxKind.NullKeyword)
            return Constant(CType.Null, null, "NULL");
        if (syntax.LiteralKind == SyntaxKind.StringToken)
            return new IrExpressionValue { Type = CType.String, Code = _emitter.RegisterString((string)syntax.Value!), IsConstant = true, ConstantValue = syntax.Value, Ownership = OwnershipKind.Immortal, IsKnownNonNull = true };
        if (syntax.LiteralKind == SyntaxKind.CharacterToken)
            return Constant(CType.Char, syntax.Value, ((byte)syntax.Value!).ToString(CultureInfo.InvariantCulture));
        if (syntax.LiteralKind == SyntaxKind.RuneToken)
            return Constant(CType.Rune, syntax.Value, $"UINT32_C({((uint)syntax.Value!).ToString(CultureInfo.InvariantCulture)})");
        if (syntax.Value is NumericLiteralValue numeric)
        {
            if (numeric.FloatingPoint is double value)
                return numeric.FloatingKind == FloatingLiteralKind.Double
                    ? Constant(CType.Double, value, FormatDouble(value))
                    : Constant(CType.Float, (float)value, FormatFloat((float)value));
            if (numeric.Suffix == IntegerLiteralSuffix.None && numeric.Integer <= int.MaxValue)
                return Constant(CType.Int, (int)numeric.Integer, FormatInt32((int)numeric.Integer));
            if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Unsigned && numeric.Integer <= uint.MaxValue)
                return Constant(CType.Uint, (uint)numeric.Integer, $"UINT32_C({numeric.Integer.ToString(CultureInfo.InvariantCulture)})");
            if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Long && numeric.Integer <= long.MaxValue)
                return Constant(CType.Long, (long)numeric.Integer, FormatInt64((long)numeric.Integer));
            if (numeric.Integer <= ulong.MaxValue)
                return Constant(CType.Ulong, (ulong)numeric.Integer, FormatUInt64((ulong)numeric.Integer));
            Report("CT2112", "Integer literal does not fit the type selected by its suffix.", syntax);
            return Constant(CType.Int, 0, "0");
        }
        return ErrorExpression();
    }

    private IrExpressionValue LowerName(NameExpressionSyntax syntax, bool forWrite)
    {
        if (_method.TypeSubstitutions.TryGetValue(syntax.Name, out var substitution) &&
            substitution.Kind == CTypeKind.Constant && substitution.ConstantValue is { } constantValue &&
            substitution.ElementType is { } constantType)
        {
            if (forWrite)
            {
                Report("CT2202", $"Constant parameter '{syntax.Name}' cannot be assigned.", syntax);
                return ErrorExpression();
            }
            return ConstantGenericValue(constantType, constantValue);
        }
        var local = FindLocal(syntax.Name);
        if (local is not null)
        {
            if (local.Type.ContainsPointer)
                RequireUnsafe(syntax);
            if (!forWrite && !local.IsAssigned)
                Report("CT3108", $"Local '{syntax.Name}' is read before it is assigned.", syntax);
            if (!forWrite && local.NativeResourceState == NativeResourceState.Moved)
                Report("CT1254", $"Owned native resource '{syntax.Name}' is used after ownership moved.", syntax);
            var address = local.IsDurable && local.Type.Kind != CTypeKind.FunctionPointer
                ? $"({_emitter.CTypeName(local.Type)}*)(void*)(uintptr_t)&{local.CName}"
                : $"&{local.CName}";
            return new IrExpressionValue
            {
                Type = local.Type,
                Code = local.ConstantCode ?? local.CName,
                LValue = new IrValueStorage { Store = value => $"{local.CName} = {value}", Address = address, Local = local },
                IsConstant = local.IsConst,
                ConstantValue = local.ConstantValue,
                Ownership = local.NativeResourceState == NativeResourceState.Owned ? OwnershipKind.Owned : local.NativeResourceState is NativeResourceState.Borrowed or NativeResourceState.Deferred ? OwnershipKind.Borrowed : OwnershipKind.None,
                IsKnownNonNull = local.IsKnownNonNull,
                KnownLength = local.KnownLength,
            };
        }
        if (_parameters.TryGetValue(syntax.Name, out var parameter))
        {
            if (parameter.Type.ContainsPointer)
                RequireUnsafe(syntax);
            if (!forWrite && parameter.PassingKind == ParameterPassingKind.Out && !_assignedOutParameters.Contains(parameter))
                Report("CT2174", $"Out parameter '{parameter.Name}' is read before it is assigned.", syntax);
            var name = _durableParameters.TryGetValue(parameter, out var storage)
                ? Durable(storage)
                : NameMangler.Identifier(parameter.Name);
            var byReference = parameter.PassingKind != ParameterPassingKind.Value;
            var code = parameter.Type.IsNativeBuffer
                ? $"({_emitter.CTypeName(parameter.Type)}){{ {name}_data, {name}_length }}"
                : byReference ? $"*({name})" : name;
            return new IrExpressionValue
            {
                Type = parameter.Type,
                Code = code,
                LValue = parameter.Type.IsNativeBuffer ? null : new IrValueStorage { Store = value => $"{code} = {value}", Address = byReference ? name : $"&{name}", Parameter = parameter },
                Symbol = parameter,
                IsKnownNonNull = _method.RuntimeImplementation == RuntimeImplementationRole.Free &&
                    ReferenceEquals(parameter, _method.Parameters[0]),
            };
        }
        var field = Hierarchy(_method.ContainingType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name);
        if (field is not null)
            return LowerField(field, null, syntax, forWrite);
        var property = Hierarchy(_method.ContainingType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name);
        if (property is not null)
            return LowerProperty(property, null, syntax, forWrite);
        var methods = Hierarchy(_method.ContainingType).SelectMany(type => type.Methods)
            .Where(candidate => candidate.Name == syntax.Name && (!_method.IsStatic || candidate.IsStatic))
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
        if (!forWrite && methods.Length != 0)
            return new IrExpressionValue { Type = CType.Error, Code = string.Empty, MethodGroup = new MethodGroupBinding(methods, null, false) };
        var type = _model.ResolveNamedType(syntax.Name, TreeFor(syntax));
        if (type is not null)
            return new IrExpressionValue { Type = CType.Error, Code = string.Empty, TypeReceiver = type };
        Report("CT1107", $"Name '{syntax.Name}' does not exist in the current context.", syntax);
        return ErrorExpression();
    }

    private IrExpressionValue ConstantGenericValue(CType type, BigInteger value)
    {
        var underlying = type.Kind == CTypeKind.Enum ? type.Symbol?.UnderlyingType ?? CType.Int : type;
        object boxed = underlying.Kind switch
        {
            CTypeKind.Byte => (byte)value,
            CTypeKind.Sbyte => (sbyte)value,
            CTypeKind.Short => (short)value,
            CTypeKind.Ushort => (ushort)value,
            CTypeKind.Char => (char)(ushort)value,
            CTypeKind.Int => (int)value,
            CTypeKind.Uint => (uint)value,
            CTypeKind.Long or CTypeKind.Nint => (long)value,
            CTypeKind.Ulong or CTypeKind.Nuint => (ulong)value,
            _ => (int)value,
        };
        var literal = underlying.Kind is CTypeKind.Ulong or CTypeKind.Nuint
            ? FormatUInt64((ulong)value)
            : underlying.Kind == CTypeKind.Uint
                ? $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})"
                : FormatInt64((long)value);
        return Constant(type, boxed, $"({_emitter.CTypeName(type)}){literal}");
    }

    private IrExpressionValue LowerThis(ThisExpressionSyntax syntax)
    {
        if (_method.IsStatic)
        {
            Report("CT2113", "this is not available in a static method.", syntax);
            return ErrorExpression();
        }
        if (_method.ContainingType.Kind == DeclaredTypeKind.Struct)
            return new IrExpressionValue
            {
                Type = _method.ContainingType.Type,
                Code = "(*ct_self)",
                LValue = new IrValueStorage { Store = value => $"*ct_self = {value}", Address = "ct_self" },
            };
        return new IrExpressionValue { Type = _method.ContainingType.Type, Code = "ct_self", IsKnownNonNull = true };
    }

    private IrExpressionValue LowerBase(BaseExpressionSyntax syntax)
    {
        if (_method.IsStatic || _method.ContainingType.Kind != DeclaredTypeKind.Class || _method.ContainingType.BaseType is null)
        {
            Report("CT2150", "base is available only in an instance member of a derived class.", syntax);
            return ErrorExpression();
        }
        var baseType = _method.ContainingType.BaseType;
        return new IrExpressionValue
        {
            Type = baseType.Type,
            Code = $"({NameMangler.Type(baseType)}*)(void*)ct_self",
            IsBaseReceiver = true,
            IsKnownNonNull = true,
        };
    }

    private IrExpressionValue LowerMember(MemberAccessExpressionSyntax syntax, bool forWrite)
    {
        var staticType = TryResolveTypeExpression(syntax.Receiver);
        if (staticType is not null)
        {
            if (staticType.FullName == "System.Runtime.Target")
            {
                if (_emitter.Architecture == CompilationArchitecture.Auto && syntax.Name is "Architecture" or "PointerSize")
                {
                    Report("CT4108", "The target architecture could not be resolved before evaluating this target query.", syntax);
                    return ErrorExpression();
                }
                if (syntax.Name == "Profile")
                {
                    var profile = _emitter.Target switch
                    {
                        CompilationTarget.Hosted => 0,
                        CompilationTarget.EspIdf => 1,
                        CompilationTarget.Freestanding => 2,
                        CompilationTarget.Cosmopolitan => 3,
                        _ => 0,
                    };
                    return Constant(_model.Types["System.Runtime.TargetProfile"].Type, profile,
                        profile.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (syntax.Name == "Environment")
                {
                    var environment = _emitter.Environment == TargetEnvironment.Qemu ? 1 : 0;
                    return Constant(_model.Types["System.Runtime.TargetEnvironment"].Type, environment,
                        environment.ToString(CultureInfo.InvariantCulture));
                }
                if (syntax.Name == "Architecture")
                    return Constant(_model.Types["System.Runtime.TargetArchitecture"].Type,
                        (int)_emitter.Architecture - 1, ((int)_emitter.Architecture - 1).ToString(CultureInfo.InvariantCulture));
                if (syntax.Name == "PointerSize")
                {
                    var size = _emitter.Architecture is CompilationArchitecture.X64 or CompilationArchitecture.Arm64 or CompilationArchitecture.RiscV64 ? 8 : 4;
                    return Constant(CType.Int, size, size.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (staticType.Kind == DeclaredTypeKind.Enum)
            {
                var enumValue = staticType.EnumValues.FirstOrDefault(value => value.Name == syntax.Name);
                if (enumValue is not null)
                    return Constant(staticType.Type, enumValue.Value, NameMangler.Identifier(staticType.FullName + "." + enumValue.Name), enumValue);
            }
            var field = Hierarchy(staticType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic);
            if (field is not null)
                return LowerField(field, null, syntax, forWrite);
            var property = Hierarchy(staticType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic);
            if (property is not null)
                return LowerProperty(property, null, syntax, forWrite);
            var methods = Hierarchy(staticType).SelectMany(type => type.Methods)
                .Where(candidate => candidate.Name == syntax.Name && candidate.IsStatic)
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
            if (!forWrite && methods.Length != 0)
                return new IrExpressionValue { Type = CType.Error, Code = string.Empty, MethodGroup = new MethodGroupBinding(methods, null, false) };
            Report("CT1108", $"Type '{staticType.FullName}' has no static member named '{syntax.Name}'.", syntax);
            return ErrorExpression();
        }

        var receiver = LowerExpression(syntax.Receiver);
        if (receiver.Type.Kind == CTypeKind.String && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            if (!receiver.IsKnownNonNull)
            {
                RecordRuntimeFault(syntax, "dynamic string null check");
                receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            }
            return new IrExpressionValue { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        if (receiver.Type.Kind == CTypeKind.Array && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            if (!receiver.IsKnownNonNull)
            {
                RecordRuntimeFault(syntax, "dynamic array null check");
                receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            }
            return new IrExpressionValue { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        if (receiver.Type.Kind == CTypeKind.InlineArray && syntax.Name == "Length")
            return Constant(CType.Int, receiver.Type.InlineArrayLength, receiver.Type.InlineArrayLength.ToString(CultureInfo.InvariantCulture));
        if (receiver.Type.IsNativeBuffer && syntax.Name is "Length" or "Pointer")
        {
            RequireUnsafe(syntax);
            receiver = Materialize(receiver, syntax.Receiver);
            return syntax.Name == "Length"
                ? new IrExpressionValue { Type = CType.Nuint, Code = $"(uintptr_t){receiver.Code}.Length", Prelude = receiver.Prelude }
                : new IrExpressionValue { Type = new CType(CTypeKind.Pointer, ElementType: receiver.Type.ElementType), Code = $"{receiver.Code}.Data", Prelude = receiver.Prelude };
        }
        if (receiver.Type.IsNativeUtf8String && syntax.Name is "ByteLength" or "Pointer")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            if (syntax.Name == "Pointer")
                RequireUnsafe(syntax);
            return syntax.Name == "ByteLength"
                ? new IrExpressionValue { Type = CType.Nuint, Code = $"(uintptr_t){receiver.Code}.ByteLength", Prelude = receiver.Prelude }
                : new IrExpressionValue { Type = new CType(CTypeKind.Pointer, ElementType: CType.Byte), Code = $"(uint8_t*)(void*){receiver.Code}.Data", Prelude = receiver.Prelude };
        }
        var type = receiver.Type.Symbol;
        if (type is null && (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array || receiver.Type.IsValueType))
            type = _model.Types.GetValueOrDefault("System.Object");
        if (type is null)
        {
            Report("CT2114", $"Type '{receiver.Type.DisplayName}' has no members.", syntax);
            return ErrorExpression(receiver.Prelude);
        }
        var instanceField = Hierarchy(type).SelectMany(candidateType => candidateType.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic);
        if (instanceField is not null)
            return LowerField(instanceField, receiver, syntax, forWrite);
        var instanceProperty = Hierarchy(type).SelectMany(candidateType => candidateType.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic);
        if (instanceProperty is not null)
            return LowerProperty(instanceProperty, receiver, syntax, forWrite);
        var instanceMethods = Hierarchy(type).SelectMany(candidateType => candidateType.Methods)
            .Where(candidate => candidate.Name == syntax.Name && !candidate.IsStatic)
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
        if (!forWrite && instanceMethods.Length != 0)
            return new IrExpressionValue { Type = CType.Error, Code = string.Empty, Prelude = receiver.Prelude, MethodGroup = new MethodGroupBinding(instanceMethods, receiver, receiver.IsBaseReceiver) };
        Report("CT1109", $"Type '{type.FullName}' has no instance member named '{syntax.Name}'.", syntax);
        return ErrorExpression(receiver.Prelude);
    }

    private IrExpressionValue LowerField(FieldSymbol field, IrExpressionValue? receiver, SyntaxNode syntax, bool forWrite)
    {
        var isConstInitStorage = field.IsConstInit || receiver?.IsConstInitStorage == true;
        if (field.Type.ContainsPointer || field.ExternName is not null || field.LinkerSymbolName is not null)
            RequireUnsafe(syntax);
        CheckAccess(field, syntax);
        if (field.LinkerSymbolName is not null)
        {
            if (forWrite)
            {
                Report("CT1296", $"Linker symbol '{field.Name}' is address-valued and cannot be assigned.", syntax);
                return ErrorExpression();
            }
            var address = $"(uintptr_t)(void*){field.LinkerSymbolName}";
            var linkerCode = field.Type.Kind == CTypeKind.Pointer
                ? $"({_emitter.CTypeName(field.Type)})(void*){field.LinkerSymbolName}"
                : $"({_emitter.CTypeName(field.Type)})({address})";
            return new IrExpressionValue { Type = field.Type, Code = linkerCode, Symbol = field };
        }
        if (field.IsRegister)
        {
            RequireUnsafe(syntax);
            if (field.RegisterAddress is null)
            {
                Report("CT2210", $"Register address for '{field.Name}' is unresolved.", syntax);
                return ErrorExpression();
            }
            var address = $"((uintptr_t)UINT64_C(0x{field.RegisterAddress.Value:X}))";
            var ctype = _emitter.CTypeName(field.Type);
            IrValueStorage? storage = field.IsReadonly ? null : new IrValueStorage
            {
                Field = field,
                Store = value => $"ct_mmio_barrier(); *(volatile {ctype}*)(uintptr_t){address} = ({ctype})({value}); ct_mmio_barrier()",
            };
            if (forWrite)
                return new IrExpressionValue { Type = field.Type, Code = $"*(volatile {ctype}*)(uintptr_t){address}", LValue = storage, Symbol = field };
            var registerPrelude = new List<string> { "ct_mmio_barrier();" };
            var temporary = NewTemp();
            registerPrelude.Add($"{ctype} {temporary} = *(volatile {ctype}*)(uintptr_t){address};");
            registerPrelude.Add("ct_mmio_barrier();");
            return new IrExpressionValue { Type = field.Type, Code = temporary, Prelude = registerPrelude, LValue = storage, Symbol = field };
        }
        if (field.IsBitView)
        {
            if (receiver is null)
            {
                Report("CT2209", $"Bit view '{field.Name}' requires a bitfield value.", syntax);
                return ErrorExpression();
            }
            var bitPrelude = new List<string>(receiver.Prelude);
            var first = field.BitFirst!.Value;
            var width = field.BitLast!.Value - first + 1;
            var mask = width == 64 ? "UINT64_MAX" : $"UINT64_C(0x{((BigInteger.One << width) - BigInteger.One):X})";
            var backing = field.ContainingType.BitFieldBackingType!;
            var raw = $"((uint64_t)({_emitter.CTypeName(backing)})({receiver.Code}))";
            var extracted = $"(({raw} >> {first}) & {mask})";
            var read = field.Type == CType.Bool ? $"({extracted} != UINT64_C(0))" : $"({_emitter.CTypeName(field.Type)})({extracted})";
            IrValueStorage? storage = null;
            if (!field.IsReadonly && receiver.LValue is not null)
            {
                var registerField = receiver.Symbol as FieldSymbol;
                if (forWrite && registerField?.IsRegister == true && bitPrelude.LastOrDefault() == "ct_mmio_barrier();")
                    bitPrelude.RemoveAt(bitPrelude.Count - 1);
                storage = new IrValueStorage
                {
                    Field = field,
                    IsConstInitStorage = receiver.IsConstInitStorage,
                    Store = value =>
                    {
                        var updated = $"({_emitter.CTypeName(receiver.Type)})(({raw} & ~({mask} << {first})) | ((((uint64_t)({value})) & {mask}) << {first}))";
                        if (registerField?.RegisterAddress is { } registerAddress)
                            return $"*(volatile {_emitter.CTypeName(receiver.Type)}*)(uintptr_t)((uintptr_t)UINT64_C(0x{registerAddress:X})) = {updated}; ct_mmio_barrier()";
                        return receiver.LValue.Store(updated);
                    },
                };
            }
            return new IrExpressionValue { Type = field.Type, Code = read, Prelude = bitPrelude, LValue = storage, Symbol = field, IsConstInitStorage = receiver.IsConstInitStorage };
        }
        if (!forWrite && field.IsConst && field.Initializer is not null)
        {
            if (!_constantFieldsBeingEvaluated.Add(field))
            {
                Report("CT2144", $"Const field '{field.Name}' has a circular initializer.", field.Initializer);
                return ErrorExpression();
            }
            var constant = Convert(LowerExpression(field.Initializer), field.Type, field.Initializer, false);
            _constantFieldsBeingEvaluated.Remove(field);
            if (!constant.IsConstant)
                Report("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer);
            else
                return constant;
        }
        string code;
        var prelude = new List<string>();
        if (field.IsStatic)
            code = field.CName;
        else
        {
            if (_method.IsStatic && receiver is null)
            {
                Report("CT2115", $"Instance field '{field.Name}' requires an object.", syntax);
                return ErrorExpression();
            }
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new IrExpressionValue { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new IrValueStorage { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new IrExpressionValue { Type = _method.ContainingType.Type, Code = "ct_self", IsKnownNonNull = true };
            var loweredReceiver = MaterializeReceiver(receiver, syntax);
            prelude.AddRange(loweredReceiver.Prelude);
            code = $"(({NameMangler.Type(field.ContainingType)}*)(void*){loweredReceiver.Code})->{field.CAccessPath}";
        }
        if (field.IsVolatile)
        {
            var address = $"(void*)&({code})";
            return new IrExpressionValue
            {
                Type = field.Type,
                Code = AtomicFromBits(field.Type, $"ct_atomic_scalar_load({address}, sizeof({code}), 1)"),
                Prelude = prelude,
                Symbol = field,
                LValue = new IrValueStorage
                {
                    Store = value => $"ct_atomic_scalar_store({address}, sizeof({code}), {AtomicToBits(field.Type, value)}, 2)",
                    Field = field,
                    IsConstInitStorage = isConstInitStorage,
                },
                IsConstInitStorage = isConstInitStorage,
            };
        }
        return new IrExpressionValue
        {
            Type = field.Type,
            Code = code,
            Prelude = prelude,
            IsConstant = field.IsConst,
            LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = field.ContainingType.HasNonNaturalLayout ? null : $"&({code})", Field = field, IsConstInitStorage = isConstInitStorage },
            Symbol = field,
            IsConstInitStorage = isConstInitStorage,
        };
    }

    private string AtomicToBits(CType type, string value) => type.Kind == CTypeKind.Pointer
        ? $"(uint64_t)(uintptr_t)(void*)({value})"
        : $"(uint64_t)({_emitter.CTypeName(type)})({value})";

    private string AtomicFromBits(CType type, string value) => type.Kind == CTypeKind.Pointer
        ? $"({_emitter.CTypeName(type)})(uintptr_t)({value})"
        : $"({_emitter.CTypeName(type)})({value})";

    private IrExpressionValue LowerProperty(PropertySymbol property, IrExpressionValue? receiver, SyntaxNode syntax, bool forWrite)
    {
        if (property.Type.ContainsPointer)
            RequireUnsafe(syntax);
        CheckAccess(property, syntax);
        if (property.Syntax is PropertyDeclarationSyntax propertySyntax && propertySyntax.Modifiers.Contains("unsafe", StringComparer.Ordinal))
            RequireUnsafe(syntax);
        CheckAccessibility(forWrite ? property.SetterAccessibility : property.GetterAccessibility, property, syntax);
        if (property.ContainingType.FullName == "Esp.Idf.EspError" && property.Name is "Code" or "IsSuccess")
        {
            if (forWrite)
                Report("CT1266", $"Property '{property.Name}' is read-only.", syntax);
            receiver ??= new IrExpressionValue { Type = property.ContainingType.Type, Code = "(*ct_self)" };
            receiver = Materialize(receiver, syntax);
            return property.Name == "Code"
                ? new IrExpressionValue { Type = CType.Int, Code = $"(int32_t){receiver.Code}", Prelude = receiver.Prelude, Symbol = property }
                : new IrExpressionValue { Type = CType.Bool, Code = $"({receiver.Code} == ESP_OK)", Prelude = receiver.Prelude, Symbol = property };
        }
        var prelude = new List<string>();
        string receiverArgument = string.Empty;
        var baseReceiver = receiver?.IsBaseReceiver == true;
        if (!property.IsStatic)
        {
            if (_method.IsStatic && receiver is null)
            {
                Report("CT2116", $"Instance property '{property.Name}' requires an object.", syntax);
                return ErrorExpression();
            }
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new IrExpressionValue { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new IrValueStorage { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new IrExpressionValue { Type = _method.ContainingType.Type, Code = "ct_self" };
            var loweredReceiver = MaterializeReceiver(receiver, syntax);
            prelude.AddRange(loweredReceiver.Prelude);
            receiverArgument = loweredReceiver.Code;
        }
        if (!forWrite && property.Getter is null)
            Report("CT2117", $"Property '{property.Name}' has no getter.", syntax);
        var selectedAccessor = forWrite
            ? property.Setter is null ? null : _emitter.GetAccessorMethod(property, getter: false)
            : property.Getter is null ? null : _emitter.GetAccessorMethod(property, getter: true);
        if (selectedAccessor is not null)
            _emitter.Effects.RecordCall(_method, selectedAccessor, syntax, property.IsVirtual && !baseReceiver);
        var typedReceiver = property.IsStatic ? string.Empty : $"({NameMangler.Type(property.ContainingType)}*)(void*){receiverArgument}";
        var objectReceiver = property.IsStatic ? string.Empty : $"((ct_object*)(void*){receiverArgument})";
        var getterCode = property.Getter is null
            ? _emitter.DefaultValue(property.Type)
            : property.IsVirtual && !baseReceiver
                ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualGetterSlotName(property)}({objectReceiver})"
                : $"{NameMangler.Getter(property)}({typedReceiver})";
        var result = new IrExpressionValue
        {
            Type = property.Type,
            Code = getterCode,
            Prelude = prelude,
            Symbol = property,
            LValue = property.Setter is null ? null : new IrValueStorage
            {
                Store = value => property.IsVirtual && !baseReceiver
                    ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualSetterSlotName(property)}({objectReceiver}, {value})"
                    : $"{NameMangler.Setter(property)}({(property.IsStatic ? string.Empty : typedReceiver + ", ")}{value})",
                Field = property.BackingField,
                Property = property,
                IsBaseReceiver = baseReceiver,
                IsConstInitStorage = receiver?.IsConstInitStorage == true,
            },
            IsConstInitStorage = receiver?.IsConstInitStorage == true,
        };
        return !forWrite && property.Type.ContainsManagedReferences
            ? OwnResult(property.Type, getterCode, prelude)
            : result;
    }

    private IrExpressionValue LowerIndex(IndexExpressionSyntax syntax, bool forWrite)
    {
        var loweredReceiver = LowerExpression(syntax.Receiver);
        // Inline arrays are values, but indexing addressable storage must keep
        // the original lvalue instead of indexing a materialized copy.
        var receiver = loweredReceiver.Type.Kind == CTypeKind.InlineArray && loweredReceiver.LValue is not null
            ? loweredReceiver
            : Materialize(loweredReceiver, syntax.Receiver);
        var indexer = receiver.Type.Symbol is null
            ? null
            : Hierarchy(receiver.Type.Symbol).SelectMany(type => type.Properties)
                .FirstOrDefault(property => property.IndexParameter is not null);
        var indexType = indexer?.IndexParameter?.Type ?? (receiver.Type.IsNativeBuffer ? CType.Nuint : CType.Int);
        var index = Materialize(Convert(LowerExpression(syntax.Index), indexType, syntax.Index, false), syntax.Index);
        var prelude = new List<string>(receiver.Prelude);
        prelude.AddRange(index.Prelude);
        if (indexer is not null)
        {
            CheckAccess(indexer, syntax);
            CheckAccessibility(forWrite ? indexer.SetterAccessibility : indexer.GetterAccessibility, indexer, syntax);
            if (forWrite && indexer.Setter is null)
                Report("CT1266", "Indexer is read-only.", syntax);
            if (!forWrite && indexer.Getter is null)
                Report("CT2117", "Indexer has no getter.", syntax);
            var accessor = forWrite
                ? indexer.Setter is null ? null : _emitter.GetAccessorMethod(indexer, getter: false)
                : indexer.Getter is null ? null : _emitter.GetAccessorMethod(indexer, getter: true);
            if (accessor is not null)
                _emitter.Effects.RecordCall(_method, accessor, syntax, indexer.IsVirtual && !receiver.IsBaseReceiver);
            var loweredReceiverValue = MaterializeReceiver(receiver, syntax.Receiver);
            prelude = [.. loweredReceiverValue.Prelude, .. index.Prelude];
            var typedReceiver = $"({NameMangler.Type(indexer.ContainingType)}*)(void*){loweredReceiverValue.Code}";
            var objectReceiver = $"((ct_object*)(void*){loweredReceiverValue.Code})";
            var getterCode = indexer.Getter is null
                ? _emitter.DefaultValue(indexer.Type)
                : indexer.IsVirtual && !receiver.IsBaseReceiver
                    ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualGetterSlotName(indexer)}({objectReceiver}, {index.Code})"
                    : $"{NameMangler.Getter(indexer)}({typedReceiver}, {index.Code})";
            var result = new IrExpressionValue
            {
                Type = indexer.Type,
                Code = getterCode,
                Prelude = prelude,
                Symbol = indexer,
                LValue = indexer.Setter is null ? null : new IrValueStorage
                {
                    Store = value => indexer.IsVirtual && !receiver.IsBaseReceiver
                        ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualSetterSlotName(indexer)}({objectReceiver}, {index.Code}, {value})"
                        : $"{NameMangler.Setter(indexer)}({typedReceiver}, {index.Code}, {value})",
                    Property = indexer,
                },
            };
            return !forWrite && indexer.Type.ContainsManagedReferences
                ? OwnResult(indexer.Type, getterCode, prelude)
                : result;
        }
        if (receiver.Type.Kind == CTypeKind.Array)
        {
            if (!receiver.IsKnownNonNull)
            {
                RecordRuntimeFault(syntax, "dynamic array null check");
                prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            }
            if (!IsProvenInBounds(receiver, index))
            {
                RecordRuntimeFault(syntax, "dynamic array bounds check");
                prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            }
            var code = $"{receiver.Code}->Data[{index.Code}]";
            return new IrExpressionValue
            {
                Type = receiver.Type.ElementType!,
                Code = code,
                Prelude = prelude,
                LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})", IsConstInitStorage = receiver.IsConstInitStorage },
                IsConstInitStorage = receiver.IsConstInitStorage,
            };
        }
        if (receiver.Type.Kind == CTypeKind.InlineArray)
        {
            var length = receiver.Type.InlineArrayLength;
            var constantIndex = index.ConstantValue switch
            {
                int signed => (long)signed,
                uint unsigned => unsigned,
                _ => long.MinValue,
            };
            if (constantIndex != long.MinValue && (constantIndex < 0 || constantIndex >= length))
                Report("CT2204", $"Inline-array index {constantIndex} is outside the range 0..{length - 1}.", syntax.Index);
            else if (constantIndex == long.MinValue)
            {
                RecordRuntimeFault(syntax, "dynamic inline-array bounds check");
                prelude.Add($"ct_bounds({index.Code}, {length}, {_emitter.SourceArgument(syntax)});");
            }
            var code = $"{receiver.Code}.Data[{index.Code}]";
            return new IrExpressionValue
            {
                Type = receiver.Type.ElementType!,
                Code = code,
                Prelude = prelude,
                LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})", IsConstInitStorage = receiver.IsConstInitStorage },
                IsConstInitStorage = receiver.IsConstInitStorage,
            };
        }
        if (receiver.Type.Kind == CTypeKind.String)
        {
            if (!receiver.IsKnownNonNull)
            {
                RecordRuntimeFault(syntax, "dynamic string null check");
                prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            }
            RecordRuntimeFault(syntax, "dynamic string bounds check");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            return new IrExpressionValue { Type = CType.Char, Code = $"{receiver.Code}->Data[{index.Code}]", Prelude = prelude };
        }
        if (receiver.Type.IsNativeBuffer)
        {
            RequireUnsafe(syntax);
            RecordRuntimeFault(syntax, "dynamic native-buffer bounds check");
            prelude.Add($"ct_native_bounds((size_t){index.Code}, {receiver.Code}.Length, {_emitter.SourceArgument(syntax)});");
            var code = $"{receiver.Code}.Data[(size_t){index.Code}]";
            var writable = receiver.Type.Kind == CTypeKind.NativeBuffer;
            if (forWrite && !writable)
                Report("CT2179", "ReadOnlyNativeBuffer<T> indexing is read-only.", syntax);
            return new IrExpressionValue
            {
                Type = receiver.Type.ElementType!,
                Code = code,
                Prelude = prelude,
                LValue = writable ? new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})", IsConstInitStorage = receiver.IsConstInitStorage } : null,
                IsConstInitStorage = receiver.IsConstInitStorage,
            };
        }
        if (receiver.Type.Kind == CTypeKind.Pointer)
        {
            RequireUnsafe(syntax);
            if (receiver.Type.ElementType == CType.Void)
            {
                Report("CT2180", "void* cannot be indexed.", syntax);
                return ErrorExpression(prelude);
            }
            var code = $"{receiver.Code}[{index.Code}]";
            return new IrExpressionValue { Type = receiver.Type.ElementType!, Code = code, Prelude = prelude, LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})" } };
        }
        Report("CT2118", $"Type '{receiver.Type.DisplayName}' cannot be indexed.", syntax.Receiver);
        return ErrorExpression(prelude);
    }

    private IrExpressionValue LowerNew(NewExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        if (type.ContainsPointer)
            RequireUnsafe(syntax);
        if (syntax.ArrayLength is not null)
        {
            if (type.Kind != CTypeKind.Array)
                return ErrorExpression();
            _emitter.RegisterType(type);
            _emitter.Effects.RecordAllocation(_method, syntax, "array construction");
            var length = Materialize(Convert(LowerExpression(syntax.ArrayLength), CType.Int, syntax.ArrayLength, false), syntax.ArrayLength);
            var code = $"ct_new_{NameMangler.Array(type.ElementType!)}({length.Code}, {_emitter.SourceArgument(syntax)})";
            var result = OwnResult(type, code, length.Prelude);
            if (length.IsConstant && length.ConstantValue is int constantLength && constantLength >= 0)
                result.KnownLength = constantLength;
            return result;
        }
        if (type.IsNativeBuffer)
        {
            RequireUnsafe(syntax);
            _emitter.RegisterType(type);
            if (syntax.Arguments.Length != 2 || syntax.Arguments.Any(argument => argument.PassingKind != ParameterPassingKind.Value))
            {
                Report("CT2181", "Native-buffer construction requires a pointer and a length.", syntax);
                return ErrorExpression();
            }
            var pointerType = new CType(CTypeKind.Pointer, ElementType: type.ElementType);
            var pointer = Materialize(Convert(LowerExpression(syntax.Arguments[0].Expression), pointerType, syntax.Arguments[0], false), syntax.Arguments[0]);
            var length = Materialize(Convert(LowerExpression(syntax.Arguments[1].Expression), CType.Nuint, syntax.Arguments[1], false), syntax.Arguments[1]);
            var prelude = new List<string>(pointer.Prelude); prelude.AddRange(length.Prelude);
            return new IrExpressionValue { Type = type, Code = $"({_emitter.CTypeName(type)}){{ {pointer.Code}, (size_t){length.Code} }}", Prelude = prelude };
        }
        if (type.Kind is not CTypeKind.Class and not CTypeKind.Struct)
        {
            Report("CT2119", $"new cannot construct '{type.DisplayName}'.", syntax);
            return ErrorExpression();
        }
        if (type.Symbol!.IsAbstract)
        {
            Report("CT1276", $"Abstract class '{type.DisplayName}' cannot be constructed.", syntax);
            return ErrorExpression();
        }
        var arguments = syntax.Arguments.Select(LowerArgument).ToArray();
        var constructor = SelectOverload(type.Symbol!.Constructors, type.Symbol.Name, arguments, syntax.Arguments, syntax);
        if (constructor is null)
            return ErrorExpression(arguments.SelectMany(argument => argument.Prelude));
        CheckAccess(constructor, syntax);
        if (constructor.IsUnsafe)
            RequireUnsafe(syntax);
        _emitter.Effects.RecordCall(_method, constructor, syntax, requiresContract: false);
        if (type.Kind == CTypeKind.Class)
            _emitter.Effects.RecordAllocation(_method, syntax, $"construction of class '{type.DisplayName}'");
        var lowered = LowerArguments(arguments, constructor.Parameters, syntax.Arguments);
        var construction = $"{constructor.CName}({string.Join(", ", lowered.Codes)})";
        return type.ContainsManagedReferences ? OwnResult(type, construction, lowered.Prelude, symbol: constructor) : new IrExpressionValue { Type = type, Code = construction, Prelude = lowered.Prelude, Symbol = constructor };
    }

    private IrExpressionValue LowerStackAlloc(StackAllocExpressionSyntax syntax)
    {
        RequireUnsafe(syntax);
        if (_repeatableLoopDepth > 0)
            Report("CT2182", "stackalloc is not permitted lexically inside a loop.", syntax);
        var element = ResolveType(syntax.ElementType);
        var type = new CType(CTypeKind.NativeBuffer, ElementType: element);
        _emitter.RegisterType(type);
        var count = LowerExpression(syntax.Count);
        if (count.Type.Kind is not CTypeKind.Int and not CTypeKind.Nuint)
        {
            Report("CT2183", "A stackalloc count must have type int or nuint.", syntax.Count);
            return ErrorExpression(count.Prelude);
        }
        if (count.IsConstant && count.ConstantValue is int signed && signed < 0)
            Report("CT2184", "A stackalloc count cannot be negative.", syntax.Count);
        count = Materialize(count, syntax.Count);
        var prelude = new List<string>(count.Prelude);
        var constantSizeIsSafe = TryGetConstantStackAllocationCount(count, element, out _);
        if (count.Type == CType.Int && !constantSizeIsSafe)
        {
            RecordRuntimeFault(syntax, "dynamic stack allocation count check");
            prelude.Add($"if ({count.Code} < 0) ct_raise_runtime_fault(CT_FAULT_OVERFLOW, \"CTB0002\", {_emitter.SourceArgument(syntax)});");
        }
        var bytes = constantSizeIsSafe
            ? $"((size_t){count.Code} * sizeof({_emitter.CTypeName(element)}))"
            : $"ct_stack_bytes((size_t){count.Code}, sizeof({_emitter.CTypeName(element)}), {_emitter.SourceArgument(syntax)})";
        if (!constantSizeIsSafe)
            RecordRuntimeFault(syntax, "dynamic stack allocation size check");
        var pointer = $"((size_t){count.Code} == 0u ? NULL : ({_emitter.CTypeName(element)}*)CT_ALLOCA({bytes}))";
        return new IrExpressionValue { Type = type, Code = $"({_emitter.CTypeName(type)}){{ {pointer}, (size_t){count.Code} }}", Prelude = prelude };
    }

    private static bool TryGetConstantStackAllocationCount(IrExpressionValue count, CType element, out ulong value)
    {
        value = count.ConstantValue switch
        {
            int signed when signed >= 0 => (ulong)signed,
            uint unsigned => unsigned,
            ulong nativeUnsigned => nativeUnsigned,
            _ => ulong.MaxValue,
        };
        if (value == ulong.MaxValue)
            return false;
        var maximumElementSize = element.Kind switch
        {
            CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Char => 1UL,
            CTypeKind.Short or CTypeKind.Ushort => 2UL,
            CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float => 4UL,
            CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Double => 8UL,
            CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Pointer or CTypeKind.FunctionPointer => 8UL,
            _ => 0UL,
        };
        return maximumElementSize != 0UL && value <= uint.MaxValue / maximumElementSize;
    }

    private static bool IsProvenInBounds(IrExpressionValue receiver, IrExpressionValue index)
    {
        if (receiver.KnownLength is not int length)
            return false;
        var value = index.ConstantValue switch
        {
            int signed when signed >= 0 => (ulong)signed,
            uint unsigned => unsigned,
            _ => ulong.MaxValue,
        };
        return value != ulong.MaxValue && value < (ulong)length;
    }

    private TypeSymbol? TryResolveTypeExpression(ExpressionSyntax expression)
    {
        var parts = new Stack<string>();
        var current = expression;
        ImmutableArray<TypeSyntax> typeArguments = [];
        while (current is MemberAccessExpressionSyntax member)
        {
            if (!member.TypeArguments.IsDefaultOrEmpty)
                typeArguments = member.TypeArguments;
            parts.Push(member.Name);
            current = member.Receiver;
        }
        if (current is not NameExpressionSyntax name)
            return null;
        if (!name.TypeArguments.IsDefaultOrEmpty)
            typeArguments = name.TypeArguments;
        parts.Push(name.Name);
        var qualified = string.Join('.', parts);
        if (!typeArguments.IsDefaultOrEmpty)
        {
            var typeSyntax = new TypeSyntax(expression.Source, expression.Span, qualified, TypeArguments: typeArguments);
            return ResolveType(typeSyntax).Symbol;
        }
        return _model.ResolveNamedType(qualified, TreeFor(expression));
    }

    private IrExpressionValue LowerCall(CallExpressionSyntax syntax, bool captureForDefer = false)
    {
        if (syntax.Target is MemberAccessExpressionSyntax { Name: "HasFeature" } featureMember &&
            TryResolveTypeExpression(featureMember.Receiver)?.FullName == "System.Runtime.Target")
        {
            if (syntax.Arguments.Length != 1)
            {
                Report("CT2122", "Target.HasFeature requires exactly one CpuFeature argument.", syntax);
                return ErrorExpression();
            }
            var argument = LowerExpression(syntax.Arguments[0].Expression);
            if (argument.Type.Symbol?.FullName != "System.Runtime.CpuFeature" || !argument.IsConstant || !TryIntegralConstant(argument.ConstantValue, out var numeric) || numeric < 0 || numeric > int.MaxValue)
            {
                Report("CT2225", "Target.HasFeature requires a compile-time CpuFeature value.", syntax.Arguments[0]);
                return ErrorExpression(argument.Prelude);
            }
            var enabled = _emitter.HasCpuFeature((CpuFeature)(int)numeric);
            return new IrExpressionValue { Type = CType.Bool, Code = enabled ? "true" : "false", Prelude = argument.Prelude, IsConstant = true, ConstantValue = enabled };
        }
        var possibleDelegate = syntax.Target switch
        {
            NameExpressionSyntax delegateName when IsCallablePointer(FindLocal(delegateName.Name)?.Type) ||
                                                   IsCallablePointer(_parameters.GetValueOrDefault(delegateName.Name)?.Type) ||
                                                   Hierarchy(_method.ContainingType).SelectMany(type => type.Fields).Any(field => field.Name == delegateName.Name && IsCallablePointer(field.Type)) ||
                                                   Hierarchy(_method.ContainingType).SelectMany(type => type.Properties).Any(property => property.Name == delegateName.Name && IsCallablePointer(property.Type))
                => LowerName(delegateName, false),
            MemberAccessExpressionSyntax member => TryLowerDelegateMember(member),
            _ => null,
        };
        if (possibleDelegate?.Type.Kind == CTypeKind.Delegate)
            return LowerDelegateInvocation(syntax, possibleDelegate);
        if (possibleDelegate?.Type.Kind == CTypeKind.FunctionPointer)
            return LowerFunctionPointerInvocation(syntax, possibleDelegate);

        TypeSymbol? containingType = null;
        IrExpressionValue? receiver = null;
        string methodName;
        bool requireStatic;
        if (syntax.Target is NameExpressionSyntax name)
        {
            containingType = _method.ContainingType;
            methodName = name.Name;
            requireStatic = _method.IsStatic;
        }
        else if (syntax.Target is MemberAccessExpressionSyntax member)
        {
            var staticType = TryResolveTypeExpression(member.Receiver);
            if (staticType is not null)
            {
                containingType = staticType;
                methodName = member.Name;
                requireStatic = true;
            }
            else
            {
                receiver = LowerExpression(member.Receiver);
                if (member.Name == "ToString" && SupportsBuiltInToString(receiver.Type))
                    return LowerBuiltInToString(syntax, member, receiver, captureForDefer);
                containingType = receiver.Type.Symbol;
                if (containingType is null && (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array || receiver.Type.IsValueType))
                    containingType = _model.Types.GetValueOrDefault("System.Object");
                methodName = member.Name;
                requireStatic = false;
            }
        }
        else
        {
            Report("CT2120", "Only methods, delegates, and function pointers can be called in draft 0.7.", syntax.Target);
            return ErrorExpression();
        }

        if (containingType is null)
        {
            Report("CT2121", "The call receiver does not declare methods.", syntax.Target);
            return ErrorExpression(receiver?.Prelude);
        }

        var arguments = syntax.Arguments.Select(LowerArgument).ToArray();
        var explicitTypeArguments = syntax.Target switch
        {
            NameExpressionSyntax targetName => targetName.TypeArguments,
            MemberAccessExpressionSyntax targetMember => targetMember.TypeArguments,
            _ => ImmutableArray<TypeSyntax>.Empty,
        };
        var candidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.ExplicitInterfaceType is null && method.Name == methodName && method.IsStatic == requireStatic)
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (!requireStatic && receiver is not null && receiver.Type.IsValueType)
            candidates = containingType.Methods.Where(method => !method.IsOperator && method.ExplicitInterfaceType is null && method.Name == methodName && !method.IsStatic)
                .Concat(_model.Types["System.Object"].Methods.Where(method => method.Name == methodName && !method.IsStatic))
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First())
                .ToArray();
        if (syntax.Target is NameExpressionSyntax && !_method.IsStatic)
        {
            var allCandidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.ExplicitInterfaceType is null && method.Name == methodName)
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (allCandidates.Length > 0)
                candidates = allCandidates;
        }
        candidates = ExpandGenericCandidates(candidates, explicitTypeArguments, arguments, syntax).Distinct().ToArray();
        var selected = SelectOverload(candidates, methodName, arguments, syntax.Arguments, syntax);
        if (selected is null)
            return ErrorExpression((receiver?.Prelude ?? []).Concat(arguments.SelectMany(argument => argument.Prelude)));
        if (!selected.IsStatic && receiver?.IsConstInitStorage == true)
        {
            Report("CT2219", "Instance methods cannot be called directly on ConstInit storage; copy the value to a local first.", syntax.Target);
            return ErrorExpression(receiver.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }
        if (selected.ReturnType.ContainsPointer || selected.Parameters.Any(parameter => parameter.Type.ContainsPointer))
            RequireUnsafe(syntax);
        if (selected.IsUnsafe)
            RequireUnsafe(syntax);
        CheckAccess(selected, syntax);
        _emitter.RegisterExternUse(selected, syntax);
        _emitter.Effects.RecordCall(_method, selected, syntax, selected.IsVirtual && receiver?.IsBaseReceiver != true);
        if (selected.ExternName == "ct_native_utf8_borrow" &&
            syntax.Arguments is [{ Expression: LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string utf8Literal } }] &&
            utf8Literal.Contains('\0'))
            Report("CTS0003", "NativeUtf8String.Borrow rejects strings containing an embedded NUL byte.", syntax.Arguments[0].Expression);
        if (captureForDefer && selected.Parameters.Any(parameter => parameter.IsSynchronousCallback))
        {
            Report("CT1268", "A synchronous native delegate callback cannot be captured by defer.", syntax);
            return ErrorExpression((receiver?.Prelude ?? []).Concat(arguments.SelectMany(argument => argument.Prelude)));
        }

        var prelude = new List<string>();
        string? receiverCode = null;
        if (!selected.IsStatic)
        {
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new IrExpressionValue { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new IrValueStorage { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new IrExpressionValue { Type = _method.ContainingType.Type, Code = "ct_self" };
            if ((selected.ContainingType.IsObject || selected.IsVirtual && receiver.Type.IsValueType) && receiver.Type != _model.Types["System.Object"].Type)
                receiver = Convert(receiver, _model.Types["System.Object"].Type, syntax.Target, false);
            if (captureForDefer)
            {
                prelude.AddRange(receiver.Prelude);
                var slot = $"ct_df_{_deferId}_receiver";
                AddCapturedSlot(prelude, receiver.Type, slot, receiver.Code);
                receiverCode = receiver.Type.Kind == CTypeKind.Struct
                    ? $"({_emitter.CTypeName(receiver.Type)}*)(void*)&{Durable(slot)}"
                    : receiver.IsKnownNonNull
                        ? Durable(slot)
                        : $"({_emitter.CTypeName(receiver.Type)})ct_require_nonnull({Durable(slot)}, {_emitter.SourceArgument(syntax.Target)})";
                if (receiver.Type.Kind != CTypeKind.Struct && !receiver.IsKnownNonNull)
                    RecordRuntimeFault(syntax.Target, "deferred receiver null check");
            }
            else
            {
                var loweredReceiver = MaterializeReceiver(receiver, syntax.Target);
                prelude.AddRange(loweredReceiver.Prelude);
                receiverCode = loweredReceiver.Code;
            }
        }
        var loweredArguments = captureForDefer
            ? CaptureDeferredArgumentsWithPostlude(arguments, selected.Parameters, syntax.Arguments)
            : LowerArguments(arguments, selected.Parameters, syntax.Arguments);
        prelude.AddRange(loweredArguments.Prelude);

        if (!captureForDefer && TryLowerAtomicCall(selected, receiverCode, loweredArguments.Codes, arguments, prelude, syntax, out var atomicResult))
            return atomicResult;
        if (!captureForDefer && TryLowerMmioCall(selected, loweredArguments.Codes, prelude, syntax, out var mmioResult))
            return mmioResult;
        if (!captureForDefer && TryLowerCpuCall(selected, loweredArguments.Codes, prelude, syntax, out var cpuResult))
            return cpuResult;
        if (!captureForDefer && TryLowerEndianCall(selected, loweredArguments.Codes, arguments, prelude, syntax, out var endianResult))
            return endianResult;
        if (TryLowerManagedThreadingCall(selected, receiverCode, loweredArguments.Codes, arguments, prelude, syntax, captureForDefer, out var threadingResult))
            return threadingResult;

        var callArguments = new List<string>();
        if (receiverCode is not null)
            callArguments.Add(receiverCode);
        callArguments.AddRange(loweredArguments.Codes);
        string call;
        if (selected.IsVirtual && receiverCode is not null && receiver?.IsBaseReceiver != true)
        {
            var objectReceiver = $"((ct_object*)(void*){receiverCode})";
            callArguments[0] = objectReceiver;
            if (CEmitter.VirtualSlotName(selected) == "Equals" && callArguments.Count == 2)
                callArguments[1] = $"(ct_object*)(void*){callArguments[1]}";
            call = $"{objectReceiver}->Type->VTable->{CEmitter.VirtualSlotName(selected)}({string.Join(", ", callArguments)})";
        }
        else
        {
            if (receiverCode is not null)
                callArguments[0] = selected.ContainingType.FullName == "Esp.Idf.EspError"
                    ? $"(esp_err_t*)(void*){receiverCode}"
                    : $"({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode}";
            call = $"{selected.CName}({string.Join(", ", callArguments)})";
        }
        if (captureForDefer)
            _deferId++;
        if (captureForDefer)
            return new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Ownership = selected.ReturnType.ContainsManagedReferences ? OwnershipKind.Owned : OwnershipKind.None, Symbol = selected };
        if (loweredArguments.Postlude.Count != 0)
        {
            _emitter.Effects.RecordAllocation(_method, syntax, "synchronous native delegate callback");
            if (selected.ReturnType == CType.Void)
            {
                prelude.Add(call + ";");
                prelude.AddRange(loweredArguments.Postlude);
                return new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
            }
            var callbackResult = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(selected.ReturnType, callbackResult)} = {call};");
            prelude.AddRange(loweredArguments.Postlude);
            call = callbackResult;
        }
        if (selected.ReturnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
        {
            if (!selected.ReturnsNullable && (selected.ReturnType.Kind == CTypeKind.Opaque || selected.ReturnsOwned || selected.ReturnsBorrowed))
            {
                var nativeResult = NewTemp();
                prelude.Add($"{_emitter.CDeclaration(selected.ReturnType, nativeResult)} = {call};");
                prelude.Add($"(void)ct_require_nonnull((void*){nativeResult}, {_emitter.SourceArgument(syntax)});");
                RecordRuntimeFault(syntax, "native result null check");
                call = nativeResult;
            }
            return new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Ownership = selected.ReturnsOwned ? OwnershipKind.Owned : OwnershipKind.Borrowed, Symbol = selected };
        }
        return selected.ReturnType.ContainsManagedReferences
            ? OwnResult(selected.ReturnType, call, prelude, selected.ReturnsBorrowed, selected)
            : new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Symbol = selected };
    }

    private bool TryLowerMmioCall(MethodSymbol selected, IReadOnlyList<string> arguments, List<string> prelude,
        CallExpressionSyntax syntax, out IrExpressionValue result)
    {
        result = null!;
        if (selected.ContainingType.FullName != "System.Runtime.Mmio")
            return false;
        var ordered = selected.Name is "Read" or "Write" or "Barrier";
        if (ordered && _emitter.Architecture == CompilationArchitecture.Auto)
        {
            Report("CT4109", "Ordered MMIO requires a supported resolved target architecture.", syntax);
            result = ErrorExpression(prelude);
            return true;
        }
        if (selected.Name == "Barrier")
        {
            prelude.Add("ct_mmio_barrier();");
            result = new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
            return true;
        }
        var element = selected.TypeArguments.Length == 1 ? selected.TypeArguments[0] : CType.Error;
        var width = MmioWidth(element);
        var valid = width != 0;
        if (!valid)
            Report("CT2203", $"MMIO element type '{element.DisplayName}' must be a fixed-width integer or enum.", syntax);
        if (width == 0)
            width = 1;
        if (syntax.Arguments[0].Expression is LiteralExpressionSyntax { Value: NumericLiteralValue literal } && literal.FloatingPoint is null && literal.Integer % width != 0)
            Report("CT2203", $"MMIO address must be naturally aligned to {width} byte(s).", syntax.Arguments[0].Expression);
        var ctype = _emitter.CTypeName(element);
        var address = arguments.Count == 0 ? "0" : arguments[0];
        if (ordered)
            prelude.Add("ct_mmio_barrier();");
        if (selected.Name is "Write" or "WriteRelaxed")
        {
            prelude.Add($"*(volatile {ctype}*)(uintptr_t)({address}) = ({ctype})({arguments[1]});");
            if (ordered)
                prelude.Add("ct_mmio_barrier();");
            result = new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
            return true;
        }
        var temporary = NewTemp();
        prelude.Add($"{ctype} {temporary} = *(volatile {ctype}*)(uintptr_t)({address});");
        if (ordered)
            prelude.Add("ct_mmio_barrier();");
        result = new IrExpressionValue { Type = element, Code = temporary, Prelude = prelude, Symbol = selected };
        return true;

        static int MmioWidth(CType type)
        {
            if (type.Symbol?.IsBitField == true)
                return MmioWidth(type.Symbol.BitFieldBackingType!);
            if (type.Kind == CTypeKind.Newtype && type.Symbol?.UnderlyingType is { } underlying)
                return MmioWidth(underlying);
            if (type.Kind == CTypeKind.Enum && type.Symbol?.Fields.SingleOrDefault(field => field.Name == "<underlying>") is { } enumUnderlying)
                return MmioWidth(enumUnderlying.Type);
            return type.Kind switch
            {
                CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Char => 1,
                CTypeKind.Short or CTypeKind.Ushort => 2,
                CTypeKind.Int or CTypeKind.Uint => 4,
                CTypeKind.Long or CTypeKind.Ulong => 8,
                _ => 0,
            };
        }
    }

    private bool TryLowerCpuCall(MethodSymbol selected, IReadOnlyList<string> arguments, List<string> prelude,
        CallExpressionSyntax syntax, out IrExpressionValue result)
    {
        result = null!;
        if (selected.ContainingType.FullName != "System.Runtime.Cpu")
            return false;
        if (selected.Name is "MemoryBarrier" or "Pause")
        {
            if (_emitter.Architecture == CompilationArchitecture.Auto)
            {
                Report("CT4110", $"Cpu.{selected.Name} requires a supported resolved target architecture.", syntax);
                result = ErrorExpression(prelude);
                return true;
            }
            prelude.Add(selected.Name == "MemoryBarrier" ? "ct_cpu_memory_barrier();" : "ct_cpu_pause();");
            result = new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
            return true;
        }
        var suffix = selected.Parameters[0].Type switch
        {
            { Kind: CTypeKind.Ushort } => "16",
            { Kind: CTypeKind.Uint } => "32",
            { Kind: CTypeKind.Ulong } => "64",
            _ => string.Empty,
        };
        if (suffix.Length == 0)
        {
            Report("CT2207", $"Cpu.{selected.Name} requires an unsigned fixed-width integer.", syntax);
            result = ErrorExpression(prelude);
            return true;
        }
        var helper = selected.Name switch
        {
            "ByteSwap" => $"ct_cpu_bswap{suffix}",
            "PopCount" => $"ct_cpu_popcount{suffix}",
            "LeadingZeroCount" => $"ct_cpu_lzcnt{suffix}",
            _ => string.Empty,
        };
        if (helper.Length == 0)
        {
            Report("CT2207", $"Unknown portable CPU intrinsic '{selected.Name}'.", syntax);
            result = ErrorExpression(prelude);
            return true;
        }
        result = new IrExpressionValue { Type = selected.ReturnType, Code = $"{helper}({arguments[0]})", Prelude = prelude, Symbol = selected };
        return true;
    }

    private bool TryLowerEndianCall(MethodSymbol selected, IReadOnlyList<string> arguments, IReadOnlyList<IrExpressionValue> argumentValues, List<string> prelude,
        CallExpressionSyntax syntax, out IrExpressionValue result)
    {
        result = null!;
        if (selected.ContainingType.FullName != "System.Endian")
            return false;
        if (arguments.Count != 1 || selected.Name is not ("ToBigEndian" or "FromBigEndian" or "ToLittleEndian" or "FromLittleEndian"))
        {
            Report("CT2208", $"Malformed Endian intrinsic '{selected.Name}'.", syntax);
            result = ErrorExpression(prelude);
            return true;
        }
        var storage = selected.Name.StartsWith("To", StringComparison.Ordinal)
            ? selected.Parameters[0].Type
            : selected.ReturnType;
        var helper = storage.Kind switch
        {
            CTypeKind.Ushort => "ct_cpu_bswap16",
            CTypeKind.Uint => "ct_cpu_bswap32",
            _ => string.Empty,
        };
        if (helper.Length == 0)
        {
            Report("CT2208", "Endian conversions require ushort/be16/le16 or uint/be32/le32.", syntax);
            result = ErrorExpression(prelude);
            return true;
        }
        if (argumentValues.Count == 1 && argumentValues[0].IsConstant && argumentValues[0].Prelude.Count == 0 &&
            TryUnsignedConstant(argumentValues[0].ConstantValue, out var constant))
        {
            var converted = selected.Name.Contains("BigEndian", StringComparison.Ordinal)
                ? storage.Kind == CTypeKind.Ushort
                    ? (ulong)(ushort)(((constant & 0xffu) << 8) | ((constant >> 8) & 0xffu))
                    : ((constant & 0x000000ffu) << 24) | ((constant & 0x0000ff00u) << 8) | ((constant & 0x00ff0000u) >> 8) | ((constant & 0xff000000u) >> 24)
                : constant;
            var literal = storage.Kind == CTypeKind.Ushort
                ? converted.ToString(CultureInfo.InvariantCulture)
                : $"UINT32_C({converted.ToString(CultureInfo.InvariantCulture)})";
            result = Constant(selected.ReturnType, new BigInteger(converted), $"({_emitter.CTypeName(selected.ReturnType)})({literal})", selected);
            return true;
        }
        var value = selected.Name.Contains("BigEndian", StringComparison.Ordinal)
            ? $"{helper}({arguments[0]})"
            : arguments[0];
        result = new IrExpressionValue
        {
            Type = selected.ReturnType,
            Code = $"({_emitter.CTypeName(selected.ReturnType)})({value})",
            Prelude = prelude,
            Symbol = selected,
        };
        return true;

        static bool TryUnsignedConstant(object? value, out ulong result)
        {
            try
            {
                result = value switch
                {
                    BigInteger number when number >= 0 && number <= ulong.MaxValue => (ulong)number,
                    byte number => number,
                    ushort number => number,
                    uint number => number,
                    ulong number => number,
                    sbyte number when number >= 0 => (ulong)number,
                    short number when number >= 0 => (ulong)number,
                    int number when number >= 0 => (ulong)number,
                    long number when number >= 0 => (ulong)number,
                    _ => throw new InvalidCastException(),
                };
                return true;
            }
            catch (InvalidCastException)
            {
                result = 0;
                return false;
            }
        }
    }

    private bool TryLowerAtomicCall(MethodSymbol selected, string? receiverCode, IReadOnlyList<string> arguments,
        IReadOnlyList<IrExpressionValue> argumentValues, List<string> prelude, SyntaxNode syntax, out IrExpressionValue result)
    {
        result = null!;
        var definition = selected.ContainingType.GenericDefinition;
        var isAtomicValue = definition is { Namespace: "System.Threading", Name: "Atomic" } && selected.ContainingType.TypeArguments.Length == 1;
        var isFence = selected.ContainingType is { Namespace: "System.Threading", Name: "Atomic", IsStatic: true } && selected.Name == "Fence";
        if ((isAtomicValue || isFence) && selected.Parameters.Select((parameter, index) => (parameter, index))
            .Any(pair => pair.parameter.Type.Symbol is { Namespace: "System.Threading", Name: "MemoryOrder" } &&
                (pair.index >= argumentValues.Count || !argumentValues[pair.index].IsConstant)))
            RecordRuntimeFault(syntax, "dynamic atomic memory-order validation");
        if (isFence)
        {
            prelude.Add($"ct_atomic_fence((int32_t){arguments[0]});");
            result = new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
            return true;
        }
        if (!isAtomicValue || receiverCode is null)
            return false;
        var valueType = selected.ContainingType.TypeArguments[0];
        var field = selected.ContainingType.Fields.Single(candidate => candidate.Name == "value");
        var storage = $"(void*)&(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})->{field.CAccessPath}";
        var size = $"sizeof((({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})->{field.CAccessPath})";
        string code;
        switch (selected.Name)
        {
            case "Load":
                code = AtomicFromBits(valueType, $"ct_atomic_scalar_load({storage}, {size}, (int32_t){arguments[0]})");
                break;
            case "Store":
                prelude.Add($"ct_atomic_scalar_store({storage}, {size}, {AtomicToBits(valueType, arguments[0])}, (int32_t){arguments[1]});");
                result = new IrExpressionValue { Type = CType.Void, Code = "0", Prelude = prelude, Symbol = selected };
                return true;
            case "Exchange":
                code = AtomicFromBits(valueType, $"ct_atomic_scalar_exchange({storage}, {size}, {AtomicToBits(valueType, arguments[0])}, (int32_t){arguments[1]})");
                break;
            case "CompareExchange":
                code = AtomicFromBits(valueType, $"ct_atomic_scalar_compare_exchange({storage}, {size}, {AtomicToBits(valueType, arguments[0])}, {AtomicToBits(valueType, arguments[1])}, (int32_t){arguments[2]}, (int32_t){arguments[3]})");
                break;
            case "FetchAdd" or "FetchSubtract":
                if (!valueType.IsIntegral || valueType.Kind is CTypeKind.Bool or CTypeKind.Pointer)
                    Report("CT1277", $"{selected.Name} requires an integral Atomic<T>.", syntax);
                code = AtomicFromBits(valueType, $"ct_atomic_scalar_fetch({storage}, {size}, {AtomicToBits(valueType, arguments[0])}, (int32_t){arguments[1]}, {(selected.Name == "FetchAdd" ? 0 : 1)})");
                break;
            case "FetchAnd" or "FetchOr" or "FetchXor":
                if (!(valueType.IsIntegral || valueType.Kind == CTypeKind.Bool) || valueType.Kind == CTypeKind.Pointer)
                    Report("CT1277", $"{selected.Name} requires a Boolean or integral Atomic<T>.", syntax);
                var operation = selected.Name == "FetchAnd" ? 2 : selected.Name == "FetchOr" ? 3 : 4;
                code = AtomicFromBits(valueType, $"ct_atomic_scalar_fetch({storage}, {size}, {AtomicToBits(valueType, arguments[0])}, (int32_t){arguments[1]}, {operation})");
                break;
            default:
                return false;
        }
        result = new IrExpressionValue { Type = valueType, Code = code, Prelude = prelude, Symbol = selected };
        return true;
    }

    private bool TryLowerManagedThreadingCall(MethodSymbol selected, string? receiverCode, IReadOnlyList<string> arguments,
        IReadOnlyList<IrExpressionValue> argumentValues, List<string> prelude, SyntaxNode syntax, bool captureForDefer, out IrExpressionValue result)
    {
        result = null!;
        string? call = selected.ContainingType.FullName switch
        {
            "System.Threading.Thread" when selected.Name == "Start" && receiverCode is not null => $"ct_managed_thread_start(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})",
            "System.Threading.Thread" when selected.Name == "Join" && receiverCode is not null => $"ct_managed_thread_join(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})",
            "System.Threading.Thread" when selected.Name == "Sleep" && arguments.Count == 1 => $"ct_managed_thread_sleep((uint32_t){arguments[0]})",
            "System.Threading.Thread" when selected.Name == "Yield" => "ct_managed_thread_yield()",
            "System.Threading.Mutex" when selected.Name == "Enter" && receiverCode is not null => $"ct_managed_mutex_enter(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})",
            "System.Threading.Mutex" when selected.Name == "TryEnter" && receiverCode is not null => $"ct_managed_mutex_try_enter(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})",
            "System.Threading.Mutex" when selected.Name == "Exit" && receiverCode is not null => $"ct_managed_mutex_exit(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})",
            _ => null,
        };
        if (call is null)
            return false;
        var zeroSleep = selected.Name == "Sleep" && argumentValues.Count == 1 &&
            argumentValues[0].IsConstant && IsZero(argumentValues[0].ConstantValue);
        var threadingEffects = EffectKind.UsesRuntime;
        if (selected.Name is "Start" or "Join" or "Enter" or "TryEnter" or "Exit" || selected.Name == "Sleep" && !zeroSleep)
            threadingEffects |= EffectKind.Throws;
        _emitter.Effects.Record(_method, syntax, threadingEffects, $"managed threading call to '{selected.Name}'");
        if (selected.Name is "Join" or "Enter" || selected.Name == "Sleep" && !zeroSleep)
            _emitter.Effects.Record(_method, syntax, EffectKind.Blocks, $"blocking threading call to '{selected.Name}'");
        if (captureForDefer)
            _deferId++;
        result = new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Symbol = selected };
        return true;

        static bool IsZero(object? value) => value switch
        {
            BigInteger number => number.IsZero,
            byte number => number == 0,
            sbyte number => number == 0,
            short number => number == 0,
            ushort number => number == 0,
            int number => number == 0,
            uint number => number == 0,
            long number => number == 0,
            ulong number => number == 0,
            _ => false,
        };
    }

    private (List<string> Prelude, List<string> Codes, List<string> Postlude) CaptureDeferredArgumentsWithPostlude(
        IReadOnlyList<IrExpressionValue> arguments,
        ImmutableArray<ParameterSymbol> parameters,
        ImmutableArray<ArgumentSyntax> syntax)
    {
        var captured = CaptureDeferredArguments(arguments, parameters, syntax);
        return (captured.Prelude, captured.Codes, []);
    }

    private static bool IsCallablePointer(CType? type) => type?.Kind is CTypeKind.Delegate or CTypeKind.FunctionPointer;

    private IrExpressionValue? TryLowerDelegateMember(MemberAccessExpressionSyntax syntax)
    {
        var staticType = TryResolveTypeExpression(syntax.Receiver);
        if (staticType is not null)
        {
            var field = Hierarchy(staticType).SelectMany(type => type.Fields)
                .FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic && IsCallablePointer(candidate.Type));
            if (field is not null)
                return LowerField(field, null, syntax, false);
            var property = Hierarchy(staticType).SelectMany(type => type.Properties)
                .FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic && IsCallablePointer(candidate.Type));
            return property is null ? null : LowerProperty(property, null, syntax, false);
        }

        var receiver = LowerExpression(syntax.Receiver);
        var type = receiver.Type.Symbol;
        if (type is null)
            return null;
        var instanceField = Hierarchy(type).SelectMany(candidate => candidate.Fields)
            .FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic && IsCallablePointer(candidate.Type));
        if (instanceField is not null)
            return LowerField(instanceField, receiver, syntax, false);
        var instanceProperty = Hierarchy(type).SelectMany(candidate => candidate.Properties)
            .FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic && IsCallablePointer(candidate.Type));
        return instanceProperty is null ? null : LowerProperty(instanceProperty, receiver, syntax, false);
    }

    private IrExpressionValue LowerDelegateInvocation(CallExpressionSyntax syntax, IrExpressionValue target)
    {
        var delegateType = target.Type.Symbol!;
        var parameters = delegateType.DelegateParameters;
        var arguments = syntax.Arguments.Select(LowerArgument).ToArray();
        if (arguments.Length != parameters.Length)
        {
            Report("CT2160", $"Delegate '{delegateType.FullName}' expects {parameters.Length} argument(s).", syntax);
            return ErrorExpression(target.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }
        target = Materialize(target, syntax.Target);
        var loweredArguments = LowerArguments(arguments, parameters, syntax.Arguments);
        var prelude = new List<string>(target.Prelude);
        prelude.AddRange(loweredArguments.Prelude);
        prelude.Add($"(void)ct_require_nonnull({target.Code}, {_emitter.SourceArgument(syntax.Target)});");
        RecordRuntimeFault(syntax.Target, "delegate null check");
        _emitter.Effects.Record(_method, syntax, EffectKind.All, $"indirect invocation of delegate '{delegateType.FullName}'");
        var callArguments = new[] { $"{target.Code}->ct_target" }.Concat(loweredArguments.Codes);
        var call = $"{target.Code}->ct_invoke({string.Join(", ", callArguments)})";
        var returnType = delegateType.DelegateReturnType!;
        return returnType.ContainsManagedReferences
            ? OwnResult(returnType, call, prelude, symbol: delegateType)
            : new IrExpressionValue { Type = returnType, Code = call, Prelude = prelude, Symbol = delegateType };
    }

    private IrExpressionValue LowerFunctionPointerInvocation(CallExpressionSyntax syntax, IrExpressionValue target)
    {
        RequireUnsafe(syntax);
        var signature = target.Type.FunctionPointer!;
        var arguments = syntax.Arguments.Select(LowerArgument).ToArray();
        if (arguments.Length != signature.ParameterTypes.Length || arguments.Where((argument, index) => index < signature.ParameterTypes.Length && (argument.Type != signature.ParameterTypes[index] || syntax.Arguments[index].PassingKind != signature.PassingKinds[index])).Any())
        {
            Report("CT2164", "Function-pointer invocation requires exact argument types.", syntax);
            return ErrorExpression(target.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }
        target = Materialize(target, syntax.Target);
        var prelude = new List<string>(target.Prelude);
        var codes = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (signature.PassingKinds[index] != ParameterPassingKind.Value)
            {
                var argument = arguments[index];
                prelude.AddRange(argument.Prelude);
                if (argument.LValue?.Field is { ContainingType.HasNonNaturalLayout: true })
                    Report("CT2190", "A field in a packed or explicit-layout aggregate cannot be passed by reference.", syntax.Arguments[index]);
                if (argument.LValue?.Address is not { } address)
                {
                    if (argument.Symbol is FieldSymbol { IsRegister: true })
                        Report("CT2210", "A fixed-address register cannot be passed by reference.", syntax.Arguments[index]);
                    else
                        Report("CT2171", "A by-reference function-pointer argument must be addressable.", syntax.Arguments[index]);
                    codes.Add("NULL");
                }
                else
                {
                    if (signature.PassingKinds[index] is ParameterPassingKind.Ref or ParameterPassingKind.Out && IsReadonly(argument.LValue))
                        Report("CT2172", "Readonly storage can be passed only with 'in'.", syntax.Arguments[index]);
                    if (signature.PassingKinds[index] == ParameterPassingKind.Out)
                    {
                        if (argument.Type.ContainsManagedReferences && !IsUninitializedOut(argument.LValue))
                            prelude.Add(_emitter.DropValueStatement(argument.Type, address));
                        prelude.Add($"*({address}) = {_emitter.DefaultValue(argument.Type)};");
                        MarkAssigned(argument.LValue);
                    }
                    else if (signature.PassingKinds[index] == ParameterPassingKind.Ref)
                        MarkAssigned(argument.LValue);
                    codes.Add(address);
                }
            }
            else
            {
                var argument = Materialize(arguments[index], syntax.Arguments[index].Expression);
                prelude.AddRange(argument.Prelude);
                if (argument.Type.IsNativeBuffer)
                {
                    codes.Add($"({argument.Code}).Data");
                    codes.Add($"({argument.Code}).Length");
                }
                else
                    codes.Add(argument.Code);
            }
        }
        prelude.Add($"(void)ct_require_nonnull((void*){target.Code}, {_emitter.SourceArgument(syntax.Target)});");
        RecordRuntimeFault(syntax.Target, "function-pointer null check");
        _emitter.Effects.Record(_method, syntax, EffectKind.All, "unmanaged function-pointer invocation");
        return new IrExpressionValue { Type = signature.ReturnType, Code = $"{target.Code}({string.Join(", ", codes)})", Prelude = prelude, Symbol = target.Type };
    }

    private static bool SupportsBuiltInToString(CType type) => type.Kind is
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or
        CTypeKind.Char or CTypeKind.Rune or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Double or CTypeKind.String;

    private IrExpressionValue LowerBuiltInToString(CallExpressionSyntax syntax, MemberAccessExpressionSyntax member, IrExpressionValue receiver, bool captureForDefer = false)
    {
        var arguments = syntax.Arguments.Select(LowerArgument).ToArray();
        if (arguments.Length != 0)
        {
            Report("CT2122", "No overload of 'ToString' accepts the supplied argument types.", syntax);
            return ErrorExpression(receiver.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }

        if (captureForDefer)
        {
            var prelude = new List<string>(receiver.Prelude);
            var slot = $"ct_df_{_deferId}_receiver";
            AddCapturedSlot(prelude, receiver.Type, slot, receiver.Code);
            receiver = new IrExpressionValue { Type = receiver.Type, Code = Durable(slot), Prelude = prelude };
            _deferId++;
        }
        else
            receiver = Materialize(receiver, member.Receiver);
        if (receiver.Type.Kind == CTypeKind.String)
        {
            if (captureForDefer)
            {
                RecordRuntimeFault(member, "string receiver null check");
                return new IrExpressionValue { Type = CType.String, Code = $"ct_string_v_to_string((ct_object*)(void*)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)}))", Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned };
            }
            RecordRuntimeFault(member, "string receiver null check");
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)});");
            return OwnResult(CType.String, "ct_string_v_to_string((ct_object*)(void*)" + receiver.Code + ")", receiver.Prelude);
        }

        var function = receiver.Type.Kind switch
        {
            CTypeKind.Bool => "ct_to_string_bool",
            CTypeKind.Char => "ct_to_string_char",
            CTypeKind.Rune => "ct_to_string_rune",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int",
            CTypeKind.Long => "ct_to_string_long",
            CTypeKind.Ulong => "ct_to_string_ulong",
            CTypeKind.Nint => "ct_to_string_nint",
            CTypeKind.Nuint => "ct_to_string_nuint",
            CTypeKind.Float => "ct_to_string_float",
            CTypeKind.Double => "ct_to_string_double",
            _ => throw new InvalidOperationException($"Unsupported ToString receiver '{receiver.Type.DisplayName}'."),
        };
        var argument = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort => $"(uint32_t){receiver.Code}",
            CTypeKind.Sbyte or CTypeKind.Short => $"(int32_t){receiver.Code}",
            _ => receiver.Code,
        };
        var code = $"{function}({argument}, {_emitter.SourceArgument(member)})";
        _emitter.Effects.RecordAllocation(_method, syntax, $"conversion of '{receiver.Type.DisplayName}' to string");
        return captureForDefer
            ? new IrExpressionValue { Type = CType.String, Code = code, Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned }
            : OwnResult(CType.String, code, receiver.Prelude);
    }
}
