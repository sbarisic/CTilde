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

    public CompilationModel(ImmutableArray<SyntaxTree> syntaxTrees, ImmutableArray<SyntaxTree> userSyntaxTrees, DiagnosticBag diagnostics, CompilationTarget target)
    {
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
        ValidateEntryPoint();
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public ImmutableArray<SyntaxTree> UserSyntaxTrees { get; }
    public DiagnosticBag Diagnostics { get; }
    public Dictionary<string, TypeSymbol> Types { get; }
    public DocumentationIndex Documentation { get; }
    public IEnumerable<TypeSymbol> UserTypes => Types.Values.Where(type => type.Syntax is not null && type.Kind != DeclaredTypeKind.TypeParameter && !type.IsGenericDefinition && !type.IsOpenConstructed).Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal);
    public MethodSymbol? EntryPoint { get; private set; }

    public CType ResolveType(TypeSyntax syntax, SyntaxTree tree, bool report = true)
    {
        if (syntax.TypeArguments.IsDefaultOrEmpty && _activeTypeParameters.TryGetValue(syntax.Name, out var parameterType))
            return ApplyTypeShape(parameterType, syntax, report);
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
                var arguments = syntax.TypeArguments.Select(argument => ResolveType(argument, tree, report)).ToImmutableArray();
                var definitions = ResolveNamedTypeCandidates(syntax.Name, tree, syntax.TypeArguments.Length).ToArray();
                if (definitions.Length != 1)
                {
                    if (report)
                        Diagnostics.Add("CT2176", definitions.Length == 0
                            ? $"Generic type '{syntax.Name}' with {syntax.TypeArguments.Length} type argument(s) could not be found."
                            : $"Generic type '{syntax.Name}' is ambiguous.", syntax.Source, syntax.Span);
                    return CType.Error;
                }
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
        return baseType;
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
            if (!constraints.TryGetValue(parameters[index].Name, out var constraint))
                continue;
            var argument = arguments[index];
            var valid = (!constraint.RequiresClass || argument.IsReference) &&
                (!constraint.RequiresStruct || argument.IsValueType) &&
                (!constraint.RequiresUnmanaged || IsCompleteUnmanagedType(argument)) &&
                (constraint.BaseType is null || TypeFacts.CanImplicitlyConvert(argument, constraint.BaseType)) &&
                (constraint.Interfaces.IsDefaultOrEmpty || constraint.Interfaces.All(contract => TypeFacts.CanImplicitlyConvert(argument, contract))) &&
                (!constraint.RequiresConstructor || HasPublicParameterlessConstructor(argument));
            if (!valid)
                Diagnostics.Add("CT1271", $"Type argument '{argument.DisplayName}' does not satisfy constraints for '{parameters[index].Name}'.", syntax.Source, syntax.Span);
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
            IsConstructor = method.IsConstructor,
            IsEntryPoint = method.IsEntryPoint,
            IsNoAlloc = method.IsNoAlloc,
            IsUnsafe = method.IsUnsafe,
            ReturnsBorrowed = method.ReturnsBorrowed,
            ReturnsOwned = method.ReturnsOwned,
            ReturnsNullable = method.ReturnsNullable,
            ExternName = method.ExternName,
            ExportName = method.ExportName,
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
        if (definition.BaseType is not null)
            type.BaseType = SubstituteType(definition.BaseType.Type, substitutions).Symbol;
        foreach (var contract in definition.Interfaces)
        {
            var substituted = SubstituteType(contract.Type, substitutions).Symbol;
            if (substituted is not null && !type.Interfaces.Contains(substituted))
                type.Interfaces.Add(substituted);
        }
        foreach (var field in definition.Fields)
            type.Fields.Add(new FieldSymbol
            {
                Name = field.Name,
                ContainingType = type,
                Accessibility = field.Accessibility,
                IsStatic = field.IsStatic,
                Syntax = field.Syntax,
                Type = SubstituteType(field.Type, substitutions),
                IsReadonly = field.IsReadonly,
                IsConst = field.IsConst,
                IsVolatile = field.IsVolatile,
                Initializer = field.Initializer,
            });
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
                IsNoAlloc = property.IsNoAlloc,
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
                if (implementation is not null && !implementation.ImplementedInterfaceMethods.Contains(required))
                    implementation.ImplementedInterfaceMethods.Add(required);
            }
            foreach (var required in contract.Properties)
            {
                var implementation = type.BaseTypesAndSelf().SelectMany(candidate => candidate.Properties)
                    .FirstOrDefault(candidate => ResolvesPropertyContract(candidate, required));
                if (implementation is not null && !implementation.ImplementedInterfaceProperties.Contains(required))
                    implementation.ImplementedInterfaceProperties.Add(required);
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
            IsConstructor = method.IsConstructor,
            IsEntryPoint = method.IsEntryPoint,
            IsNoAlloc = method.IsNoAlloc,
            IsUnsafe = method.IsUnsafe,
            ReturnsBorrowed = method.ReturnsBorrowed,
            ReturnsOwned = method.ReturnsOwned,
            ReturnsNullable = method.ReturnsNullable,
            ExternName = method.ExternName,
            ExportName = method.ExportName,
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
        if (type.Symbol?.GenericDefinition is { } definition && !type.Symbol.TypeArguments.IsDefaultOrEmpty)
        {
            var arguments = type.Symbol.TypeArguments.Select(argument => SubstituteType(argument, substitutions)).ToImmutableArray();
            var source = definition.Syntax?.Source ?? type.Symbol.Syntax!.Source;
            var tree = SyntaxTrees.First(candidate => candidate.Text == source || candidate.Text.FilePath == source.FilePath);
            return ConstructGenericType(definition, arguments, tree, definition.Syntax!).Type;
        }
        return type;
    }

    private static bool IsUnmanagedFunctionPointerElement(CType type, bool allowVoid) =>
        type.Kind == CTypeKind.Void ? allowVoid :
        type.Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
            CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer;

    private static bool IsCompleteUnmanagedType(CType type) => type.Kind switch
    {
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or
        CTypeKind.Float or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.FunctionPointer => true,
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
                    TypeDeclarationKind.Struct => DeclaredTypeKind.Struct,
                    TypeDeclarationKind.Interface => DeclaredTypeKind.Interface,
                    TypeDeclarationKind.Enum => DeclaredTypeKind.Enum,
                    TypeDeclarationKind.Delegate => DeclaredTypeKind.Delegate,
                    TypeDeclarationKind.Opaque => DeclaredTypeKind.Opaque,
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
                foreach (var invalidModifier in declaration.Modifiers.Where(modifier => modifier is "const" or "unsafe" or "virtual" or "override" or "volatile" || modifier == "readonly" && declaration.Kind != TypeDeclarationKind.Struct))
                    Diagnostics.Add("CT1219", $"Modifier '{invalidModifier}' is not valid on a type declaration.", declaration.Source, declaration.Span);
                ValidateAttributes(declaration.Attributes, declaration, declaration.Kind == TypeDeclarationKind.Opaque ? ["NativeType"] : []);
                string? nativeTypeName = null;
                string? nativeHeader = null;
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
                }).ToImmutableArray();
                Types.Add(typeKey, new TypeSymbol
                {
                    Namespace = namespaceName,
                    Name = declaration.Name,
                    Kind = kind,
                    Syntax = declaration,
                    Accessibility = typeAccessibility,
                    NativeTypeName = nativeTypeName,
                    NativeHeader = nativeHeader,
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
                var fullName = string.IsNullOrEmpty(_namespaces[tree]) ? declaration.Name : $"{_namespaces[tree]}.{declaration.Name}";
                if (!Types.TryGetValue(DeclarationKey(fullName, declaration), out var type) || type.Kind is not (DeclaredTypeKind.Class or DeclaredTypeKind.Struct or DeclaredTypeKind.Interface))
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
                    ValidateAttributes(field.Attributes, field, []);
                    if (type.Kind == DeclaredTypeKind.Interface)
                        Diagnostics.Add("CT1273", "An interface can contain only instance method and property contracts.", field.Source, field.Span);
                    var isVolatile = field.Modifiers.Contains("volatile", StringComparer.Ordinal);
                    var symbol = new FieldSymbol
                    {
                        Name = field.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = field,
                        Type = ResolveType(field.Type, tree),
                        IsReadonly = field.Modifiers.Contains("readonly", StringComparer.Ordinal),
                        IsConst = field.Modifiers.Contains("const", StringComparer.Ordinal),
                        IsVolatile = isVolatile,
                        Initializer = field.Initializer,
                    };
                    if (symbol.Type.IsNativeBuffer)
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
                    AddUnique(type, symbol);
                    break;
                }
            case PropertyDeclarationSyntax property:
                {
                    ValidateAllowedModifiers(property.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed", "abstract"], property);
                    ValidateAttributes(property.Attributes, property, ["NoAlloc"]);
                    var noAlloc = FindAttribute(property.Attributes, "NoAlloc");
                    if (noAlloc is not null && noAlloc.Arguments.Length != 0)
                        Diagnostics.Add("CT1233", "NoAlloc does not accept arguments.", noAlloc.Source, noAlloc.Span);
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
                    if (property.Getter is not null)
                        ValidateAllowedModifiers(property.Getter.Modifiers, ["public", "internal", "protected", "private"], property.Getter);
                    if (property.Setter is not null)
                        ValidateAllowedModifiers(property.Setter.Modifiers, ["public", "internal", "protected", "private"], property.Setter);
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
                        IsNoAlloc = noAlloc is not null,
                    };
                    if (AccessRank(symbol.GetterAccessibility) > AccessRank(accessibility) || AccessRank(symbol.SetterAccessibility) > AccessRank(accessibility))
                        Diagnostics.Add("CT1222", "An accessor cannot be more accessible than its property.", property.Source, property.Span);
                    if (symbol.IsVirtual && accessibility == Accessibility.Private)
                        Diagnostics.Add("CT1228", "A virtual or override property cannot be private.", property.Source, property.Span);
                    AddUnique(type, symbol);
                    break;
                }
            case ConstructorDeclarationSyntax constructor:
                {
                    ValidateAllowedModifiers(constructor.Modifiers, ["public", "internal", "protected", "private", "unsafe"], constructor);
                    ValidateAttributes(constructor.Attributes, constructor, []);
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
                    ValidateAllowedModifiers(method.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed", "abstract"], method);
                    ValidateAttributes(method.Attributes, method, ["EntryPoint", "Extern", "Export", "NoAlloc", "ReturnsBorrowed", "ReturnsOwned", "ReturnsNullable"]);
                    var entry = FindAttribute(method.Attributes, "EntryPoint");
                    var external = FindAttribute(method.Attributes, "Extern");
                    var export = FindAttribute(method.Attributes, "Export");
                    var noAlloc = FindAttribute(method.Attributes, "NoAlloc");
                    var returnsBorrowed = FindAttribute(method.Attributes, "ReturnsBorrowed");
                    var returnsOwned = FindAttribute(method.Attributes, "ReturnsOwned");
                    var returnsNullable = FindAttribute(method.Attributes, "ReturnsNullable");
                    var previousTypeParameters = _activeTypeParameters;
                    var methodTypeParameters = method.TypeParameters.Select(parameter => new TypeSymbol
                    {
                        Namespace = $"{type.FullName}.{method.Name}",
                        Name = parameter.Name,
                        Kind = DeclaredTypeKind.TypeParameter,
                        Syntax = null,
                        Accessibility = Accessibility.Private,
                    }).ToImmutableArray();
                    if (methodTypeParameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != methodTypeParameters.Length)
                        Diagnostics.Add("CT1271", "Generic method type-parameter names must be unique.", method.Source, method.Span);
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
                    if (isAbstractMethod && method.Body is not null)
                        Diagnostics.Add("CT1270", "An abstract or interface method cannot have a body.", method.Source, method.Span);
                    if (entry is not null && entry.Arguments.Length != 0)
                        Diagnostics.Add("CT1223", "EntryPoint does not accept arguments.", entry.Source, entry.Span);
                    if (noAlloc is not null && noAlloc.Arguments.Length != 0)
                        Diagnostics.Add("CT1233", "NoAlloc does not accept arguments.", noAlloc.Source, noAlloc.Span);
                    string? externalName = null;
                    string? exportName = null;
                    if (external is not null)
                    {
                        if (external.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsPortableExternalIdentifier(value))
                            externalName = value;
                        else
                            Diagnostics.Add("CT1204", "Extern requires one string containing a portable C identifier.", external.Source, external.Span);
                        if (!isStatic || method.Body is not null)
                            Diagnostics.Add("CT1205", "An Extern method must be static and bodyless.", method.Source, method.Span);
                    }
                    else if (method.Body is null && !isAbstractMethod)
                        Diagnostics.Add("CT1206", "A bodyless method requires Extern or abstract.", method.Source, method.Span);
                    if (export is not null)
                    {
                        if (export.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsPortableExternalIdentifier(value))
                            exportName = value;
                        else
                            Diagnostics.Add("CT1243", "Export requires one string containing a portable C identifier.", export.Source, export.Span);
                        if (external is not null || entry is not null || !isStatic || method.Body is null || accessibility != Accessibility.Public)
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
                        IsEntryPoint = entry is not null,
                        IsNoAlloc = noAlloc is not null,
                        IsUnsafe = method.Modifiers.Contains("unsafe", StringComparer.Ordinal),
                        ReturnsBorrowed = returnsBorrowed is not null && returnsBorrowed.Arguments.Length == 0 && external is not null && (returnType.IsReference || returnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer),
                        ReturnsOwned = returnsOwned is not null,
                        ReturnsNullable = returnsNullable is not null,
                        ExternName = externalName,
                        ExportName = exportName,
                        IsTrustedExtern = !UserSyntaxTrees.Contains(tree),
                        IsVirtual = isAbstractMethod || method.Modifiers.Contains("virtual", StringComparer.Ordinal) || method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsAbstract = isAbstractMethod,
                        TypeParameters = methodTypeParameters,
                        TypeParameterConstraints = methodConstraints,
                        IsOverride = method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsSealedOverride = method.Modifiers.Contains("sealed", StringComparer.Ordinal),
                    };
                    if (entry is not null && (!isStatic || symbol.ReturnType != CType.Void || symbol.Parameters.Length != 0 || method.Body is null))
                        Diagnostics.Add("CT1207", "EntryPoint must mark a body-bearing static void method with no parameters.", entry.Source, entry.Span);
                    if (symbol.IsVirtual && accessibility == Accessibility.Private)
                        Diagnostics.Add("CT1228", "A virtual or override method cannot be private.", method.Source, method.Span);
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
        });
    }

    private void ValidateEntryPoint()
    {
        var entries = UserTypes.SelectMany(type => type.Methods).Where(method => method.IsEntryPoint).ToArray();
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
                        property.IsNoAlloc |= candidate.IsNoAlloc;
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
                        method.IsNoAlloc |= candidate.IsNoAlloc;
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
                        implementation.IsNoAlloc |= required.IsNoAlloc;
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
                        implementation.IsNoAlloc |= required.IsNoAlloc;
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
                Diagnostics.Add("CT1213", $"Unknown or invalid attribute '{attribute.Name}' on this declaration.", attribute.Source, attribute.Span);
        }
        foreach (var duplicate in attributes.GroupBy(attribute => attribute.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add("CT1214", $"Attribute '{duplicate.Key}' cannot be applied more than once.", syntax.Source, syntax.Span);
    }

    private static AttributeSyntax? FindAttribute(ImmutableArray<AttributeSyntax> attributes, string name) => attributes.FirstOrDefault(attribute => attribute.Name == name);

    private static bool IsPortableExternalIdentifier(string value) =>
        CIdentifier.IsMatch(value) && !value.StartsWith('_') && !CKeywords.Contains(value);

    private static bool IsPortableHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 200 &&
        !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/');
}
