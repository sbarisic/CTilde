using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed partial class CEmitter : ILoweringServices
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<CType> _functionPointerTypes = [];
    private readonly HashSet<CType> _nativeBufferTypes = [];
    private readonly HashSet<string> _emittedThunks = new(StringComparer.Ordinal);
    private readonly Dictionary<(TypeSymbol DelegateType, MethodSymbol Method, bool VirtualDispatch), string> _delegateThunks = [];
    private readonly Dictionary<(CType Type, MethodSymbol Method), string> _functionPointerTrampolines = [];
    private readonly List<(MethodSymbol Method, SyntaxNode Syntax)> _externUses = [];
    private readonly Dictionary<(PropertySymbol Property, bool Getter), MethodSymbol> _accessorMethods = [];
    private readonly CompilationTarget _target;
    private bool _usesExceptions;
    private bool _usesNativeIntegers;
    private bool _usesDraft08;

    public CEmitter(CompilationModel model, CompilationTarget target)
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
                {
                    RegisterType(parameter.Type);
                    _usesDraft08 |= parameter.PassingKind != ParameterPassingKind.Value;
                }
            }
        }
    }

    public CompilationModel Model { get; }
    public DiagnosticBag Diagnostics { get; }
    public AllocationEffectRegistry AllocationEffects { get; } = new();
    public IEnumerable<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses => _externUses;
    private bool IsEspIdf => _target == CompilationTarget.EspIdf;

    public IEnumerable<string> DynamicGeneratedSymbols =>
        _arrayTypes.SelectMany(type => new[] { NameMangler.Array(type.ElementType!), $"ct_new_{NameMangler.Array(type.ElementType!)}" })
            .Concat(_arrayTypes.Select(type => ArrayDescriptorName(type.ElementType!)))
            .Concat(_stringLiterals.Values.SelectMany(id => new[] { $"ct_sl_{id}", $"ct_slb_{id}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class)
                .SelectMany(type => new[] { DescriptorName(type), $"ct_vtable_{NameMangler.Identifier(type.FullName)}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate)
                .SelectMany(type => new[] { DescriptorName(type), DelegateFactoryName(type), DelegateDropName(type) }))
            .Concat(_delegateThunks.Values)
            .Concat(_functionPointerTrampolines.Values)
            .Concat(Model.UserTypes.SelectMany(type => type.Constructors).Select(ConstructorInitializerName))
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
            .Concat(BoxedTypes.SelectMany(type =>
            {
                var code = NameMangler.TypeCode(type);
                return new[]
                {
                    BoxName(type), BoxDescriptorName(type), BoxFunctionName(type), UnboxFunctionName(type),
                    $"ct_vtable_box_{code}", $"ct_box_to_string_{code}", $"ct_box_equals_{code}",
                    $"ct_box_hash_{code}", $"ct_enum_to_string_{code}",
                };
            }));

    public IEnumerable<CType> BoxedTypes => _boxedTypes.OrderBy(NameMangler.TypeCode, StringComparer.Ordinal);

    public void RegisterExceptions() => _usesExceptions = true;

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

    public string Emit(TypedIrProgram program)
    {
        RegisterDeclaredTypes();
        var definitions = program.Functions.Select(RenderFunction).ToImmutableArray();
        var moduleInitializer = RenderModuleInitializer(program.ModuleInitializers);
        var writer = new CWriter();
        EmitPreamble(writer);
        EmitStringLiterals(writer);
        EmitForwardDeclarations(writer);
        EmitTypeLayouts(writer);
        EmitArrayLayouts(writer);
        EmitBoxLayouts(writer);
        EmitGlobals(writer);
        EmitOwnershipHelpers(writer);
        EmitPrototypes(writer);
        EmitObjectMetadata(writer);
        EmitDelegateSupport(writer);
        EmitFunctionPointerTrampolines(writer);
        writer.WriteLine();
        foreach (var definition in definitions)
        {
            writer.WriteBlock(definition.TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        writer.WriteBlock(moduleInitializer.TrimEnd().Split('\n'));
        writer.WriteLine();
        if (!IsEspIdf)
        {
            EmitKeepSymbols(writer);
            writer.WriteLine();
        }
        EmitMain(writer);
        var output = writer.ToString();
        return IsEspIdf ? MarkUnusedDefinitions(output) : output;
    }

    private string RenderFunction(IrFunction function)
    {
        if (function.Property is null)
            return new CBodyLowerer(this, function.Body, function.Method).LowerDefinition();

        var method = GetAccessorMethod(function.Property, function.IsGetter);
        var name = function.IsGetter ? NameMangler.Getter(function.Property) : NameMangler.Setter(function.Property);
        return new CBodyLowerer(this, function.Body, method, name, function.Property, function.IsGetter).LowerDefinition();
    }

    private string RenderModuleInitializer(ImmutableArray<IrStaticInitializer> initializers)
    {
        var writer = new CWriter();
        writer.WriteLine("static void ct_module_init(void)");
        writer.WriteLine("{");
        var initializerIndex = 0;
        foreach (var initializer in initializers)
        {
            var field = initializer.Field;
            var lowerer = new CBodyLowerer(this, initializer.Body, initializer.Body.Method, temporaryPrefix: $"_mi_{initializerIndex++}");
            var expression = lowerer.LowerExpression(field.Initializer!);
            foreach (var line in expression.Prelude)
                writer.WriteLine("    " + line);
            var value = lowerer.ConvertExpression(expression, field.Type, field.Initializer!);
            if (field.IsConst && !value.IsConstant)
                Model.Diagnostics.Add("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer!.Source, field.Initializer.Span);
            foreach (var line in value.Prelude.Skip(expression.Prelude.Count))
                writer.WriteLine("    " + line);
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"    {CTypeName(field.Type)} ct_static_value_{initializerIndex} = {value.Code};");
                if (value.Ownership != OwnershipKind.Owned)
                    writer.WriteLine("    " + RetainValueStatement(field.Type, $"&ct_static_value_{initializerIndex}"));
                writer.WriteLine($"    {field.CName} = ct_static_value_{initializerIndex};");
            }
            else
                writer.WriteLine($"    {field.CName} = {value.Code};");
        }
        writer.WriteLine("}");
        return writer.ToString();
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
        CTypeKind.Class => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Delegate => $"{NameMangler.Type(type.Symbol!)}*",
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

    private string ParameterTypeName(ParameterSymbol parameter) => parameter.PassingKind switch
    {
        _ when parameter.Type.IsNativeBuffer => $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}*, size_t",
        ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}*",
        ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}*",
        _ => CTypeName(parameter.Type),
    };

    private static IEnumerable<string> ParameterArgumentNames(ParameterSymbol parameter, string name) => parameter.Type.IsNativeBuffer
        ? [$"{name}_data", $"{name}_length"]
        : [name];

    public string CCastType(CType type)
    {
        if (type.Kind != CTypeKind.FunctionPointer)
            return CTypeName(type);
        var signature = type.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*)({FunctionPointerParameters(signature)})";
    }

    private string CFunctionDeclaration(CType returnType, string name, IReadOnlyList<string> parameters)
    {
        var arguments = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
        if (returnType.Kind != CTypeKind.FunctionPointer)
            return $"{CTypeName(returnType)} {name}({arguments})";
        var signature = returnType.FunctionPointer!;
        return $"{CTypeName(signature.ReturnType)} (*{name}({arguments}))({FunctionPointerParameters(signature)})";
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
        if (type.Kind is CTypeKind.Nint or CTypeKind.Nuint)
        {
            _usesNativeIntegers = true;
            _usesDraft08 = true;
        }
        if (type.Kind == CTypeKind.Pointer && type.ElementType == CType.Void)
            _usesDraft08 = true;
        if (type.Kind == CTypeKind.Array)
        {
            _arrayTypes.Add(type);
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.Pointer)
        {
            RegisterType(type.ElementType!);
        }
        else if (type.Kind == CTypeKind.FunctionPointer)
        {
            _functionPointerTypes.Add(type);
            foreach (var parameter in type.FunctionPointer!.ParameterTypes)
                RegisterType(parameter);
            RegisterType(type.FunctionPointer.ReturnType);
        }
        else if (type.IsNativeBuffer)
        {
            _nativeBufferTypes.Add(type);
            _usesNativeIntegers = true;
            _usesDraft08 = true;
            RegisterType(type.ElementType!);
        }
    }

    public void RegisterBox(CType type)
    {
        if (type.Kind is CTypeKind.Void or CTypeKind.Null or CTypeKind.Error or CTypeKind.String or CTypeKind.Class or CTypeKind.Array)
            return;
        _boxedTypes.Add(type);
        RegisterType(type);
    }

    public static string BoxName(CType type) => $"ct_box_{NameMangler.TypeCode(type)}";
    public static string BoxDescriptorName(CType type) => $"ct_desc_box_{NameMangler.TypeCode(type)}";
    public static string BoxFunctionName(CType type) => $"ct_box_value_{NameMangler.TypeCode(type)}";
    public static string UnboxFunctionName(CType type) => $"ct_unbox_value_{NameMangler.TypeCode(type)}";
    public static string ValueRetainName(CType type) => type.IsReference ? "ct_retain_ref_value" : $"ct_retain_value_{NameMangler.TypeCode(type)}";
    public static string ValueDropName(CType type) => type.IsReference ? "ct_drop_ref_value" : $"ct_drop_value_{NameMangler.TypeCode(type)}";

    public string RetainValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{ValueRetainName(type)}((void*)({address}));"
        : string.Empty;

    public string DropValueStatement(CType type, string address) => type.ContainsManagedReferences
        ? $"{ValueDropName(type)}((void*)({address}));"
        : string.Empty;

    public string DescriptorExpression(CType type) => type.Kind switch
    {
        CTypeKind.String => "&ct_desc_string",
        CTypeKind.Class or CTypeKind.Delegate => $"&{DescriptorName(type.Symbol!)}",
        CTypeKind.Array => $"&{ArrayDescriptorName(type.ElementType!)}",
        _ => $"&{BoxDescriptorName(type)}",
    };

    public static string VirtualSlotName(MethodSymbol method)
    {
        var root = method;
        while (root.OverriddenMethod is not null)
            root = root.OverriddenMethod;
        if (root.ContainingType.IsObject)
            return root.Name switch { "ToString" => "ToString", "Equals" => "Equals", "GetHashCode" => "GetHashCode", _ => $"m_{NameMangler.Identifier(root.CName)}" };
        return $"m_{NameMangler.Identifier(root.CName)}";
    }

    public static string VirtualGetterSlotName(PropertySymbol property)
    {
        var root = property;
        while (root.OverriddenProperty is not null)
            root = root.OverriddenProperty;
        return $"g_{NameMangler.Identifier(NameMangler.Getter(root))}";
    }

    public static string VirtualSetterSlotName(PropertySymbol property)
    {
        var root = property;
        while (root.OverriddenProperty is not null)
            root = root.OverriddenProperty;
        return $"s_{NameMangler.Identifier(NameMangler.Setter(root))}";
    }

    public string RegisterString(string value)
    {
        if (!_stringLiterals.TryGetValue(value, out var id))
        {
            id = _stringLiterals.Count;
            _stringLiterals.Add(value, id);
        }
        return $"(&ct_sl_{id})";
    }

    public static string EscapeCString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder();
        foreach (var valueByte in bytes)
        {
            if (valueByte is >= 32 and <= 126 && valueByte is not (byte)'"' and not (byte)'\\')
                builder.Append((char)valueByte);
            else
                builder.Append("\\x").Append(valueByte.ToString("X2", CultureInfo.InvariantCulture)).Append("\"\"");
        }
        return builder.ToString();
    }

    public string SourceArgument(SyntaxNode syntax)
    {
        var path = IsEspIdf ? Path.GetFileName(syntax.Source.FilePath) : syntax.Source.FilePath.Replace('\\', '/');
        return $"\"{EscapeCString(path)}\", {syntax.Source.GetLocation(syntax.Span).Line}";
    }

    private static string FormatIntegralConstant(BigInteger value, CType type) => type.Kind switch
    {
        CTypeKind.Uint => $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})",
        CTypeKind.Ulong => $"UINT64_C({value.ToString(CultureInfo.InvariantCulture)})",
        CTypeKind.Nuint => $"((uintptr_t)UINT64_C({value.ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Nint => $"((intptr_t){(value < 0 ? "-" : string.Empty)}UINT64_C({BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Long when value == long.MinValue => "INT64_MIN",
        CTypeKind.Long when value < 0 => $"(-INT64_C({BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture)}))",
        CTypeKind.Long => $"INT64_C({value.ToString(CultureInfo.InvariantCulture)})",
        _ when value == int.MinValue => "INT32_MIN",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    public static string DescriptorName(TypeSymbol type) => $"ct_desc_{NameMangler.Identifier(type.FullName)}";
    public static string ArrayDescriptorName(CType elementType) => $"ct_desc_{NameMangler.Array(elementType)}";
    public static string ConstructorInitializerName(MethodSymbol constructor) => $"ct_init_{constructor.CName}";
    public static string ObjectDropName(TypeSymbol type) => $"ct_drop_object_{NameMangler.Identifier(type.FullName)}";
    public static string ArrayDropName(CType elementType) => $"ct_drop_array_{NameMangler.TypeCode(elementType)}";
    public static string BoxDropName(CType type) => $"ct_drop_box_{NameMangler.TypeCode(type)}";
    public static string DelegateFactoryName(TypeSymbol type) => $"ct_new_delegate_{NameMangler.Identifier(type.FullName)}";
    public static string DelegateDropName(TypeSymbol type) => $"ct_drop_delegate_{NameMangler.Identifier(type.FullName)}";

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
        var signature = storage + CFunctionDeclaration(returnType, name ?? method.CName, parameters);
        return prototype ? signature + ";" : signature;
    }

    internal void RegisterDeclaredTypes()
    {
        foreach (var type in Model.UserTypes)
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
}
