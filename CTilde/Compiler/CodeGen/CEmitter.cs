using System.Globalization;
using System.Text;

namespace CTilde;

internal sealed class CEmitter
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];
    private readonly HashSet<CType> _boxedTypes = [];
    private readonly HashSet<string> _emittedThunks = new(StringComparer.Ordinal);

    public CEmitter(CompilationModel model)
    {
        Model = model;
        Diagnostics = model.Diagnostics;
    }

    public CompilationModel Model { get; }
    public DiagnosticBag Diagnostics { get; }

    public IEnumerable<string> DynamicGeneratedSymbols =>
        _arrayTypes.SelectMany(type => new[] { NameMangler.Array(type.ElementType!), $"ct_new_{NameMangler.Array(type.ElementType!)}" })
            .Concat(_arrayTypes.Select(type => ArrayDescriptorName(type.ElementType!)))
            .Concat(_stringLiterals.Values.SelectMany(id => new[] { $"ct_sl_{id}", $"ct_slb_{id}" }))
            .Concat(Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class)
                .SelectMany(type => new[] { DescriptorName(type), $"ct_vtable_{NameMangler.Identifier(type.FullName)}" }))
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
        EmitPrototypes(writer);
        EmitObjectMetadata(writer);
        writer.WriteLine();
        foreach (var definition in program.Functions)
        {
            writer.WriteBlock(definition.Render().TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        writer.WriteBlock(string.Join('\n', program.ModuleInitializer.Select(instruction => instruction.Text)).TrimEnd().Split('\n'));
        writer.WriteLine();
        EmitKeepSymbols(writer);
        writer.WriteLine();
        EmitMain(writer);
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
        CTypeKind.Float => "float",
        CTypeKind.String => "ct_string*",
        CTypeKind.Class => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Struct or CTypeKind.Enum => NameMangler.Type(type.Symbol!),
        CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*",
        CTypeKind.Null => "void*",
        _ => "int32_t",
    };

    public string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.Bool => "false",
        CTypeKind.Float => "0.0f",
        CTypeKind.String or CTypeKind.Class or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.Null => "NULL",
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

    public string DescriptorExpression(CType type) => type.Kind switch
    {
        CTypeKind.String => "&ct_desc_string",
        CTypeKind.Class => $"&{DescriptorName(type.Symbol!)}",
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

    public static string SourceArgument(SyntaxNode syntax) => $"\"{EscapeCString(syntax.Source.FilePath.Replace('\\', '/'))}\", {syntax.Source.GetLocation(syntax.Span).Line}";

    public static string DescriptorName(TypeSymbol type) => $"ct_desc_{NameMangler.Identifier(type.FullName)}";
    public static string ArrayDescriptorName(CType elementType) => $"ct_desc_{NameMangler.Array(elementType)}";
    public static string ConstructorInitializerName(MethodSymbol constructor) => $"ct_init_{constructor.CName}";

    public string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false)
    {
        var returnType = method.IsConstructor ? method.ContainingType.Type : method.ReturnType;
        var parameters = new List<string>();
        if (!method.IsStatic && !method.IsConstructor)
            parameters.Add($"{NameMangler.Type(method.ContainingType)}* ct_self");
        foreach (var parameter in method.Parameters)
            parameters.Add($"{CTypeName(parameter.Type)} {NameMangler.Identifier(parameter.Name)}");
        var storage = method.ExternName is not null ? "extern " : "static ";
        var signature = $"{storage}{CTypeName(returnType)} {name ?? method.CName}({(parameters.Count == 0 ? "void" : string.Join(", ", parameters))})";
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

    private static void EmitPreamble(CWriter writer)
    {
        writer.WriteLine("/* Generated by C~ draft 0.4 for GNU C23. Do not edit. */");
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
        writer.WriteLine();
        writer.WriteLine("static_assert(CHAR_BIT == 8, \"C~ requires 8-bit bytes\");");
        writer.WriteLine("static_assert(sizeof(int32_t) == 4 && sizeof(uint32_t) == 4, \"C~ requires exact 32-bit integers\");");
        writer.WriteLine("static_assert(sizeof(float) == 4 && FLT_RADIX == 2 && FLT_MANT_DIG == 24, \"C~ requires IEEE-754 binary32 float\");");
        writer.WriteLine("static_assert(INT32_MIN == (-2147483647 - 1), \"C~ requires two's-complement int32_t\");");
        writer.WriteLine();
        writer.WriteLine("typedef struct ct_vtable ct_vtable;");
        writer.WriteLine("typedef struct ct_type_descriptor ct_type_descriptor;");
        writer.WriteLine("typedef struct ct_object { const ct_type_descriptor* Type; uint32_t IdentityHash; } ct_object;");
        writer.WriteLine("struct ct_type_descriptor { const char* Name; const ct_type_descriptor* Base; const ct_vtable* VTable; uint32_t TypeId; size_t Size; size_t Alignment; bool IsValue; };");
        writer.WriteLine("static ct_type_descriptor ct_desc_string;");
        writer.WriteLine("typedef struct ct_string { ct_object Object; int32_t Length; const uint8_t* Data; } ct_string;");
        writer.WriteLine("static const uint8_t ct_empty_bytes[1] = { 0 };");
        writer.WriteLine("static ct_string ct_empty_string = { { &ct_desc_string, 0 }, 0, ct_empty_bytes };");
        writer.WriteLine();
        writer.WriteLine("static void ct_fail(const char* code, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    (void)fprintf(stderr, \"C~ runtime error %s at %s:%d\\n\", code, file, line);");
        writer.WriteLine("    exit(EXIT_FAILURE);");
        writer.WriteLine("}");
        writer.WriteLine("static void* ct_require_nonnull(void* value, const char* file, int line) { if (value == NULL) ct_fail(\"CTN0001\", file, line); return value; }");
        writer.WriteLine("static void* ct_alloc(size_t size, const char* file, int line) { void* value = calloc(1u, size == 0u ? 1u : size); if (value == NULL) ct_fail(\"CTM0001\", file, line); return value; }");
        writer.WriteLine("static uint32_t ct_next_identity = 1u;");
        writer.WriteLine("static void ct_init_object(void* value, const ct_type_descriptor* type) { ct_object* object = (ct_object*)value; object->Type = type; object->IdentityHash = ct_next_identity++; if (ct_next_identity == 0u) ct_next_identity = 1u; }");
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
        writer.WriteLine("static ct_string* ct_to_string_float(float value, const char* file, int line) { char buffer[32]; int length = snprintf(buffer, sizeof(buffer), \"%.9g\", (double)value); return ct_string_from_format(buffer, length, sizeof(buffer), file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_bool(bool value, const char* file, int line) { const char* text = value ? \"True\" : \"False\"; return ct_string_from_bytes((const uint8_t*)text, value ? 4 : 5, file, line); }");
        writer.WriteLine("static ct_string* ct_to_string_char(uint8_t value, const char* file, int line) { return ct_string_from_bytes(&value, 1, file, line); }");
        writer.WriteLine("void ct_write_string(ct_string* value) { if (value != NULL && value->Length > 0) (void)fwrite(value->Data, 1u, (size_t)value->Length, stdout); }");
        writer.WriteLine("void ct_write_char(uint8_t value) { (void)fputc((int)value, stdout); }");
        writer.WriteLine("void ct_write_int(int32_t value) { (void)fprintf(stdout, \"%\" PRId32, value); }");
        writer.WriteLine("void ct_write_uint(uint32_t value) { (void)fprintf(stdout, \"%\" PRIu32, value); }");
        writer.WriteLine("void ct_write_float(float value) { (void)fprintf(stdout, \"%.9g\", (double)value); }");
        writer.WriteLine("void ct_write_bool(bool value) { (void)fputs(value ? \"True\" : \"False\", stdout); }");
        writer.WriteLine("void ct_write_line(void) { (void)fputc('\\n', stdout); }");
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
            writer.WriteLine($"static ct_string ct_sl_{pair.Value} = {{ {{ &ct_desc_string, 0 }}, {bytes.Length}, ct_slb_{pair.Value} }};");
        }
        if (_stringLiterals.Count > 0)
            writer.WriteLine();
    }

    private void EmitForwardDeclarations(CWriter writer)
    {
        foreach (var type in Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
            writer.WriteLine($"typedef struct {NameMangler.Type(type)} {NameMangler.Type(type)};");
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
            writer.WriteLine($"typedef struct {NameMangler.Array(array.ElementType!)} {NameMangler.Array(array.ElementType!)};");
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class))
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
                writer.WriteLine($"#define {NameMangler.Identifier(type.FullName + "." + value.Name)} (({NameMangler.Type(type)}){value.Value.ToString(CultureInfo.InvariantCulture)})");
            writer.WriteLine();
        }
        foreach (var type in OrderLayoutTypes())
        {
            writer.WriteLine($"struct {NameMangler.Type(type)}");
            writer.WriteLine("{");
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
                writer.WriteLine($"    {CTypeName(field.Type)} {field.CName};");
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

    private void EmitBoxLayouts(CWriter writer)
    {
        foreach (var type in BoxedTypes)
            writer.WriteLine($"typedef struct {BoxName(type)} {{ ct_object Object; {CTypeName(type)} Value; }} {BoxName(type)};");
        if (_boxedTypes.Count > 0)
            writer.WriteLine();
    }

    private void EmitGlobals(CWriter writer)
    {
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Name != "<underlying>"))
        {
            writer.WriteLine($"static {CTypeName(field.Type)} {field.CName} = {DefaultValue(field.Type)};");
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
                        .Concat(constructor.Parameters.Select(parameter => $"{CTypeName(parameter.Type)} {NameMangler.Identifier(parameter.Name)}"));
                    writer.WriteLine($"static void {ConstructorInitializerName(constructor)}({string.Join(", ", parameters)});");
                }
            }
            foreach (var method in type.Methods)
            {
                if (method.ExternName is not null && !emittedExternalSymbols.Add(method.ExternName))
                    continue;
                writer.WriteLine(MethodSignature(method, prototype: true));
            }
            foreach (var property in type.Properties)
            {
                var self = property.IsStatic ? string.Empty : $"{NameMangler.Type(type)}* ct_self";
                if (property.Getter is not null)
                    writer.WriteLine($"static {CTypeName(property.Type)} {NameMangler.Getter(property)}({(self.Length == 0 ? "void" : self)});");
                if (property.Setter is not null)
                    writer.WriteLine($"static void {NameMangler.Setter(property)}({(self.Length == 0 ? string.Empty : self + ", ")}{CTypeName(property.Type)} {NameMangler.Identifier("value")});");
            }
        }
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
        writer.WriteLine("static ct_string* ct_string_v_to_string(ct_object* value) { return (ct_string*)(void*)value; }");
        writer.WriteLine("static bool ct_string_v_equals(ct_object* left, ct_object* right) { return right != NULL && right->Type == &ct_desc_string && ct_string_equal((ct_string*)(void*)left, (ct_string*)(void*)right); }");
        writer.WriteLine("static int32_t ct_string_v_hash(ct_object* value) { ct_string* text = (ct_string*)(void*)value; return ct_i32_bits(ct_hash_bytes(text->Data, (size_t)text->Length)); }");
        EmitSpecialVTable(writer, "ct_string_vtable", "ct_string_v_to_string", "ct_string_v_equals", "ct_string_v_hash", virtualMethods, virtualProperties);
        writer.WriteLine("static ct_type_descriptor ct_desc_string = { \"string\", &" + DescriptorName(Model.Types["System.Object"]) + ", &ct_string_vtable, 1u, sizeof(ct_string), _Alignof(ct_string), false };");
        uint id = 2;
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            EmitClassVTable(writer, type, virtualMethods, virtualProperties);
            var baseDescriptor = type.BaseType is null ? "NULL" : $"&{DescriptorName(type.BaseType)}";
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)} = {{ \"{EscapeCString(type.FullName)}\", {baseDescriptor}, &ct_vtable_{NameMangler.Identifier(type.FullName)}, {id++}u, sizeof({NameMangler.Type(type)}), _Alignof({NameMangler.Type(type)}), false }};");
        }
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var name = NameMangler.Array(array.ElementType!);
            writer.WriteLine($"static ct_type_descriptor {ArrayDescriptorName(array.ElementType!)} = {{ \"{EscapeCString(array.ElementType!.DisplayName)}[]\", &{DescriptorName(Model.Types["System.Object"])}, &ct_default_vtable, {id++}u, sizeof({name}), _Alignof({name}), false }};");
        }
        foreach (var type in BoxedTypes)
        {
            EmitBoxMetadata(writer, type, virtualMethods, virtualProperties);
            writer.WriteLine($"static ct_type_descriptor {BoxDescriptorName(type)} = {{ \"{EscapeCString(type.DisplayName)}\", &{DescriptorName(Model.Types["System.Object"])}, &ct_vtable_box_{NameMangler.TypeCode(type)}, {id++}u, sizeof({BoxName(type)}), _Alignof({BoxName(type)}), true }};");
        }
        writer.WriteLine("static ct_string* ct_object_default_to_string(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_string_from_bytes((const uint8_t*)value->Type->Name, (int32_t)strlen(value->Type->Name), \"<runtime>\", 0); }");
        writer.WriteLine("static bool ct_object_default_equals(ct_object* left, ct_object* right) { return left == right; }");
        writer.WriteLine("static int32_t ct_object_default_hash(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_i32_bits(value->IdentityHash); }");
        writer.WriteLine("static bool ct_object_value_equals(ct_object* left, ct_object* right) { if (left == right) return true; if (left == NULL || right == NULL) return false; return left->Type->VTable->Equals(left, right); }");
        writer.WriteLine("static uint32_t ct_object_value_hash(ct_object* value) { return value == NULL ? 0u : (uint32_t)value->Type->VTable->GetHashCode(value); }");
        var objectType = Model.Types.GetValueOrDefault("System.Object");
        var objectCType = objectType is null ? "ct_object" : NameMangler.Type(objectType);
        writer.WriteLine($"ct_string* ct_object_to_string({objectCType}* value) {{ return value == NULL ? NULL : ((ct_object*)(void*)value)->Type->VTable->ToString((ct_object*)(void*)value); }}");
        writer.WriteLine($"int32_t ct_object_hash({objectCType}* value) {{ return ((ct_object*)(void*)ct_require_nonnull(value, \"<runtime>\", 0))->Type->VTable->GetHashCode((ct_object*)(void*)value); }}");
        writer.WriteLine($"bool ct_object_reference_equals({objectCType}* left, {objectCType}* right) {{ return left == right; }}");
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
                writer.WriteLine($"    if (value == ({CTypeName(type)}){enumValue.Value.ToString(CultureInfo.InvariantCulture)}) return ct_string_from_bytes((const uint8_t*)\"{escaped}\", {Encoding.UTF8.GetByteCount(enumValue.Name)}, \"<runtime>\", 0);");
            }
            var fallback = underlying.Kind is CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint
                ? "ct_to_string_uint((uint32_t)value, \"<runtime>\", 0)"
                : "ct_to_string_int((int32_t)value, \"<runtime>\", 0)";
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
        writer.WriteLine($"static {NameMangler.Type(Model.Types["System.Object"])}* {BoxFunctionName(type)}({CTypeName(type)} value, const char* file, int line) {{ {box}* box = ({box}*)ct_alloc(sizeof({box}), file, line); ct_init_object(box, &{descriptor}); box->Value = value; return ({NameMangler.Type(Model.Types["System.Object"])}*)(void*)box; }}");
        writer.WriteLine($"static {CTypeName(type)} {UnboxFunctionName(type)}({NameMangler.Type(Model.Types["System.Object"])}* value, const char* file, int line) {{ if (value == NULL) ct_fail(\"CTO0002\", file, line); ct_object* object = (ct_object*)(void*)value; if (object->Type != &{descriptor}) ct_fail(\"CTO0003\", file, line); return (({box}*)(void*)value)->Value; }}");
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
        writer.WriteLine("int main(void)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_keep_symbols();");
        writer.WriteLine("    ct_module_init();");
        if (Model.EntryPoint is not null)
            writer.WriteLine($"    {Model.EntryPoint.CName}();");
        writer.WriteLine("    return EXIT_SUCCESS;");
        writer.WriteLine("}");
    }

    private void EmitKeepSymbols(CWriter writer)
    {
        writer.WriteLine("static void ct_keep_symbols(void)");
        writer.WriteLine("{");
        var runtime = new[]
        {
            "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_init_object", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint",
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash", "ct_object_to_string", "ct_object_hash", "ct_object_reference_equals",
            "ct_type_is_assignable", "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_object_value_equals", "ct_object_value_hash",
        };
        foreach (var name in runtime)
            writer.WriteLine($"    (void)&{name};");
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
