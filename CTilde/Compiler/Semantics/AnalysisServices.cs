using System.Numerics;

namespace CTilde;

internal sealed class AnalysisServices : ILoweringServices
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _inlineArrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<CType> _functionPointerTypes = [];
    private readonly Dictionary<(TypeSymbol DelegateType, MethodSymbol Method, bool VirtualDispatch), string> _delegateThunks = [];
    private readonly Dictionary<(CType Type, MethodSymbol Method), string> _functionPointerTrampolines = [];
    private readonly List<(MethodSymbol Method, SyntaxNode Syntax)> _externUses = [];
    private readonly Dictionary<(PropertySymbol Property, bool Getter), MethodSymbol> _accessorMethods = [];
    private readonly CompilationTarget _target;
    private readonly CompilationArchitecture _architecture;
    private readonly string? _sourceRoot;

    public AnalysisServices(CompilationModel model, CompilationTarget target, CompilationArchitecture architecture, string? sourceRoot = null)
    {
        Model = model;
        Diagnostics = model.Diagnostics;
        _target = target;
        _architecture = architecture;
        _sourceRoot = sourceRoot;
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
    public bool EmitDebugInformation => false;
    public bool EmitDebugInstrumentation => false;
    public CompilationTarget Target => _target;
    public CompilationArchitecture Architecture => _architecture;

    public IEnumerable<string> DynamicGeneratedSymbols =>
        _arrayTypes.SelectMany(type => new[] { NameMangler.Array(type.ElementType!), $"ct_new_{NameMangler.Array(type.ElementType!)}" })
            .Concat(_arrayTypes.Select(type => CEmitter.ArrayDescriptorName(type.ElementType!)))
            .Concat(_stringLiterals.Values.SelectMany(id => new[] { $"ct_sl_{id}", $"ct_slb_{id}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class)
                .SelectMany(type => new[] { CEmitter.DescriptorName(type), CEmitter.VTableName(type) }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate)
                .SelectMany(type => new[] { CEmitter.DescriptorName(type), CEmitter.DelegateFactoryName(type), CEmitter.DelegateDropName(type) }))
            .Concat(_delegateThunks.Values)
            .Concat(_functionPointerTrampolines.Values)
            .Concat(Model.UserTypes.SelectMany(type => type.Constructors).Select(CEmitter.ConstructorInitializerName))
            .Concat(Model.UserTypes.SelectMany(type => type.Methods)
                .Where(method => method.IsVirtual && !method.ContainingType.IsObject)
                .Select(CEmitter.VirtualMethodThunkName))
            .Concat(Model.UserTypes.SelectMany(type => type.Properties)
                .Where(property => property.IsVirtual)
                .SelectMany(property => new[]
                {
                    CEmitter.VirtualPropertyThunkName(property, true),
                    CEmitter.VirtualPropertyThunkName(property, false),
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
            IsNoRecursion = property.IsNoRecursion,
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
        CTypeKind.Interface => "ct_object*",
        CTypeKind.Opaque => type.Symbol!.NativeTypeName!,
        CTypeKind.EspError => "esp_err_t",
        CTypeKind.Struct or CTypeKind.Enum or CTypeKind.Newtype => NameMangler.Type(type.Symbol!),
        CTypeKind.InlineArray => NameMangler.InlineArray(type),
        CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*",
        CTypeKind.FunctionPointer => $"ct_fp_{NameMangler.TypeCode(type)}",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"ct_{NameMangler.TypeCode(type)}",
        CTypeKind.NativeUtf8String => "ct_native_utf8_string",
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
        _ when parameter.IsSynchronousCallback => SynchronousCallbackDeclaration(parameter.Type.Symbol!, name),
        _ when parameter.Type.IsNativeBuffer => $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}* {name}_data, size_t {name}_length",
        _ when parameter.Type.IsNativeUtf8String => $"const char* {name}",
        ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}* {name}",
        ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}* {name}",
        _ => CDeclaration(parameter.Type, name),
    };

    private string SynchronousCallbackDeclaration(TypeSymbol delegateType, string name)
    {
        var parameters = delegateType.DelegateParameters.Select(parameter => CParameterDeclaration(parameter, string.Empty).Trim()).Append("void*");
        return $"{CTypeName(delegateType.DelegateReturnType!)} (*{name})({string.Join(", ", parameters)})";
    }

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
        CTypeKind.String or CTypeKind.Class or CTypeKind.Interface or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.Null => "NULL",
        CTypeKind.Opaque => $"({CTypeName(type)})0",
        CTypeKind.EspError => "ESP_OK",
        CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer => $"({CTypeName(type)}){{ NULL, (size_t)0 }}",
        CTypeKind.NativeUtf8String => "(ct_native_utf8_string){ NULL, NULL, (size_t)0 }",
        CTypeKind.Struct => $"({CTypeName(type)}){{0}}",
        CTypeKind.InlineArray => $"({CTypeName(type)}){{0}}",
        CTypeKind.Newtype => $"({CTypeName(type)})0",
        _ => "0",
    };

    public void RegisterType(CType type)
    {
        if (type.Kind == CTypeKind.Array)
        {
            _arrayTypes.Add(type);
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.InlineArray)
        {
            _inlineArrayTypes.Add(type);
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
        CTypeKind.Class or CTypeKind.Interface or CTypeKind.Delegate => $"&{CEmitter.DescriptorName(type.Symbol!)}",
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
        var path = _target == CompilationTarget.EspIdf
            ? Path.GetFileName(syntax.Source.FilePath)
            : _sourceRoot is not null && Path.IsPathFullyQualified(syntax.Source.FilePath)
                ? Path.GetRelativePath(_sourceRoot, Path.GetFullPath(syntax.Source.FilePath)).Replace('\\', '/')
                : syntax.Source.FilePath.Replace('\\', '/');
        return $"\"{CEmitter.EscapeCString(path)}\", {syntax.Source.GetLocation(syntax.Span).Line}";
    }

    public string DebugSourceDirective(SyntaxNode syntax) => string.Empty;
    public string DebugGeneratedDirective() => string.Empty;
    public void RegisterDebugExecutable(MethodSymbol method, SyntaxNode syntax) { }
    public void RegisterDebugLocal(MethodSymbol method, LocalSymbol local, int liveStart, int? liveEnd) { }
    public int RegisterDebugSite(MethodSymbol method, SyntaxNode syntax, string kind) => -1;

    public string RegisterDelegateThunk(TypeSymbol delegateType, MethodSymbol method, bool virtualDispatch)
    {
        var key = (delegateType, method, virtualDispatch);
        if (_delegateThunks.TryGetValue(key, out var existing))
            return existing;
        var name = NameMangler.Artifact("ct_h_", $"delegate-thunk:{NameMangler.TypeIdentity(delegateType)}:{NameMangler.MethodIdentity(method)}:{(virtualDispatch ? "virtual" : "direct")}");
        _delegateThunks.Add(key, name);
        return name;
    }

    public string RegisterFunctionPointerTrampoline(CType type, MethodSymbol method)
    {
        var key = (type, method);
        if (_functionPointerTrampolines.TryGetValue(key, out var existing))
            return existing;
        RegisterExceptions();
        var name = NameMangler.Artifact("ct_k_", $"function-pointer-callback:{NameMangler.CanonicalType(type)}:{NameMangler.MethodIdentity(method)}");
        _functionPointerTrampolines.Add(key, name);
        return name;
    }

    public string DirectDeferThunkName(MethodSymbol method, int id) =>
        $"ct_defer_{NameMangler.Identifier(method.CName)}_{id}";

    public string DurableStateTypeName(MethodSymbol method) =>
        $"ct_state_{NameMangler.Identifier(method.CName)}";

    public void RegisterDirectDeferState(MethodSymbol method, IReadOnlyDictionary<string, CType> fields, IReadOnlyList<DirectDeferThunk> thunks)
    {
    }

    public string SynchronousCallbackAdapterName(TypeSymbol delegateType) => NameMangler.Artifact("ct_k_", $"callback-adapter:{NameMangler.TypeIdentity(delegateType)}");

    public string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false)
    {
        var returnType = method.IsConstructor ? method.ContainingType.Type : method.ReturnType;
        var parameters = new List<string>();
        if (!method.IsStatic && !method.IsConstructor)
            parameters.Add($"{InstanceStorageType(method.ContainingType)}* ct_self");
        foreach (var parameter in method.Parameters)
        {
            var parameterName = NameMangler.Identifier(parameter.Name);
            parameters.Add(parameter.Type.IsNativeUtf8String && method.ExternName is null
                ? CDeclaration(parameter.Type, parameterName)
                : CParameterDeclaration(parameter, parameterName));
            if (parameter.IsSynchronousCallback)
                parameters.Add($"void* {parameterName}_context");
        }
        var storage = method.ExternName is not null ? "extern " : "static ";
        var arguments = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
        var declaration = returnType.Kind == CTypeKind.FunctionPointer
            ? $"{CTypeName(returnType.FunctionPointer!.ReturnType)} (*{name ?? method.CName}({arguments}))({FunctionPointerParameters(returnType.FunctionPointer)})"
            : $"{CTypeName(returnType)} {name ?? method.CName}({arguments})";
        var signature = storage + declaration;
        return prototype ? signature + ";" : signature;
    }

    private static string InstanceStorageType(TypeSymbol type) => type.FullName == "Esp.Idf.EspError"
        ? "esp_err_t"
        : NameMangler.Type(type);

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
