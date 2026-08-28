using System.Collections.Immutable;
using System.Numerics;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private IEnumerable<MethodSymbol> ExpandGenericCandidates(
        IEnumerable<MethodSymbol> candidates,
        ImmutableArray<TypeSyntax> explicitArguments,
        IReadOnlyList<IrExpressionValue> arguments,
        SyntaxNode syntax)
    {
        foreach (var candidate in candidates)
        {
            if (!candidate.IsGenericDefinition)
            {
                if (explicitArguments.IsDefaultOrEmpty)
                    yield return candidate;
                continue;
            }
            ImmutableArray<CType> typeArguments;
            if (!explicitArguments.IsDefaultOrEmpty)
            {
                if (explicitArguments.Length != candidate.TypeParameters.Length)
                    continue;
                typeArguments = _model.ResolveGenericArguments(candidate.TypeParameters, explicitArguments, TreeFor(syntax), syntax, _method.TypeSubstitutions);
            }
            else
            {
                if (candidate.TypeParameters.Any(parameter => parameter.IsConstantParameter))
                    continue;
                var inferred = new Dictionary<string, CType>(StringComparer.Ordinal);
                if (candidate.Parameters.Length != arguments.Count)
                    continue;
                var valid = true;
                for (var index = 0; index < arguments.Count; index++)
                    valid &= InferTypeArguments(candidate.Parameters[index].Type, arguments[index].Type, inferred);
                if (!valid || candidate.TypeParameters.Any(parameter => !inferred.ContainsKey(parameter.Name)))
                    continue;
                typeArguments = candidate.TypeParameters.Select(parameter => inferred[parameter.Name]).ToImmutableArray();
            }
            var constructed = _model.ConstructGenericMethod(candidate, typeArguments, syntax);
            if (constructed is not null)
                yield return constructed;
        }
    }

    private static bool InferTypeArguments(CType parameter, CType argument, Dictionary<string, CType> inferred)
    {
        if (parameter.Kind == CTypeKind.TypeParameter && parameter.Symbol is not null)
        {
            if (!inferred.TryGetValue(parameter.Symbol.Name, out var existing))
            {
                inferred.Add(parameter.Symbol.Name, argument);
                return true;
            }
            if (existing == argument)
                return true;
            if (TypeFacts.CanImplicitlyConvert(argument, existing))
                return true;
            if (TypeFacts.CanImplicitlyConvert(existing, argument))
            {
                inferred[parameter.Symbol.Name] = argument;
                return true;
            }
            return false;
        }
        if (parameter.Kind != argument.Kind)
            return false;
        if (parameter.ElementType is not null || argument.ElementType is not null)
            return parameter.ElementType is not null && argument.ElementType is not null && InferTypeArguments(parameter.ElementType, argument.ElementType, inferred);
        if (parameter.Symbol?.GenericDefinition is { } parameterDefinition && argument.Symbol?.GenericDefinition == parameterDefinition)
        {
            if (parameter.Symbol.TypeArguments.Length != argument.Symbol.TypeArguments.Length)
                return false;
            for (var index = 0; index < parameter.Symbol.TypeArguments.Length; index++)
                if (!InferTypeArguments(parameter.Symbol.TypeArguments[index], argument.Symbol.TypeArguments[index], inferred))
                    return false;
        }
        return true;
    }

    private MethodSymbol? SelectOverload(IEnumerable<MethodSymbol> candidates, string name, IReadOnlyList<IrExpressionValue> arguments, ImmutableArray<ArgumentSyntax> argumentSyntax, SyntaxNode syntax)
        => SelectOverload(candidates, arguments, argumentSyntax, syntax, "CT2122", "CT2123", $"No overload of '{name}' accepts the supplied argument types.", $"Call to '{name}' is ambiguous.");

    private MethodSymbol? SelectOperatorOverload(IEnumerable<MethodSymbol> candidates, SyntaxKind operatorKind, IReadOnlyList<IrExpressionValue> arguments, ImmutableArray<ArgumentSyntax> argumentSyntax, SyntaxNode syntax)
        => SelectOverload(
            candidates,
            arguments,
            argumentSyntax,
            syntax,
            "CT2167",
            "CT2168",
            $"No applicable '{OperatorFacts.DisplayName(operatorKind)}' exists for the operand types.",
            $"The '{OperatorFacts.DisplayName(operatorKind)}' invocation is ambiguous.");

    private MethodSymbol? SelectOverload(
        IEnumerable<MethodSymbol> candidates,
        IReadOnlyList<IrExpressionValue> arguments,
        ImmutableArray<ArgumentSyntax> argumentSyntax,
        SyntaxNode syntax,
        string noMatchCode,
        string ambiguousCode,
        string noMatchMessage,
        string ambiguousMessage)
    {
        var matches = candidates
            .Where(candidate => candidate.Parameters.Length == arguments.Count)
            .Where(candidate => candidate.Parameters.Select((parameter, index) => parameter.PassingKind == argumentSyntax[index].PassingKind).All(matches => matches))
            .Where(candidate => candidate.Parameters
                .Select((parameter, index) => CanConvertExpression(arguments[index], parameter.Type))
                .All(valid => valid))
            .ToArray();
        if (matches.Length == 0)
        {
            Report(noMatchCode, noMatchMessage, syntax);
            return null;
        }
        var winners = matches.Where(candidate => matches.All(other =>
            ReferenceEquals(candidate, other) || IsBetterCandidate(candidate, other, arguments))).ToArray();
        if (winners.Length != 1)
        {
            Report(ambiguousCode, ambiguousMessage, syntax);
            return null;
        }
        return winners[0];
    }

    private static bool IsBetterCandidate(MethodSymbol candidate, MethodSymbol other, IReadOnlyList<IrExpressionValue> arguments)
    {
        var better = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var comparison = CompareConversion(arguments[index].Type, candidate.Parameters[index].Type, other.Parameters[index].Type);
            if (comparison > 0)
                return false;
            better |= comparison < 0;
        }
        return better;
    }

    private static int CompareConversion(CType source, CType leftTarget, CType rightTarget)
    {
        if (leftTarget == rightTarget)
            return 0;
        if (source == leftTarget)
            return -1;
        if (source == rightTarget)
            return 1;
        var leftToRight = TypeFacts.CanImplicitlyConvert(leftTarget, rightTarget);
        var rightToLeft = TypeFacts.CanImplicitlyConvert(rightTarget, leftTarget);
        if (leftToRight != rightToLeft)
            return leftToRight ? -1 : 1;
        if (source.IsIntegral && leftTarget.IsIntegral && rightTarget.IsIntegral)
        {
            var leftSigned = leftTarget.Kind is CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int;
            var rightSigned = rightTarget.Kind is CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int;
            if (leftSigned != rightSigned)
                return leftSigned ? -1 : 1;
        }
        return 0;
    }

    private static bool CanConvertExpression(IrExpressionValue expression, CType target) =>
        expression.MethodGroup is { } group
            ? target.Kind == CTypeKind.Delegate && FindDelegateMethod(group, target.Symbol!) is not null
            : TypeFacts.CanImplicitlyConvert(expression.Type, target) || CanImplicitNativeConstant(expression, target);

    private static bool CanImplicitNativeConstant(IrExpressionValue expression, CType target)
    {
        if (!expression.IsConstant || target.Kind is not CTypeKind.Nint and not CTypeKind.Nuint || !TryIntegralConstant(expression.ConstantValue, out var value))
            return false;
        return target == CType.Nint
            ? value >= int.MinValue && value <= int.MaxValue
            : value >= uint.MinValue && value <= uint.MaxValue;
    }

    private static bool TryIntegralConstant(object? value, out BigInteger result)
    {
        switch (value)
        {
            case byte item: result = item; return true;
            case sbyte item: result = item; return true;
            case short item: result = item; return true;
            case ushort item: result = item; return true;
            case int item: result = item; return true;
            case uint item: result = item; return true;
            case long item: result = item; return true;
            case ulong item: result = item; return true;
            case BigInteger item: result = item; return true;
            default: result = default; return false;
        }
    }

    private static MethodSymbol? FindDelegateMethod(MethodGroupBinding group, TypeSymbol delegateType)
    {
        var matches = group.Candidates.Where(candidate =>
            candidate.ReturnType == delegateType.DelegateReturnType &&
            candidate.Parameters.Select(parameter => (parameter.Type, parameter.PassingKind)).SequenceEqual(delegateType.DelegateParameters.Select(parameter => (parameter.Type, parameter.PassingKind)))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private (List<string> Prelude, List<string> Codes, List<string> Postlude) LowerArguments(IReadOnlyList<IrExpressionValue> arguments, ImmutableArray<ParameterSymbol> parameters, ImmutableArray<ArgumentSyntax> syntax)
    {
        var prelude = new List<string>();
        var codes = new List<string>();
        var postlude = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var parameter = parameters[index];
            var argumentSyntax = syntax[index];
            if (parameter.PassingKind != ParameterPassingKind.Value)
            {
                var argument = arguments[index];
                prelude.AddRange(argument.Prelude);
                if (argument.LValue?.Field is { ContainingType.HasNonNaturalLayout: true })
                    Report("CT2190", "A field in a packed or explicit-layout aggregate cannot be passed by reference.", argumentSyntax);
                if (argument.LValue?.Address is not { } address || argument.Type != parameter.Type)
                {
                    if (argument.Symbol is FieldSymbol { IsRegister: true })
                        Report("CT2210", "A fixed-address register cannot be passed by reference.", argumentSyntax);
                    else
                        Report("CT2171", $"A '{parameter.PassingKind.ToString().ToLowerInvariant()}' argument must be an addressable variable of exact type '{parameter.Type.DisplayName}'.", argumentSyntax);
                    codes.Add("NULL");
                    continue;
                }
                if (parameter.PassingKind is ParameterPassingKind.Ref or ParameterPassingKind.Out)
                {
                    if (argument.LValue.IsConstInitStorage)
                        Report("CT2219", "ConstInit storage can be passed only with 'in'.", argumentSyntax);
                    else if (IsReadonly(argument.LValue))
                        Report("CT2172", "Readonly storage can be passed only with 'in'.", argumentSyntax);
                }
                if (parameter.PassingKind == ParameterPassingKind.Out)
                {
                    if (argument.Type.ContainsManagedReferences && !IsUninitializedOut(argument.LValue))
                        prelude.Add(_emitter.DropValueStatement(argument.Type, address));
                    prelude.Add($"*({address}) = {_emitter.DefaultValue(argument.Type)};");
                    MarkAssigned(argument.LValue);
                    if (parameter.NativeOwnership == NativeParameterOwnership.Creates && argument.LValue.Local is { } createdLocal)
                        createdLocal.NativeResourceState = NativeResourceState.Owned;
                }
                else if (parameter.PassingKind == ParameterPassingKind.Ref)
                    MarkAssigned(argument.LValue);
                codes.Add(address);
                continue;
            }
            var converted = Convert(arguments[index], parameter.Type, argumentSyntax.Expression, false);
            if (parameter.NativeOwnership is NativeParameterOwnership.Consumes or NativeParameterOwnership.Retained)
                ConsumeOwnedExpression(converted, argumentSyntax.Expression);
            prelude.AddRange(converted.Prelude);
            if (converted.Type.Kind == CTypeKind.Void)
            {
                codes.Add(converted.Code);
                continue;
            }
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(converted.Type, temp)} = {converted.Code};");
            if (!parameter.IsNullable && !converted.IsKnownNonNull && parameter.Type.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
            {
                RecordRuntimeFault(argumentSyntax, "native argument null check");
                prelude.Add($"(void)ct_require_nonnull((void*){temp}, {_emitter.SourceArgument(argumentSyntax)});");
            }
            if (!parameter.IsNullable && parameter.Type.IsNativeUtf8String)
            {
                RecordRuntimeFault(argumentSyntax, "native UTF-8 argument null check");
                prelude.Add($"(void)ct_require_nonnull((void*){temp}.Data, {_emitter.SourceArgument(argumentSyntax)});");
            }
            if (parameter.IsSynchronousCallback)
            {
                if (!parameter.IsNullable)
                {
                    RecordRuntimeFault(argumentSyntax, "synchronous callback null check");
                    prelude.Add($"(void)ct_require_nonnull({temp}, {_emitter.SourceArgument(argumentSyntax)});");
                }
                prelude.Add($"ct_retain((ct_object*)(void*){temp});");
                var adapter = _emitter.SynchronousCallbackAdapterName(parameter.Type.Symbol!);
                codes.Add(parameter.IsNullable ? $"{temp} == NULL ? NULL : &{adapter}" : $"&{adapter}");
                codes.Add($"(void*){temp}");
                postlude.Add($"ct_release((ct_object*)(void*){temp});");
                continue;
            }
            if (parameter.IsRetained)
                prelude.Add($"ct_retain((ct_object*)(void*){temp});");
            if (converted.Type.IsNativeBuffer)
            {
                codes.Add($"{temp}.Data");
                codes.Add($"{temp}.Length");
            }
            else if (converted.Type.IsNativeUtf8String)
                codes.Add($"(const char*)(const void*){temp}.Data");
            else
                codes.Add(temp);
        }
        return (prelude, codes, postlude);
    }

    private (List<string> Prelude, List<string> Codes) CaptureDeferredArguments(IReadOnlyList<IrExpressionValue> arguments, ImmutableArray<ParameterSymbol> parameters, ImmutableArray<ArgumentSyntax> syntax)
    {
        var prelude = new List<string>();
        var codes = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (parameters[index].PassingKind != ParameterPassingKind.Value)
            {
                var argument = arguments[index];
                prelude.AddRange(argument.Prelude);
                if (argument.LValue?.Field is { ContainingType.HasNonNaturalLayout: true })
                    Report("CT2190", "A field in a packed or explicit-layout aggregate cannot be captured by reference.", syntax[index]);
                if (argument.LValue?.Address is not { } address || argument.Type != parameters[index].Type)
                {
                    Report("CT2171", "Deferred by-reference arguments must remain addressable in the enclosing scope.", syntax[index]);
                    codes.Add("NULL");
                }
                else
                {
                    if (_capturingDirectDefer)
                    {
                        var addressSlot = $"ct_df_{_deferId}_arg_{index}_address";
                        AddCapturedSlot(prelude, new CType(CTypeKind.Pointer, ElementType: argument.Type), addressSlot, address);
                        codes.Add(Durable(addressSlot));
                    }
                    else
                        codes.Add(address);
                }
                continue;
            }
            var converted = Convert(arguments[index], parameters[index].Type, syntax[index].Expression, false);
            if (parameters[index].NativeOwnership is NativeParameterOwnership.Consumes or NativeParameterOwnership.Retained)
            {
                if (converted.LValue?.Local is { NativeResourceState: NativeResourceState.Owned } resource)
                    resource.NativeResourceState = NativeResourceState.Deferred;
                else
                    Report("CT1261", "Deferred native cleanup requires an owned local resource.", syntax[index]);
            }
            prelude.AddRange(converted.Prelude);
            var slot = $"ct_df_{_deferId}_arg_{index}";
            AddCapturedSlot(prelude, converted.Type, slot, converted.Code);
            codes.Add(parameters[index].IsRetained
                ? $"(ct_retain((ct_object*)(void*){Durable(slot)}), {Durable(slot)})"
                : converted.Type.IsNativeUtf8String
                    ? $"(const char*)(const void*){Durable(slot)}.Data"
                    : Durable(slot));
        }
        return (prelude, codes);
    }

    private static bool IsReadonly(IrValueStorage lvalue) => lvalue.IsConstInitStorage || lvalue.Local?.IsReadonly == true || lvalue.Local?.IsConst == true || lvalue.Field?.IsReadonly == true;

    private void ConsumeOwnedExpression(IrExpressionValue expression, SyntaxNode syntax)
    {
        if (expression.Ownership != OwnershipKind.Owned)
        {
            Report("CT1259", "This operation requires ownership of the native resource.", syntax);
            return;
        }
        if (expression.LValue?.Local is { } local)
        {
            if (local.NativeResourceState == NativeResourceState.Deferred)
                Report("CT1260", $"Native resource '{local.Name}' is already reserved by defer.", syntax);
            else if (local.NativeResourceState != NativeResourceState.Owned)
                Report("CT1254", $"Owned native resource '{local.Name}' has already moved.", syntax);
            else
                local.NativeResourceState = NativeResourceState.Moved;
        }
    }
}
