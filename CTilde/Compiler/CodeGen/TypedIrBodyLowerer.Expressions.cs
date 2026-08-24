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
            _ => ErrorExpression(),
        };
        if (syntax is CallExpressionSyntax && _emitter.EmitDebugInstrumentation && !_analysisOnly)
            result.Prelude.Insert(0, $"ct_debug_site(UINT32_C({_emitter.RegisterDebugSite(_method, syntax, "call")}));");
        if (_optimizationFacts.KnownNonNullExpressions.Contains(syntax))
            result.IsKnownNonNull = true;
        return RecordSemantic(syntax, result);
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
        if (syntax.Value is NumericLiteralValue numeric)
        {
            if (numeric.FloatingPoint is float value)
                return Constant(CType.Float, value, FormatFloat(value));
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
                receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            return new IrExpressionValue { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        if (receiver.Type.Kind == CTypeKind.Array && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            if (!receiver.IsKnownNonNull)
                receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            return new IrExpressionValue { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
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
        if (field.Type.ContainsPointer)
            RequireUnsafe(syntax);
        CheckAccess(field, syntax);
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
            code = $"(({NameMangler.Type(field.ContainingType)}*)(void*){loweredReceiver.Code})->{field.CName}";
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
                },
            };
        }
        return new IrExpressionValue
        {
            Type = field.Type,
            Code = code,
            Prelude = prelude,
            IsConstant = field.IsConst,
            LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})", Field = field },
            Symbol = field,
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
            _emitter.AllocationEffects.RecordCall(_method, selectedAccessor, syntax, property.IsVirtual && !baseReceiver);
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
            },
        };
        return !forWrite && property.Type.ContainsManagedReferences
            ? OwnResult(property.Type, getterCode, prelude)
            : result;
    }

    private IrExpressionValue LowerIndex(IndexExpressionSyntax syntax, bool forWrite)
    {
        var receiver = Materialize(LowerExpression(syntax.Receiver), syntax.Receiver);
        var indexType = receiver.Type.IsNativeBuffer ? CType.Nuint : CType.Int;
        var index = Materialize(Convert(LowerExpression(syntax.Index), indexType, syntax.Index, false), syntax.Index);
        var prelude = new List<string>(receiver.Prelude);
        prelude.AddRange(index.Prelude);
        if (receiver.Type.Kind == CTypeKind.Array)
        {
            if (!receiver.IsKnownNonNull)
                prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            if (!IsProvenInBounds(receiver, index))
                prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            var code = $"{receiver.Code}->Data[{index.Code}]";
            return new IrExpressionValue
            {
                Type = receiver.Type.ElementType!,
                Code = code,
                Prelude = prelude,
                LValue = new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})" },
            };
        }
        if (receiver.Type.Kind == CTypeKind.String)
        {
            if (!receiver.IsKnownNonNull)
                prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            return new IrExpressionValue { Type = CType.Char, Code = $"{receiver.Code}->Data[{index.Code}]", Prelude = prelude };
        }
        if (receiver.Type.IsNativeBuffer)
        {
            RequireUnsafe(syntax);
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
                LValue = writable ? new IrValueStorage { Store = value => $"{code} = {value}", Address = $"&({code})" } : null,
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
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "array construction");
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
        _emitter.AllocationEffects.RecordCall(_method, constructor, syntax, requiresContract: false);
        if (type.Kind == CTypeKind.Class)
            _emitter.AllocationEffects.RecordDirect(_method, syntax, $"construction of class '{type.DisplayName}'");
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
            prelude.Add($"if ({count.Code} < 0) ct_raise_runtime_fault(CT_FAULT_OVERFLOW, \"CTB0002\", {_emitter.SourceArgument(syntax)});");
        var bytes = constantSizeIsSafe
            ? $"((size_t){count.Code} * sizeof({_emitter.CTypeName(element)}))"
            : $"ct_stack_bytes((size_t){count.Code}, sizeof({_emitter.CTypeName(element)}), {_emitter.SourceArgument(syntax)})";
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
            CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Pointer or CTypeKind.FunctionPointer => 8UL,
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
        var candidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.Name == methodName && method.IsStatic == requireStatic)
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (!requireStatic && receiver is not null && receiver.Type.IsValueType)
            candidates = containingType.Methods.Where(method => !method.IsOperator && method.Name == methodName && !method.IsStatic)
                .Concat(_model.Types["System.Object"].Methods.Where(method => method.Name == methodName && !method.IsStatic))
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First())
                .ToArray();
        if (syntax.Target is NameExpressionSyntax && !_method.IsStatic)
        {
            var allCandidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => !method.IsOperator && method.Name == methodName)
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (allCandidates.Length > 0)
                candidates = allCandidates;
        }
        candidates = ExpandGenericCandidates(candidates, explicitTypeArguments, arguments, syntax).Distinct().ToArray();
        var selected = SelectOverload(candidates, methodName, arguments, syntax.Arguments, syntax);
        if (selected is null)
            return ErrorExpression((receiver?.Prelude ?? []).Concat(arguments.SelectMany(argument => argument.Prelude)));
        if (selected.ReturnType.ContainsPointer || selected.Parameters.Any(parameter => parameter.Type.ContainsPointer))
            RequireUnsafe(syntax);
        if (selected.IsUnsafe)
            RequireUnsafe(syntax);
        CheckAccess(selected, syntax);
        _emitter.RegisterExternUse(selected, syntax);
        _emitter.AllocationEffects.RecordCall(_method, selected, syntax, selected.IsVirtual && receiver?.IsBaseReceiver != true);
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

        if (!captureForDefer && TryLowerAtomicCall(selected, receiverCode, loweredArguments.Codes, prelude, syntax, out var atomicResult))
            return atomicResult;
        if (TryLowerManagedThreadingCall(selected, receiverCode, loweredArguments.Codes, prelude, captureForDefer, out var threadingResult))
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
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "synchronous native delegate callback");
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
                call = nativeResult;
            }
            return new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Ownership = selected.ReturnsOwned ? OwnershipKind.Owned : OwnershipKind.Borrowed, Symbol = selected };
        }
        return selected.ReturnType.ContainsManagedReferences
            ? OwnResult(selected.ReturnType, call, prelude, selected.ReturnsBorrowed, selected)
            : new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Symbol = selected };
    }

    private bool TryLowerAtomicCall(MethodSymbol selected, string? receiverCode, IReadOnlyList<string> arguments, List<string> prelude, SyntaxNode syntax, out IrExpressionValue result)
    {
        result = null!;
        var definition = selected.ContainingType.GenericDefinition;
        var isAtomicValue = definition is { Namespace: "System.Threading", Name: "Atomic" } && selected.ContainingType.TypeArguments.Length == 1;
        var isFence = selected.ContainingType is { Namespace: "System.Threading", Name: "Atomic", IsStatic: true } && selected.Name == "Fence";
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
        var storage = $"(void*)&(({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})->{field.CName}";
        var size = $"sizeof((({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode})->{field.CName})";
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
        List<string> prelude, bool captureForDefer, out IrExpressionValue result)
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
        if (captureForDefer)
            _deferId++;
        result = new IrExpressionValue { Type = selected.ReturnType, Code = call, Prelude = prelude, Symbol = selected };
        return true;
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
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"indirect invocation of delegate '{delegateType.FullName}'");
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
                if (argument.LValue?.Address is not { } address)
                {
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
        _emitter.AllocationEffects.RecordDirect(_method, syntax, "unmanaged function-pointer invocation");
        return new IrExpressionValue { Type = signature.ReturnType, Code = $"{target.Code}({string.Join(", ", codes)})", Prelude = prelude, Symbol = target.Type };
    }

    private static bool SupportsBuiltInToString(CType type) => type.Kind is
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or
        CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.String;

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
                return new IrExpressionValue { Type = CType.String, Code = $"ct_string_v_to_string((ct_object*)(void*)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)}))", Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned };
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)});");
            return OwnResult(CType.String, "ct_string_v_to_string((ct_object*)(void*)" + receiver.Code + ")", receiver.Prelude);
        }

        var function = receiver.Type.Kind switch
        {
            CTypeKind.Bool => "ct_to_string_bool",
            CTypeKind.Char => "ct_to_string_char",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int",
            CTypeKind.Long => "ct_to_string_long",
            CTypeKind.Ulong => "ct_to_string_ulong",
            CTypeKind.Nint => "ct_to_string_nint",
            CTypeKind.Nuint => "ct_to_string_nuint",
            CTypeKind.Float => "ct_to_string_float",
            _ => throw new InvalidOperationException($"Unsupported ToString receiver '{receiver.Type.DisplayName}'."),
        };
        var argument = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort => $"(uint32_t){receiver.Code}",
            CTypeKind.Sbyte or CTypeKind.Short => $"(int32_t){receiver.Code}",
            _ => receiver.Code,
        };
        var code = $"{function}({argument}, {_emitter.SourceArgument(member)})";
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"conversion of '{receiver.Type.DisplayName}' to string");
        return captureForDefer
            ? new IrExpressionValue { Type = CType.String, Code = code, Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned }
            : OwnResult(CType.String, code, receiver.Prelude);
    }
}
