using System.Numerics;

namespace CTilde;

internal sealed class AnalysisServices : ILoweringServices
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<CType> _functionPointerTypes = [];
    private readonly Dictionary<(TypeSymbol DelegateType, MethodSymbol Method, bool VirtualDispatch), string> _delegateThunks = [];
    private readonly Dictionary<(CType Type, MethodSymbol Method), string> _functionPointerTrampolines = [];
    private readonly List<(MethodSymbol Method, SyntaxNode Syntax)> _externUses = [];
    private readonly Dictionary<(PropertySymbol Property, bool Getter), MethodSymbol> _accessorMethods = [];
    private readonly CompilationTarget _target;

    public AnalysisServices(CompilationModel model, CompilationTarget target)
    {
        Model = model;
        Diagnostics = model.Diagnostics;
        _target = target;
        foreach (var type in model.Types.Values)
        {
            foreach (var field in type.Fields)
                RegisterType(field.Type);
            foreach (var property in type.Properties)
                RegisterType(property.Type);
            foreach (var method in type.Methods.Concat(type.Constructors))
            {
                RegisterType(method.ReturnType);
                foreach (var parameter in method.Parameters)
                    RegisterType(parameter.Type);
            }
        }
    }

    public CompilationModel Model { get; }
    public DiagnosticBag Diagnostics { get; }
    public AllocationEffectRegistry AllocationEffects { get; } = new();
    public IEnumerable<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses => _externUses;
    public bool UsesExceptions { get; private set; }

    public IEnumerable<string> DynamicGeneratedSymbols =>
        _arrayTypes.SelectMany(type => new[] { NameMangler.Array(type.ElementType!), $"ct_new_{NameMangler.Array(type.ElementType!)}" })
            .Concat(_arrayTypes.Select(type => CEmitter.ArrayDescriptorName(type.ElementType!)))
            .Concat(_stringLiterals.Values.SelectMany(id => new[] { $"ct_sl_{id}", $"ct_slb_{id}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class)
                .SelectMany(type => new[] { CEmitter.DescriptorName(type), $"ct_vtable_{NameMangler.Identifier(type.FullName)}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate)
                .SelectMany(type => new[] { CEmitter.DescriptorName(type), CEmitter.DelegateFactoryName(type), CEmitter.DelegateDropName(type) }))
            .Concat(_delegateThunks.Values)
            .Concat(_functionPointerTrampolines.Values)
            .Concat(Model.UserTypes.SelectMany(type => type.Constructors).Select(CEmitter.ConstructorInitializerName))
            .Concat(Model.UserTypes.SelectMany(type => type.Methods)
                .Where(method => method.IsVirtual && !method.ContainingType.IsObject)
                .Select(method => $"ct_vthunk_{NameMangler.Identifier(method.CName)}"))
            .Concat(Model.UserTypes.SelectMany(type => type.Properties)
                .Where(property => property.IsVirtual)
                .SelectMany(property => new[]
                {
                    $"ct_vthunk_get_{NameMangler.Identifier(property.ContainingType.FullName + "." + property.Name)}",
                    $"ct_vthunk_set_{NameMangler.Identifier(property.ContainingType.FullName + "." + property.Name)}",
                }))
            .Concat(_boxedTypes.OrderBy(NameMangler.TypeCode, StringComparer.Ordinal).SelectMany(type =>
            {
                var code = NameMangler.TypeCode(type);
                return new[]
                {
                    CEmitter.BoxName(type), CEmitter.BoxDescriptorName(type), CEmitter.BoxFunctionName(type), CEmitter.UnboxFunctionName(type),
                    $"ct_vtable_box_{code}", $"ct_box_to_string_{code}", $"ct_box_equals_{code}",
                    $"ct_box_hash_{code}", $"ct_enum_to_string_{code}",
                };
            }));

    public void RegisterExceptions() => UsesExceptions = true;

    public MethodSymbol GetAccessorMethod(PropertySymbol property, bool getter)
    {
        if (_accessorMethods.TryGetValue((property, getter), out var method))
            return method;
        var syntax = getter ? property.Getter! : property.Setter!;
        var parameters = getter ? Array.Empty<ParameterSymbol>() : [new ParameterSymbol { Name = "value", Type = property.Type, Syntax = null }];
        method = new MethodSymbol
        {
            Name = getter ? $"get_{property.Name}" : $"set_{property.Name}",
            ContainingType = property.ContainingType,
            Accessibility = property.Accessibility,
            IsStatic = property.IsStatic,
            Syntax = syntax,
            ReturnType = getter ? property.Type : CType.Void,
            Parameters = [.. parameters],
            Body = syntax.Body,
            IsNoAlloc = property.IsNoAlloc,
            IsUnsafe = property.Syntax is PropertyDeclarationSyntax propertySyntax && propertySyntax.Modifiers.Contains("unsafe", StringComparer.Ordinal),
            IsVirtual = property.IsVirtual,
            IsOverride = property.IsOverride,
            IsSealedOverride = property.IsSealedOverride,
        };
        _accessorMethods.Add((property, getter), method);
        return method;
    }

    public void RegisterExternUse(MethodSymbol method, SyntaxNode syntax)
    {
        if (method.ExternName is not null)
            _externUses.Add((method, syntax));
    }

    public string CTypeName(CType type) => type.Kind switch
    {
        CTypeKind.Void => "void",
        CTypeKind.Bool => "bool",
        CTypeKind.Byte or CTypeKind.Char => "uint8_t",
        CTypeKind.Sbyte => "int8_t",
        CTypeKind.Short => "int16_t",
        CTypeKind.Ushort => "uint16_t",
        CTypeKind.Int => "int32_t",
        CTypeKind.Uint => "uint32_t",
        CTypeKind.Long => "int64_t",
        CTypeKind.Ulong => "uint64_t",
        CTypeKind.Nint => "intptr_t",
        CTypeKind.Nuint => "uintptr_t",
        CTypeKind.Float => "float",
        CTypeKind.String => "ct_string*",
        CTypeKind.Class or CTypeKind.Delegate => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Struct or CTypeKind.Enum => NameMangler.Type(type.Symbol!),
        CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*",
        CTypeKind.FunctionPointer => $"ct_fp_{NameMangler.TypeCode(type)}",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"ct_{NameMangler.TypeCode(type)}",
        CTypeKind.Null => "void*",
        _ => "int32_t",
    };

    public string CDeclaration(CType type, string name)
    {
        if (type.Kind != CTypeKind.FunctionPointer)
            return $"{CTypeName(type)} {name}";
        var signature = type.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*{name})({FunctionPointerParameters(signature)})";
    }

    public string CParameterDeclaration(ParameterSymbol parameter, string name) => parameter.PassingKind switch
    {
        _ when parameter.Type.IsNativeBuffer => $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}* {name}_data, size_t {name}_length",
        ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}* {name}",
        ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}* {name}",
        _ => CDeclaration(parameter.Type, name),
    };

    public string CCastType(CType type)
    {
        if (type.Kind != CTypeKind.FunctionPointer)
            return CTypeName(type);
        var signature = type.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*)({FunctionPointerParameters(signature)})";
    }

    public string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.Bool => "false",
        CTypeKind.Float => "0.0f",
        CTypeKind.String or CTypeKind.Class or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.Null => "NULL",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"({CTypeName(type)}){{ NULL, (size_t)0 }}",
        CTypeKind.Struct => $"({CTypeName(type)}){{0}}",
        _ => "0",
    };

    public void RegisterType(CType type)
    {
        if (type.Kind == CTypeKind.Array)
        {
            _arrayTypes.Add(type);
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.Pointer)
            RegisterType(type.ElementType!);
        else if (type.Kind == CTypeKind.FunctionPointer)
        {
            _functionPointerTypes.Add(type);
            foreach (var parameter in type.FunctionPointer!.ParameterTypes)
                RegisterType(parameter);
            RegisterType(type.FunctionPointer.ReturnType);
        }
        else if (type.IsNativeBuffer)
            RegisterType(type.ElementType!);
    }

    public void RegisterBox(CType type)
    {
        if (type.Kind is CTypeKind.Void or CTypeKind.Null or CTypeKind.Error or CTypeKind.String or CTypeKind.Class or CTypeKind.Array)
            return;
        _boxedTypes.Add(type);
        RegisterType(type);
    }

    public string RetainValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{CEmitter.ValueRetainName(type)}((void*)({address}));"
        : string.Empty;

    public string DropValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{CEmitter.ValueDropName(type)}((void*)({address}));"
        : string.Empty;

    public string DescriptorExpression(CType type) => type.Kind switch
    {
        CTypeKind.String => "&ct_desc_string",
        CTypeKind.Class or CTypeKind.Delegate => $"&{CEmitter.DescriptorName(type.Symbol!)}",
        CTypeKind.Array => $"&{CEmitter.ArrayDescriptorName(type.ElementType!)}",
        _ => $"&{CEmitter.BoxDescriptorName(type)}",
    };

    public string RegisterString(string value)
    {
        if (!_stringLiterals.TryGetValue(value, out var id))
        {
            id = _stringLiterals.Count;
            _stringLiterals.Add(value, id);
        }
        return $"(&ct_sl_{id})";
    }

    public string SourceArgument(SyntaxNode syntax)
    {
        var path = _target == CompilationTarget.EspIdf ? Path.GetFileName(syntax.Source.FilePath) : syntax.Source.FilePath.Replace('\\', '/');
        return $"\"{CEmitter.EscapeCString(path)}\", {syntax.Source.GetLocation(syntax.Span).Line}";
    }

    public string RegisterDelegateThunk(TypeSymbol delegateType, MethodSymbol method, bool virtualDispatch)
    {
        var key = (delegateType, method, virtualDispatch);
        if (_delegateThunks.TryGetValue(key, out var existing))
            return existing;
        var name = $"ct_delegate_thunk_{NameMangler.Identifier(delegateType.FullName)}_{NameMangler.Identifier(method.CName)}_{(virtualDispatch ? "virtual" : "direct")}";
        _delegateThunks.Add(key, name);
        return name;
    }

    public string RegisterFunctionPointerTrampoline(CType type, MethodSymbol method)
    {
        var key = (type, method);
        if (_functionPointerTrampolines.TryGetValue(key, out var existing))
            return existing;
        RegisterExceptions();
        var name = $"ct_callback_{NameMangler.Identifier(method.CName)}_{NameMangler.TypeCode(type)}";
        _functionPointerTrampolines.Add(key, name);
        return name;
    }

    public string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false)
    {
        var returnType = method.IsConstructor ? method.ContainingType.Type : method.ReturnType;
        var parameters = new List<string>();
        if (!method.IsStatic && !method.IsConstructor)
            parameters.Add($"{NameMangler.Type(method.ContainingType)}* ct_self");
        foreach (var parameter in method.Parameters)
            parameters.Add(CParameterDeclaration(parameter, NameMangler.Identifier(parameter.Name)));
        var storage = method.ExternName is not null ? "extern " : "static ";
        var arguments = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
        var declaration = returnType.Kind == CTypeKind.FunctionPointer
            ? $"{CTypeName(returnType.FunctionPointer!.ReturnType)} (*{name ?? method.CName}({arguments}))({FunctionPointerParameters(returnType.FunctionPointer)})"
            : $"{CTypeName(returnType)} {name ?? method.CName}({arguments})";
        var signature = storage + declaration;
        return prototype ? signature + ";" : signature;
    }

    private string FunctionPointerParameters(FunctionPointerSignature signature) => signature.ParameterTypes.Length == 0
        ? "void"
        : string.Join(", ", signature.ParameterTypes.Select((type, index) => type.IsNativeBuffer
            ? $"{(type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(type.ElementType!)}*, size_t"
            : signature.PassingKinds[index] switch
            {
                ParameterPassingKind.In => $"const {CTypeName(type)}*",
                ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(type)}*",
                _ => CTypeName(type),
            }));
}
