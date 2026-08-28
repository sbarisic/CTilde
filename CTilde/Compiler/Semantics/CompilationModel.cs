using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CTilde;

internal sealed partial class CompilationModel
{
    private static readonly Regex CIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "auto", "bool", "break", "case", "char", "const", "constexpr", "continue",
        "default", "do", "double", "else", "enum", "extern", "false", "float", "for", "goto", "if", "inline",
        "int", "long", "nullptr", "register", "restrict", "return", "short", "signed", "sizeof", "static",
        "static_assert", "struct", "switch", "thread_local", "true", "typedef", "typeof", "typeof_unqual", "union",
        "unsigned", "void", "volatile", "while",
    };
    private readonly Dictionary<SyntaxTree, string> _namespaces = [];
    private readonly Dictionary<SyntaxTree, ImmutableArray<string>> _usings = [];
    private readonly Dictionary<(TypeSymbol Definition, string Arguments), TypeSymbol> _constructedTypes = [];
    private readonly Dictionary<(MethodSymbol Definition, string Arguments), MethodSymbol> _constructedMethods = [];
    private IReadOnlyDictionary<string, CType> _activeTypeParameters = ImmutableDictionary<string, CType>.Empty;
    private readonly CompilationTarget _target;
    private readonly CompilationArchitecture _architecture;

    public CompilationModel(ImmutableArray<SyntaxTree> syntaxTrees, ImmutableArray<SyntaxTree> userSyntaxTrees, DiagnosticBag diagnostics, CompilationTarget target, CompilationArchitecture architecture, ImmutableArray<CpuFeature> cpuFeatures = default)
    {
        _target = target;
        _architecture = architecture;
        CpuFeatures = (cpuFeatures.IsDefault ? ImmutableArray<CpuFeature>.Empty : cpuFeatures).ToImmutableHashSet();
        SyntaxTrees = syntaxTrees;
        UserSyntaxTrees = userSyntaxTrees;
        Diagnostics = diagnostics;
        Types = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
        DeclareTypes();
        ValidateUsings();
        ResolveBaseTypes();
        DeclareMembers();
        ValidateInheritanceMembers();
        Documentation = DocumentationIndex.Build(this, target);
        ValidateRecursivePointerExposure();
        ValidateExternalSymbols();
        ValidateNativeSections();
        ValidateEntryPoint();
        ValidateRuntimeImplementations();
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public ImmutableArray<SyntaxTree> UserSyntaxTrees { get; }
    public CompilationTarget Target => _target;
    public CompilationArchitecture Architecture => _architecture;
    public ImmutableHashSet<CpuFeature> CpuFeatures { get; }
    public DiagnosticBag Diagnostics { get; }
    public List<BoundStaticAssertion> StaticAssertions { get; } = [];
    public ImmutableDictionary<FieldSymbol, ConstDataValue> ConstInitializers { get; set; } = ImmutableDictionary<FieldSymbol, ConstDataValue>.Empty;
    public HashSet<TypeSymbol> StaticAssertionLayoutTypes { get; } = [];
    public Dictionary<string, TypeSymbol> Types { get; }
    public Dictionary<LambdaExpressionSyntax, MethodSymbol> LambdaMethods { get; } = [];
    public Dictionary<LambdaExpressionSyntax, TypeSymbol> LambdaEnvironments { get; } = [];
    public DocumentationIndex Documentation { get; }
    public IEnumerable<TypeSymbol> UserTypes => Types.Values.Where(type => type.Syntax is not null && type.Kind != DeclaredTypeKind.TypeParameter && !type.IsGenericDefinition && !type.IsOpenConstructed).Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal);
    public MethodSymbol? EntryPoint { get; private set; }
    public IReadOnlyDictionary<RuntimeImplementationRole, MethodSymbol> RuntimeImplementations { get; private set; } =
        ImmutableDictionary<RuntimeImplementationRole, MethodSymbol>.Empty;
    public bool FreestandingRuntimeRequired { get; set; }
    public bool FreestandingHeapRequired { get; set; }
    public EffectAnalysis Effects { get; set; } = EffectAnalysis.Empty;

    public SourceOwnerIdentity? SourceOwnerFor(SourceText source) =>
        SyntaxTrees.FirstOrDefault(tree => ReferenceEquals(tree.Text, source))?.SourceOwner;

    public CType ResolveType(TypeSyntax syntax, SyntaxTree tree, bool report = true)
    {
        if (syntax.TypeArguments.IsDefaultOrEmpty && _activeTypeParameters.TryGetValue(syntax.Name, out var parameterType))
        {
            if (parameterType.Kind == CTypeKind.Constant)
            {
                if (report)
                    Diagnostics.Add("CT2202", $"Constant parameter '{syntax.Name}' is a value and cannot be used as a type.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            return ApplyTypeShape(parameterType, syntax, report);
        }
        if ((syntax.Name is "NativeUtf8String" or "System.Runtime.NativeUtf8String") && syntax.TypeArguments.IsDefaultOrEmpty)
        {
            if (syntax.PointerDepth != 0 || syntax.IsArray)
            {
                if (report)
                    Diagnostics.Add("CT1263", "NativeUtf8String cannot be a pointer or array element type.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            return new CType(CTypeKind.NativeUtf8String);
        }
        if (!syntax.TypeArguments.IsDefaultOrEmpty)
        {
            var bufferKind = syntax.Name is "NativeBuffer" or "System.Runtime.NativeBuffer"
                ? CTypeKind.NativeBuffer
                : syntax.Name is "ReadOnlyNativeBuffer" or "System.Runtime.ReadOnlyNativeBuffer"
                    ? CTypeKind.ReadOnlyNativeBuffer
                    : CTypeKind.Error;
            if (bufferKind == CTypeKind.Error)
            {
                var definitions = ResolveNamedTypeCandidates(syntax.Name, tree, syntax.TypeArguments.Length).ToArray();
                if (definitions.Length != 1)
                {
                    if (report)
                        Diagnostics.Add("CT2176", definitions.Length == 0
                            ? $"Generic type '{syntax.Name}' with {syntax.TypeArguments.Length} type argument(s) could not be found."
                            : $"Generic type '{syntax.Name}' is ambiguous.", syntax.Source, syntax.Span);
                    return CType.Error;
                }
                var arguments = ResolveGenericArguments(definitions[0].TypeParameters, syntax.TypeArguments, tree, syntax, report);
                if (syntax.PointerDepth != 0 || syntax.IsArray)
                    return ApplyTypeShape(ConstructGenericType(definitions[0], arguments, tree, syntax).Type, syntax, report);
                return ConstructGenericType(definitions[0], arguments, tree, syntax).Type;
            }
            if (syntax.TypeArguments.Length != 1)
            {
                if (report)
                    Diagnostics.Add("CT2176", $"{syntax.Name} requires one type argument.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            var element = ResolveType(syntax.TypeArguments[0], tree, report);
            if (!IsCompleteUnmanagedType(element))
            {
                if (report)
                    Diagnostics.Add("CT2177", $"Native-buffer element type '{element.DisplayName}' must be a complete unmanaged type.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            if (syntax.PointerDepth != 0 || syntax.IsArray)
            {
                if (report)
                    Diagnostics.Add("CT2178", "Native-buffer views cannot be pointer or array element types.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            return new CType(bufferKind, ElementType: element);
        }
        if (syntax.FunctionPointer is not null)
        {
            var elements = syntax.FunctionPointer.Elements.Select(element => ResolveType(element.Type, tree, report)).ToImmutableArray();
            if (elements.Length == 0)
                return CType.Error;
            var returnType = elements[^1];
            var parameters = elements.RemoveAt(elements.Length - 1);
            var passingKinds = syntax.FunctionPointer.Elements.RemoveAt(syntax.FunctionPointer.Elements.Length - 1).Select(element => element.PassingKind).ToImmutableArray();
            foreach (var parameter in parameters.Where(parameter => !IsUnmanagedFunctionPointerElement(parameter, allowVoid: false)))
                if (report)
                    Diagnostics.Add("CT2162", $"Function-pointer parameter type '{parameter.DisplayName}' is not unmanaged.", syntax.Source, syntax.Span);
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].IsNativeBuffer && passingKinds[index] != ParameterPassingKind.Value && report)
                    Diagnostics.Add("CT2187", "Native-buffer function-pointer parameters cannot use ref, in, or out.", syntax.Source, syntax.FunctionPointer.Elements[index].Span);
            if (!IsUnmanagedFunctionPointerElement(returnType, allowVoid: true) && report)
                Diagnostics.Add("CT2162", $"Function-pointer return type '{returnType.DisplayName}' is not unmanaged.", syntax.Source, syntax.Span);
            if (syntax.FunctionPointer.Elements[^1].PassingKind != ParameterPassingKind.Value && report)
                Diagnostics.Add("CT2166", "A function-pointer return type cannot have a passing modifier.", syntax.Source, syntax.FunctionPointer.Elements[^1].Span);
            return new CType(CTypeKind.FunctionPointer, FunctionPointer: new FunctionPointerSignature(parameters, passingKinds, returnType));
        }
        var baseType = syntax.Name == "object" && Types.TryGetValue("System.Object", out var objectType)
            ? objectType.Type
            : TypeFacts.BuiltIn(syntax.Name);
        if (baseType is null)
        {
            var candidates = ResolveNamedTypeCandidates(syntax.Name, tree).ToArray();
            if (candidates.Length > 1)
            {
                if (report)
                    Diagnostics.Add("CT1112", $"Type name '{syntax.Name}' is ambiguous between {string.Join(", ", candidates.Select(candidate => $"'{candidate.FullName}'"))}.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            var type = candidates.SingleOrDefault();
            if (type is null)
            {
                if (report)
                    Diagnostics.Add("CT1101", $"The type '{syntax.Name}' could not be found.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            baseType = type.Type;
        }

        if (baseType == CType.Void && syntax.IsArray)
        {
            Diagnostics.Add("CT2101", "void cannot be used as an element or pointed type.", syntax.Source, syntax.Span);
            return CType.Error;
        }

        if (baseType.Kind == CTypeKind.Opaque && (syntax.IsArray || syntax.PointerDepth != 0))
        {
            if (report)
                Diagnostics.Add("CT1241", "Opaque handles cannot be array elements or pointed types.", syntax.Source, syntax.Span);
            return CType.Error;
        }

        for (var i = 0; i < syntax.PointerDepth; i++)
            baseType = new CType(CTypeKind.Pointer, ElementType: baseType);
        if (syntax.IsArray && baseType.ContainsAtomic)
        {
            if (report)
                Diagnostics.Add("CT1278", "Atomic<T> cannot be used as an array element.", syntax.Source, syntax.Span);
            return CType.Error;
        }
        if (syntax.IsArray)
            baseType = new CType(CTypeKind.Array, ElementType: baseType);
        if (syntax.InlineArrayLength is not null)
            baseType = CreateInlineArray(baseType, syntax.InlineArrayLength, report);
        return baseType;
    }

    internal CType ResolveType(TypeSyntax syntax, SyntaxTree tree, IReadOnlyDictionary<string, CType> substitutions, bool report = true)
    {
        var previous = _activeTypeParameters;
        var builder = previous.ToImmutableDictionary(StringComparer.Ordinal).ToBuilder();
        foreach (var substitution in substitutions)
            builder[substitution.Key] = substitution.Value;
        _activeTypeParameters = builder.ToImmutable();
        try
        {
            return ResolveType(syntax, tree, report);
        }
        finally
        {
            _activeTypeParameters = previous;
        }
    }

    private CType ApplyTypeShape(CType baseType, TypeSyntax syntax, bool report)
    {
        if (baseType == CType.Void && syntax.IsArray)
        {
            if (report)
                Diagnostics.Add("CT2101", "void cannot be used as an element or pointed type.", syntax.Source, syntax.Span);
            return CType.Error;
        }
        for (var index = 0; index < syntax.PointerDepth; index++)
            baseType = new CType(CTypeKind.Pointer, ElementType: baseType);
        if (syntax.IsArray && baseType.ContainsAtomic)
        {
            if (report)
                Diagnostics.Add("CT1278", "Atomic<T> cannot be used as an array element.", syntax.Source, syntax.Span);
            return CType.Error;
        }
        if (syntax.IsArray)
            baseType = new CType(CTypeKind.Array, ElementType: baseType);
        if (syntax.InlineArrayLength is not null)
            baseType = CreateInlineArray(baseType, syntax.InlineArrayLength, report);
        return baseType;
    }

    private CType CreateInlineArray(CType element, ExpressionSyntax lengthSyntax, bool report)
    {
        if (element.Kind == CTypeKind.Void || element.IsError || element.ContainsAtomic)
        {
            if (report)
                Diagnostics.Add("CT2204", $"Inline-array element type '{element.DisplayName}' is not complete and storable.", lengthSyntax.Source, lengthSyntax.Span);
            return CType.Error;
        }
        if (lengthSyntax is LiteralExpressionSyntax { Value: NumericLiteralValue numeric } && numeric.FloatingPoint is null && numeric.Integer > BigInteger.Zero && numeric.Integer <= int.MaxValue)
            return new CType(CTypeKind.InlineArray, ElementType: element, InlineArrayLength: (int)numeric.Integer);
        if (lengthSyntax is NameExpressionSyntax name && _activeTypeParameters.TryGetValue(name.Name, out var parameter) && parameter.Kind == CTypeKind.Constant)
        {
            if (parameter.ConstantValue is BigInteger value && value > BigInteger.Zero && value <= int.MaxValue)
                return new CType(CTypeKind.InlineArray, ElementType: element, InlineArrayLength: (int)value);
            return new CType(CTypeKind.InlineArray, ElementType: element, InlineArrayLengthParameter: name.Name);
        }
        if (report)
            Diagnostics.Add("CT2204", "Inline-array length must be a positive compile-time integral value no greater than int.MaxValue.", lengthSyntax.Source, lengthSyntax.Span);
        return CType.Error;
    }

    private TypeSymbol ConstructGenericType(TypeSymbol definition, ImmutableArray<CType> arguments, SyntaxTree tree, SyntaxNode syntax)
    {
        if (!definition.IsGenericDefinition || definition.TypeParameters.Length != arguments.Length)
        {
            Diagnostics.Add("CT1271", $"Type '{definition.FullName}' does not accept {arguments.Length} type argument(s).", syntax.Source, syntax.Span);
            return definition;
        }
        ValidateGenericArguments(definition.TypeParameters, definition.TypeParameterConstraints, arguments, syntax);
        if (definition is { Namespace: "System.Threading", Name: "Atomic" } &&
            (arguments.Length != 1 || !IsAtomicElementType(arguments[0])))
            Diagnostics.Add("CT1277", "Atomic<T> requires Boolean, integral, native-integral, enum, or unsafe-pointer T.", syntax.Source, syntax.Span);
        var identity = string.Join(";", arguments.Select(NameMangler.CanonicalType));
        if (_constructedTypes.TryGetValue((definition, identity), out var existing))
            return existing;
        if (_constructedTypes.Count >= 1024)
        {
            Diagnostics.Add("CT1272", "Generic instantiation exceeded the deterministic limit of 1024 closed types.", syntax.Source, syntax.Span);
            return definition;
        }
        var constructed = new TypeSymbol
        {
            Namespace = definition.Namespace,
            Name = definition.Name,
            Kind = definition.Kind,
            Syntax = definition.Syntax,
            Accessibility = definition.Accessibility,
            NativeTypeName = definition.NativeTypeName,
            NativeHeader = definition.NativeHeader,
            AggregateLayout = definition.AggregateLayout,
            Pack = definition.Pack,
            Alignment = ResolveAlignment(definition.Alignment, definition.AlignmentParameter,
                definition.TypeParameters.Select((parameter, index) => (parameter.Name, arguments[index])).ToImmutableDictionary(pair => pair.Name, pair => pair.Item2, StringComparer.Ordinal), syntax),
            AlignmentParameter = definition.AlignmentParameter,
            BitFieldBackingSyntax = definition.BitFieldBackingSyntax,
            BitFieldBackingType = definition.BitFieldBackingType,
            IsSealed = definition.IsSealed,
            IsAbstract = definition.IsAbstract,
            TypeArguments = arguments,
            GenericDefinition = definition,
        };
        _constructedTypes.Add((definition, identity), constructed);
        Types.TryAdd(constructed.FullName, constructed);
        PopulateConstructedType(constructed);
        return constructed;
    }

    private void ValidateGenericArguments(
        ImmutableArray<TypeSymbol> parameters,
        IReadOnlyDictionary<string, GenericConstraintSet> constraints,
        ImmutableArray<CType> arguments,
        SyntaxNode syntax)
    {
        for (var index = 0; index < Math.Min(parameters.Length, arguments.Length); index++)
        {
            var parameter = parameters[index];
            var argument = arguments[index];
            if (parameter.IsConstantParameter)
            {
                if (argument.Kind != CTypeKind.Constant || argument.ConstantValue is null || argument.ElementType != parameter.ConstantParameterType)
                    Diagnostics.Add("CT2202", $"Constant argument '{argument.DisplayName}' is not valid for '{parameter.Name}'.", syntax.Source, syntax.Span);
                continue;
            }
            if (argument.Kind == CTypeKind.Constant)
            {
                Diagnostics.Add("CT2202", $"Type parameter '{parameter.Name}' requires a type argument.", syntax.Source, syntax.Span);
                continue;
            }
            if (!constraints.TryGetValue(parameter.Name, out var constraint))
                continue;
            var valid = (!constraint.RequiresClass || argument.IsReference) &&
                (!constraint.RequiresStruct || argument.IsValueType) &&
                (!constraint.RequiresUnmanaged || IsCompleteUnmanagedType(argument)) &&
                (constraint.BaseType is null || TypeFacts.CanImplicitlyConvert(argument, constraint.BaseType)) &&
                (constraint.Interfaces.IsDefaultOrEmpty || constraint.Interfaces.All(contract => TypeFacts.CanImplicitlyConvert(argument, contract))) &&
                (!constraint.RequiresConstructor || HasPublicParameterlessConstructor(argument));
            if (!valid)
                Diagnostics.Add("CT1271", $"Type argument '{argument.DisplayName}' does not satisfy constraints for '{parameter.Name}'.", syntax.Source, syntax.Span);
        }
    }

    private static bool HasPublicParameterlessConstructor(CType type) => type.IsValueType ||
        type.Symbol is { IsAbstract: false } symbol && symbol.Constructors.Any(constructor => constructor.Accessibility == Accessibility.Public && constructor.Parameters.Length == 0);

    private static bool IsAtomicElementType(CType type) => type.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or
        CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Enum or CTypeKind.Pointer;

    internal MethodSymbol? ConstructGenericMethod(MethodSymbol definition, ImmutableArray<CType> arguments, SyntaxNode syntax)
    {
        if (!definition.IsGenericDefinition || definition.TypeParameters.Length != arguments.Length)
        {
            Diagnostics.Add("CT1271", $"Method '{definition.Name}' does not accept {arguments.Length} type argument(s).", syntax.Source, syntax.Span);
            return null;
        }
        ValidateGenericArguments(definition.TypeParameters, definition.TypeParameterConstraints, arguments, syntax);
        ValidateSimdLaneArguments(definition, arguments, syntax);
        var identity = string.Join(";", arguments.Select(NameMangler.CanonicalType));
        if (_constructedMethods.TryGetValue((definition, identity), out var existing))
            return existing;
        if (_constructedMethods.Count >= 4096)
        {
            Diagnostics.Add("CT1272", $"Generic method instantiation exceeded the deterministic limit of 4096 while expanding '{definition.Name}<{string.Join(", ", arguments.Select(argument => argument.DisplayName))}>'.", syntax.Source, syntax.Span);
            return null;
        }
        var substitutions = definition.TypeSubstitutions.ToBuilder();
        for (var index = 0; index < definition.TypeParameters.Length; index++)
            substitutions[definition.TypeParameters[index].Name] = arguments[index];
        var constructed = WithGeneric(CloneMethod(definition, definition.ContainingType, substitutions.ToImmutable()), arguments, definition);
        _constructedMethods.Add((definition, identity), constructed);
        definition.ContainingType.Methods.Add(constructed);
        return constructed;

        static MethodSymbol WithGeneric(MethodSymbol method, ImmutableArray<CType> typeArguments, MethodSymbol genericDefinition) => new()
        {
            Name = method.Name,
            ContainingType = method.ContainingType,
            Accessibility = method.Accessibility,
            IsStatic = method.IsStatic,
            Syntax = method.Syntax,
            ReturnType = method.ReturnType,
            Parameters = method.Parameters,
            Body = method.Body,
            AssemblyBody = method.AssemblyBody,
            IsConstructor = method.IsConstructor,
            IsEntryPoint = method.IsEntryPoint,
            DeclaredEffects = method.DeclaredEffects,
            IsNoRecursion = method.IsNoRecursion,
            IsUnsafe = method.IsUnsafe,
            ReturnsBorrowed = method.ReturnsBorrowed,
            ReturnsOwned = method.ReturnsOwned,
            ReturnsNullable = method.ReturnsNullable,
            ExternName = method.ExternName,
            ExportName = method.ExportName,
            SectionName = method.SectionName,
            IsUsed = method.IsUsed,
            RuntimeImplementation = method.RuntimeImplementation,
            IsNaked = method.IsNaked,
            IsInterrupt = method.IsInterrupt,
            IsInterruptSafe = method.IsInterruptSafe,
            IsInterruptCode = method.IsInterruptCode,
            TaskStackSize = method.TaskStackSize,
            IsTrustedExtern = method.IsTrustedExtern,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsSealedOverride = method.IsSealedOverride,
            IsAbstract = method.IsAbstract,
            IsOperator = method.IsOperator,
            OperatorKind = method.OperatorKind,
            ConstructorInitializer = method.ConstructorInitializer,
            TypeParameters = method.TypeParameters,
            TypeParameterConstraints = method.TypeParameterConstraints,
            TypeArguments = typeArguments,
            TypeSubstitutions = method.TypeSubstitutions,
            GenericDefinition = genericDefinition,
        };
    }

    private void ValidateSimdLaneArguments(MethodSymbol definition, ImmutableArray<CType> arguments, SyntaxNode syntax)
    {
        if (definition.ContainingType.Namespace != "System.Simd" ||
            definition.ContainingType.Name is not ("F32x4" or "I32x4" or "U32x4" or "Mask32x4") ||
            definition.Name is not ("GetLane" or "WithLane" or "Shuffle"))
            return;

        foreach (var argument in arguments)
        {
            if (argument.Kind == CTypeKind.Constant && argument.ConstantValue is BigInteger lane && (lane < BigInteger.Zero || lane > new BigInteger(3)))
                Diagnostics.Add("CT2220", $"SIMD lane index '{lane}' is outside the fixed range 0..3.", syntax.Source, syntax.Span);
        }
    }

    private void FinalizeConstructedTypes()
    {
        var completed = new HashSet<TypeSymbol>();
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var type in _constructedTypes.Values.ToArray())
            {
                if (completed.Contains(type) || type.IsOpenConstructed)
                    continue;
                PopulateConstructedType(type);
                completed.Add(type);
                progress = true;
            }
        }
    }

    private void PopulateConstructedType(TypeSymbol type)
    {
        if (type.GenericDefinition is not { } definition || type.Fields.Count != 0 || type.Methods.Count != 0 || type.Properties.Count != 0 || type.Constructors.Count != 0)
            return;
        var substitutions = definition.TypeParameters.Select((parameter, index) => (parameter.Name, Type: type.TypeArguments[index]))
            .ToImmutableDictionary(pair => pair.Name, pair => pair.Type, StringComparer.Ordinal);
        if (definition.UnderlyingType is not null)
            type.UnderlyingType = SubstituteType(definition.UnderlyingType, substitutions);
        if (definition.BitFieldBackingType is not null)
            type.BitFieldBackingType = SubstituteType(definition.BitFieldBackingType, substitutions);
        if (definition.BaseType is not null)
            type.BaseType = SubstituteType(definition.BaseType.Type, substitutions).Symbol;
        foreach (var contract in definition.Interfaces)
        {
            var substituted = SubstituteType(contract.Type, substitutions).Symbol;
            if (substituted is not null && !type.Interfaces.Contains(substituted))
                type.Interfaces.Add(substituted);
        }
        foreach (var field in definition.Fields)
        {
            var substitutedType = SubstituteType(field.Type, substitutions);
            var registerAddress = ResolveRegisterAddress(field.RegisterAddress, field.RegisterAddressParameter, substitutions, field.Syntax ?? type.Syntax!);
            if (field.IsRegister && registerAddress is { } address)
            {
                var width = MmioStorageWidth(substitutedType);
                var pointerBits = _architecture is CompilationArchitecture.X64 or CompilationArchitecture.Arm64 or CompilationArchitecture.RiscV64 ? 64 : 32;
                if (width == 0 || address < 0 || address >= (BigInteger.One << pointerBits) || address % width != 0)
                    Diagnostics.Add("CT2210", $"Register address for '{field.Name}' must fit the selected pointer width and satisfy its natural alignment.",
                        field.Syntax!.Source, field.Syntax.Span);
            }
            type.Fields.Add(new FieldSymbol
            {
                Name = field.Name,
                ContainingType = type,
                Accessibility = field.Accessibility,
                IsStatic = field.IsStatic,
                Syntax = field.Syntax,
                Type = substitutedType,
                IsReadonly = field.IsReadonly,
                IsConst = field.IsConst,
                IsVolatile = field.IsVolatile,
                IsUnsafe = field.IsUnsafe,
                Initializer = field.Initializer,
                Offset = field.Offset,
                Alignment = ResolveAlignment(field.Alignment, field.AlignmentParameter, substitutions, field.Syntax ?? type.Syntax!),
                AlignmentParameter = field.AlignmentParameter,
                SectionName = field.SectionName,
                ExternName = field.ExternName,
                LinkerSymbolName = field.LinkerSymbolName,
                IsNativeVolatile = field.IsNativeVolatile,
                IsUsed = field.IsUsed,
                IsInterruptSafe = field.IsInterruptSafe,
                IsInterruptData = field.IsInterruptData,
                BitFirst = field.BitFirst,
                BitLast = field.BitLast,
                RegisterAddress = registerAddress,
                RegisterAddressParameter = field.RegisterAddressParameter,
                EmbeddedData = field.EmbeddedData,
                EmbeddedResourceIdentity = field.EmbeddedResourceIdentity,
            });
        }
        foreach (var property in definition.Properties)
        {
            var cloned = new PropertySymbol
            {
                Name = property.Name,
                ContainingType = type,
                Accessibility = property.Accessibility,
                IsStatic = property.IsStatic,
                Syntax = property.Syntax,
                Type = SubstituteType(property.Type, substitutions),
                Getter = property.Getter,
                Setter = property.Setter,
                BackingField = property.BackingField is null ? null : type.Fields.FirstOrDefault(field => field.Name == property.BackingField.Name),
                GetterAccessibility = property.GetterAccessibility,
                SetterAccessibility = property.SetterAccessibility,
                IsVirtual = property.IsVirtual,
                IsOverride = property.IsOverride,
                IsSealedOverride = property.IsSealedOverride,
                IsAbstract = property.IsAbstract,
                DeclaredEffects = property.DeclaredEffects,
                GetterDeclaredEffects = property.GetterDeclaredEffects,
                SetterDeclaredEffects = property.SetterDeclaredEffects,
                IsNoRecursion = property.IsNoRecursion,
            };
            cloned.ImplementedInterfaceProperties.AddRange(property.ImplementedInterfaceProperties);
            type.Properties.Add(cloned);
        }
        foreach (var method in definition.Methods)
            type.Methods.Add(CloneMethod(method, type, substitutions));
        foreach (var constructor in definition.Constructors)
            type.Constructors.Add(CloneMethod(constructor, type, substitutions));
        foreach (var contract in EnumerateInterfaces(type))
        {
            foreach (var required in contract.Methods)
            {
                var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Methods)
                    .FirstOrDefault(candidate => candidate.Accessibility == Accessibility.Public && !candidate.IsStatic && !candidate.IsAbstract &&
                        HaveSameSourceSignature(candidate, required) && candidate.ReturnType == required.ReturnType);
                if (implementation is not null)
                {
                    if (!implementation.ImplementedInterfaceMethods.Contains(required))
                        implementation.ImplementedInterfaceMethods.Add(required);
                    implementation.DeclaredEffects |= required.DeclaredEffects;
                }
            }
            foreach (var required in contract.Properties)
            {
                var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Properties)
                    .FirstOrDefault(candidate => ResolvesPropertyContract(candidate, required));
                if (implementation is not null)
                {
                    if (!implementation.ImplementedInterfaceProperties.Contains(required))
                        implementation.ImplementedInterfaceProperties.Add(required);
                    implementation.DeclaredEffects |= required.DeclaredEffects;
                    implementation.GetterDeclaredEffects |= required.GetterDeclaredEffects;
                    implementation.SetterDeclaredEffects |= required.SetterDeclaredEffects;
                }
            }
        }
        if (definition.Kind == DeclaredTypeKind.Delegate)
        {
            type.DelegateReturnType = definition.DelegateReturnType is null ? null : SubstituteType(definition.DelegateReturnType, substitutions);
            type.DelegateParameters = definition.DelegateParameters.Select(parameter => new ParameterSymbol
            {
                Name = parameter.Name,
                Type = SubstituteType(parameter.Type, substitutions),
                Syntax = parameter.Syntax,
                PassingKind = parameter.PassingKind,
                IsRetained = parameter.IsRetained,
                NativeOwnership = parameter.NativeOwnership,
                IsNullable = parameter.IsNullable,
                IsSynchronousCallback = parameter.IsSynchronousCallback,
            }).ToImmutableArray();
        }
    }

    private MethodSymbol CloneMethod(MethodSymbol method, TypeSymbol containingType, ImmutableDictionary<string, CType> substitutions)
    {
        var cloned = new MethodSymbol
        {
            Name = method.Name,
            ContainingType = containingType,
            Accessibility = method.Accessibility,
            IsStatic = method.IsStatic,
            Syntax = method.Syntax,
            ReturnType = method.IsConstructor ? containingType.Type : SubstituteType(method.ReturnType, substitutions),
            Parameters = method.Parameters.Select(parameter => new ParameterSymbol
            {
                Name = parameter.Name,
                Type = SubstituteType(parameter.Type, substitutions),
                Syntax = parameter.Syntax,
                PassingKind = parameter.PassingKind,
                IsRetained = parameter.IsRetained,
                NativeOwnership = parameter.NativeOwnership,
                IsNullable = parameter.IsNullable,
                IsSynchronousCallback = parameter.IsSynchronousCallback,
            }).ToImmutableArray(),
            Body = method.Body,
            AssemblyBody = method.AssemblyBody,
            IsConstructor = method.IsConstructor,
            IsEntryPoint = method.IsEntryPoint,
            DeclaredEffects = method.DeclaredEffects,
            IsNoRecursion = method.IsNoRecursion,
            IsUnsafe = method.IsUnsafe,
            ReturnsBorrowed = method.ReturnsBorrowed,
            ReturnsOwned = method.ReturnsOwned,
            ReturnsNullable = method.ReturnsNullable,
            ExternName = method.ExternName,
            ExportName = method.ExportName,
            SectionName = method.SectionName,
            IsUsed = method.IsUsed,
            RuntimeImplementation = method.RuntimeImplementation,
            IsNaked = method.IsNaked,
            IsInterrupt = method.IsInterrupt,
            IsInterruptSafe = method.IsInterruptSafe,
            IsInterruptCode = method.IsInterruptCode,
            TaskStackSize = method.TaskStackSize,
            IsTrustedExtern = method.IsTrustedExtern,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsSealedOverride = method.IsSealedOverride,
            IsAbstract = method.IsAbstract,
            IsOperator = method.IsOperator,
            OperatorKind = method.OperatorKind,
            ConstructorInitializer = method.ConstructorInitializer,
            TypeParameters = method.TypeParameters,
            TypeParameterConstraints = method.TypeParameterConstraints,
            TypeSubstitutions = substitutions,
            GenericDefinition = method.GenericDefinition,
        };
        cloned.ImplementedInterfaceMethods.AddRange(method.ImplementedInterfaceMethods);
        return cloned;
    }

    internal CType SubstituteType(CType type, IReadOnlyDictionary<string, CType> substitutions)
    {
        if (type.Kind == CTypeKind.TypeParameter && type.Symbol is not null && substitutions.TryGetValue(type.Symbol.Name, out var replacement))
            return replacement;
        if (type.Kind is CTypeKind.Array or CTypeKind.Pointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer)
            return type with { ElementType = SubstituteType(type.ElementType!, substitutions) };
        if (type.Kind == CTypeKind.InlineArray)
        {
            var element = SubstituteType(type.ElementType!, substitutions);
            if (type.InlineArrayLengthParameter is { } name && substitutions.TryGetValue(name, out var value) && value.Kind == CTypeKind.Constant && value.ConstantValue is BigInteger length)
                return new CType(CTypeKind.InlineArray, ElementType: element, InlineArrayLength: (int)length);
            return type with { ElementType = element };
        }
        if (type.Symbol?.GenericDefinition is { } definition && !type.Symbol.TypeArguments.IsDefaultOrEmpty)
        {
            var arguments = type.Symbol.TypeArguments.Select(argument => SubstituteType(argument, substitutions)).ToImmutableArray();
            var source = definition.Syntax?.Source ?? type.Symbol.Syntax!.Source;
            var tree = SyntaxTrees.First(candidate => candidate.Text == source || candidate.Text.FilePath == source.FilePath);
            return ConstructGenericType(definition, arguments, tree, definition.Syntax!).Type;
        }
        return type;
    }

    private int? ResolveAlignment(int? fixedAlignment, string? parameterName, IReadOnlyDictionary<string, CType> substitutions, SyntaxNode syntax)
    {
        if (fixedAlignment is not null || parameterName is null)
            return fixedAlignment;
        if (substitutions.TryGetValue(parameterName, out var value) && value.Kind == CTypeKind.Constant && value.ConstantValue is { } constant &&
            constant >= BigInteger.One && constant <= new BigInteger(8192) && (constant & (constant - BigInteger.One)) == BigInteger.Zero)
            return (int)constant;
        Diagnostics.Add("CT1293", $"Align parameter '{parameterName}' must specialize to a power-of-two value from 1 through 8192.", syntax.Source, syntax.Span);
        return null;
    }

    private static bool IsUnmanagedFunctionPointerElement(CType type, bool allowVoid) =>
        type.Kind == CTypeKind.Void ? allowVoid :
        type.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
            CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Double or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer;

    private static bool IsCompleteUnmanagedType(CType type) => type.Kind switch
    {
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or
        CTypeKind.Float or CTypeKind.Double or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.FunctionPointer => true,
        CTypeKind.Newtype => type.Symbol?.UnderlyingType is { } underlying && IsCompleteUnmanagedType(underlying),
        CTypeKind.InlineArray => type.InlineArrayLength > 0 && IsCompleteUnmanagedType(type.ElementType!),
        CTypeKind.Struct => !type.ContainsManagedReferences,
        _ => false,
    };

    public TypeSymbol? ResolveNamedType(string name, SyntaxTree tree)
    {
        var candidates = ResolveNamedTypeCandidates(name, tree, 0).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private IEnumerable<TypeSymbol> ResolveNamedTypeCandidates(string name, SyntaxTree tree, int arity = 0)
    {
        var suffix = arity == 0 ? string.Empty : $"`{arity}";
        if (name.Contains('.', StringComparison.Ordinal))
        {
            if (Types.TryGetValue(name + suffix, out var qualified))
                yield return qualified;
            yield break;
        }
        var currentNamespace = _namespaces.GetValueOrDefault(tree, string.Empty);
        if (!string.IsNullOrEmpty(currentNamespace) && Types.TryGetValue($"{currentNamespace}.{name}{suffix}", out var local))
        {
            yield return local;
            yield break;
        }
        var emitted = new HashSet<TypeSymbol>();
        foreach (var imported in _usings.GetValueOrDefault(tree, []))
        {
            if (Types.TryGetValue($"{imported}.{name}{suffix}", out var importedType) && emitted.Add(importedType))
                yield return importedType;
        }
        if (Types.TryGetValue(name + suffix, out var global) && emitted.Add(global))
            yield return global;
    }

    private void ValidateUsings()
    {
        foreach (var tree in SyntaxTrees)
        {
            foreach (var directive in tree.Root.Usings)
            {
                if (!Types.Values.Any(type => type.Namespace == directive.Name || type.Namespace.StartsWith(directive.Name + ".", StringComparison.Ordinal)))
                    Diagnostics.Add("CT1111", $"Namespace '{directive.Name}' does not exist in this compilation.", directive.Source, directive.Span);
            }
        }
    }

    private void DeclareTypes()
    {
        foreach (var tree in SyntaxTrees)
        {
            var namespaceName = tree.Root.Namespace?.Name ?? string.Empty;
            _namespaces[tree] = namespaceName;
            _usings[tree] = tree.Root.Usings.Select(@using => @using.Name).Append("System").Distinct(StringComparer.Ordinal).ToImmutableArray();
            foreach (var declaration in tree.Root.Types)
            {
                var fullName = string.IsNullOrEmpty(namespaceName) ? declaration.Name : $"{namespaceName}.{declaration.Name}";
                var typeKey = fullName + (declaration.TypeParameters.IsDefaultOrEmpty ? string.Empty : $"`{declaration.TypeParameters.Length}");
                if (Types.TryGetValue(typeKey, out var existing))
                {
                    Diagnostics.Add("CT1100", $"The type '{fullName}' is already declared.", declaration.Source, declaration.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
                    continue;
                }
                var kind = declaration.Kind switch
                {
                    TypeDeclarationKind.Struct or TypeDeclarationKind.Union => DeclaredTypeKind.Struct,
                    TypeDeclarationKind.Interface => DeclaredTypeKind.Interface,
                    TypeDeclarationKind.Enum => DeclaredTypeKind.Enum,
                    TypeDeclarationKind.Delegate => DeclaredTypeKind.Delegate,
                    TypeDeclarationKind.Opaque => DeclaredTypeKind.Opaque,
                    TypeDeclarationKind.Newtype => DeclaredTypeKind.Newtype,
                    _ when declaration.Modifiers.Contains("static", StringComparer.Ordinal) => DeclaredTypeKind.StaticClass,
                    _ => DeclaredTypeKind.Class,
                };
                ValidateModifiers(declaration.Modifiers, declaration);
                var typeAccessibility = GetAccessibility(declaration.Modifiers, declaration, Accessibility.Internal);
                if (typeAccessibility is Accessibility.Private or Accessibility.Protected)
                    Diagnostics.Add("CT1216", "A namespace type can be only public or internal.", declaration.Source, declaration.Span);
                if (declaration.Kind != TypeDeclarationKind.Class && declaration.Modifiers.Contains("static", StringComparer.Ordinal))
                    Diagnostics.Add("CT1217", "Only a class can be static.", declaration.Source, declaration.Span);
                if (declaration.Kind != TypeDeclarationKind.Class && declaration.Modifiers.Contains("sealed", StringComparer.Ordinal))
                    Diagnostics.Add("CT1218", "sealed applies only to classes.", declaration.Source, declaration.Span);
                if (declaration.Modifiers.Contains("abstract", StringComparer.Ordinal) && declaration.Kind != TypeDeclarationKind.Class)
                    Diagnostics.Add("CT1270", "abstract applies only to classes and their members.", declaration.Source, declaration.Span);
                foreach (var invalidModifier in declaration.Modifiers.Where(modifier => modifier is "const" or "unsafe" or "virtual" or "override" or "volatile" || modifier == "readonly" && declaration.Kind is not TypeDeclarationKind.Struct and not TypeDeclarationKind.Union))
                    Diagnostics.Add("CT1219", $"Modifier '{invalidModifier}' is not valid on a type declaration.", declaration.Source, declaration.Span);
                ValidateAttributes(declaration.Attributes, declaration, declaration.Kind == TypeDeclarationKind.Opaque ? ["NativeType"] : declaration.Kind is TypeDeclarationKind.Struct or TypeDeclarationKind.Union ? ["Packed", "Align", "BitField"] : declaration.Kind == TypeDeclarationKind.Newtype ? ["Align"] : []);
                string? nativeTypeName = null;
                string? nativeHeader = null;
                int? pack = null;
                var declaredTypeParameters = declaration.TypeParameters.IsDefault ? [] : declaration.TypeParameters;
                var alignment = ParseAlignment(FindAttribute(declaration.Attributes, "Align"),
                    declaredTypeParameters.Where(parameter => parameter.IsConstant).Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal), out var alignmentParameter);
                var packed = FindAttribute(declaration.Attributes, "Packed");
                var bitField = FindAttribute(declaration.Attributes, "BitField");
                TypeSyntax? bitFieldBackingSyntax = null;
                if (bitField is not null)
                {
                    if (declaration.Kind == TypeDeclarationKind.Struct && bitField.Arguments is [TypeOfExpressionSyntax typeOf])
                        bitFieldBackingSyntax = typeOf.Type;
                    else
                        Diagnostics.Add("CT1297", "BitField requires one typeof(byte), typeof(ushort), typeof(uint), or typeof(ulong) argument on a struct.", bitField.Source, bitField.Span);
                    if (packed is not null || alignment is not null || declaration.Kind == TypeDeclarationKind.Union)
                        Diagnostics.Add("CT1297", "BitField types cannot use Packed, Align, union, or explicit-layout facilities.", bitField.Source, bitField.Span);
                }
                if (packed is not null)
                {
                    if (packed.Arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue numeric, LiteralKind: SyntaxKind.NumberToken }] &&
                        numeric.FloatingPoint is null && (numeric.Integer == 1 || numeric.Integer == 2 || numeric.Integer == 4 || numeric.Integer == 8 || numeric.Integer == 16))
                        pack = (int)numeric.Integer;
                    else
                        Diagnostics.Add("CT1280", "Packed requires one integral argument: 1, 2, 4, 8, or 16.", packed.Source, packed.Span);
                }
                if (declaration.Kind == TypeDeclarationKind.Opaque)
                {
                    var nativeType = FindAttribute(declaration.Attributes, "NativeType");
                    if (nativeType?.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string typeName }, LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string header }] &&
                        IsPortableExternalIdentifier(typeName) && IsPortableHeaderName(header))
                    {
                        nativeTypeName = typeName;
                        nativeHeader = header;
                    }
                    else
                        Diagnostics.Add("CT1240", "An opaque declaration requires NativeType with a portable C typedef and header name.", declaration.Source, nativeType?.Span ?? declaration.Span);
                }
                var typeParameters = (declaration.TypeParameters.IsDefault ? [] : declaration.TypeParameters).Select(parameter => new TypeSymbol
                {
                    Namespace = fullName,
                    Name = parameter.Name,
                    Kind = DeclaredTypeKind.TypeParameter,
                    Syntax = null,
                    Accessibility = Accessibility.Private,
                    IsSealed = false,
                    IsConstantParameter = parameter.IsConstant,
                }).ToImmutableArray();
                if (typeParameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != typeParameters.Length)
                    Diagnostics.Add("CT1271", "Generic type-parameter names must be unique.", declaration.Source, declaration.Span);
                Types.Add(typeKey, new TypeSymbol
                {
                    Namespace = namespaceName,
                    Name = declaration.Name,
                    Kind = kind,
                    Syntax = declaration,
                    Accessibility = typeAccessibility,
                    NativeTypeName = nativeTypeName,
                    NativeHeader = nativeHeader,
                    AggregateLayout = declaration.Kind == TypeDeclarationKind.Union ? AggregateLayoutKind.Union : AggregateLayoutKind.Sequential,
                    Pack = pack,
                    Alignment = alignment,
                    AlignmentParameter = alignmentParameter,
                    BitFieldBackingSyntax = bitFieldBackingSyntax,
                    IsSealed = declaration.Modifiers.Contains("sealed", StringComparer.Ordinal) || kind is DeclaredTypeKind.StaticClass or DeclaredTypeKind.Delegate,
                    IsAbstract = declaration.Modifiers.Contains("abstract", StringComparer.Ordinal) || kind == DeclaredTypeKind.Interface,
                    TypeParameters = typeParameters,
                });
            }
        }

    }

    private void ResolveBaseTypes()
    {
        Types.TryGetValue("System.Object", out var objectType);
        foreach (var tree in SyntaxTrees)
        {
            foreach (var declaration in tree.Root.Types)
            {
                _activeTypeParameters = ImmutableDictionary<string, CType>.Empty;
                var fullName = string.IsNullOrEmpty(_namespaces[tree]) ? declaration.Name : $"{_namespaces[tree]}.{declaration.Name}";
                if (!Types.TryGetValue(DeclarationKey(fullName, declaration), out var type))
                    continue;
                ResolveConstantParameterTypes(type.TypeParameters, declaration.TypeParameters, tree, declaration);
                if (type.Kind is not (DeclaredTypeKind.Class or DeclaredTypeKind.Struct or DeclaredTypeKind.Interface))
                    continue;
                _activeTypeParameters = type.TypeParameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Type, StringComparer.Ordinal);
                type.TypeParameterConstraints = BuildConstraintSets(declaration.TypeParameters, declaration.ConstraintClauses, tree, declaration);
                if (type.IsObject)
                {
                    if (declaration.BaseType is not null)
                        Diagnostics.Add("CT1225", "System.Object cannot declare a base type.", declaration.BaseType.Source, declaration.BaseType.Span);
                    continue;
                }
                var declaredBases = declaration.BaseTypes.IsDefaultOrEmpty
                    ? declaration.BaseType is null ? [] : [declaration.BaseType]
                    : declaration.BaseTypes;
                var baseClassSeen = false;
                foreach (var baseSyntax in declaredBases)
                {
                    var resolved = ResolveType(baseSyntax, tree);
                    if (resolved.Kind == CTypeKind.Interface && resolved.Symbol is not null)
                    {
                        if (!type.Interfaces.Contains(resolved.Symbol))
                            type.Interfaces.Add(resolved.Symbol);
                        continue;
                    }
                    if (type.Kind != DeclaredTypeKind.Class || baseClassSeen || resolved.Kind != CTypeKind.Class || resolved.Symbol is null || resolved.Symbol.IsStatic)
                    {
                        Diagnostics.Add("CT1225", $"Type '{type.FullName}' requires interface bases and at most one non-static class base.", baseSyntax.Source, baseSyntax.Span);
                        continue;
                    }
                    baseClassSeen = true;
                    type.BaseType = resolved.Symbol;
                    if (resolved.Symbol.IsSealed)
                        Diagnostics.Add("CT1227", $"Class '{type.FullName}' cannot derive from sealed class '{resolved.Symbol.FullName}'.", baseSyntax.Source, baseSyntax.Span);
                }
                if (type.Kind == DeclaredTypeKind.Class && type.BaseType is null)
                    type.BaseType = objectType;
            }
        }
        _activeTypeParameters = ImmutableDictionary<string, CType>.Empty;

        var complete = new HashSet<TypeSymbol>();
        var active = new HashSet<TypeSymbol>();
        foreach (var type in Types.Values.Where(type => type.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Interface))
            Visit(type);

        void Visit(TypeSymbol type)
        {
            if (complete.Contains(type))
                return;
            if (!active.Add(type))
            {
                if (type.Syntax is not null)
                    Diagnostics.Add("CT1226", $"Class '{type.FullName}' participates in an inheritance cycle.", type.Syntax.Source, type.Syntax.Span);
                return;
            }
            if (type.BaseType is not null)
                Visit(type.BaseType);
            foreach (var contract in type.Interfaces)
                Visit(contract);
            active.Remove(type);
            complete.Add(type);
        }
    }

    private void DeclareMembers()
    {
        foreach (var tree in SyntaxTrees)
        {
            foreach (var declaration in tree.Root.Types)
            {
                var fullName = string.IsNullOrEmpty(_namespaces[tree]) ? declaration.Name : $"{_namespaces[tree]}.{declaration.Name}";
                if (!Types.TryGetValue(DeclarationKey(fullName, declaration), out var type) || type.Syntax != declaration)
                    continue;
                _activeTypeParameters = type.TypeParameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Type, StringComparer.Ordinal);
                if (type.Kind == DeclaredTypeKind.Enum)
                {
                    DeclareEnum(type, declaration, tree);
                    continue;
                }
                if (type.Kind == DeclaredTypeKind.Delegate)
                {
                    type.DelegateReturnType = ResolveType(declaration.DelegateReturnType!, tree);
                    type.DelegateParameters = DeclareParameters(declaration.DelegateParameters, tree, isExtern: false);
                    continue;
                }
                if (type.Kind == DeclaredTypeKind.Opaque)
                    continue;
                if (type.Kind == DeclaredTypeKind.Newtype)
                {
                    var underlying = ResolveType(declaration.EnumUnderlyingType!, tree);
                    type.UnderlyingType = underlying;
                    if (!IsValidNewtypeUnderlying(underlying, type, []))
                        Diagnostics.Add("CT1295", $"Newtype '{type.FullName}' requires a complete unmanaged non-void non-recursive underlying type.", declaration.Source, declaration.Span);
                    continue;
                }
                if (type.IsBitField)
                {
                    type.BitFieldBackingType = ResolveType(type.BitFieldBackingSyntax!, tree);
                    if (type.BitFieldBackingType.Kind is not (CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint or CTypeKind.Ulong))
                        Diagnostics.Add("CT1297", $"BitField backing type '{type.BitFieldBackingType.DisplayName}' must be byte, ushort, uint, or ulong.", declaration.Source, declaration.Span);
                }
                foreach (var member in declaration.Members)
                    DeclareMember(type, member, tree);
                if (!type.IsStatic && type.Kind != DeclaredTypeKind.Interface && type.FullName != "Esp.Idf.EspError" && type.Constructors.Count == 0)
                {
                    type.Constructors.Add(new MethodSymbol
                    {
                        Name = type.Name,
                        ContainingType = type,
                        Accessibility = Accessibility.Public,
                        IsStatic = false,
                        Syntax = null,
                        ReturnType = type.Type,
                        Parameters = [],
                        Body = null,
                        IsConstructor = true,
                    });
                }
            }
        }
        _activeTypeParameters = ImmutableDictionary<string, CType>.Empty;
        FinalizeConstructedTypes();
        ValidateAggregateLayouts();
    }

    private static string DeclarationKey(string fullName, TypeDeclarationSyntax declaration) =>
        fullName + (declaration.TypeParameters.IsDefaultOrEmpty ? string.Empty : $"`{declaration.TypeParameters.Length}");

    private ImmutableDictionary<string, GenericConstraintSet> BuildConstraintSets(
        ImmutableArray<TypeParameterSyntax> parameters,
        ImmutableArray<TypeParameterConstraintClauseSyntax> clauses,
        SyntaxTree tree,
        SyntaxNode owner)
    {
        var names = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        var result = parameters.ToDictionary(parameter => parameter.Name, _ => new GenericConstraintSet(Interfaces: []), StringComparer.Ordinal);
        foreach (var clause in clauses)
        {
            if (!names.Contains(clause.TypeParameterName))
            {
                Diagnostics.Add("CT1271", $"Constraint clause names unknown type parameter '{clause.TypeParameterName}'.", clause.Source, clause.Span);
                continue;
            }
            if (parameters.First(parameter => parameter.Name == clause.TypeParameterName).IsConstant)
            {
                Diagnostics.Add("CT2202", $"Constant parameter '{clause.TypeParameterName}' cannot declare generic constraints.", clause.Source, clause.Span);
                continue;
            }
            var requiresClass = false;
            var requiresStruct = false;
            var requiresUnmanaged = false;
            var requiresConstructor = false;
            CType? baseType = null;
            var interfaces = ImmutableArray.CreateBuilder<CType>();
            foreach (var constraint in clause.Constraints)
            {
                switch (constraint.Kind)
                {
                    case TypeParameterConstraintKind.Class: requiresClass = true; break;
                    case TypeParameterConstraintKind.Struct: requiresStruct = true; break;
                    case TypeParameterConstraintKind.Unmanaged: requiresUnmanaged = true; requiresStruct = true; break;
                    case TypeParameterConstraintKind.Constructor: requiresConstructor = true; break;
                    case TypeParameterConstraintKind.Type:
                        var resolved = ResolveType(constraint.Type!, tree);
                        if (resolved.Kind == CTypeKind.Interface)
                            interfaces.Add(resolved);
                        else if (resolved.Kind == CTypeKind.Class && baseType is null)
                            baseType = resolved;
                        else
                            Diagnostics.Add("CT1271", "A generic constraint must be one base class or an interface.", constraint.Source, constraint.Span);
                        break;
                }
            }
            if (requiresClass && requiresStruct || requiresClass && requiresUnmanaged || baseType is not null && requiresStruct)
                Diagnostics.Add("CT1271", "class, struct, unmanaged, and base-class constraints must be mutually compatible.", clause.Source, clause.Span);
            result[clause.TypeParameterName] = new GenericConstraintSet(requiresClass, requiresStruct, requiresUnmanaged, requiresConstructor, baseType, interfaces.ToImmutable());
        }
        foreach (var duplicate in clauses.GroupBy(clause => clause.TypeParameterName, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add("CT1271", $"Type parameter '{duplicate.Key}' has more than one constraint clause.", owner.Source, owner.Span);
        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private void ResolveConstantParameterTypes(
        ImmutableArray<TypeSymbol> symbols,
        ImmutableArray<TypeParameterSyntax> syntax,
        SyntaxTree tree,
        SyntaxNode owner)
    {
        if (symbols.IsDefaultOrEmpty || syntax.IsDefaultOrEmpty)
            return;
        for (var index = 0; index < Math.Min(symbols.Length, syntax.Length); index++)
        {
            var parameter = symbols[index];
            var declaration = syntax[index];
            if (!declaration.IsConstant)
                continue;
            if (declaration.ConstantType is null)
            {
                Diagnostics.Add("CT2202", $"Constant parameter '{declaration.Name}' requires an integral declared type.", owner.Source, owner.Span);
                parameter.ConstantParameterType = CType.Error;
                continue;
            }
            var resolved = ResolveType(declaration.ConstantType, tree);
            if (!IsConstantParameterType(resolved))
            {
                Diagnostics.Add("CT2202", $"Constant parameter '{declaration.Name}' requires an integral, character, native-integral, or enum type.", declaration.Source, declaration.Span);
                parameter.ConstantParameterType = CType.Error;
                continue;
            }
            parameter.ConstantParameterType = resolved;
        }
    }

    internal ImmutableArray<CType> ResolveGenericArguments(
        ImmutableArray<TypeSymbol> parameters,
        ImmutableArray<TypeSyntax> arguments,
        SyntaxTree tree,
        SyntaxNode owner,
        bool report = true)
    {
        var result = ImmutableArray.CreateBuilder<CType>(arguments.Length);
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = index < parameters.Length ? parameters[index] : null;
            var argument = arguments[index];
            if (parameter?.IsConstantParameter == true)
            {
                var expression = argument.ConstantArgument ?? new NameExpressionSyntax(argument.Source, argument.Span, argument.Name);
                if (!TryEvaluateConstantArgument(expression, tree, out var value) ||
                    parameter.ConstantParameterType is not { } declaredType ||
                    !TryConvertConstant(value, declaredType, out var canonical))
                {
                    if (report)
                        Diagnostics.Add("CT2202", $"Argument for constant parameter '{parameter.Name}' must be a known checked value of type '{parameter.ConstantParameterType?.DisplayName ?? "?"}'.", argument.Source, argument.Span);
                    result.Add(new CType(CTypeKind.Constant, Symbol: parameter, ElementType: parameter.ConstantParameterType ?? CType.Error, ConstantValue: BigInteger.Zero));
                }
                else
                    result.Add(new CType(CTypeKind.Constant, Symbol: parameter, ElementType: declaredType, ConstantValue: canonical));
                continue;
            }
            if (argument.ConstantArgument is not null)
            {
                if (report)
                    Diagnostics.Add("CT2202", $"Type parameter '{parameter?.Name ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)}' requires a type argument, not a constant value.", argument.Source, argument.Span);
                result.Add(CType.Error);
                continue;
            }
            result.Add(ResolveType(argument, tree, report));
        }
        return result.ToImmutable();
    }

    internal ImmutableArray<CType> ResolveGenericArguments(
        ImmutableArray<TypeSymbol> parameters,
        ImmutableArray<TypeSyntax> arguments,
        SyntaxTree tree,
        SyntaxNode owner,
        IReadOnlyDictionary<string, CType> substitutions,
        bool report = true)
    {
        var previous = _activeTypeParameters;
        var builder = previous.ToImmutableDictionary(StringComparer.Ordinal).ToBuilder();
        foreach (var substitution in substitutions)
            builder[substitution.Key] = substitution.Value;
        _activeTypeParameters = builder.ToImmutable();
        try
        {
            return ResolveGenericArguments(parameters, arguments, tree, owner, report);
        }
        finally
        {
            _activeTypeParameters = previous;
        }
    }

    private bool TryEvaluateConstantArgument(ExpressionSyntax expression, SyntaxTree tree, out BigInteger value)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax { Value: NumericLiteralValue numeric } when numeric.FloatingPoint is null:
                value = numeric.Integer;
                return true;
            case LiteralExpressionSyntax { Value: char character }:
                value = character;
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryEvaluateConstantArgument(parenthesized.Expression, tree, out value);
            case NameExpressionSyntax name when _activeTypeParameters.TryGetValue(name.Name, out var parameter) &&
                                                parameter.Kind == CTypeKind.Constant && parameter.ConstantValue is { } constant:
                value = constant;
                return true;
            case MemberAccessExpressionSyntax { Receiver: NameExpressionSyntax receiver } member:
                {
                    if (receiver.Name is "Target" or "System.Runtime.Target")
                    {
                        if (member.Name == "PointerSize" && _architecture != CompilationArchitecture.Auto)
                        {
                            value = _architecture is CompilationArchitecture.X64 or CompilationArchitecture.Arm64 or CompilationArchitecture.RiscV64 ? 8 : 4;
                            return true;
                        }
                    }
                    var enumType = ResolveNamedType(receiver.Name, tree);
                    var enumValue = enumType?.EnumValues.FirstOrDefault(candidate => candidate.Name == member.Name);
                    if (enumValue is not null)
                    {
                        value = enumValue.Value;
                        return true;
                    }
                    break;
                }
            case CastExpressionSyntax cast when TryEvaluateConstantArgument(cast.Expression, tree, out var operand):
                {
                    var target = ResolveType(cast.Type, tree, report: false);
                    return TryConvertConstant(operand, target, out value);
                }
            case UnaryExpressionSyntax unary when TryEvaluateConstantArgument(unary.Operand, tree, out var operand):
                value = unary.OperatorKind switch
                {
                    SyntaxKind.PlusToken => operand,
                    SyntaxKind.MinusToken => -operand,
                    SyntaxKind.TildeToken => ~operand,
                    _ => default,
                };
                return unary.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.TildeToken;
            case BinaryExpressionSyntax binary when TryEvaluateConstantArgument(binary.Left, tree, out var left) &&
                                                    TryEvaluateConstantArgument(binary.Right, tree, out var right):
                try
                {
                    value = binary.OperatorKind switch
                    {
                        SyntaxKind.PlusToken => left + right,
                        SyntaxKind.MinusToken => left - right,
                        SyntaxKind.StarToken => left * right,
                        SyntaxKind.SlashToken when right != 0 => left / right,
                        SyntaxKind.PercentToken when right != 0 => left % right,
                        SyntaxKind.AmpersandToken => left & right,
                        SyntaxKind.PipeToken => left | right,
                        SyntaxKind.HatToken => left ^ right,
                        SyntaxKind.LessLessToken when right >= 0 && right <= int.MaxValue => left << (int)right,
                        SyntaxKind.GreaterGreaterToken when right >= 0 && right <= int.MaxValue => left >> (int)right,
                        _ => default,
                    };
                    return binary.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or
                        SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.HatToken ||
                        binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken && right != 0 ||
                        binary.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken && right >= 0 && right <= int.MaxValue;
                }
                catch (ArithmeticException)
                {
                    break;
                }
        }
        value = default;
        return false;
    }

    private bool TryConvertConstant(BigInteger value, CType type, out BigInteger canonical)
    {
        var underlying = type.Kind == CTypeKind.Enum ? type.Symbol?.UnderlyingType ?? CType.Int : type;
        var pointer64 = _architecture is CompilationArchitecture.X64 or CompilationArchitecture.Arm64 or CompilationArchitecture.RiscV64;
        (BigInteger Min, BigInteger Max)? range = underlying.Kind switch
        {
            CTypeKind.Byte => (byte.MinValue, byte.MaxValue),
            CTypeKind.Sbyte => (sbyte.MinValue, sbyte.MaxValue),
            CTypeKind.Short => (short.MinValue, short.MaxValue),
            CTypeKind.Ushort or CTypeKind.Char => (ushort.MinValue, ushort.MaxValue),
            CTypeKind.Int => (int.MinValue, int.MaxValue),
            CTypeKind.Uint => (uint.MinValue, uint.MaxValue),
            CTypeKind.Long => (long.MinValue, long.MaxValue),
            CTypeKind.Ulong => (ulong.MinValue, ulong.MaxValue),
            CTypeKind.Nint when pointer64 => (long.MinValue, long.MaxValue),
            CTypeKind.Nint => (int.MinValue, int.MaxValue),
            CTypeKind.Nuint when pointer64 => (ulong.MinValue, ulong.MaxValue),
            CTypeKind.Nuint => (uint.MinValue, uint.MaxValue),
            _ => null,
        };
        canonical = value;
        return range is { } bounds && value >= bounds.Min && value <= bounds.Max;
    }

    private static bool IsConstantParameterType(CType type) => type.Kind is
        CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Enum;

    private static bool IsValidNewtypeUnderlying(CType type, TypeSymbol owner, HashSet<TypeSymbol> visited)
    {
        if (type.Kind is CTypeKind.Void or CTypeKind.Error || type.Symbol == owner)
            return false;
        if (type.Kind == CTypeKind.Newtype)
            return type.Symbol is { UnderlyingType: { } nested } symbol && visited.Add(symbol) && IsValidNewtypeUnderlying(nested, owner, visited);
        return IsCompleteUnmanagedType(type);
    }

    private void DeclareMember(TypeSymbol type, MemberDeclarationSyntax declaration, SyntaxTree tree)
    {
        ValidateModifiers(declaration.Modifiers, declaration);
        var accessibility = GetAccessibility(declaration.Modifiers, declaration, type.Kind == DeclaredTypeKind.Interface ? Accessibility.Public : Accessibility.Private);
        var isStatic = declaration.Modifiers.Contains("static", StringComparer.Ordinal) ||
            declaration is FieldDeclarationSyntax { Modifiers: var fieldModifiers } && fieldModifiers.Contains("const", StringComparer.Ordinal);
        if (type.IsStatic && !isStatic)
            Diagnostics.Add("CT1201", "A static class can contain only static members.", declaration.Source, declaration.Span);

        switch (declaration)
        {
            case FieldDeclarationSyntax field:
                {
                    ValidateAllowedModifiers(field.Modifiers, ["public", "internal", "protected", "private", "static", "const", "readonly", "unsafe", "volatile"], field);
                    ValidateAttributes(field.Attributes, field, ["FieldOffset", "Section", "Used", "Extern", "NativeVolatile", "Align", "LinkerSymbol", "Register", "Bit", "Bits", "InterruptSafe", "ConstInit", "Embed"]);
                    if (type.Kind == DeclaredTypeKind.Interface)
                        Diagnostics.Add("CT1273", "An interface can contain only instance method and property contracts.", field.Source, field.Span);
                    var isVolatile = field.Modifiers.Contains("volatile", StringComparer.Ordinal);
                    int? fieldOffset = null;
                    var fieldAlignment = ParseAlignment(FindAttribute(field.Attributes, "Align"),
                        _activeTypeParameters.Where(pair => pair.Value.Kind == CTypeKind.Constant).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal), out var fieldAlignmentParameter);
                    var sectionAttribute = FindAttribute(field.Attributes, "Section");
                    var sectionName = ParseSectionName(sectionAttribute);
                    var usedAttribute = FindAttribute(field.Attributes, "Used");
                    var externAttribute = FindAttribute(field.Attributes, "Extern");
                    var nativeVolatileAttribute = FindAttribute(field.Attributes, "NativeVolatile");
                    var linkerSymbolAttribute = FindAttribute(field.Attributes, "LinkerSymbol");
                    var bitAttribute = FindAttribute(field.Attributes, "Bit");
                    var bitsAttribute = FindAttribute(field.Attributes, "Bits");
                    var registerAttribute = FindAttribute(field.Attributes, "Register");
                    var interruptSafeAttribute = FindAttribute(field.Attributes, "InterruptSafe");
                    var constInitAttribute = FindAttribute(field.Attributes, "ConstInit");
                    var embedAttribute = FindAttribute(field.Attributes, "Embed");
                    string? externName = null;
                    string? linkerSymbolName = null;
                    byte[]? embeddedData = null;
                    string? embeddedResourceIdentity = null;
                    int? bitFirst = null;
                    int? bitLast = null;
                    var registerAddress = ParseRegisterAddress(registerAttribute,
                        _activeTypeParameters.Where(pair => pair.Value.Kind == CTypeKind.Constant).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal), out var registerAddressParameter);
                    if (externAttribute is not null)
                    {
                        if (externAttribute.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsPortableExternalIdentifier(value))
                            externName = value;
                        else
                            Diagnostics.Add("CT1289", "Extern data requires one string containing a portable C identifier.", externAttribute.Source, externAttribute.Span);
                    }
                    if (linkerSymbolAttribute is not null)
                    {
                        if (linkerSymbolAttribute.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsLinkerSymbolIdentifier(value))
                            linkerSymbolName = value;
                        else
                            Diagnostics.Add("CT1296", "LinkerSymbol requires one string containing a native linker identifier.", linkerSymbolAttribute.Source, linkerSymbolAttribute.Span);
                    }
                    if (bitAttribute is not null)
                    {
                        if (TryParseNonnegativeInt(bitAttribute.Arguments, out var bit))
                            bitFirst = bitLast = bit;
                        else
                            Diagnostics.Add("CT2209", "Bit requires one nonnegative integral bit index.", bitAttribute.Source, bitAttribute.Span);
                    }
                    if (bitsAttribute is not null)
                    {
                        if (bitsAttribute.Arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue first }, LiteralExpressionSyntax { Value: NumericLiteralValue last }] &&
                            first.FloatingPoint is null && last.FloatingPoint is null && first.Integer >= 0 && last.Integer >= first.Integer && last.Integer <= int.MaxValue)
                        {
                            bitFirst = (int)first.Integer;
                            bitLast = (int)last.Integer;
                        }
                        else
                            Diagnostics.Add("CT2209", "Bits requires two nonnegative inclusive integral endpoints in ascending order.", bitsAttribute.Source, bitsAttribute.Span);
                    }
                    if (usedAttribute is not null && usedAttribute.Arguments.Length != 0)
                        Diagnostics.Add("CT1288", "Used does not accept arguments.", usedAttribute.Source, usedAttribute.Span);
                    if (nativeVolatileAttribute is not null && nativeVolatileAttribute.Arguments.Length != 0)
                        Diagnostics.Add("CT1290", "NativeVolatile does not accept arguments.", nativeVolatileAttribute.Source, nativeVolatileAttribute.Span);
                    if (interruptSafeAttribute is not null && !interruptSafeAttribute.Arguments.IsEmpty)
                        Diagnostics.Add("CT1306", "InterruptSafe does not accept arguments.", interruptSafeAttribute.Source, interruptSafeAttribute.Span);
                    var fieldOffsetAttribute = FindAttribute(field.Attributes, "FieldOffset");
                    if (fieldOffsetAttribute is not null)
                    {
                        if (fieldOffsetAttribute.Arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue offset, LiteralKind: SyntaxKind.NumberToken }] &&
                            offset.FloatingPoint is null && offset.Integer >= 0 && offset.Integer <= int.MaxValue)
                            fieldOffset = (int)offset.Integer;
                        else
                            Diagnostics.Add("CT1281", "FieldOffset requires one nonnegative integral literal no greater than int.MaxValue.", fieldOffsetAttribute.Source, fieldOffsetAttribute.Span);
                        if (type.Kind != DeclaredTypeKind.Struct || isStatic)
                            Diagnostics.Add("CT1281", "FieldOffset is valid only on an instance field of a struct.", fieldOffsetAttribute.Source, fieldOffsetAttribute.Span);
                    }
                    var resolvedFieldType = ResolveType(field.Type, tree);
                    if (embedAttribute is not null)
                    {
                        var validShape = isStatic && field.Modifiers.Contains("readonly", StringComparer.Ordinal) &&
                            !field.Modifiers.Contains("const", StringComparer.Ordinal) && !isVolatile && field.Initializer is null &&
                            resolvedFieldType.Kind == CTypeKind.ReadOnlyNativeBuffer && resolvedFieldType.ElementType == CType.Byte &&
                            field.Attributes.Length == 1 && field.Modifiers.All(modifier => modifier is "public" or "internal" or "protected" or "private" or "static" or "readonly" or "unsafe");
                        if (!validShape)
                            Diagnostics.Add("CT2222", "Embed requires an otherwise unadorned static readonly unsafe ReadOnlyNativeBuffer<byte> field without an initializer.", embedAttribute.Source, embedAttribute.Span);
                        if (embedAttribute.Arguments is not [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string resourcePath }] ||
                            string.IsNullOrWhiteSpace(resourcePath) || Path.IsPathFullyQualified(resourcePath))
                        {
                            Diagnostics.Add("CT2222", "Embed requires one non-empty owner-relative resource path.", embedAttribute.Source, embedAttribute.Span);
                        }
                        else if (validShape)
                        {
                            var owner = SourceOwnerFor(field.Source);
                            if (owner?.ContentRoot is null || !Path.IsPathFullyQualified(owner.ContentRoot))
                                Diagnostics.Add("CT2222", "Embed requires the source owner to define an absolute content root.", embedAttribute.Source, embedAttribute.Span);
                            else
                            {
                                try
                                {
                                    var contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owner.ContentRoot));
                                    var resourceFullPath = Path.GetFullPath(resourcePath, contentRoot);
                                    var relative = Path.GetRelativePath(contentRoot, resourceFullPath);
                                    if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                                        Diagnostics.Add("CT2222", $"Embedded resource '{resourcePath}' escapes its source owner's content root.", embedAttribute.Source, embedAttribute.Span);
                                    else if (!File.Exists(resourceFullPath))
                                        Diagnostics.Add("CT2222", $"Embedded resource '{resourcePath}' does not exist under its source owner's content root.", embedAttribute.Source, embedAttribute.Span);
                                    else
                                    {
                                        embeddedData = File.ReadAllBytes(resourceFullPath);
                                        embeddedResourceIdentity = $"{owner.ModulePath}/{relative.Replace('\\', '/')}";
                                    }
                                }
                                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
                                {
                                    Diagnostics.Add("CT2222", $"Embedded resource '{resourcePath}' could not be read: {exception.Message}", embedAttribute.Source, embedAttribute.Span);
                                }
                            }
                        }
                    }
                    var symbol = new FieldSymbol
                    {
                        Name = field.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = field,
                        Type = resolvedFieldType,
                        IsReadonly = field.Modifiers.Contains("readonly", StringComparer.Ordinal),
                        IsConst = field.Modifiers.Contains("const", StringComparer.Ordinal),
                        IsVolatile = isVolatile,
                        IsUnsafe = field.Modifiers.Contains("unsafe", StringComparer.Ordinal),
                        Initializer = field.Initializer,
                        Offset = fieldOffset,
                        Alignment = fieldAlignment,
                        AlignmentParameter = fieldAlignmentParameter,
                        SectionName = sectionName,
                        ExternName = externName,
                        LinkerSymbolName = linkerSymbolName,
                        IsNativeVolatile = nativeVolatileAttribute is not null,
                        IsUsed = usedAttribute is not null,
                        IsConstInit = constInitAttribute is not null,
                        EmbeddedData = embeddedData,
                        EmbeddedResourceIdentity = embeddedResourceIdentity,
                        IsInterruptSafe = interruptSafeAttribute is not null,
                        BitFirst = bitFirst,
                        BitLast = bitLast,
                        RegisterAddress = registerAddress,
                        RegisterAddressParameter = registerAddressParameter,
                    };
                    if (symbol.Type.IsNativeBuffer && embedAttribute is null)
                        Diagnostics.Add("CT2185", "Native-buffer views cannot be stored in fields.", field.Source, field.Span);
                    if (symbol.Type.Kind == CTypeKind.Opaque)
                        Diagnostics.Add("CT1242", "Opaque handles cannot be stored in fields.", field.Source, field.Span);
                    if (symbol.Type.IsNativeUtf8String)
                        Diagnostics.Add("CT1265", "NativeUtf8String cannot be stored in fields or static storage.", field.Source, field.Span);
                    if (type.Kind == DeclaredTypeKind.Struct && symbol.Type.ContainsAtomic)
                        Diagnostics.Add("CT1278", "A structure cannot contain Atomic<T> because structure copies would duplicate atomic storage.", field.Source, field.Span);
                    if (symbol.IsConst && field.Initializer is null)
                        Diagnostics.Add("CT1202", "A const field requires an initializer.", field.Source, field.Span);
                    if (symbol.IsConst && symbol.IsReadonly)
                        Diagnostics.Add("CT1220", "A field cannot be both const and readonly.", field.Source, field.Span);
                    if (isVolatile && (symbol.IsConst || symbol.IsReadonly || !IsVolatileType(symbol.Type)))
                        Diagnostics.Add("CT1274", "volatile requires a writable Boolean, integral, native-integral, enum, or unsafe-pointer field.", field.Source, field.Span);
                    if (sectionAttribute is not null && (!symbol.IsStatic || symbol.IsConst || !IsCompleteUnmanagedType(symbol.Type)))
                        Diagnostics.Add("CT1287", "Section requires a non-const static field with a complete unmanaged type.", sectionAttribute.Source, sectionAttribute.Span);
                    if (usedAttribute is not null && (!symbol.IsStatic || symbol.IsConst || externAttribute is not null || !IsCompleteUnmanagedType(symbol.Type)))
                        Diagnostics.Add("CT1288", "Used requires an owned non-const static field with a complete unmanaged type.", usedAttribute.Source, usedAttribute.Span);
                    if (externAttribute is not null && (!symbol.IsStatic || symbol.IsConst || symbol.Initializer is not null || type.IsGenericDefinition || !IsCompleteUnmanagedType(symbol.Type) || sectionAttribute is not null || usedAttribute is not null || isVolatile))
                        Diagnostics.Add("CT1289", "Extern data requires a non-generic static unmanaged field without an initializer, Section, Used, const, or C~ volatile.", externAttribute.Source, externAttribute.Span);
                    if (nativeVolatileAttribute is not null && externAttribute is null)
                        Diagnostics.Add("CT1290", "NativeVolatile is valid only on an extern data field.", nativeVolatileAttribute.Source, nativeVolatileAttribute.Span);
                    if (interruptSafeAttribute is not null && externAttribute is null)
                        Diagnostics.Add("CT1306", "InterruptSafe data requires an extern static field.", interruptSafeAttribute.Source, interruptSafeAttribute.Span);
                    if (constInitAttribute is not null)
                    {
                        if (!constInitAttribute.Arguments.IsEmpty)
                            Diagnostics.Add("CT1308", "ConstInit does not accept arguments.", constInitAttribute.Source, constInitAttribute.Span);
                        if (!symbol.IsStatic || !symbol.IsReadonly || symbol.Initializer is null || symbol.IsConst || symbol.IsVolatile ||
                            symbol.ExternName is not null || symbol.LinkerSymbolName is not null || symbol.IsRegister || nativeVolatileAttribute is not null ||
                            !IsCompleteUnmanagedType(symbol.Type) || symbol.Type.ContainsManagedReferences || symbol.Type.ContainsPointer || symbol.Type.ContainsAtomic)
                            Diagnostics.Add("CT1308", "ConstInit requires owned static readonly pointer-free unmanaged storage with an initializer and no conflicting storage attributes.", constInitAttribute.Source, constInitAttribute.Span);
                    }
                    if (linkerSymbolAttribute is not null && (!symbol.IsStatic || !symbol.IsReadonly || !symbol.IsUnsafe || symbol.IsConst || symbol.IsVolatile || symbol.Initializer is not null || !IsLinkerAddressType(symbol.Type) || externAttribute is not null || sectionAttribute is not null || usedAttribute is not null || nativeVolatileAttribute is not null || fieldAlignment is not null))
                        Diagnostics.Add("CT1296", "LinkerSymbol requires a static unsafe readonly pointer, nuint, or nuint-backed newtype field without storage, initialization, volatility, or conflicting native attributes.", linkerSymbolAttribute.Source, linkerSymbolAttribute.Span);
                    if (fieldAlignment is not null && (symbol.IsConst || symbol.ExternName is not null || !symbol.IsStatic && type.Kind != DeclaredTypeKind.Struct))
                        Diagnostics.Add("CT1293", "Align is valid only on owned non-const static storage or instance fields of value aggregates.", field.Source, field.Span);
                    if (fieldAlignment is int requested && type.Pack is int packValue && !symbol.IsStatic && requested > packValue)
                        Diagnostics.Add("CT1293", $"Field alignment {requested} exceeds containing pack {packValue}.", field.Source, field.Span);
                    if (fieldAlignment is int explicitAlignment && fieldOffset is int explicitOffset && explicitOffset % explicitAlignment != 0)
                        Diagnostics.Add("CT1293", $"Explicit field offset {explicitOffset} is not divisible by alignment {explicitAlignment}.", field.Source, field.Span);
                    if (type.IsBitField)
                    {
                        var backingBits = FixedUnsignedWidth(type.BitFieldBackingType!);
                        var width = bitFirst is int first && bitLast is int last ? last - first + 1 : 0;
                        var validViewType = bitAttribute is not null ? symbol.Type == CType.Bool : IsUnsignedBitViewType(symbol.Type) && FixedUnsignedWidth(symbol.Type) >= width;
                        if (symbol.IsStatic || symbol.Initializer is not null || bitFirst is null || bitLast is null || bitLast >= backingBits || !validViewType ||
                            field.Attributes.Any(attribute => attribute.Name is not ("Bit" or "Bits")) || symbol.IsConst || symbol.IsVolatile || fieldOffset is not null || fieldAlignment is not null)
                            Diagnostics.Add("CT1297", "BitField members must be non-static Bit/Bits views without storage, initialization, volatility, or layout attributes and must use a compatible view type.", field.Source, field.Span);
                    }
                    else if (bitAttribute is not null || bitsAttribute is not null)
                        Diagnostics.Add("CT1297", "Bit and Bits are valid only on fields declared inside a BitField type.", field.Source, field.Span);
                    if (registerAttribute is not null)
                    {
                        var width = MmioStorageWidth(symbol.Type);
                        var pointerBits = _architecture is CompilationArchitecture.X64 or CompilationArchitecture.Arm64 or CompilationArchitecture.RiscV64 ? 64 : 32;
                        var fitsPointer = registerAddress is null || registerAddress >= 0 && registerAddress < (BigInteger.One << pointerBits);
                        var aligned = registerAddress is null || width > 0 && registerAddress % width == 0;
                        if (!symbol.IsStatic || !symbol.IsUnsafe || symbol.IsConst || symbol.IsVolatile || symbol.Initializer is not null || width == 0 || !fitsPointer || !aligned ||
                            externAttribute is not null || sectionAttribute is not null || usedAttribute is not null || nativeVolatileAttribute is not null || linkerSymbolAttribute is not null || fieldAlignment is not null)
                            Diagnostics.Add(width == 0 || !fitsPointer || !aligned ? "CT2210" : "CT1298", "Register requires a naturally aligned compile-time address and a static unsafe fixed-width scalar, enum, or BitField field without storage or conflicting native attributes.", registerAttribute.Source, registerAttribute.Span);
                    }
                    AddUnique(type, symbol);
                    break;
                }
            case PropertyDeclarationSyntax property:
                {
                    ValidateAllowedModifiers(property.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed", "abstract"], property);
                    ValidateAttributes(property.Attributes, property, ["NoAlloc", "NoThrow", "NoBlock", "NoRuntime", "NoRecursion", "Section"]);
                    var propertyEffects = ParseEffectContracts(property.Attributes);
                    var noRecursion = FindAttribute(property.Attributes, "NoRecursion");
                    var propertySection = FindAttribute(property.Attributes, "Section");
                    _ = ParseSectionName(propertySection);
                    if (propertySection is not null)
                        Diagnostics.Add("CT1287", "Section is not valid on a property.", propertySection.Source, propertySection.Span);
                    if (noRecursion is not null && noRecursion.Arguments.Length != 0)
                        Diagnostics.Add("CT1294", "NoRecursion does not accept arguments.", noRecursion.Source, noRecursion.Span);
                    if (property.Getter is null && property.Setter is null)
                        Diagnostics.Add("CT1224", "A property requires a getter, a setter, or both.", property.Source, property.Span);
                    var propertyType = ResolveType(property.Type, tree);
                    if (propertyType.IsNativeBuffer)
                        Diagnostics.Add("CT2185", "Native-buffer views cannot be stored in properties.", property.Source, property.Span);
                    if (propertyType.Kind == CTypeKind.Opaque)
                        Diagnostics.Add("CT1242", "Opaque handles cannot be stored in properties.", property.Source, property.Span);
                    if (propertyType.IsNativeUtf8String && UserSyntaxTrees.Contains(tree))
                        Diagnostics.Add("CT1265", "NativeUtf8String cannot be stored in properties.", property.Source, property.Span);
                    if (propertyType.ContainsAtomic)
                        Diagnostics.Add("CT1278", "Atomic<T> cannot be stored in a property.", property.Source, property.Span);
                    var getterEffects = EffectContract.None;
                    var setterEffects = EffectContract.None;
                    if (property.Getter is not null)
                    {
                        ValidateAllowedModifiers(property.Getter.Modifiers, ["public", "internal", "protected", "private"], property.Getter);
                        ValidateAttributes(property.Getter.Attributes, property.Getter, ["NoAlloc", "NoThrow", "NoBlock", "NoRuntime"]);
                        getterEffects = ParseEffectContracts(property.Getter.Attributes);
                    }
                    if (property.Setter is not null)
                    {
                        ValidateAllowedModifiers(property.Setter.Modifiers, ["public", "internal", "protected", "private"], property.Setter);
                        ValidateAttributes(property.Setter.Attributes, property.Setter, ["NoAlloc", "NoThrow", "NoBlock", "NoRuntime"]);
                        setterEffects = ParseEffectContracts(property.Setter.Attributes);
                    }
                    var isAbstractProperty = type.Kind == DeclaredTypeKind.Interface || property.Modifiers.Contains("abstract", StringComparer.Ordinal);
                    if (type.Kind == DeclaredTypeKind.Interface && (isStatic || accessibility != Accessibility.Public))
                        Diagnostics.Add("CT1273", "Interface properties are public instance contracts.", property.Source, property.Span);
                    if (isAbstractProperty && type.Kind != DeclaredTypeKind.Interface && !type.IsAbstract)
                        Diagnostics.Add("CT1270", "An abstract property requires an abstract class.", property.Source, property.Span);
                    if (isAbstractProperty && (property.Getter?.Body is not null || property.Setter?.Body is not null))
                        Diagnostics.Add("CT1270", "An abstract or interface property cannot have an accessor body.", property.Source, property.Span);
                    FieldSymbol? backing = null;
                    if (!isAbstractProperty && (property.Getter is { Body: null } || property.Setter is { Body: null }))
                    {
                        backing = new FieldSymbol
                        {
                            Name = $"<{property.Name}>k__BackingField",
                            ContainingType = type,
                            Accessibility = Accessibility.Private,
                            IsStatic = isStatic,
                            Syntax = property,
                            Type = propertyType,
                            IsReadonly = false,
                            IsConst = false,
                        };
                        type.Fields.Add(backing);
                    }
                    var symbol = new PropertySymbol
                    {
                        Name = property.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = property,
                        Type = propertyType,
                        Getter = property.Getter,
                        Setter = property.Setter,
                        BackingField = backing,
                        GetterAccessibility = property.Getter is null ? Accessibility.Private : GetAccessibility(property.Getter.Modifiers, property.Getter, accessibility),
                        SetterAccessibility = property.Setter is null ? Accessibility.Private : GetAccessibility(property.Setter.Modifiers, property.Setter, accessibility),
                        IsVirtual = isAbstractProperty || property.Modifiers.Contains("virtual", StringComparer.Ordinal) || property.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsAbstract = isAbstractProperty,
                        IsOverride = property.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsSealedOverride = property.Modifiers.Contains("sealed", StringComparer.Ordinal),
                        DeclaredEffects = propertyEffects,
                        GetterDeclaredEffects = getterEffects,
                        SetterDeclaredEffects = setterEffects,
                        IsNoRecursion = noRecursion is not null,
                    };
                    if (noRecursion is not null && isAbstractProperty)
                        Diagnostics.Add("CT1294", "NoRecursion requires body-bearing accessors.", noRecursion.Source, noRecursion.Span);
                    if (AccessRank(symbol.GetterAccessibility) > AccessRank(accessibility) || AccessRank(symbol.SetterAccessibility) > AccessRank(accessibility))
                        Diagnostics.Add("CT1222", "An accessor cannot be more accessible than its property.", property.Source, property.Span);
                    if (symbol.IsVirtual && accessibility == Accessibility.Private)
                        Diagnostics.Add("CT1228", "A virtual or override property cannot be private.", property.Source, property.Span);
                    if (((symbol.DeclaredEffects | symbol.GetterDeclaredEffects | symbol.SetterDeclaredEffects) & EffectContract.NoRuntime) != 0 &&
                        propertyType.ContainsManagedReferences)
                        Diagnostics.Add("CT1305", "NoRuntime properties cannot have managed accessor results or values.", property.Source, property.Span);
                    AddUnique(type, symbol);
                    break;
                }
            case ConstructorDeclarationSyntax constructor:
                {
                    ValidateAllowedModifiers(constructor.Modifiers, ["public", "internal", "protected", "private", "unsafe"], constructor);
                    ValidateAttributes(constructor.Attributes, constructor, ["NoAlloc", "NoThrow", "NoBlock", "NoRuntime", "Section", "NoRecursion"]);
                    var constructorSection = FindAttribute(constructor.Attributes, "Section");
                    var constructorNoRecursion = FindAttribute(constructor.Attributes, "NoRecursion");
                    var constructorEffects = ParseEffectContracts(constructor.Attributes);
                    _ = ParseSectionName(constructorSection);
                    if (constructorSection is not null)
                        Diagnostics.Add("CT1287", "Section is not valid on a constructor.", constructorSection.Source, constructorSection.Span);
                    if (constructorNoRecursion is not null && constructorNoRecursion.Arguments.Length != 0)
                        Diagnostics.Add("CT1294", "NoRecursion does not accept arguments.", constructorNoRecursion.Source, constructorNoRecursion.Span);
                    if (isStatic)
                        Diagnostics.Add("CT1203", "Static constructors are not part of draft 0.7.", constructor.Source, constructor.Span);
                    if (type.Kind == DeclaredTypeKind.Interface)
                        Diagnostics.Add("CT1273", "An interface cannot declare constructors.", constructor.Source, constructor.Span);
                    var parameters = DeclareParameters(constructor.Parameters, tree, isExtern: false);
                    var symbol = new MethodSymbol
                    {
                        Name = constructor.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = false,
                        Syntax = constructor,
                        ReturnType = type.Type,
                        Parameters = parameters,
                        Body = constructor.Body,
                        IsConstructor = true,
                        DeclaredEffects = constructorEffects,
                        IsNoRecursion = constructorNoRecursion is not null,
                        IsUnsafe = constructor.Modifiers.Contains("unsafe", StringComparer.Ordinal),
                        ConstructorInitializer = constructor.Initializer,
                    };
                    AddMethod(type.Constructors, symbol);
                    break;
                }
            case OperatorDeclarationSyntax @operator:
                if (type.Kind == DeclaredTypeKind.Interface)
                    Diagnostics.Add("CT1273", "An interface cannot declare operators.", @operator.Source, @operator.Span);
                DeclareOperator(type, @operator, tree, accessibility, isStatic);
                break;
            case MethodDeclarationSyntax method:
                {
                    var hasBody = method.Body is not null || method.AssemblyBody is not null;
                    var isAssemblyFunction = method.AssemblyBody is not null;
                    ValidateAllowedModifiers(method.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed", "abstract"], method);
                    ValidateAttributes(method.Attributes, method, ["EntryPoint", "Extern", "Export", "NoAlloc", "NoThrow", "NoBlock", "NoRuntime", "NoRecursion", "ReturnsBorrowed", "ReturnsOwned", "ReturnsNullable", "Section", "Used", "TaskEntry", "RuntimeImpl", "Naked", "Interrupt", "InterruptSafe"]);
                    var entry = FindAttribute(method.Attributes, "EntryPoint");
                    var external = FindAttribute(method.Attributes, "Extern");
                    var export = FindAttribute(method.Attributes, "Export");
                    var noAlloc = FindAttribute(method.Attributes, "NoAlloc");
                    var methodEffects = ParseEffectContracts(method.Attributes);
                    var noRecursion = FindAttribute(method.Attributes, "NoRecursion");
                    var returnsBorrowed = FindAttribute(method.Attributes, "ReturnsBorrowed");
                    var returnsOwned = FindAttribute(method.Attributes, "ReturnsOwned");
                    var returnsNullable = FindAttribute(method.Attributes, "ReturnsNullable");
                    var sectionAttribute = FindAttribute(method.Attributes, "Section");
                    var usedAttribute = FindAttribute(method.Attributes, "Used");
                    var taskEntryAttribute = FindAttribute(method.Attributes, "TaskEntry");
                    var runtimeImplAttribute = FindAttribute(method.Attributes, "RuntimeImpl");
                    var nakedAttribute = FindAttribute(method.Attributes, "Naked");
                    var interruptAttribute = FindAttribute(method.Attributes, "Interrupt");
                    var interruptSafeAttribute = FindAttribute(method.Attributes, "InterruptSafe");
                    var runtimeImplementation = ParseRuntimeImplementation(runtimeImplAttribute);
                    uint? taskStackSize = null;
                    if (taskEntryAttribute is not null)
                    {
                        if (taskEntryAttribute.Arguments is [AssignmentExpressionSyntax
                            {
                                Left: NameExpressionSyntax { Name: "StackSize" },
                                OperatorKind: SyntaxKind.EqualsToken,
                                Right: LiteralExpressionSyntax { Value: NumericLiteralValue stack }
                            }] && stack.FloatingPoint is null && stack.Integer > 0 && stack.Integer <= uint.MaxValue && stack.Integer % 4 == 0)
                            taskStackSize = (uint)stack.Integer;
                        else
                            Diagnostics.Add("CT1291", "TaskEntry requires one StackSize assignment using a positive uint value divisible by four.", taskEntryAttribute.Source, taskEntryAttribute.Span);
                    }
                    var sectionName = ParseSectionName(sectionAttribute);
                    var previousTypeParameters = _activeTypeParameters;
                    var methodTypeParameters = method.TypeParameters.Select(parameter => new TypeSymbol
                    {
                        Namespace = $"{type.FullName}.{method.Name}",
                        Name = parameter.Name,
                        Kind = DeclaredTypeKind.TypeParameter,
                        Syntax = null,
                        Accessibility = Accessibility.Private,
                        IsConstantParameter = parameter.IsConstant,
                    }).ToImmutableArray();
                    if (methodTypeParameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != methodTypeParameters.Length)
                        Diagnostics.Add("CT1271", "Generic method type-parameter names must be unique.", method.Source, method.Span);
                    ResolveConstantParameterTypes(methodTypeParameters, method.TypeParameters, tree, method);
                    var activeBuilder = previousTypeParameters.ToImmutableDictionary(StringComparer.Ordinal).ToBuilder();
                    foreach (var parameter in methodTypeParameters)
                    {
                        if (activeBuilder.ContainsKey(parameter.Name))
                            Diagnostics.Add("CT1271", $"Method type parameter '{parameter.Name}' conflicts with a containing type parameter.", method.Source, method.Span);
                        activeBuilder[parameter.Name] = parameter.Type;
                    }
                    _activeTypeParameters = activeBuilder.ToImmutable();
                    var methodConstraints = BuildConstraintSets(method.TypeParameters, method.ConstraintClauses, tree, method);
                    var isAbstractMethod = type.Kind == DeclaredTypeKind.Interface || method.Modifiers.Contains("abstract", StringComparer.Ordinal);
                    if (type.Kind == DeclaredTypeKind.Interface && (isStatic || accessibility != Accessibility.Public))
                        Diagnostics.Add("CT1273", "Interface methods are public instance contracts.", method.Source, method.Span);
                    if (isAbstractMethod && type.Kind != DeclaredTypeKind.Interface && !type.IsAbstract)
                        Diagnostics.Add("CT1270", "An abstract method requires an abstract class.", method.Source, method.Span);
                    if (isAbstractMethod && hasBody)
                        Diagnostics.Add("CT1270", "An abstract or interface method cannot have a body.", method.Source, method.Span);
                    if (sectionAttribute is not null && (!isStatic || !hasBody || isAbstractMethod || external is not null))
                        Diagnostics.Add("CT1287", "Section requires a body-bearing static non-extern method.", sectionAttribute.Source, sectionAttribute.Span);
                    if (entry is not null && entry.Arguments.Length != 0)
                        Diagnostics.Add("CT1223", "EntryPoint does not accept arguments.", entry.Source, entry.Span);
                    if (noRecursion is not null && noRecursion.Arguments.Length != 0)
                        Diagnostics.Add("CT1294", "NoRecursion does not accept arguments.", noRecursion.Source, noRecursion.Span);
                    if (noRecursion is not null && (!hasBody || isAbstractMethod || external is not null))
                        Diagnostics.Add("CT1294", "NoRecursion requires a body-bearing non-extern method.", noRecursion.Source, noRecursion.Span);
                    if (usedAttribute is not null && usedAttribute.Arguments.Length != 0)
                        Diagnostics.Add("CT1288", "Used does not accept arguments.", usedAttribute.Source, usedAttribute.Span);
                    if (usedAttribute is not null && (!isStatic || !hasBody || isAbstractMethod || external is not null))
                        Diagnostics.Add("CT1288", "Used requires a body-bearing static non-extern method.", usedAttribute.Source, usedAttribute.Span);
                    if (nakedAttribute is not null && nakedAttribute.Arguments.Length != 0)
                        Diagnostics.Add("CT1302", "Naked does not accept arguments.", nakedAttribute.Source, nakedAttribute.Span);
                    if (interruptAttribute is not null && !interruptAttribute.Arguments.IsEmpty)
                        Diagnostics.Add("CT1306", "Interrupt does not accept arguments.", interruptAttribute.Source, interruptAttribute.Span);
                    if (interruptSafeAttribute is not null && !interruptSafeAttribute.Arguments.IsEmpty)
                        Diagnostics.Add("CT1306", "InterruptSafe does not accept arguments.", interruptSafeAttribute.Source, interruptSafeAttribute.Span);
                    string? externalName = null;
                    string? exportName = null;
                    if (external is not null)
                    {
                        if (external.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsPortableExternalIdentifier(value))
                            externalName = value;
                        else
                            Diagnostics.Add("CT1204", "Extern requires one string containing a portable C identifier.", external.Source, external.Span);
                        if (!isStatic || hasBody)
                            Diagnostics.Add("CT1205", "An Extern method must be static and bodyless.", method.Source, method.Span);
                    }
                    else if (!hasBody && !isAbstractMethod)
                        Diagnostics.Add("CT1206", "A bodyless method requires Extern or abstract.", method.Source, method.Span);
                    if (export is not null)
                    {
                        if (export.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] &&
                            (IsPortableExternalIdentifier(value) || (nakedAttribute is not null && IsLinkerSymbolIdentifier(value))))
                            exportName = value;
                        else
                            Diagnostics.Add("CT1243", "Export requires one string containing a portable C identifier.", export.Source, export.Span);
                        if (external is not null || entry is not null || !isStatic || !hasBody || accessibility != Accessibility.Public)
                            Diagnostics.Add("CT1244", "Export requires a public static body-bearing method and cannot be combined with EntryPoint or Extern.", method.Source, method.Span);
                    }
                    var returnType = ResolveType(method.ReturnType, tree);
                    if (returnType.IsNativeBuffer)
                        Diagnostics.Add("CT2186", "Native-buffer views cannot be returned.", method.ReturnType.Source, method.ReturnType.Span);
                    if (returnType.IsNativeUtf8String && UserSyntaxTrees.Contains(tree))
                        Diagnostics.Add("CT1266", "NativeUtf8String is scoped and cannot be returned.", method.ReturnType.Source, method.ReturnType.Span);
                    if (returnType.ContainsAtomic)
                        Diagnostics.Add("CT1278", "Atomic<T> cannot be returned by value.", method.ReturnType.Source, method.ReturnType.Span);
                    var methodParameters = DeclareParameters(method.Parameters, tree, external is not null || export is not null);
                    foreach (var parameter in methodParameters.Where(parameter => parameter.Type.ContainsAtomic && parameter.PassingKind == ParameterPassingKind.Value))
                        Diagnostics.Add("CT1278", "Atomic<T> cannot be passed by value.", parameter.Syntax!.Source, parameter.Syntax.Span);
                    if (external is not null || export is not null)
                    {
                        if (!method.TypeParameters.IsDefaultOrEmpty || ForbiddenNativeBoundaryType(returnType))
                            Diagnostics.Add("CT1279", "Open generic, interface, atomic, and runtime-backed threading types cannot cross an extern or export boundary.", method.ReturnType.Source, method.ReturnType.Span);
                        foreach (var parameter in methodParameters.Where(parameter => ForbiddenNativeBoundaryType(parameter.Type)))
                            Diagnostics.Add("CT1279", "Open generic, interface, atomic, and runtime-backed threading types cannot cross an extern or export boundary.", parameter.Syntax!.Source, parameter.Syntax.Span);
                    }
                    if (returnsBorrowed is not null &&
                        (returnsBorrowed.Arguments.Length != 0 || external is null || !(returnType.IsReference || returnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer)))
                        Diagnostics.Add("CT1235", "ReturnsBorrowed accepts no arguments and is valid only on an extern method with a managed-reference, opaque-handle, or pointer return type.", returnsBorrowed.Source, returnsBorrowed.Span);
                    if (returnsOwned is not null &&
                        (returnsOwned.Arguments.Length != 0 || returnType.Kind is not CTypeKind.Opaque and not CTypeKind.Pointer))
                        Diagnostics.Add("CT1245", "ReturnsOwned accepts no arguments and requires an opaque-handle or pointer return type.", returnsOwned.Source, returnsOwned.Span);
                    if (returnsNullable is not null &&
                        (returnsNullable.Arguments.Length != 0 || returnType.Kind is not CTypeKind.Opaque and not CTypeKind.Pointer))
                        Diagnostics.Add("CT1246", "ReturnsNullable accepts no arguments and requires an opaque-handle or pointer return type.", returnsNullable.Source, returnsNullable.Span);
                    if (returnsOwned is not null && returnsBorrowed is not null)
                        Diagnostics.Add("CT1247", "A return value cannot be both owned and borrowed.", method.Source, method.Span);
                    if (external is not null && returnType.Kind == CTypeKind.Opaque && returnsOwned is null && returnsBorrowed is null)
                        Diagnostics.Add("CT1248", "An extern opaque-handle result must declare ReturnsOwned or ReturnsBorrowed.", method.ReturnType.Source, method.ReturnType.Span);
                    var symbol = new MethodSymbol
                    {
                        Name = method.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = method,
                        ReturnType = returnType,
                        Parameters = methodParameters,
                        Body = method.Body,
                        AssemblyBody = method.AssemblyBody,
                        IsEntryPoint = entry is not null,
                        DeclaredEffects = methodEffects,
                        IsNoRecursion = noRecursion is not null,
                        IsUnsafe = method.Modifiers.Contains("unsafe", StringComparer.Ordinal),
                        ReturnsBorrowed = returnsBorrowed is not null && returnsBorrowed.Arguments.Length == 0 && external is not null && (returnType.IsReference || returnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer),
                        ReturnsOwned = returnsOwned is not null,
                        ReturnsNullable = returnsNullable is not null,
                        ExternName = externalName,
                        ExportName = exportName,
                        SectionName = sectionName,
                        IsUsed = usedAttribute is not null,
                        RuntimeImplementation = runtimeImplementation,
                        IsNaked = nakedAttribute is not null,
                        IsInterrupt = interruptAttribute is not null,
                        IsInterruptSafe = interruptSafeAttribute is not null,
                        TaskStackSize = taskStackSize,
                        IsTrustedExtern = !UserSyntaxTrees.Contains(tree) || tree.Origin == SyntaxTreeOrigin.EspIdfBinding,
                        IsVirtual = isAbstractMethod || method.Modifiers.Contains("virtual", StringComparer.Ordinal) || method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsAbstract = isAbstractMethod,
                        TypeParameters = methodTypeParameters,
                        TypeParameterConstraints = methodConstraints,
                        IsOverride = method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsSealedOverride = method.Modifiers.Contains("sealed", StringComparer.Ordinal),
                    };
                    if (isAssemblyFunction &&
                        (!isStatic || !symbol.IsUnsafe || isAbstractMethod || external is not null || symbol.IsVirtual ||
                         !method.TypeParameters.IsDefaultOrEmpty || entry is not null || taskEntryAttribute is not null ||
                         runtimeImplAttribute is not null || interruptAttribute is not null ||
                         !IsInlineAssemblyValueType(returnType, allowVoid: true) || methodParameters.Any(parameter => !IsInlineAssemblyValueType(parameter.Type, allowVoid: false))))
                        Diagnostics.Add("CT1307", "An asm function must be a static unsafe non-generic non-virtual method with only scalar assembly-compatible parameters and result.", method.Source, method.Span);
                    if (entry is not null && (!isStatic || symbol.ReturnType != CType.Void || symbol.Parameters.Length != 0 || !hasBody))
                        Diagnostics.Add("CT1207", "EntryPoint must mark a body-bearing static void method with no parameters.", entry.Source, entry.Span);
                    if (taskEntryAttribute is not null &&
                        (_target != CompilationTarget.EspIdf || accessibility != Accessibility.Public || !isStatic || !hasBody || isAbstractMethod ||
                         external is not null || exportName is null || entry is not null || !method.TypeParameters.IsDefaultOrEmpty || returnType != CType.Void ||
                         methodParameters is not [{ Type.Kind: CTypeKind.Pointer, Type.ElementType.Kind: CTypeKind.Void }]))
                        Diagnostics.Add("CT1292", "TaskEntry requires an ESP-IDF public static non-generic exported void(void*) method body.", taskEntryAttribute.Source, taskEntryAttribute.Span);
                    if (runtimeImplAttribute is not null &&
                        (_target != CompilationTarget.Freestanding || runtimeImplementation is null ||
                         !isStatic || !hasBody || isAbstractMethod || external is not null || export is not null || entry is not null ||
                         taskEntryAttribute is not null || nakedAttribute is not null || !method.TypeParameters.IsDefaultOrEmpty || noAlloc is null))
                        Diagnostics.Add("CT1299", "RuntimeImpl requires a freestanding non-generic static body-bearing NoAlloc method without native-entry attributes.", runtimeImplAttribute.Source, runtimeImplAttribute.Span);
                    if (nakedAttribute is not null && !IsValidNakedMethod(symbol))
                        Diagnostics.Add("CT1302", "Naked requires a freestanding public static unsafe non-generic NoAlloc exported void() assembly function without operands, or the compatible one-statement NoAlloc asm form.", nakedAttribute.Source, nakedAttribute.Span);
                    if (interruptSafeAttribute is not null && external is null && !isAssemblyFunction)
                        Diagnostics.Add("CT1306", "InterruptSafe methods must be extern or assembly native boundaries.", interruptSafeAttribute.Source, interruptSafeAttribute.Span);
                    if (interruptAttribute is not null && _target != CompilationTarget.EspIdf)
                        Diagnostics.Add("CT4117", "Interrupt entry points require the ESP-IDF target.", interruptAttribute.Source, interruptAttribute.Span);
                    if (interruptAttribute is not null &&
                        (accessibility != Accessibility.Public || !isStatic || !symbol.IsUnsafe || !hasBody || isAbstractMethod ||
                         external is not null || exportName is null || entry is not null || taskEntryAttribute is not null || runtimeImplAttribute is not null ||
                         nakedAttribute is not null || sectionAttribute is not null || symbol.IsVirtual || !method.TypeParameters.IsDefaultOrEmpty ||
                         returnType != CType.Void || methodParameters is not [{ PassingKind: ParameterPassingKind.Value, Type.Kind: CTypeKind.Pointer, Type.ElementType.Kind: CTypeKind.Void }]))
                        Diagnostics.Add("CT1306", "Interrupt requires an ESP-IDF public static unsafe non-generic exported void(void*) method body without conflicting entry or placement attributes.", interruptAttribute.Source, interruptAttribute.Span);
                    if (symbol.IsVirtual && accessibility == Accessibility.Private)
                        Diagnostics.Add("CT1228", "A virtual or override method cannot be private.", method.Source, method.Span);
                    if (symbol.IsNoRuntime && (returnType.ContainsManagedReferences || methodParameters.Any(parameter => parameter.Type.ContainsManagedReferences)))
                        Diagnostics.Add("CT1305", "NoRuntime methods cannot have managed parameters or results.", method.Source, method.Span);
                    AddMethod(type.Methods, symbol);
                    _activeTypeParameters = previousTypeParameters;
                    break;
                }
        }
    }

    private static bool ForbiddenNativeBoundaryType(CType type)
    {
        if (type.Kind is CTypeKind.Interface or CTypeKind.TypeParameter || type.IsAtomic ||
            type.Symbol is { Namespace: "System.Threading", Name: "Thread" or "Mutex" } ||
            type.Symbol?.IsOpenConstructed == true)
            return true;
        return type.ElementType is not null && ForbiddenNativeBoundaryType(type.ElementType);
    }

    private static bool IsInlineAssemblyValueType(CType type, bool allowVoid) =>
        allowVoid && type == CType.Void || type.Kind is
            CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
            CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or
            CTypeKind.Float or CTypeKind.Double or CTypeKind.Enum or CTypeKind.Newtype or CTypeKind.Opaque or CTypeKind.Pointer or CTypeKind.FunctionPointer;

    private ImmutableArray<ParameterSymbol> DeclareParameters(ImmutableArray<ParameterSyntax> parameters, SyntaxTree tree, bool isExtern)
    {
        var result = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!names.Add(parameter.Name))
                Diagnostics.Add("CT1102", $"Parameter '{parameter.Name}' is already declared.", parameter.Source, parameter.Span);
            ValidateAttributes(parameter.Attributes, parameter, ["Borrowed", "Consumes", "Retained", "Creates", "Nullable", "SynchronousCallback"]);
            var type = ResolveType(parameter.Type, tree);
            var borrowed = FindAttribute(parameter.Attributes, "Borrowed");
            var consumes = FindAttribute(parameter.Attributes, "Consumes");
            var retained = FindAttribute(parameter.Attributes, "Retained");
            var creates = FindAttribute(parameter.Attributes, "Creates");
            var nullable = FindAttribute(parameter.Attributes, "Nullable");
            var synchronousCallback = FindAttribute(parameter.Attributes, "SynchronousCallback");
            if (type.IsNativeBuffer && parameter.PassingKind != ParameterPassingKind.Value)
                Diagnostics.Add("CT2187", "Native-buffer parameters cannot use ref, in, or out.", parameter.Source, parameter.Span);
            if (type.IsNativeUtf8String && parameter.PassingKind != ParameterPassingKind.Value)
                Diagnostics.Add("CT1264", "NativeUtf8String parameters cannot use ref, in, or out.", parameter.Source, parameter.Span);
            if (isExtern && parameter.PassingKind != ParameterPassingKind.Value && !IsCompleteUnmanagedType(type))
                Diagnostics.Add("CT2188", $"Extern by-reference parameter type '{type.DisplayName}' is not unmanaged ABI-safe.", parameter.Source, parameter.Span);
            var ownershipAttributes = new[] { borrowed, consumes, retained, creates }.Where(attribute => attribute is not null).ToArray();
            if (ownershipAttributes.Any(attribute => attribute!.Arguments.Length != 0) || ownershipAttributes.Length > 1)
                Diagnostics.Add("CT1249", "Borrowed, Consumes, Retained, and Creates accept no arguments and are mutually exclusive.", parameter.Source, parameter.Span);
            var nativeResource = type.Kind is CTypeKind.Opaque or CTypeKind.Pointer;
            if (retained is not null && (retained.Arguments.Length != 0 || !isExtern || !(type.IsReference && parameter.PassingKind == ParameterPassingKind.Value || nativeResource && parameter.PassingKind == ParameterPassingKind.Value)))
                Diagnostics.Add("CT1234", "Retained requires a value parameter of an extern method and a managed reference, opaque handle, or pointer type.", retained.Source, retained.Span);
            if ((borrowed is not null || consumes is not null) && (!isExtern || !nativeResource || parameter.PassingKind != ParameterPassingKind.Value))
                Diagnostics.Add("CT1250", "Borrowed and Consumes require a value opaque-handle or pointer parameter of an extern method.", parameter.Source, parameter.Span);
            if (creates is not null && (!isExtern || !nativeResource || parameter.PassingKind != ParameterPassingKind.Out))
                Diagnostics.Add("CT1251", "Creates requires an out opaque-handle or pointer parameter of an extern method.", creates.Source, creates.Span);
            if (nullable is not null && (nullable.Arguments.Length != 0 || !(nativeResource || type.Kind == CTypeKind.Delegate || type.IsNativeUtf8String)))
                Diagnostics.Add("CT1252", "Nullable accepts no arguments and requires an opaque handle, pointer, delegate, or NativeUtf8String parameter.", nullable.Source, nullable.Span);
            if (synchronousCallback is not null && (synchronousCallback.Arguments.Length != 0 || !isExtern || type.Kind != CTypeKind.Delegate || parameter.PassingKind != ParameterPassingKind.Value))
                Diagnostics.Add("CT1253", "SynchronousCallback requires a value delegate parameter of an extern method.", synchronousCallback.Source, synchronousCallback.Span);
            result.Add(new ParameterSymbol
            {
                Name = parameter.Name,
                Type = type,
                Syntax = parameter,
                PassingKind = parameter.PassingKind,
                IsRetained = retained is not null && retained.Arguments.Length == 0 && isExtern && type.IsReference && parameter.PassingKind == ParameterPassingKind.Value,
                NativeOwnership = creates is not null ? NativeParameterOwnership.Creates : consumes is not null ? NativeParameterOwnership.Consumes : retained is not null && nativeResource ? NativeParameterOwnership.Retained : NativeParameterOwnership.Borrowed,
                IsNullable = nullable is not null,
                IsSynchronousCallback = synchronousCallback is not null,
            });
        }
        return result.ToImmutable();
    }

    private void DeclareEnum(TypeSymbol type, TypeDeclarationSyntax declaration, SyntaxTree tree)
    {
        var underlying = declaration.EnumUnderlyingType is null ? CType.Int : ResolveType(declaration.EnumUnderlyingType, tree);
        if (underlying.Kind is not CTypeKind.Byte and not CTypeKind.Sbyte and not CTypeKind.Short and not CTypeKind.Ushort and not CTypeKind.Int and not CTypeKind.Uint and not CTypeKind.Long and not CTypeKind.Ulong)
            Diagnostics.Add("CT1208", "An enum underlying type must be an integral type other than bool or char.", declaration.Source, declaration.EnumUnderlyingType?.Span ?? declaration.Span);
        var value = BigInteger.Zero;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in declaration.EnumMembers)
        {
            if (!names.Add(member.Name))
            {
                Diagnostics.Add("CT1103", $"Enum member '{member.Name}' is already declared.", member.Source, member.Span);
                continue;
            }
            if (member.Value is LiteralExpressionSyntax { Value: NumericLiteralValue numeric, LiteralKind: SyntaxKind.NumberToken } && numeric.FloatingPoint is null)
                value = numeric.Integer;
            else if (member.Value is not null)
                Diagnostics.Add("CT1209", "An enum value must be an integral constant.", member.Source, member.Value.Span);
            if (!FitsEnumValue(value, underlying))
                Diagnostics.Add("CT1215", $"Enum value {value} does not fit underlying type '{underlying.DisplayName}'.", member.Source, member.Span);
            type.EnumValues.Add(new EnumValueSymbol(member.Name, value, member));
            value++;
        }
        type.Fields.Add(new FieldSymbol
        {
            Name = "<underlying>",
            ContainingType = type,
            Accessibility = Accessibility.Private,
            IsStatic = true,
            Syntax = declaration,
            Type = underlying,
            IsReadonly = true,
            IsConst = true,
            IsUnsafe = false,
        });
    }

    private RuntimeImplementationRole? ParseRuntimeImplementation(AttributeSyntax? attribute)
    {
        if (attribute is null)
            return null;
        if (attribute.Arguments is [MemberAccessExpressionSyntax { Receiver: NameExpressionSyntax receiver } member] &&
            receiver.Name is "Runtime" or "System.Runtime.Runtime" &&
            Enum.TryParse<RuntimeImplementationRole>(member.Name, ignoreCase: false, out var role))
            return role;
        Diagnostics.Add("CT1299", "RuntimeImpl requires one Runtime.Allocate, Runtime.Free, or Runtime.Panic role.", attribute.Source, attribute.Span);
        return null;
    }

    private bool IsValidNakedMethod(MethodSymbol method)
    {
        if (_target != CompilationTarget.Freestanding || method.Accessibility != Accessibility.Public || !method.IsStatic || !method.IsUnsafe ||
            method.ReturnType != CType.Void || method.Parameters.Length != 0 || method.ExportName is null || !method.IsNoAlloc || method.IsGenericDefinition)
            return false;
        if (method.IsAssemblyFunction)
            return method.AssemblyBody is { Operands.IsEmpty: true, Clobbers.IsEmpty: true };
        if (method.Body is not { Statements: [InlineAssemblyStatementSyntax assembly] })
            return false;
        return assembly.Operands.IsEmpty && assembly.Clobbers.IsEmpty &&
            assembly.Attributes is [AttributeSyntax { Name: "NoAlloc", Arguments.IsEmpty: true }];
    }

    private void ValidateRuntimeImplementations()
    {
        var groups = UserTypes.SelectMany(type => type.Methods)
            .Where(method => method.RuntimeImplementation is not null)
            .GroupBy(method => method.RuntimeImplementation!.Value)
            .ToArray();
        var implementations = ImmutableDictionary.CreateBuilder<RuntimeImplementationRole, MethodSymbol>();
        foreach (var group in groups)
        {
            var methods = group.ToArray();
            if (methods.Length > 1)
            {
                foreach (var duplicate in methods.Skip(1))
                    Diagnostics.Add("CT4114", $"Runtime role '{group.Key}' is implemented more than once.", duplicate.Syntax!.Source, duplicate.Syntax.Span,
                        methods[0].Syntax!.Source.GetLocation(methods[0].Syntax!.Span));
            }
            implementations[group.Key] = methods[0];
        }
        RuntimeImplementations = implementations.ToImmutable();

        foreach (var pair in RuntimeImplementations)
        {
            var method = pair.Value;
            var valid = pair.Key switch
            {
                RuntimeImplementationRole.Allocate => method.ReturnType is { Kind: CTypeKind.Pointer, ElementType.Kind: CTypeKind.Void } &&
                    method.Parameters is [{ PassingKind: ParameterPassingKind.Value, Type.Kind: CTypeKind.Nuint }],
                RuntimeImplementationRole.Free => method.ReturnType == CType.Void &&
                    method.Parameters is [{ PassingKind: ParameterPassingKind.Value, Type: { Kind: CTypeKind.Pointer, ElementType.Kind: CTypeKind.Void } }],
                RuntimeImplementationRole.Panic => method.ReturnType == CType.Void && method.Parameters is [{ PassingKind: ParameterPassingKind.Value } parameter] &&
                    parameter.Type.Symbol?.FullName == "System.Runtime.RuntimePanicInfo",
                _ => false,
            };
            if (!valid)
                Diagnostics.Add("CT1299", $"Runtime role '{pair.Key}' has an invalid signature.", method.Syntax!.Source, method.Syntax.Span);
        }
    }

    private void ValidateEntryPoint()
    {
        var entries = UserTypes.SelectMany(type => type.Methods).Where(method => method.IsEntryPoint).ToArray();
        if (_target == CompilationTarget.Freestanding)
        {
            foreach (var entry in entries)
                Diagnostics.Add("CT4115", "EntryPoint is unavailable for freestanding compilations; export an explicit native entry symbol instead.", entry.Syntax!.Source, entry.Syntax.Span);
            return;
        }
        if (entries.Length == 1)
            EntryPoint = entries[0];
        else if (entries.Length == 0)
        {
            var source = UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
            Diagnostics.Add("CT1300", "The program must declare exactly one EntryPoint.", source, new TextSpan(0, 0));
        }
        else
        {
            foreach (var entry in entries.Skip(1))
                Diagnostics.Add("CT1301", "The program has more than one EntryPoint.", entry.Syntax!.Source, entry.Syntax.Span, entries[0].Syntax!.Source.GetLocation(entries[0].Syntax!.Span));
            EntryPoint = entries[0];
        }
    }

    private void ValidateAggregateLayouts()
    {
        var definitions = Types.Values.Where(type => type is { Kind: DeclaredTypeKind.Struct, Syntax: not null, GenericDefinition: null } && !type.IsBitField).Distinct().ToArray();
        foreach (var type in definitions)
        {
            var declaration = type.Syntax!;
            var isUnion = declaration.Kind == TypeDeclarationKind.Union;
            var instanceFields = type.Fields.Where(field => !field.IsStatic).ToArray();

            if (isUnion)
            {
                if (!declaration.BaseTypes.IsDefaultOrEmpty || declaration.BaseType is not null)
                    Diagnostics.Add("CT1282", "A union cannot declare base types or interfaces.", declaration.Source, declaration.Span);
                foreach (var member in declaration.Members)
                {
                    var permitted = member switch
                    {
                        FieldDeclarationSyntax => true,
                        MethodDeclarationSyntax method => method.Modifiers.Contains("static", StringComparer.Ordinal),
                        _ => false,
                    };
                    if (!permitted)
                        Diagnostics.Add("CT1282", "A union permits instance fields, static fields or constants, and static methods only.", member.Source, member.Span);
                }
            }

            if (isUnion && instanceFields.Any(field => field.Offset is not null))
                foreach (var field in instanceFields.Where(field => field.Offset is not null))
                    Diagnostics.Add("CT1281", "Union fields are implicitly located at offset zero and cannot use FieldOffset.", field.Syntax!.Source, field.Syntax.Span);

            var explicitLayout = !isUnion && instanceFields.Any(field => field.Offset is not null);
            if (explicitLayout)
                type.AggregateLayout = AggregateLayoutKind.Explicit;
            if (explicitLayout)
            {
                foreach (var field in instanceFields.Where(field => field.Offset is null))
                    Diagnostics.Add("CT1283", "Every instance field in an explicit-layout struct requires FieldOffset.", field.Syntax!.Source, field.Syntax.Span);
                foreach (var property in type.Properties.Where(property => !property.IsStatic && property.BackingField is not null))
                    Diagnostics.Add("CT1283", "An explicit-layout struct cannot contain an auto-property because its backing field has no explicit offset.", property.Syntax!.Source, property.Syntax.Span);
            }

            foreach (var field in instanceFields)
            {
                if (field.Offset is not null && field.ContainingType.Kind != DeclaredTypeKind.Struct)
                    Diagnostics.Add("CT1281", "FieldOffset is valid only on instance fields of a struct.", field.Syntax!.Source, field.Syntax.Span);
                if ((isUnion || explicitLayout) && field.Initializer is not null)
                    Diagnostics.Add("CT1284", "Union and explicit-layout instance fields cannot have initializers.", field.Syntax!.Source, field.Syntax.Span);
            }
        }

        foreach (var type in Types.Values.Where(type => type.Kind == DeclaredTypeKind.Struct && !type.IsOpenConstructed).Distinct())
        {
            if (type.GenericDefinition is not null)
            {
                type.AggregateLayout = type.GenericDefinition.AggregateLayout;
                foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Offset is not null))
                    type.AggregateLayout = AggregateLayoutKind.Explicit;
            }
            if (type.AggregateLayout == AggregateLayoutKind.Sequential && type.Pack is null)
                continue;
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
            {
                if (!IsLayoutUnmanaged(field.Type, type, []))
                    Diagnostics.Add("CT1285", $"Field '{field.Name}' must have a complete unmanaged type in a union, packed struct, or explicit-layout struct.", field.Syntax!.Source, field.Syntax.Span);
                if (field.IsVolatile)
                    Diagnostics.Add("CT1285", "Non-natural aggregate layouts cannot contain volatile fields.", field.Syntax!.Source, field.Syntax.Span);
            }
        }

        bool IsLayoutUnmanaged(CType candidate, TypeSymbol context, HashSet<TypeSymbol> visiting)
        {
            if (candidate.Kind == CTypeKind.TypeParameter && candidate.Symbol is not null)
                return context.TypeParameterConstraints.TryGetValue(candidate.Symbol.Name, out var constraint) && constraint.RequiresUnmanaged;
            if (candidate.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
                CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Double or
                CTypeKind.Enum or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.FunctionPointer)
                return true;
            if (candidate.Kind == CTypeKind.Newtype && candidate.Symbol?.UnderlyingType is { } underlying)
                return IsLayoutUnmanaged(underlying, context, visiting);
            if (candidate.Kind != CTypeKind.Struct || candidate.Symbol is null || !visiting.Add(candidate.Symbol))
                return false;
            var result = candidate.Symbol.Fields.Where(field => !field.IsStatic).All(field => IsLayoutUnmanaged(field.Type, candidate.Symbol, visiting));
            visiting.Remove(candidate.Symbol);
            return result;
        }
    }

    private void ValidateInheritanceMembers()
    {
        var objectType = Types.GetValueOrDefault("System.Object");
        foreach (var contract in Types.Values.Where(type => type.Kind == DeclaredTypeKind.Interface && !type.IsGenericDefinition && !type.IsOpenConstructed).Distinct())
        {
            foreach (var property in contract.Properties)
                ValidateVirtualModifiers(property.IsStatic, property.IsVirtual, property.IsOverride, property.IsSealedOverride, property.Syntax!);
            foreach (var method in contract.Methods)
                ValidateVirtualModifiers(method.IsStatic, method.IsVirtual, method.IsOverride, method.IsSealedOverride, method.Syntax!);
        }
        foreach (var type in Types.Values.Where(type => type.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Struct))
        {
            var baseTypes = type.Kind == DeclaredTypeKind.Class
                ? type.BaseTypesAndSelf().Skip(1).ToArray()
                : Array.Empty<TypeSymbol>();
            var inheritedMembers = baseTypes.SelectMany(baseType => baseType.Fields.Cast<MemberSymbol>().Concat(baseType.Properties).Concat(baseType.Methods)).ToArray();

            foreach (var member in type.Fields.Cast<MemberSymbol>())
            {
                if (inheritedMembers.Any(candidate => candidate.Name == member.Name))
                    Diagnostics.Add("CT1230", $"Member '{member.Name}' hides an inherited member; member hiding is not supported.", member.Syntax!.Source, member.Syntax.Span);
            }

            foreach (var property in type.Properties)
            {
                ValidateVirtualModifiers(property.IsStatic, property.IsVirtual, property.IsOverride, property.IsSealedOverride, property.Syntax!);
                if (type.Kind == DeclaredTypeKind.Struct && property.IsVirtual)
                    Diagnostics.Add("CT1228", "A structure cannot declare a virtual property.", property.Syntax!.Source, property.Syntax.Span);
                var candidate = baseTypes.SelectMany(baseType => baseType.Properties).FirstOrDefault(baseProperty => baseProperty.Name == property.Name);
                if (property.IsOverride)
                {
                    if (candidate is null || !candidate.IsVirtual || candidate.IsSealedOverride || candidate.Type != property.Type ||
                        (candidate.Getter is null) != (property.Getter is null) || (candidate.Setter is null) != (property.Setter is null) ||
                        candidate.Accessibility != property.Accessibility || candidate.GetterAccessibility != property.GetterAccessibility ||
                        candidate.SetterAccessibility != property.SetterAccessibility)
                        Diagnostics.Add("CT1229", $"Property '{property.Name}' does not match an accessible unsealed virtual base property.", property.Syntax!.Source, property.Syntax.Span);
                    else
                    {
                        property.OverriddenProperty = candidate;
                        property.DeclaredEffects |= candidate.DeclaredEffects;
                        property.GetterDeclaredEffects |= candidate.GetterDeclaredEffects;
                        property.SetterDeclaredEffects |= candidate.SetterDeclaredEffects;
                    }
                }
                else if (candidate is not null)
                    Diagnostics.Add("CT1230", $"Property '{property.Name}' hides an inherited property; use override for a virtual property.", property.Syntax!.Source, property.Syntax.Span);
            }

            foreach (var method in type.Methods)
            {
                if (method.IsOperator)
                    continue;
                ValidateVirtualModifiers(method.IsStatic, method.IsVirtual, method.IsOverride, method.IsSealedOverride, method.Syntax!);
                if (type.Kind == DeclaredTypeKind.Struct && method.IsVirtual && !method.IsOverride)
                    Diagnostics.Add("CT1228", "A structure can override only ToString, Equals(object), and GetHashCode.", method.Syntax!.Source, method.Syntax.Span);
                var candidate = baseTypes.SelectMany(baseType => baseType.Methods)
                    .FirstOrDefault(baseMethod => HaveSameSourceSignature(baseMethod, method));
                if (type.Kind == DeclaredTypeKind.Struct && method.IsOverride && objectType is not null)
                    candidate = objectType.Methods.FirstOrDefault(baseMethod => HaveSameSourceSignature(baseMethod, method) && baseMethod.Name is "ToString" or "Equals" or "GetHashCode");
                if (method.IsOverride)
                {
                    if (candidate is null || !candidate.IsVirtual || candidate.IsSealedOverride || candidate.ReturnType != method.ReturnType || candidate.Accessibility != method.Accessibility)
                        Diagnostics.Add("CT1229", $"Method '{method.Name}' does not match an accessible unsealed virtual base method.", method.Syntax!.Source, method.Syntax.Span);
                    else
                    {
                        method.OverriddenMethod = candidate;
                        method.DeclaredEffects |= candidate.DeclaredEffects;
                    }
                }
                else if (candidate is not null && type.Kind == DeclaredTypeKind.Class)
                    Diagnostics.Add("CT1230", $"Method '{method.Name}' hides an inherited method; use override for a virtual method.", method.Syntax!.Source, method.Syntax.Span);
                if (inheritedMembers.Any(member => member.Name == method.Name && member is not MethodSymbol))
                    Diagnostics.Add("CT1230", $"Method '{method.Name}' hides an inherited member; member hiding is not supported.", method.Syntax!.Source, method.Syntax.Span);
            }

            var concrete = type.Kind == DeclaredTypeKind.Struct || !type.IsAbstract;
            foreach (var baseProperty in baseTypes.SelectMany(baseType => baseType.Properties).Where(property => property.IsAbstract))
            {
                var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Properties)
                    .FirstOrDefault(candidate => candidate != baseProperty && ResolvesPropertyContract(candidate, baseProperty));
                if (concrete && implementation is null)
                    Diagnostics.Add("CT1275", $"Concrete type '{type.FullName}' does not implement abstract property '{baseProperty.Name}'.", type.Syntax!.Source, type.Syntax.Span);
            }
            foreach (var baseMethod in baseTypes.SelectMany(baseType => baseType.Methods).Where(method => method.IsAbstract))
            {
                var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Methods)
                    .FirstOrDefault(candidate => candidate != baseMethod && !candidate.IsAbstract && HaveSameSourceSignature(candidate, baseMethod) && candidate.ReturnType == baseMethod.ReturnType);
                if (concrete && implementation is null)
                    Diagnostics.Add("CT1275", $"Concrete type '{type.FullName}' does not implement abstract method '{baseMethod.Name}'.", type.Syntax!.Source, type.Syntax.Span);
            }

            foreach (var contract in EnumerateInterfaces(type))
            {
                foreach (var required in contract.Properties)
                {
                    var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Properties)
                        .FirstOrDefault(candidate => ResolvesPropertyContract(candidate, required));
                    if (implementation is not null)
                    {
                        if (!implementation.ImplementedInterfaceProperties.Contains(required))
                            implementation.ImplementedInterfaceProperties.Add(required);
                        implementation.DeclaredEffects |= required.DeclaredEffects;
                        implementation.GetterDeclaredEffects |= required.GetterDeclaredEffects;
                        implementation.SetterDeclaredEffects |= required.SetterDeclaredEffects;
                    }
                    else if (concrete)
                        Diagnostics.Add("CT1275", $"Concrete type '{type.FullName}' does not implement interface property '{contract.FullName}.{required.Name}'.", type.Syntax!.Source, type.Syntax.Span);
                }
                foreach (var required in contract.Methods)
                {
                    var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Methods)
                        .FirstOrDefault(candidate => candidate.Accessibility == Accessibility.Public && !candidate.IsStatic && !candidate.IsAbstract &&
                            HaveSameSourceSignature(candidate, required) && candidate.ReturnType == required.ReturnType);
                    if (implementation is not null)
                    {
                        if (!implementation.ImplementedInterfaceMethods.Contains(required))
                            implementation.ImplementedInterfaceMethods.Add(required);
                        implementation.DeclaredEffects |= required.DeclaredEffects;
                    }
                    else if (concrete)
                        Diagnostics.Add("CT1275", $"Concrete type '{type.FullName}' does not implement interface method '{contract.FullName}.{required.Name}'.", type.Syntax!.Source, type.Syntax.Span);
                }
            }
        }
    }

    private static bool ResolvesPropertyContract(PropertySymbol candidate, PropertySymbol contract) =>
        candidate.Accessibility == Accessibility.Public && !candidate.IsStatic && !candidate.IsAbstract && candidate.Name == contract.Name &&
        candidate.Type == contract.Type && (contract.Getter is null || candidate.Getter is not null) && (contract.Setter is null || candidate.Setter is not null);

    private static IEnumerable<TypeSymbol> EnumerateInterfaces(TypeSymbol type)
    {
        var pending = new Stack<TypeSymbol>(type.BaseTypesAndSelf().SelectMany(candidate => candidate.Interfaces));
        var visited = new HashSet<TypeSymbol>();
        while (pending.TryPop(out var contract))
        {
            if (!visited.Add(contract))
                continue;
            yield return contract;
            foreach (var inherited in contract.Interfaces)
                pending.Push(inherited);
        }
    }

    private void ValidateVirtualModifiers(bool isStatic, bool isVirtual, bool isOverride, bool isSealedOverride, SyntaxNode syntax)
    {
        if (isStatic && isVirtual)
            Diagnostics.Add("CT1228", "A static member cannot be virtual or override.", syntax.Source, syntax.Span);
        if (isVirtual && !isOverride && syntax is MemberDeclarationSyntax declaration && declaration.Modifiers.Contains("sealed", StringComparer.Ordinal))
            Diagnostics.Add("CT1228", "sealed on a member requires override.", syntax.Source, syntax.Span);
        if (syntax is MemberDeclarationSyntax member && member.Modifiers.Contains("virtual", StringComparer.Ordinal) && member.Modifiers.Contains("override", StringComparer.Ordinal))
            Diagnostics.Add("CT1228", "A member cannot be both virtual and override.", syntax.Source, syntax.Span);
    }

    private static bool HaveSameSourceSignature(MethodSymbol left, MethodSymbol right) =>
        left.Name == right.Name && left.Parameters.Select(parameter => (parameter.Type, parameter.PassingKind)).SequenceEqual(right.Parameters.Select(parameter => (parameter.Type, parameter.PassingKind)));

    private static bool FitsEnumValue(BigInteger value, CType underlying) => underlying.Kind switch
    {
        CTypeKind.Byte => value >= byte.MinValue && value <= byte.MaxValue,
        CTypeKind.Sbyte => value >= sbyte.MinValue && value <= sbyte.MaxValue,
        CTypeKind.Short => value >= short.MinValue && value <= short.MaxValue,
        CTypeKind.Ushort => value >= ushort.MinValue && value <= ushort.MaxValue,
        CTypeKind.Int => value >= int.MinValue && value <= int.MaxValue,
        CTypeKind.Uint => value >= uint.MinValue && value <= uint.MaxValue,
        CTypeKind.Long => value >= long.MinValue && value <= long.MaxValue,
        CTypeKind.Ulong => value >= ulong.MinValue && value <= ulong.MaxValue,
        _ => false,
    };

    private static bool IsVolatileType(CType type) => type.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or
        CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or
        CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Enum or CTypeKind.Pointer;

    private static int AccessRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.Protected => 1,
        Accessibility.Internal => 2,
        Accessibility.Public => 3,
        _ => 0,
    };

    private void AddUnique(TypeSymbol type, MemberSymbol member)
    {
        var existing = type.Fields.Cast<MemberSymbol>().Concat(type.Properties).FirstOrDefault(candidate => candidate.Name == member.Name);
        if (existing is not null)
        {
            Diagnostics.Add("CT1104", $"Member '{member.Name}' is already declared in '{type.FullName}'.", member.Syntax!.Source, member.Syntax.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
            return;
        }
        if (member is FieldSymbol field)
            type.Fields.Add(field);
        else
            type.Properties.Add((PropertySymbol)member);
    }

    private void AddMethod(List<MethodSymbol> methods, MethodSymbol method)
    {
        var existing = methods.FirstOrDefault(candidate => candidate.Name == method.Name &&
            candidate.Parameters.Select(parameter => (parameter.Type, NormalizePassingKind(parameter.PassingKind)))
                .SequenceEqual(method.Parameters.Select(parameter => (parameter.Type, NormalizePassingKind(parameter.PassingKind)))));
        if (existing is not null)
        {
            var displayName = method.IsOperator ? OperatorFacts.DisplayName(method.OperatorKind) : method.Name;
            Diagnostics.Add("CT1105", $"Method '{displayName}' with the same parameter types is already declared.", method.Syntax!.Source, method.Syntax.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
            return;
        }
        methods.Add(method);
    }

    private static ParameterPassingKind NormalizePassingKind(ParameterPassingKind kind) => kind == ParameterPassingKind.Out ? ParameterPassingKind.Ref : kind;

    private Accessibility GetAccessibility(ImmutableArray<string> modifiers, SyntaxNode syntax, Accessibility fallback)
    {
        var values = modifiers.Where(modifier => modifier is "public" or "internal" or "protected" or "private").ToArray();
        if (values.Length > 1)
            Diagnostics.Add("CT1210", "Only one access modifier is permitted.", syntax.Source, syntax.Span);
        return values.FirstOrDefault() switch
        {
            "public" => Accessibility.Public,
            "internal" => Accessibility.Internal,
            "protected" => Accessibility.Protected,
            "private" => Accessibility.Private,
            _ => fallback,
        };
    }

    private void ValidateModifiers(ImmutableArray<string> modifiers, SyntaxNode syntax)
    {
        foreach (var duplicate in modifiers.GroupBy(modifier => modifier, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add("CT1212", $"Duplicate modifier '{duplicate.Key}'.", syntax.Source, syntax.Span);
    }

    private void ValidateAllowedModifiers(ImmutableArray<string> modifiers, string[] allowed, SyntaxNode syntax)
    {
        foreach (var modifier in modifiers.Where(modifier => !allowed.Contains(modifier, StringComparer.Ordinal)))
            Diagnostics.Add("CT1221", $"Modifier '{modifier}' is not valid on this declaration.", syntax.Source, syntax.Span);
    }

    private void ValidatePointerExposure(CType type, ImmutableArray<string> modifiers, SyntaxNode syntax)
    {
        if (type.ContainsPointer && !modifiers.Contains("unsafe", StringComparer.Ordinal))
            Diagnostics.Add("CT2141", "A pointer in a member signature requires the unsafe modifier.", syntax.Source, syntax.Span);
    }

    private void ValidateRecursivePointerExposure()
    {
        foreach (var type in Types.Values)
        {
            foreach (var field in type.Fields.Where(field => field.Syntax is MemberDeclarationSyntax))
                ValidatePointerExposure(field.Type, ((MemberDeclarationSyntax)field.Syntax!).Modifiers, field.Syntax!);
            foreach (var property in type.Properties)
                ValidatePointerExposure(property.Type, ((MemberDeclarationSyntax)property.Syntax!).Modifiers, property.Syntax!);
            foreach (var method in type.Constructors.Concat(type.Methods).Where(method => method.Syntax is MemberDeclarationSyntax))
            {
                var modifiers = ((MemberDeclarationSyntax)method.Syntax!).Modifiers;
                ValidatePointerExposure(method.ReturnType, modifiers, method.Syntax!);
                foreach (var parameter in method.Parameters)
                    ValidatePointerExposure(parameter.Type, modifiers, method.Syntax!);
            }
        }
    }

    private void ValidateAttributes(ImmutableArray<AttributeSyntax> attributes, SyntaxNode syntax, string[] allowed)
    {
        foreach (var attribute in attributes)
        {
            if (!allowed.Contains(attribute.Name, StringComparer.Ordinal))
            {
                var code = attribute.Name switch
                {
                    "NoThrow" => "CT1303",
                    "NoBlock" => "CT1304",
                    "NoRuntime" => "CT1305",
                    "Interrupt" or "InterruptSafe" => "CT1306",
                    _ => "CT1213",
                };
                Diagnostics.Add(code, $"Unknown or invalid attribute '{attribute.Name}' on this declaration.", attribute.Source, attribute.Span);
            }
        }
        foreach (var duplicate in attributes.GroupBy(attribute => attribute.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add(duplicate.Key is "Interrupt" or "InterruptSafe" ? "CT1306" : "CT1214",
                $"Attribute '{duplicate.Key}' cannot be applied more than once.", syntax.Source, syntax.Span);
    }

    private static AttributeSyntax? FindAttribute(ImmutableArray<AttributeSyntax> attributes, string name) => attributes.FirstOrDefault(attribute => attribute.Name == name);

    private EffectContract ParseEffectContracts(ImmutableArray<AttributeSyntax> attributes)
    {
        var result = EffectContract.None;
        foreach (var (name, contract, malformedCode) in new[]
                 {
                     ("NoAlloc", EffectContract.NoAlloc, "CT1233"),
                     ("NoThrow", EffectContract.NoThrow, "CT1303"),
                     ("NoBlock", EffectContract.NoBlock, "CT1304"),
                     ("NoRuntime", EffectContract.NoRuntime, "CT1305"),
                 })
        {
            var attribute = FindAttribute(attributes, name);
            if (attribute is null)
                continue;
            result |= contract;
            if (!attribute.Arguments.IsEmpty)
                Diagnostics.Add(malformedCode, $"{name} does not accept arguments.", attribute.Source, attribute.Span);
        }
        return result;
    }

    private int? ParseAlignment(AttributeSyntax? attribute, IReadOnlySet<string> symbolicNames, out string? parameterName)
    {
        parameterName = null;
        if (attribute is null)
            return null;
        if (attribute.Arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue numeric, LiteralKind: SyntaxKind.NumberToken }] &&
            numeric.FloatingPoint is null && numeric.Integer >= BigInteger.One && numeric.Integer <= new BigInteger(8192) && (numeric.Integer & (numeric.Integer - BigInteger.One)) == BigInteger.Zero)
            return (int)numeric.Integer;
        if (attribute.Arguments is [NameExpressionSyntax name] && symbolicNames.Contains(name.Name))
        {
            parameterName = name.Name;
            return null;
        }
        Diagnostics.Add("CT1293", "Align requires one power-of-two integral constant from 1 through 8192.", attribute.Source, attribute.Span);
        return null;
    }

    private string? ParseSectionName(AttributeSyntax? attribute)
    {
        if (attribute is null)
            return null;
        if (attribute.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && NativeSection.IsValidName(value))
            return value;
        Diagnostics.Add("CT1286", "Section requires one ASCII section name of 1 to 128 characters using letters, digits, '.', '_', '$', or '-'.", attribute.Source, attribute.Span);
        return null;
    }

    private static bool IsPortableExternalIdentifier(string value) =>
        CIdentifier.IsMatch(value) && !value.StartsWith('_') && !CKeywords.Contains(value);

    private static bool IsLinkerSymbolIdentifier(string value) =>
        CIdentifier.IsMatch(value) && !CKeywords.Contains(value);

    private static bool IsLinkerAddressType(CType type)
    {
        if (type.Kind is CTypeKind.Pointer or CTypeKind.Nuint)
            return true;
        var visited = new HashSet<TypeSymbol>();
        while (type.Kind == CTypeKind.Newtype && type.Symbol is { UnderlyingType: { } underlying } symbol && visited.Add(symbol))
            type = underlying;
        return type.Kind == CTypeKind.Nuint;
    }

    private static bool TryParseNonnegativeInt(ImmutableArray<ExpressionSyntax> arguments, out int value)
    {
        if (arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue numeric }] && numeric.FloatingPoint is null && numeric.Integer >= 0 && numeric.Integer <= int.MaxValue)
        {
            value = (int)numeric.Integer;
            return true;
        }
        value = 0;
        return false;
    }

    private static bool IsUnsignedBitViewType(CType type) => type.Kind is CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint or CTypeKind.Ulong ||
        type.Kind == CTypeKind.Enum && type.Symbol?.Fields.SingleOrDefault(field => field.Name == "<underlying>") is { Type.Kind: CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint or CTypeKind.Ulong } ||
        type.Kind == CTypeKind.Newtype && type.Symbol?.UnderlyingType is { } underlying && IsUnsignedBitViewType(underlying);

    private static int FixedUnsignedWidth(CType type) => type.Kind switch
    {
        CTypeKind.Byte => 8,
        CTypeKind.Ushort => 16,
        CTypeKind.Uint => 32,
        CTypeKind.Ulong => 64,
        CTypeKind.Enum when type.Symbol?.Fields.SingleOrDefault(field => field.Name == "<underlying>") is { } underlying => FixedUnsignedWidth(underlying.Type),
        CTypeKind.Newtype when type.Symbol?.UnderlyingType is { } underlying => FixedUnsignedWidth(underlying),
        _ => 0,
    };

    private BigInteger? ParseRegisterAddress(AttributeSyntax? attribute, IReadOnlySet<string> symbolicNames, out string? parameterName)
    {
        parameterName = null;
        if (attribute is null)
            return null;
        if (attribute.Arguments is [LiteralExpressionSyntax { Value: NumericLiteralValue numeric }] && numeric.FloatingPoint is null && numeric.Integer >= 0)
            return numeric.Integer;
        if (attribute.Arguments is [NameExpressionSyntax name] && symbolicNames.Contains(name.Name))
        {
            parameterName = name.Name;
            return null;
        }
        Diagnostics.Add("CT2210", "Register requires one nonnegative compile-time integral address.", attribute.Source, attribute.Span);
        return null;
    }

    private BigInteger? ResolveRegisterAddress(BigInteger? address, string? parameterName, IReadOnlyDictionary<string, CType> substitutions, SyntaxNode syntax)
    {
        if (address is not null || parameterName is null)
            return address;
        if (substitutions.TryGetValue(parameterName, out var argument) && argument.ConstantValue is { } value)
            return value;
        Diagnostics.Add("CT2210", $"Register address parameter '{parameterName}' did not resolve to a compile-time value.", syntax.Source, syntax.Span);
        return null;
    }

    private static int MmioStorageWidth(CType type)
    {
        if (type.Symbol?.IsBitField == true)
            return MmioStorageWidth(type.Symbol.BitFieldBackingType!);
        if (type.Kind == CTypeKind.Newtype && type.Symbol?.UnderlyingType is { } underlying)
            return MmioStorageWidth(underlying);
        if (type.Kind == CTypeKind.Enum && type.Symbol?.Fields.SingleOrDefault(field => field.Name == "<underlying>") is { } enumUnderlying)
            return MmioStorageWidth(enumUnderlying.Type);
        return type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Char => 1,
            CTypeKind.Short or CTypeKind.Ushort => 2,
            CTypeKind.Int or CTypeKind.Uint => 4,
            CTypeKind.Long or CTypeKind.Ulong => 8,
            _ => 0,
        };
    }

    private static bool IsPortableHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 200 &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/');
}
