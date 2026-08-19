using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed class CEmitter
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<CType> _functionPointerTypes = [];
    private readonly HashSet<string> _emittedThunks = new(StringComparer.Ordinal);
    private readonly Dictionary<(TypeSymbol DelegateType, MethodSymbol Method, bool VirtualDispatch), string> _delegateThunks = [];
    private readonly Dictionary<(CType Type, MethodSymbol Method), string> _functionPointerTrampolines = [];
    private readonly List<(MethodSymbol Method, SyntaxNode Syntax)> _externUses = [];
    private readonly Dictionary<(PropertySymbol Property, bool Getter), MethodSymbol> _accessorMethods = [];
    private readonly CompilationTarget _target;
    private bool _usesExceptions;

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
                    RegisterType(parameter.Type);
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
        foreach (var definition in program.Functions)
        {
            writer.WriteBlock(definition.Render().TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        writer.WriteBlock(string.Join('\n', program.ModuleInitializer.Select(instruction => instruction.Text)).TrimEnd().Split('\n'));
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
        CTypeKind.Float => "float",
        CTypeKind.String => "ct_string*",
        CTypeKind.Class => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Delegate => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Struct or CTypeKind.Enum => NameMangler.Type(type.Symbol!),
        CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*",
        CTypeKind.FunctionPointer => $"ct_fp_{NameMangler.TypeCode(type)}",
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
        : string.Join(", ", signature.ParameterTypes.Select(CTypeName));

    public string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.Bool => "false",
        CTypeKind.Float => "0.0f",
        CTypeKind.String or CTypeKind.Class or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.Null => "NULL",
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
            parameters.Add(CDeclaration(parameter.Type, NameMangler.Identifier(parameter.Name)));
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

    private void EmitPreamble(CWriter writer)
    {
        writer.WriteLine(IsEspIdf
            ? "/* Generated by C~ draft 0.7 for ESP-IDF GNU C23. Do not edit. */"
            : "/* Generated by C~ draft 0.7 for GNU C23. Do not edit. */");
        writer.WriteLine("#include <stdbool.h>");
        writer.WriteLine("#include <stddef.h>");
        writer.WriteLine("#include <stdint.h>");
        writer.WriteLine("#include <inttypes.h>");
        writer.WriteLine("#include <stdio.h>");
        writer.WriteLine("#include <stdlib.h>");
        writer.WriteLine("#include <string.h>");
        writer.WriteLine("#include <limits.h>");
        writer.WriteLine("#include <float.h>");
        writer.WriteLine("#include <math.h>");
        if (_usesExceptions)
            writer.WriteLine("#include <setjmp.h>");
        if (IsEspIdf)
            writer.WriteLine("#include \"ctilde_esp_shim.h\"");
        writer.WriteLine();
        writer.WriteLine("static_assert(CHAR_BIT == 8, \"C~ requires 8-bit bytes\");");
        writer.WriteLine("static_assert(sizeof(int32_t) == 4 && sizeof(uint32_t) == 4, \"C~ requires exact 32-bit integers\");");
        writer.WriteLine("static_assert(sizeof(int64_t) == 8 && sizeof(uint64_t) == 8, \"C~ requires exact 64-bit integers\");");
        writer.WriteLine("static_assert(sizeof(float) == 4 && FLT_RADIX == 2 && FLT_MANT_DIG == 24, \"C~ requires IEEE-754 binary32 float\");");
        writer.WriteLine("static_assert(INT32_MIN == (-2147483647 - 1), \"C~ requires two's-complement int32_t\");");
        if (IsEspIdf)
            writer.WriteLine("static_assert(sizeof(void*) == 4, \"C~ ESP-IDF requires 32-bit pointers\");");
        writer.WriteLine();
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("#define CT_NORETURN __declspec(noreturn)");
        writer.WriteLine("#elif defined(__GNUC__) || defined(__clang__)");
        writer.WriteLine("#define CT_NORETURN __attribute__((noreturn))");
        writer.WriteLine("#else");
        writer.WriteLine("#define CT_NORETURN _Noreturn");
        writer.WriteLine("#endif");
        writer.WriteLine("#if defined(__GNUC__) || defined(__clang__)");
        writer.WriteLine("#define CT_UNUSED __attribute__((unused))");
        writer.WriteLine("#else");
        writer.WriteLine("#define CT_UNUSED");
        writer.WriteLine("#endif");
        writer.WriteLine();
        writer.WriteLine("typedef struct ct_vtable ct_vtable;");
        writer.WriteLine("typedef struct ct_type_descriptor ct_type_descriptor;");
        writer.WriteLine("typedef struct ct_object { const ct_type_descriptor* Type; uint32_t IdentityHash; uint32_t RefCount; struct ct_object* ReleaseNext; } ct_object;");
        writer.WriteLine("typedef void (*ct_drop_value_fn)(void*);");
        writer.WriteLine("typedef struct ct_cleanup_record { struct ct_cleanup_record* Previous; void* Value; ct_drop_value_fn Drop; bool Active; } ct_cleanup_record;");
        writer.WriteLine("static ct_cleanup_record* ct_cleanup_top = NULL;");
        if (_usesExceptions)
        {
            writer.WriteLine("typedef struct ct_exception_frame { jmp_buf* Target; struct ct_exception_frame* Previous; ct_cleanup_record* CleanupBoundary; } ct_exception_frame;");
            writer.WriteLine("static ct_exception_frame* ct_exception_top = NULL;");
            writer.WriteLine("static ct_object* ct_current_exception = NULL;");
            writer.WriteLine("CT_NORETURN static void ct_unhandled_exception(ct_object* exception);");
        }
        writer.WriteLine("struct ct_type_descriptor { const char* Name; const ct_type_descriptor* Base; const ct_vtable* VTable; uint32_t TypeId; size_t Size; size_t Alignment; bool IsValue; void (*Drop)(ct_object*); };");
        writer.WriteLine("static ct_type_descriptor ct_desc_string;");
        writer.WriteLine("typedef struct ct_string { ct_object Object; int32_t Length; const uint8_t* Data; } ct_string;");
        writer.WriteLine("static const uint8_t ct_empty_bytes[1] = { 0 };");
        writer.WriteLine("static ct_string ct_empty_string = { { &ct_desc_string, 0, UINT32_MAX, NULL }, 0, ct_empty_bytes };");
        writer.WriteLine();
        writer.WriteLine("CT_NORETURN static void ct_fail(const char* code, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    (void)fprintf(stderr, \"C~ runtime error %s at %s:%d\\n\", code, file, line);");
        writer.WriteLine(IsEspIdf ? "    abort();" : "    exit(EXIT_FAILURE);");
        writer.WriteLine("}");
        writer.WriteLine("static void* ct_require_nonnull(void* value, const char* file, int line) { if (value == NULL) ct_fail(\"CTN0001\", file, line); return value; }");
        EmitPlatformAllocation(writer);
        writer.WriteLine("static ct_object* ct_release_head = NULL;");
        writer.WriteLine("static bool ct_release_draining = false;");
        writer.WriteLine("void ct_retain(ct_object* object)");
        writer.WriteLine("{");
        writer.WriteLine("    if (object == NULL || object->RefCount == UINT32_MAX) return;");
        writer.WriteLine("    if (object->RefCount == 0u || object->RefCount == UINT32_MAX - 1u) ct_fail(\"CTM0002\", \"<runtime>\", 0);");
        writer.WriteLine("    object->RefCount++;");
        writer.WriteLine("}");
        writer.WriteLine("void ct_release(ct_object* object)");
        writer.WriteLine("{");
        writer.WriteLine("    if (object == NULL || object->RefCount == UINT32_MAX) return;");
        writer.WriteLine("    if (object->RefCount == 0u) ct_fail(\"CTM0003\", \"<runtime>\", 0);");
        writer.WriteLine("    object->RefCount--;");
        writer.WriteLine("    if (object->RefCount != 0u) return;");
        writer.WriteLine("    object->ReleaseNext = ct_release_head;");
        writer.WriteLine("    ct_release_head = object;");
        writer.WriteLine("    if (ct_release_draining) return;");
        writer.WriteLine("    ct_release_draining = true;");
        writer.WriteLine("    while (ct_release_head != NULL)");
        writer.WriteLine("    {");
        writer.WriteLine("        ct_object* current = ct_release_head;");
        writer.WriteLine("        ct_release_head = current->ReleaseNext;");
        writer.WriteLine("        current->ReleaseNext = NULL;");
        writer.WriteLine("        if (current->Type->Drop != NULL) current->Type->Drop(current);");
        writer.WriteLine("#if defined(CT_MEMORY_DIAGNOSTICS)");
        writer.WriteLine("        ct_memory_live_objects--;");
        writer.WriteLine("#endif");
        writer.WriteLine("        ct_dealloc(current);");
        writer.WriteLine("    }");
        writer.WriteLine("    ct_release_draining = false;");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_retain_ref_value(void* value) { ct_retain(*(ct_object**)value); }");
        writer.WriteLine("static void ct_drop_ref_value(void* value) { ct_object* object = *(ct_object**)value; *(ct_object**)value = NULL; ct_release(object); }");
        writer.WriteLine("static void ct_cleanup_push(ct_cleanup_record* record, void* value, ct_drop_value_fn drop) { record->Previous = ct_cleanup_top; record->Value = value; record->Drop = drop; record->Active = true; ct_cleanup_top = record; }");
        writer.WriteLine("static void ct_cleanup_unwind_to(ct_cleanup_record* boundary) { while (ct_cleanup_top != boundary) { ct_cleanup_record* record = ct_cleanup_top; if (record == NULL) ct_fail(\"CTM0003\", \"<runtime>\", 0); ct_cleanup_top = record->Previous; if (record->Active) { record->Active = false; record->Drop(record->Value); } } }");
        writer.WriteLine("static void ct_cleanup_disarm(ct_cleanup_record* record) { record->Active = false; }");
        if (_usesExceptions)
        {
            writer.WriteLine("CT_NORETURN static void ct_throw(ct_object* exception, const char* file, int line)");
            writer.WriteLine("{");
            writer.WriteLine("    if (exception == NULL) ct_fail(\"CTE0002\", file, line);");
            writer.WriteLine("    if (ct_exception_top == NULL) ct_unhandled_exception(exception);");
            writer.WriteLine("    ct_retain(exception);");
            writer.WriteLine("    ct_release(ct_current_exception);");
            writer.WriteLine("    ct_current_exception = exception;");
            writer.WriteLine("    ct_exception_frame* target = ct_exception_top;");
            writer.WriteLine("    ct_cleanup_unwind_to(target->CleanupBoundary);");
            writer.WriteLine("    longjmp(*target->Target, 1);");
            writer.WriteLine("}");
        }
        writer.WriteLine("static uint32_t ct_next_identity = 1u;");
        writer.WriteLine("static void ct_init_object(void* value, const ct_type_descriptor* type)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_object* object = (ct_object*)value;");
        writer.WriteLine("    object->Type = type; object->IdentityHash = ct_next_identity++; object->RefCount = 1u; object->ReleaseNext = NULL;");
        writer.WriteLine("    if (ct_next_identity == 0u) ct_next_identity = 1u;");
        writer.WriteLine("#if defined(CT_MEMORY_DIAGNOSTICS)");
        writer.WriteLine("    ct_memory_live_objects++;");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_drop_string(ct_object* object) { ct_string* value = (ct_string*)(void*)object; ct_dealloc((void*)value->Data); value->Data = NULL; value->Length = 0; }");
        writer.WriteLine("static void* ct_alloc_array(int32_t length, size_t element_size, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    if (length < 0) ct_fail(\"CTA0001\", file, line);");
        writer.WriteLine("    if ((size_t)length > SIZE_MAX / element_size) ct_fail(\"CTA0002\", file, line);");
        writer.WriteLine("    if (length == 0) return NULL;");
        writer.WriteLine("    return ct_alloc((size_t)length * element_size, file, line);");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_bounds(int32_t index, int32_t length, const char* file, int line) { if (index < 0 || index >= length) ct_fail(\"CTA0003\", file, line); }");
        writer.WriteLine("static int32_t ct_i32_bits(uint32_t value) { int32_t result; (void)memcpy(&result, &value, sizeof(result)); return result; }");
        writer.WriteLine("static int32_t ct_i32_add(int32_t a, int32_t b) { return ct_i32_bits((uint32_t)a + (uint32_t)b); }");
        writer.WriteLine("static int32_t ct_i32_sub(int32_t a, int32_t b) { return ct_i32_bits((uint32_t)a - (uint32_t)b); }");
        writer.WriteLine("static int32_t ct_i32_mul(int32_t a, int32_t b) { return ct_i32_bits((uint32_t)a * (uint32_t)b); }");
        writer.WriteLine("static int32_t ct_i32_neg(int32_t value) { return ct_i32_bits(0u - (uint32_t)value); }");
        writer.WriteLine("static int32_t ct_i32_div(int32_t a, int32_t b, const char* file, int line) { if (b == 0) ct_fail(\"CTI0001\", file, line); if (a == INT32_MIN && b == -1) return INT32_MIN; return a / b; }");
        writer.WriteLine("static int32_t ct_i32_mod(int32_t a, int32_t b, const char* file, int line) { if (b == 0) ct_fail(\"CTI0001\", file, line); if (a == INT32_MIN && b == -1) return 0; return a % b; }");
        writer.WriteLine("static uint32_t ct_u32_div(uint32_t a, uint32_t b, const char* file, int line) { if (b == 0u) ct_fail(\"CTI0001\", file, line); return a / b; }");
        writer.WriteLine("static uint32_t ct_u32_mod(uint32_t a, uint32_t b, const char* file, int line) { if (b == 0u) ct_fail(\"CTI0001\", file, line); return a % b; }");
        writer.WriteLine("static int32_t ct_i32_shl(int32_t a, int32_t b) { return ct_i32_bits((uint32_t)a << ((uint32_t)b & 31u)); }");
        writer.WriteLine("static int32_t ct_i32_shr(int32_t a, int32_t b) { uint32_t n = (uint32_t)b & 31u; if (n == 0u) return a; return a >= 0 ? (int32_t)((uint32_t)a >> n) : ct_i32_bits(((uint32_t)a >> n) | (~UINT32_C(0) << (32u - n))); }");
        writer.WriteLine("static int64_t ct_i64_bits(uint64_t value) { int64_t result; (void)memcpy(&result, &value, sizeof(result)); return result; }");
        writer.WriteLine("static int64_t ct_i64_add(int64_t a, int64_t b) { return ct_i64_bits((uint64_t)a + (uint64_t)b); }");
        writer.WriteLine("static int64_t ct_i64_sub(int64_t a, int64_t b) { return ct_i64_bits((uint64_t)a - (uint64_t)b); }");
        writer.WriteLine("static int64_t ct_i64_mul(int64_t a, int64_t b) { return ct_i64_bits((uint64_t)a * (uint64_t)b); }");
        writer.WriteLine("static int64_t ct_i64_neg(int64_t value) { return ct_i64_bits(UINT64_C(0) - (uint64_t)value); }");
        writer.WriteLine("static int64_t ct_i64_div(int64_t a, int64_t b, const char* file, int line) { if (b == 0) ct_fail(\"CTI0001\", file, line); if (a == INT64_MIN && b == -1) return INT64_MIN; return a / b; }");
        writer.WriteLine("static int64_t ct_i64_mod(int64_t a, int64_t b, const char* file, int line) { if (b == 0) ct_fail(\"CTI0001\", file, line); if (a == INT64_MIN && b == -1) return 0; return a % b; }");
        writer.WriteLine("static uint64_t ct_u64_div(uint64_t a, uint64_t b, const char* file, int line) { if (b == UINT64_C(0)) ct_fail(\"CTI0001\", file, line); return a / b; }");
        writer.WriteLine("static uint64_t ct_u64_mod(uint64_t a, uint64_t b, const char* file, int line) { if (b == UINT64_C(0)) ct_fail(\"CTI0001\", file, line); return a % b; }");
        writer.WriteLine("static int64_t ct_i64_shl(int64_t a, int32_t b) { return ct_i64_bits((uint64_t)a << ((uint32_t)b & 63u)); }");
        writer.WriteLine("static int64_t ct_i64_shr(int64_t a, int32_t b) { uint32_t n = (uint32_t)b & 63u; if (n == 0u) return a; return a >= 0 ? (int64_t)((uint64_t)a >> n) : ct_i64_bits(((uint64_t)a >> n) | (~UINT64_C(0) << (64u - n))); }");
        writer.WriteLine("static bool ct_string_equal(const ct_string* a, const ct_string* b) { if (a == b) return true; if (a == NULL || b == NULL || a->Length != b->Length) return false; return a->Length == 0 || memcmp(a->Data, b->Data, (size_t)a->Length) == 0; }");
        writer.WriteLine("static ct_string* ct_string_concat(const ct_string* a, const ct_string* b, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    if (a == NULL) a = &ct_empty_string;");
        writer.WriteLine("    if (b == NULL) b = &ct_empty_string;");
        writer.WriteLine("    if (a->Length > INT32_MAX - b->Length) ct_fail(\"CTS0001\", file, line);");
        writer.WriteLine("    ct_string* result = (ct_string*)ct_alloc(sizeof(ct_string), file, line);");
        writer.WriteLine("    ct_init_object(result, &ct_desc_string);");
        writer.WriteLine("    int32_t length = a->Length + b->Length;");
        writer.WriteLine("    uint8_t* data = (uint8_t*)ct_alloc((size_t)length + 1u, file, line);");
        writer.WriteLine("    if (a->Length != 0) (void)memcpy(data, a->Data, (size_t)a->Length);");
        writer.WriteLine("    if (b->Length != 0) (void)memcpy(data + a->Length, b->Data, (size_t)b->Length);");
        writer.WriteLine("    result->Length = length;");
        writer.WriteLine("    result->Data = data;");
        writer.WriteLine("    return result;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_string* ct_string_from_bytes(const uint8_t* source, int32_t length, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_string* result = (ct_string*)ct_alloc(sizeof(ct_string), file, line);");
        writer.WriteLine("    ct_init_object(result, &ct_desc_string);");
        writer.WriteLine("    uint8_t* data = (uint8_t*)ct_alloc((size_t)length + 1u, file, line);");
        writer.WriteLine("    if (length > 0) (void)memcpy(data, source, (size_t)length);");
        writer.WriteLine("    data[length] = 0;");
        writer.WriteLine("    result->Length = length;");
        writer.WriteLine("    result->Data = data;");
        writer.WriteLine("    return result;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_string* ct_string_from_format(const char* buffer, int length, size_t capacity, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    if (length < 0 || (size_t)length >= capacity) ct_fail(\"CTS0002\", file, line);");
        writer.WriteLine("    return ct_string_from_bytes((const uint8_t*)buffer, (int32_t)length, file, line);");
        writer.WriteLine("}");
        writer.WriteLine("static ct_string* ct_to_string_int(int32_t value, const char* file, int line) { char buffer[12]; int length = snprintf(buffer, sizeof(buffer), \"%\" PRId32, value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_uint(uint32_t value, const char* file, int line) { char buffer[11]; int length = snprintf(buffer, sizeof(buffer), \"%\" PRIu32, value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_long(int64_t value, const char* file, int line) { char buffer[21]; int length = snprintf(buffer, sizeof(buffer), \"%\" PRId64, value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_ulong(uint64_t value, const char* file, int line) { char buffer[21]; int length = snprintf(buffer, sizeof(buffer), \"%\" PRIu64, value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_float(float value, const char* file, int line) { char buffer[32]; int length = snprintf(buffer, sizeof(buffer), \"%.9g\", (double)value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_bool(bool value, const char* file, int line) { const char* text = value ? \"True\" : \"False\"; return ct_string_from_bytes((const uint8_t*)text, value ? 4 : 5, file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_char(uint8_t value, const char* file, int line) { return ct_string_from_bytes(&value, 1, file, line); }");
        writer.WriteLine("void ct_write_string(ct_string* value) { if (value != NULL && value->Length > 0) (void)fwrite(value->Data, 1u, (size_t)value->Length, stdout); }");
        writer.WriteLine("void ct_write_char(uint8_t value) { (void)fputc((int)value, stdout); }");
        writer.WriteLine("void ct_write_int(int32_t value) { (void)fprintf(stdout, \"%\" PRId32, value); }");
        writer.WriteLine("void ct_write_uint(uint32_t value) { (void)fprintf(stdout, \"%\" PRIu32, value); }");
        writer.WriteLine("void ct_write_long(int64_t value) { (void)fprintf(stdout, \"%\" PRId64, value); }");
        writer.WriteLine("void ct_write_ulong(uint64_t value) { (void)fprintf(stdout, \"%\" PRIu64, value); }");
        writer.WriteLine("void ct_write_float(float value) { (void)fprintf(stdout, \"%.9g\", (double)value); }");
        writer.WriteLine("void ct_write_bool(bool value) { (void)fputs(value ? \"True\" : \"False\", stdout); }");
        writer.WriteLine("void ct_write_line(void) { (void)fputc('\\n', stdout); }");
        if (!IsEspIdf)
            writer.WriteLine("void ct_environment_exit(int32_t code) { exit((int)code); }");
        writer.WriteLine();
    }

    private void EmitStringLiterals(CWriter writer)
    {
        foreach (var pair in _stringLiterals.OrderBy(pair => pair.Value))
        {
            var bytes = Encoding.UTF8.GetBytes(pair.Key);
            var values = bytes.Length == 0 ? "0" : string.Join(", ", bytes.Select(value => value.ToString(CultureInfo.InvariantCulture)).Append("0"));
            writer.WriteLine($"static const uint8_t ct_slb_{pair.Value}[] = {{ {values} }};");
            writer.WriteLine($"static ct_string ct_sl_{pair.Value} = {{ {{ &ct_desc_string, 0, UINT32_MAX, NULL }}, {bytes.Length}, ct_slb_{pair.Value} }};");
        }
        if (_stringLiterals.Count > 0)
            writer.WriteLine();
    }

    private void EmitForwardDeclarations(CWriter writer)
    {
        foreach (var type in _functionPointerTypes.OrderBy(type => NameMangler.TypeCode(type), StringComparer.Ordinal))
        {
            var parameters = type.FunctionPointer!.ParameterTypes.Length == 0
                ? "void"
                : string.Join(", ", type.FunctionPointer.ParameterTypes.Select(CTypeName));
            writer.WriteLine($"typedef {CTypeName(type.FunctionPointer.ReturnType)} (*{CTypeName(type)})({parameters});");
        }
        if (_functionPointerTypes.Count != 0)
            writer.WriteLine();
        foreach (var type in Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
            writer.WriteLine($"typedef struct {NameMangler.Type(type)} {NameMangler.Type(type)};");
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
            writer.WriteLine($"typedef struct {NameMangler.Array(array.ElementType!)} {NameMangler.Array(array.ElementType!)};");
        foreach (var type in OrderLayoutTypes().Where(type => type.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Delegate))
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)};");
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
            writer.WriteLine($"static ct_type_descriptor {ArrayDescriptorName(array.ElementType!)};");
        if (Model.UserTypes.Any(type => type.Kind != DeclaredTypeKind.Enum) || _arrayTypes.Count > 0)
            writer.WriteLine();
    }

    private void EmitTypeLayouts(CWriter writer)
    {
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Enum))
        {
            var underlying = type.Fields.Single(field => field.Name == "<underlying>").Type;
            writer.WriteLine($"typedef {CTypeName(underlying)} {NameMangler.Type(type)};");
            foreach (var value in type.EnumValues)
                writer.WriteLine($"#define {NameMangler.Identifier(type.FullName + "." + value.Name)} (({NameMangler.Type(type)}){FormatIntegralConstant(value.Value, underlying)})");
            writer.WriteLine();
        }
        foreach (var type in OrderLayoutTypes())
        {
            writer.WriteLine($"struct {NameMangler.Type(type)}");
            writer.WriteLine("{");
            if (type.Kind == DeclaredTypeKind.Delegate)
            {
                var parameters = string.Concat(type.DelegateParameters.Select(parameter => $", {CTypeName(parameter.Type)}"));
                writer.WriteLine("    ct_object ct_header;");
                writer.WriteLine($"    {CTypeName(type.DelegateReturnType!)} (*ct_invoke)(ct_object*{parameters});");
                writer.WriteLine("    ct_object* ct_target;");
                writer.WriteLine("};");
                writer.WriteLine();
                continue;
            }
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            if (type.Kind == DeclaredTypeKind.Class)
            {
                if (type.IsObject)
                    writer.WriteLine("    ct_object ct_header;");
                else if (type.BaseType is not null)
                    writer.WriteLine($"    {NameMangler.Type(type.BaseType)} ct_base;");
            }
            else if (fields.Length == 0)
                writer.WriteLine("    uint8_t ct_empty;");
            foreach (var field in fields)
                writer.WriteLine($"    {CDeclaration(field.Type, field.CName)};");
            writer.WriteLine("};");
            writer.WriteLine();
        }
    }

    private IEnumerable<TypeSymbol> OrderLayoutTypes()
    {
        var types = Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum).ToArray();
        var emitted = new HashSet<TypeSymbol>();
        var visiting = new HashSet<TypeSymbol>();
        foreach (var type in types)
            foreach (var result in Visit(type))
                yield return result;

        IEnumerable<TypeSymbol> Visit(TypeSymbol type)
        {
            if (emitted.Contains(type))
                yield break;
            if (!visiting.Add(type))
                yield break;
            if (type.BaseType is not null)
                foreach (var result in Visit(type.BaseType))
                    yield return result;
            foreach (var dependency in type.Fields.Where(field => !field.IsStatic && field.Type.Kind == CTypeKind.Struct).Select(field => field.Type.Symbol!).Distinct())
                foreach (var result in Visit(dependency))
                    yield return result;
            visiting.Remove(type);
            if (emitted.Add(type))
                yield return type;
        }
    }

    private void EmitArrayLayouts(CWriter writer)
    {
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var name = NameMangler.Array(array.ElementType!);
            writer.WriteLine($"struct {name} {{ ct_object Object; int32_t Length; {CTypeName(array.ElementType!)}* Data; }};");
            writer.WriteLine($"static {name}* ct_new_{name}(int32_t length, const char* file, int line) {{ {name}* value = ({name}*)ct_alloc(sizeof({name}), file, line); ct_init_object(value, &{ArrayDescriptorName(array.ElementType!)}); value->Length = length; value->Data = ({CTypeName(array.ElementType!)}*)ct_alloc_array(length, sizeof({CTypeName(array.ElementType!)}), file, line); return value; }}");
        }
        if (_arrayTypes.Count > 0)
            writer.WriteLine();
    }

    private static void EmitPlatformAllocation(CWriter writer)
    {
        writer.WriteLine("#if defined(CT_MEMORY_DIAGNOSTICS)");
        writer.WriteLine("static uint32_t ct_memory_live_allocations = 0u;");
        writer.WriteLine("static uint32_t ct_memory_live_objects = 0u;");
        writer.WriteLine("uint32_t ct_memory_diagnostic_live_allocations(void) { return ct_memory_live_allocations; }");
        writer.WriteLine("uint32_t ct_memory_diagnostic_live_objects(void) { return ct_memory_live_objects; }");
        writer.WriteLine("#endif");
        writer.WriteLine("static void* ct_alloc(size_t size, const char* file, int line) { void* value = calloc(1u, size == 0u ? 1u : size); if (value == NULL) ct_fail(\"CTM0001\", file, line);");
        writer.WriteLine("#if defined(CT_MEMORY_DIAGNOSTICS)");
        writer.WriteLine("    ct_memory_live_allocations++;");
        writer.WriteLine("#endif");
        writer.WriteLine("    return value; }");
        writer.WriteLine("static void ct_dealloc(void* value) { if (value == NULL) return;");
        writer.WriteLine("#if defined(CT_MEMORY_DIAGNOSTICS)");
        writer.WriteLine("    if (ct_memory_live_allocations == 0u) ct_fail(\"CTM0003\", \"<runtime>\", 0);");
        writer.WriteLine("    ct_memory_live_allocations--;");
        writer.WriteLine("#endif");
        writer.WriteLine("    free(value); }");
    }

    private void EmitBoxLayouts(CWriter writer)
    {
        foreach (var type in BoxedTypes)
            writer.WriteLine($"typedef struct {BoxName(type)} {{ ct_object Object; {CDeclaration(type, "Value")}; }} {BoxName(type)};");
        if (_boxedTypes.Count > 0)
            writer.WriteLine();
    }

    private void EmitGlobals(CWriter writer)
    {
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Name != "<underlying>"))
        {
            writer.WriteLine($"static {CDeclaration(field.Type, field.CName)} = {DefaultValue(field.Type)};");
        }
        if (Model.UserTypes.SelectMany(type => type.Fields).Any(field => field.IsStatic && field.Name != "<underlying>"))
            writer.WriteLine();
    }

    private void EmitPrototypes(CWriter writer)
    {
        var emittedExternalSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
        {
            foreach (var constructor in type.Constructors)
            {
                writer.WriteLine(MethodSignature(constructor, prototype: true));
                if (type.Kind == DeclaredTypeKind.Class)
                {
                    var parameters = new[] { $"{NameMangler.Type(type)}* ct_self" }
                        .Concat(constructor.Parameters.Select(parameter => CDeclaration(parameter.Type, NameMangler.Identifier(parameter.Name))));
                    writer.WriteLine($"static void {ConstructorInitializerName(constructor)}({string.Join(", ", parameters)});");
                }
            }
            foreach (var method in type.Methods)
            {
                if (IsEspIdf && method.ExternName == "ct_environment_exit")
                    continue;
                if (method.ExternName is not null && !emittedExternalSymbols.Add(method.ExternName))
                    continue;
                writer.WriteLine(MethodSignature(method, prototype: true));
            }
            foreach (var property in type.Properties)
            {
                var self = property.IsStatic ? string.Empty : $"{NameMangler.Type(type)}* ct_self";
                if (property.Getter is not null)
                    writer.WriteLine("static " + CFunctionDeclaration(property.Type, NameMangler.Getter(property), self.Length == 0 ? [] : [self]) + ";");
                if (property.Setter is not null)
                {
                    var parameters = self.Length == 0
                        ? new[] { CDeclaration(property.Type, NameMangler.Identifier("value")) }
                        : new[] { self, CDeclaration(property.Type, NameMangler.Identifier("value")) };
                    writer.WriteLine("static " + CFunctionDeclaration(CType.Void, NameMangler.Setter(property), parameters) + ";");
                }
            }
        }
    }

    private void EmitOwnershipHelpers(CWriter writer)
    {
        var objectType = Model.Types["System.Object"];
        writer.WriteLine($"void ct_memory_retain({NameMangler.Type(objectType)}* value) {{ ct_retain((ct_object*)(void*)value); }}");
        writer.WriteLine($"void ct_memory_release({NameMangler.Type(objectType)}* value) {{ ct_release((ct_object*)(void*)value); }}");

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Struct && type.Type.ContainsManagedReferences))
        {
            var valueType = type.Type;
            writer.WriteLine($"static CT_UNUSED void {ValueRetainName(valueType)}(void* storage)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)storage;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences))
                writer.WriteLine($"    {ValueRetainName(field.Type)}((void*)&value->{field.CName});");
            writer.WriteLine("}");
            writer.WriteLine($"static CT_UNUSED void {ValueDropName(valueType)}(void* storage)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)storage;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences).Reverse())
                writer.WriteLine($"    {ValueDropName(field.Type)}((void*)&value->{field.CName});");
            writer.WriteLine("}");
        }

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Class))
        {
            writer.WriteLine($"static void {ObjectDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)(void*)object;");
            writer.WriteLine("    (void)value;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences).Reverse())
                writer.WriteLine($"    {ValueDropName(field.Type)}((void*)&value->{field.CName});");
            if (type.BaseType is not null)
                writer.WriteLine($"    {ObjectDropName(type.BaseType)}(object);");
            writer.WriteLine("}");
        }

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Delegate))
        {
            writer.WriteLine($"static void {DelegateDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)(void*)object;");
            writer.WriteLine("    ct_object* target = value->ct_target;");
            writer.WriteLine("    value->ct_target = NULL;");
            writer.WriteLine("    ct_release(target);");
            writer.WriteLine("}");
        }

        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var element = array.ElementType!;
            var name = NameMangler.Array(element);
            writer.WriteLine($"static void {ArrayDropName(element)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {name}* value = ({name}*)(void*)object;");
            if (element.ContainsManagedReferences)
            {
                writer.WriteLine("    for (int32_t index = value->Length; index > 0; --index)");
                writer.WriteLine($"        {ValueDropName(element)}((void*)&value->Data[index - 1]);");
            }
            writer.WriteLine("    ct_dealloc(value->Data);");
            writer.WriteLine("    value->Data = NULL; value->Length = 0;");
            writer.WriteLine("}");
        }

        foreach (var type in BoxedTypes)
        {
            writer.WriteLine($"static void {BoxDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            if (type.ContainsManagedReferences)
            {
                writer.WriteLine($"    {BoxName(type)}* value = ({BoxName(type)}*)(void*)object;");
                writer.WriteLine($"    {ValueDropName(type)}((void*)&value->Value);");
            }
            else
                writer.WriteLine("    (void)object;");
            writer.WriteLine("}");
        }
        writer.WriteLine();
    }

    private void EmitObjectMetadata(CWriter writer)
    {
        var virtualMethods = VirtualMethodRoots().ToArray();
        var virtualProperties = VirtualPropertyRoots().ToArray();
        writer.WriteLine("struct ct_vtable");
        writer.WriteLine("{");
        writer.WriteLine("    ct_string* (*ToString)(ct_object*);");
        writer.WriteLine("    bool (*Equals)(ct_object*, ct_object*);");
        writer.WriteLine("    int32_t (*GetHashCode)(ct_object*);");
        foreach (var method in virtualMethods)
        {
            var parameters = string.Concat(method.Parameters.Select(parameter => $", {CTypeName(parameter.Type)}"));
            writer.WriteLine($"    {CTypeName(method.ReturnType)} (*{VirtualSlotName(method)})(ct_object*{parameters});");
        }
        foreach (var property in virtualProperties)
        {
            if (property.Getter is not null)
                writer.WriteLine($"    {CTypeName(property.Type)} (*{VirtualGetterSlotName(property)})(ct_object*);");
            if (property.Setter is not null)
                writer.WriteLine($"    void (*{VirtualSetterSlotName(property)})(ct_object*, {CTypeName(property.Type)});");
        }
        writer.WriteLine("};");
        writer.WriteLine("static ct_string* ct_object_default_to_string(ct_object* value);");
        writer.WriteLine("static bool ct_object_default_equals(ct_object* left, ct_object* right);");
        writer.WriteLine("static int32_t ct_object_default_hash(ct_object* value);");
        writer.WriteLine("static bool ct_object_value_equals(ct_object* left, ct_object* right);");
        writer.WriteLine("static uint32_t ct_object_value_hash(ct_object* value);");
        writer.WriteLine("static bool ct_type_is_assignable(const ct_type_descriptor* actual, const ct_type_descriptor* target) { for (const ct_type_descriptor* current = actual; current != NULL; current = current->Base) if (current == target) return true; return false; }");
        writer.WriteLine("static ct_object* ct_checked_cast(ct_object* value, const ct_type_descriptor* target, const char* file, int line) { if (value == NULL) return NULL; if (!ct_type_is_assignable(value->Type, target)) ct_fail(\"CTO0001\", file, line); return value; }");
        writer.WriteLine("static ct_object* ct_safe_cast(ct_object* value, const ct_type_descriptor* target) { return value != NULL && ct_type_is_assignable(value->Type, target) ? value : NULL; }");
        writer.WriteLine("static uint32_t ct_hash_bytes(const void* value, size_t size) { const uint8_t* bytes = (const uint8_t*)value; uint32_t hash = UINT32_C(2166136261); for (size_t i = 0; i < size; ++i) { hash ^= bytes[i]; hash *= UINT32_C(16777619); } return hash; }");
        writer.WriteLine("static uint32_t ct_hash_float(float value) { if (isnan(value)) return UINT32_C(0x7FC00000); if (value == 0.0f) return 0u; return ct_hash_bytes(&value, sizeof(value)); }");
        EmitDefaultVTable(writer, "ct_default_vtable", virtualMethods, virtualProperties);
        writer.WriteLine("static ct_string* ct_string_v_to_string(ct_object* value) { ct_retain(value); return (ct_string*)(void*)value; }");
        writer.WriteLine("static bool ct_string_v_equals(ct_object* left, ct_object* right) { return right != NULL && right->Type == &ct_desc_string && ct_string_equal((ct_string*)(void*)left, (ct_string*)(void*)right); }");
        writer.WriteLine("static int32_t ct_string_v_hash(ct_object* value) { ct_string* text = (ct_string*)(void*)value; return ct_i32_bits(ct_hash_bytes(text->Data, (size_t)text->Length)); }");
        EmitSpecialVTable(writer, "ct_string_vtable", "ct_string_v_to_string", "ct_string_v_equals", "ct_string_v_hash", virtualMethods, virtualProperties);
        writer.WriteLine("static ct_type_descriptor ct_desc_string = { \"string\", &" + DescriptorName(Model.Types["System.Object"]) + ", &ct_string_vtable, 1u, sizeof(ct_string), _Alignof(ct_string), false, ct_drop_string };");
        uint id = 2;
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            EmitClassVTable(writer, type, virtualMethods, virtualProperties);
            var baseDescriptor = type.BaseType is null ? "NULL" : $"&{DescriptorName(type.BaseType)}";
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)} = {{ \"{EscapeCString(type.FullName)}\", {baseDescriptor}, &ct_vtable_{NameMangler.Identifier(type.FullName)}, {id++}u, sizeof({NameMangler.Type(type)}), _Alignof({NameMangler.Type(type)}), false, {ObjectDropName(type)} }};");
        }
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)} = {{ \"{EscapeCString(type.FullName)}\", &{DescriptorName(Model.Types["System.Object"])}, &ct_default_vtable, {id++}u, sizeof({NameMangler.Type(type)}), _Alignof({NameMangler.Type(type)}), false, {DelegateDropName(type)} }};");
        }
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var name = NameMangler.Array(array.ElementType!);
            writer.WriteLine($"static ct_type_descriptor {ArrayDescriptorName(array.ElementType!)} = {{ \"{EscapeCString(array.ElementType!.DisplayName)}[]\", &{DescriptorName(Model.Types["System.Object"])}, &ct_default_vtable, {id++}u, sizeof({name}), _Alignof({name}), false, {ArrayDropName(array.ElementType!)} }};");
        }
        foreach (var type in BoxedTypes)
        {
            EmitBoxMetadata(writer, type, virtualMethods, virtualProperties);
            writer.WriteLine($"static ct_type_descriptor {BoxDescriptorName(type)} = {{ \"{EscapeCString(type.DisplayName)}\", &{DescriptorName(Model.Types["System.Object"])}, &ct_vtable_box_{NameMangler.TypeCode(type)}, {id++}u, sizeof({BoxName(type)}), _Alignof({BoxName(type)}), true, {BoxDropName(type)} }};");
        }
        writer.WriteLine("static ct_string* ct_object_default_to_string(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_string_from_bytes((const uint8_t*)value->Type->Name, (int32_t)strlen(value->Type->Name), \"<runtime>\", 0); }");
        writer.WriteLine("static bool ct_object_default_equals(ct_object* left, ct_object* right) { return left == right; }");
        writer.WriteLine("static int32_t ct_object_default_hash(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_i32_bits(value->IdentityHash); }");
        writer.WriteLine("static bool ct_object_value_equals(ct_object* left, ct_object* right) { if (left == right) return true; if (left == NULL || right == NULL) return false; return left->Type->VTable->Equals(left, right); }");
        writer.WriteLine("static uint32_t ct_object_value_hash(ct_object* value) { return value == NULL ? 0u : (uint32_t)value->Type->VTable->GetHashCode(value); }");
        var objectType = Model.Types.GetValueOrDefault("System.Object");
        var objectCType = objectType is null ? "ct_object" : NameMangler.Type(objectType);
        writer.WriteLine($"ct_string* ct_object_to_string({objectCType}* value) {{ return value == NULL ? NULL : ((ct_object*)(void*)value)->Type->VTable->ToString((ct_object*)(void*)value); }}");
        writer.WriteLine($"ct_string* ct_object_base_to_string({objectCType}* value) {{ return ct_object_default_to_string((ct_object*)(void*)value); }}");
        writer.WriteLine($"int32_t ct_object_hash({objectCType}* value) {{ return ((ct_object*)(void*)ct_require_nonnull(value, \"<runtime>\", 0))->Type->VTable->GetHashCode((ct_object*)(void*)value); }}");
        writer.WriteLine($"bool ct_object_reference_equals({objectCType}* left, {objectCType}* right) {{ return left == right; }}");
        if (_usesExceptions && Model.Types.TryGetValue("System.Exception", out var exceptionType))
        {
            var message = exceptionType.Properties.Single(property => property.Name == "Message");
            writer.WriteLine("CT_NORETURN static void ct_unhandled_exception(ct_object* exception)");
            writer.WriteLine("{");
            writer.WriteLine($"    ct_string* message = {NameMangler.Getter(message)}(({NameMangler.Type(exceptionType)}*)(void*)exception);");
            writer.WriteLine("    (void)fprintf(stderr, \"C~ unhandled exception CTE0001: %s\", exception->Type->Name);");
            writer.WriteLine("    if (message != NULL && message->Length != 0) (void)fprintf(stderr, \": %.*s\", (int)message->Length, (const char*)message->Data);");
            writer.WriteLine("    (void)fputc('\\n', stderr);");
            writer.WriteLine(IsEspIdf ? "    abort();" : "    exit(EXIT_FAILURE);");
            writer.WriteLine("}");
        }
        writer.WriteLine();
    }

    private void EmitDelegateSupport(CWriter writer)
    {
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var parameters = string.Concat(type.DelegateParameters.Select(parameter => $", {CTypeName(parameter.Type)}"));
            writer.WriteLine($"static {NameMangler.Type(type)}* {DelegateFactoryName(type)}(ct_object* target, {CTypeName(type.DelegateReturnType!)} (*invoke)(ct_object*{parameters}), const char* file, int line)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)ct_alloc(sizeof({NameMangler.Type(type)}), file, line);");
            writer.WriteLine($"    ct_init_object(value, &{DescriptorName(type)});");
            writer.WriteLine("    value->ct_target = target;");
            writer.WriteLine("    value->ct_invoke = invoke;");
            writer.WriteLine("    ct_retain(target);");
            writer.WriteLine("    return value;");
            writer.WriteLine("}");
        }

        foreach (var ((delegateType, method, virtualDispatch), name) in _delegateThunks.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var parameters = delegateType.DelegateParameters.Select((parameter, index) => $"{CTypeName(parameter.Type)} ct_arg_{index}").ToArray();
            var signatureParameters = string.Join(", ", new[] { "ct_object* ct_target" }.Concat(parameters));
            writer.WriteLine($"static {CTypeName(delegateType.DelegateReturnType!)} {name}({signatureParameters})");
            writer.WriteLine("{");
            writer.WriteLine("    (void)ct_target;");
            var arguments = Enumerable.Range(0, parameters.Length).Select(index => $"ct_arg_{index}").ToList();
            string call;
            if (method.IsStatic)
                call = $"{method.CName}({string.Join(", ", arguments)})";
            else if (virtualDispatch)
                call = $"ct_target->Type->VTable->{VirtualSlotName(method)}({string.Join(", ", new[] { "ct_target" }.Concat(arguments))})";
            else
                call = $"{method.CName}(({NameMangler.Type(method.ContainingType)}*)(void*)ct_target{(arguments.Count == 0 ? string.Empty : ", " + string.Join(", ", arguments))})";
            if (delegateType.DelegateReturnType == CType.Void)
                writer.WriteLine($"    {call};");
            else
                writer.WriteLine($"    return {call};");
            writer.WriteLine("}");
        }
        if (Model.UserTypes.Any(type => type.Kind == DeclaredTypeKind.Delegate))
            writer.WriteLine();
    }

    private void EmitFunctionPointerTrampolines(CWriter writer)
    {
        foreach (var ((type, method), name) in _functionPointerTrampolines.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var signature = type.FunctionPointer!;
            var parameters = signature.ParameterTypes.Select((parameter, index) => $"{CTypeName(parameter)} ct_arg_{index}").ToArray();
            writer.WriteLine($"static {CTypeName(signature.ReturnType)} {name}({(parameters.Length == 0 ? "void" : string.Join(", ", parameters))})");
            writer.WriteLine("{");
            writer.WriteLine("    jmp_buf ct_callback_jump;");
            writer.WriteLine("    ct_exception_frame ct_callback_frame = { &ct_callback_jump, ct_exception_top, ct_cleanup_top };");
            writer.WriteLine("    ct_exception_top = &ct_callback_frame;");
            writer.WriteLine("    if (setjmp(ct_callback_jump) != 0)");
            writer.WriteLine("    {");
            writer.WriteLine("        ct_exception_top = ct_callback_frame.Previous;");
            writer.WriteLine("        ct_fail(\"CTE0003\", \"<native-callback>\", 0);");
            writer.WriteLine("    }");
            var call = $"{method.CName}({string.Join(", ", Enumerable.Range(0, parameters.Length).Select(index => $"ct_arg_{index}"))})";
            if (signature.ReturnType == CType.Void)
            {
                writer.WriteLine($"    {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
            }
            else
            {
                writer.WriteLine($"    {CTypeName(signature.ReturnType)} ct_callback_result = {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
                writer.WriteLine("    return ct_callback_result;");
            }
            writer.WriteLine("}");
        }
        if (_functionPointerTrampolines.Count != 0)
            writer.WriteLine();
    }

    private IEnumerable<MethodSymbol> VirtualMethodRoots() => Model.UserTypes
        .SelectMany(type => type.Methods)
        .Where(method => method.IsVirtual && method.OverriddenMethod is null && !method.ContainingType.IsObject)
        .OrderBy(method => method.ContainingType.FullName, StringComparer.Ordinal)
        .ThenBy(method => method.CName, StringComparer.Ordinal);

    private IEnumerable<PropertySymbol> VirtualPropertyRoots() => Model.UserTypes
        .SelectMany(type => type.Properties)
        .Where(property => property.IsVirtual && property.OverriddenProperty is null)
        .OrderBy(property => property.ContainingType.FullName, StringComparer.Ordinal)
        .ThenBy(property => property.Name, StringComparer.Ordinal);

    private void EmitDefaultVTable(CWriter writer, string name, MethodSymbol[] methods, PropertySymbol[] properties) =>
        EmitSpecialVTable(writer, name, "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash", methods, properties);

    private static void EmitSpecialVTable(CWriter writer, string name, string toString, string equals, string hash, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        writer.WriteLine($"static const ct_vtable {name} = {{");
        writer.WriteLine($"    .ToString = {toString}, .Equals = {equals}, .GetHashCode = {hash},");
        foreach (var method in methods)
            writer.WriteLine($"    .{VirtualSlotName(method)} = NULL,");
        foreach (var property in properties)
        {
            if (property.Getter is not null)
                writer.WriteLine($"    .{VirtualGetterSlotName(property)} = NULL,");
            if (property.Setter is not null)
                writer.WriteLine($"    .{VirtualSetterSlotName(property)} = NULL,");
        }
        writer.WriteLine("};");
    }

    private void EmitClassVTable(CWriter writer, TypeSymbol type, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        var objectMethods = Model.Types["System.Object"].Methods;
        var toStringRoot = objectMethods.Single(method => method.Name == "ToString" && method.Parameters.Length == 0);
        var equalsRoot = objectMethods.Single(method => method.Name == "Equals" && method.Parameters.Length == 1 && !method.IsStatic);
        var hashRoot = objectMethods.Single(method => method.Name == "GetHashCode" && method.Parameters.Length == 0);
        var toString = ResolveVirtualMethod(type, toStringRoot);
        var equals = ResolveVirtualMethod(type, equalsRoot);
        var hash = ResolveVirtualMethod(type, hashRoot);
        var toStringThunk = toString == toStringRoot ? "ct_object_default_to_string" : EmitMethodThunk(writer, toString!);
        var equalsThunk = equals == equalsRoot ? "ct_object_default_equals" : EmitMethodThunk(writer, equals!);
        var hashThunk = hash == hashRoot ? "ct_object_default_hash" : EmitMethodThunk(writer, hash!);
        var methodEntries = methods.Select(root => (Root: root, Implementation: ResolveVirtualMethod(type, root)))
            .Select(entry => (entry.Root, Name: entry.Implementation is null ? "NULL" : EmitMethodThunk(writer, entry.Implementation)))
            .ToArray();
        var propertyEntries = properties.Select(root =>
        {
            var implementation = ResolveVirtualProperty(type, root);
            return (Root: root,
                Getter: implementation?.Getter is null ? "NULL" : EmitPropertyThunk(writer, implementation, true),
                Setter: implementation?.Setter is null ? "NULL" : EmitPropertyThunk(writer, implementation, false));
        }).ToArray();
        writer.WriteLine($"static const ct_vtable ct_vtable_{NameMangler.Identifier(type.FullName)} = {{");
        writer.WriteLine($"    .ToString = {toStringThunk}, .Equals = {equalsThunk}, .GetHashCode = {hashThunk},");
        foreach (var entry in methodEntries)
            writer.WriteLine($"    .{VirtualSlotName(entry.Root)} = {entry.Name},");
        foreach (var entry in propertyEntries)
        {
            if (entry.Root.Getter is not null)
                writer.WriteLine($"    .{VirtualGetterSlotName(entry.Root)} = {entry.Getter},");
            if (entry.Root.Setter is not null)
                writer.WriteLine($"    .{VirtualSetterSlotName(entry.Root)} = {entry.Setter},");
        }
        writer.WriteLine("};");
    }

    private MethodSymbol? ResolveVirtualMethod(TypeSymbol type, MethodSymbol root)
    {
        if (!type.BaseTypesAndSelf().Contains(root.ContainingType))
            return null;
        foreach (var current in type.BaseTypesAndSelf())
        {
            var match = current.Methods.FirstOrDefault(method => VirtualRoot(method) == root);
            if (match is not null)
                return match;
        }
        return root;
    }

    private PropertySymbol? ResolveVirtualProperty(TypeSymbol type, PropertySymbol root)
    {
        if (!type.BaseTypesAndSelf().Contains(root.ContainingType))
            return null;
        foreach (var current in type.BaseTypesAndSelf())
        {
            var match = current.Properties.FirstOrDefault(property => VirtualRoot(property) == root);
            if (match is not null)
                return match;
        }
        return root;
    }

    private static MethodSymbol VirtualRoot(MethodSymbol method)
    {
        while (method.OverriddenMethod is not null)
            method = method.OverriddenMethod;
        return method;
    }

    private static PropertySymbol VirtualRoot(PropertySymbol property)
    {
        while (property.OverriddenProperty is not null)
            property = property.OverriddenProperty;
        return property;
    }

    private string EmitMethodThunk(CWriter writer, MethodSymbol method)
    {
        var name = $"ct_vthunk_{NameMangler.Identifier(method.CName)}";
        if (!_emittedThunks.Add(name))
            return name;
        var objectSlot = VirtualSlotName(method);
        var parameters = method.Parameters.Select((parameter, index) => objectSlot == "Equals"
            ? $"ct_object* a{index}"
            : $"{CTypeName(parameter.Type)} a{index}").ToArray();
        var signatureParameters = string.Join(", ", new[] { "ct_object* self" }.Concat(parameters));
        var arguments = string.Join(", ", new[] { $"({NameMangler.Type(method.ContainingType)}*)(void*)self" }.Concat(method.Parameters.Select((parameter, index) => objectSlot == "Equals"
            ? $"({CTypeName(parameter.Type)})(void*)a{index}"
            : $"a{index}")));
        writer.WriteLine($"static {CTypeName(method.ReturnType)} {name}({signatureParameters}) {{ {(method.ReturnType == CType.Void ? string.Empty : "return ")}{method.CName}({arguments}); }}");
        return name;
    }

    private string EmitPropertyThunk(CWriter writer, PropertySymbol property, bool getter)
    {
        var name = $"ct_vthunk_{(getter ? "get" : "set")}_{NameMangler.Identifier(property.ContainingType.FullName + "." + property.Name)}";
        if (!_emittedThunks.Add(name))
            return name;
        var self = $"({NameMangler.Type(property.ContainingType)}*)(void*)self";
        if (getter)
            writer.WriteLine($"static {CTypeName(property.Type)} {name}(ct_object* self) {{ return {NameMangler.Getter(property)}({self}); }}");
        else
            writer.WriteLine($"static void {name}(ct_object* self, {CTypeName(property.Type)} value) {{ {NameMangler.Setter(property)}({self}, value); }}");
        return name;
    }

    private void EmitBoxMetadata(CWriter writer, CType type, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        var code = NameMangler.TypeCode(type);
        var box = BoxName(type);
        var descriptor = BoxDescriptorName(type);
        writer.WriteLine($"static ct_type_descriptor {descriptor};");
        var toString = $"ct_box_to_string_{code}";
        var equals = $"ct_box_equals_{code}";
        var hash = $"ct_box_hash_{code}";
        var structToString = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "ToString" && method.Parameters.Length == 0) : null;
        var structEquals = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "Equals" && method.Parameters.Length == 1) : null;
        var structHash = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "GetHashCode" && method.Parameters.Length == 0) : null;
        var enumFormatter = $"ct_enum_to_string_{code}";
        if (type.Kind == CTypeKind.Enum)
        {
            var underlying = type.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type;
            writer.WriteLine($"static ct_string* {enumFormatter}({CTypeName(type)} value)");
            writer.WriteLine("{");
            foreach (var enumValue in type.Symbol.EnumValues.GroupBy(value => value.Value).Select(group => group.First()))
            {
                var escaped = EscapeCString(enumValue.Name);
                writer.WriteLine($"    if (value == ({CTypeName(type)}){FormatIntegralConstant(enumValue.Value, underlying)}) return ct_string_from_bytes((const uint8_t*)\"{escaped}\", {Encoding.UTF8.GetByteCount(enumValue.Name)}, \"<runtime>\", 0);");
            }
            var fallback = underlying.Kind switch
            {
                CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint((uint32_t)value, \"<runtime>\", 0)",
                CTypeKind.Ulong => "ct_to_string_ulong((uint64_t)value, \"<runtime>\", 0)",
                CTypeKind.Long => "ct_to_string_long((int64_t)value, \"<runtime>\", 0)",
                _ => "ct_to_string_int((int32_t)value, \"<runtime>\", 0)",
            };
            writer.WriteLine($"    return {fallback};");
            writer.WriteLine("}");
        }
        var toStringExpression = type.Kind switch
        {
            CTypeKind.Struct when structToString is not null => $"{structToString.CName}(&box->Value)",
            CTypeKind.Enum => $"{enumFormatter}(box->Value)",
            CTypeKind.Bool => "ct_to_string_bool(box->Value, \"<runtime>\", 0)",
            CTypeKind.Char => "ct_to_string_char(box->Value, \"<runtime>\", 0)",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint((uint32_t)box->Value, \"<runtime>\", 0)",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int((int32_t)box->Value, \"<runtime>\", 0)",
            CTypeKind.Long => "ct_to_string_long(box->Value, \"<runtime>\", 0)",
            CTypeKind.Ulong => "ct_to_string_ulong(box->Value, \"<runtime>\", 0)",
            CTypeKind.Float => "ct_to_string_float(box->Value, \"<runtime>\", 0)",
            _ => $"ct_string_from_bytes((const uint8_t*)\"{EscapeCString(type.DisplayName)}\", {Encoding.UTF8.GetByteCount(type.DisplayName)}, \"<runtime>\", 0)",
        };
        writer.WriteLine($"static ct_string* {toString}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; (void)box; return {toStringExpression}; }}");
        var comparison = type.Kind == CTypeKind.Float
            ? "left->Value == right->Value || (isnan(left->Value) && isnan(right->Value))"
            : type.Kind == CTypeKind.Struct
                ? StructEqualityExpression(type.Symbol!, "left->Value", "right->Value")
                : "left->Value == right->Value";
        if (structEquals is not null)
            writer.WriteLine($"static bool {equals}(ct_object* a, ct_object* b) {{ {box}* left = ({box}*)(void*)a; return {structEquals.CName}(&left->Value, ({NameMangler.Type(Model.Types["System.Object"])}*)(void*)b); }}");
        else
            writer.WriteLine($"static bool {equals}(ct_object* a, ct_object* b) {{ if (b == NULL || b->Type != &{descriptor}) return false; {box}* left = ({box}*)(void*)a; {box}* right = ({box}*)(void*)b; return {comparison}; }}");
        if (structHash is not null)
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return {structHash.CName}(&box->Value); }}");
        else if (type.Kind == CTypeKind.Struct)
        {
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; uint32_t result = UINT32_C(2166136261);");
            foreach (var field in type.Symbol!.Fields.Where(field => !field.IsStatic))
                writer.WriteLine($"    result = (result ^ {ValueHashExpression(field.Type, $"box->Value.{field.CName}")}) * UINT32_C(16777619);");
            writer.WriteLine("    return ct_i32_bits(result); }");
        }
        else if (type.Kind == CTypeKind.Float)
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return ct_i32_bits(ct_hash_float(box->Value)); }}");
        else
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return ct_i32_bits(ct_hash_bytes(&box->Value, sizeof(box->Value))); }}");
        EmitSpecialVTable(writer, $"ct_vtable_box_{code}", toString, equals, hash, methods, properties);
        var boxRetain = type.ContainsManagedReferences ? $" {ValueRetainName(type)}((void*)&box->Value);" : string.Empty;
        var unboxRetain = type.ContainsManagedReferences ? $" {ValueRetainName(type)}((void*)&result);" : string.Empty;
        writer.WriteLine($"static {NameMangler.Type(Model.Types["System.Object"])}* {BoxFunctionName(type)}({CTypeName(type)} value, const char* file, int line) {{ {box}* box = ({box}*)ct_alloc(sizeof({box}), file, line); ct_init_object(box, &{descriptor}); box->Value = value;{boxRetain} return ({NameMangler.Type(Model.Types["System.Object"])}*)(void*)box; }}");
        writer.WriteLine($"static {CTypeName(type)} {UnboxFunctionName(type)}({NameMangler.Type(Model.Types["System.Object"])}* value, const char* file, int line) {{ if (value == NULL) ct_fail(\"CTO0002\", file, line); ct_object* object = (ct_object*)(void*)value; if (object->Type != &{descriptor}) ct_fail(\"CTO0003\", file, line); {CTypeName(type)} result = (({box}*)(void*)value)->Value;{unboxRetain} return result; }}");
    }

    private static string StructEqualityExpression(TypeSymbol type, string left, string right)
    {
        var comparisons = type.Fields.Where(field => !field.IsStatic)
            .Select(field => ValueEqualityExpression(field.Type, $"{left}.{field.CName}", $"{right}.{field.CName}"))
            .ToArray();
        return comparisons.Length == 0 ? "true" : string.Join(" && ", comparisons.Select(value => $"({value})"));
    }

    private static string ValueEqualityExpression(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.Float => $"{left} == {right} || (isnan({left}) && isnan({right}))",
        CTypeKind.String => $"ct_string_equal({left}, {right})",
        CTypeKind.Class or CTypeKind.Array => $"ct_object_value_equals((ct_object*)(void*){left}, (ct_object*)(void*){right})",
        CTypeKind.Struct => StructEqualityExpression(type.Symbol!, left, right),
        _ => $"{left} == {right}",
    };

    private static string ValueHashExpression(CType type, string value) => type.Kind switch
    {
        CTypeKind.Float => $"ct_hash_float({value})",
        CTypeKind.String => $"({value} == NULL ? 0u : ct_hash_bytes({value}->Data, (size_t){value}->Length))",
        CTypeKind.Class or CTypeKind.Array => $"ct_object_value_hash((ct_object*)(void*){value})",
        CTypeKind.Struct => StructHashExpression(type.Symbol!, value),
        _ => $"ct_hash_bytes(&{value}, sizeof({value}))",
    };

    private static string StructHashExpression(TypeSymbol type, string value)
    {
        var result = "UINT32_C(2166136261)";
        foreach (var field in type.Fields.Where(field => !field.IsStatic))
            result = $"(({result} ^ {ValueHashExpression(field.Type, $"{value}.{field.CName}")}) * UINT32_C(16777619))";
        return result;
    }

    private void EmitMain(CWriter writer)
    {
        if (IsEspIdf)
        {
            writer.WriteLine("void app_main(void)");
            writer.WriteLine("{");
            writer.WriteLine("    (void)setvbuf(stdout, NULL, _IONBF, 0);");
            writer.WriteLine("    (void)setvbuf(stderr, NULL, _IONBF, 0);");
            writer.WriteLine("    ct_module_init();");
            if (Model.EntryPoint is not null)
                writer.WriteLine($"    {Model.EntryPoint.CName}();");
            writer.WriteLine("}");
            return;
        }

        writer.WriteLine("int main(void)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_keep_symbols();");
        writer.WriteLine("    ct_module_init();");
        if (Model.EntryPoint is not null)
            writer.WriteLine($"    {Model.EntryPoint.CName}();");
        writer.WriteLine("    return EXIT_SUCCESS;");
        writer.WriteLine("}");
    }

    private static string MarkUnusedDefinitions(string output)
    {
        var lines = output.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("static ", StringComparison.Ordinal))
                lines[index] = "static CT_UNUSED " + lines[index][7..];
        }
        return string.Join('\n', lines);
    }

    private void EmitKeepSymbols(CWriter writer)
    {
        writer.WriteLine("static void ct_keep_symbols(void)");
        writer.WriteLine("{");
        var runtime = new[]
        {
            "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_dealloc", "ct_retain", "ct_release", "ct_memory_retain", "ct_memory_release", "ct_init_object", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_cleanup_push", "ct_cleanup_unwind_to", "ct_cleanup_disarm", "ct_retain_ref_value", "ct_drop_ref_value",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_i64_bits", "ct_i64_add", "ct_i64_sub", "ct_i64_mul", "ct_i64_neg", "ct_i64_div", "ct_i64_mod",
            "ct_u64_div", "ct_u64_mod", "ct_i64_shl", "ct_i64_shr",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint", "ct_to_string_long", "ct_to_string_ulong",
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_long", "ct_write_ulong", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash", "ct_object_to_string", "ct_object_base_to_string", "ct_object_hash", "ct_object_reference_equals",
            "ct_type_is_assignable", "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_object_value_equals", "ct_object_value_hash",
        };
        foreach (var name in runtime)
            writer.WriteLine($"    (void)&{name};");
        if (_usesExceptions)
        {
            writer.WriteLine("    (void)&ct_throw;");
            writer.WriteLine("    (void)&ct_unhandled_exception;");
        }
        writer.WriteLine("    (void)&ct_default_vtable;");
        foreach (var literal in _stringLiterals.Values.Order())
            writer.WriteLine($"    (void)&ct_sl_{literal};");
        foreach (var type in Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
        {
            foreach (var constructor in type.Constructors)
                writer.WriteLine($"    (void)&{constructor.CName};");
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                writer.WriteLine($"    (void)&{method.CName};");
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                    writer.WriteLine($"    (void)&{NameMangler.Getter(property)};");
                if (property.Setter is not null)
                    writer.WriteLine($"    (void)&{NameMangler.Setter(property)};");
            }
        }
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
            writer.WriteLine($"    (void)&ct_new_{NameMangler.Array(array.ElementType!)};");
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            writer.WriteLine($"    (void)&{DelegateFactoryName(type)};");
            writer.WriteLine($"    (void)&{DelegateDropName(type)};");
            writer.WriteLine($"    (void)&{DescriptorName(type)};");
        }
        foreach (var type in BoxedTypes)
        {
            writer.WriteLine($"    (void)&{BoxFunctionName(type)};");
            writer.WriteLine($"    (void)&{UnboxFunctionName(type)};");
            writer.WriteLine($"    (void)&{BoxDescriptorName(type)};");
            writer.WriteLine($"    (void)&ct_vtable_box_{NameMangler.TypeCode(type)};");
        }
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Name != "<underlying>"))
            writer.WriteLine($"    (void)&{field.CName};");
        writer.WriteLine("}");
    }
}
