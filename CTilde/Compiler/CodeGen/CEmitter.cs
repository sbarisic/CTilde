using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CTilde;

internal sealed class CEmitter
{
    private readonly Dictionary<string, int> _stringLiterals = new(StringComparer.Ordinal);
    private readonly HashSet<CType> _arrayTypes = [];

    public CEmitter(CompilationModel model)
    {
        Model = model;
        Diagnostics = model.Diagnostics;
    }

    public CompilationModel Model { get; }
    public DiagnosticBag Diagnostics { get; }

    public string Emit()
    {
        RegisterDeclaredTypes();
        var definitions = new List<string>();
        foreach (var type in Model.UserTypes)
        {
            if (type.Kind == DeclaredTypeKind.Enum)
                continue;
            foreach (var constructor in type.Constructors)
                definitions.Add(new MethodLowerer(this, constructor).EmitDefinition());
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                definitions.Add(new MethodLowerer(this, method).EmitDefinition());
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                    definitions.Add(EmitAccessor(property, true));
                if (property.Setter is not null)
                    definitions.Add(EmitAccessor(property, false));
            }
        }

        var moduleInitializer = EmitModuleInitializer();
        var writer = new CWriter();
        EmitPreamble(writer);
        EmitStringLiterals(writer);
        EmitForwardDeclarations(writer);
        EmitTypeLayouts(writer);
        EmitArrayLayouts(writer);
        EmitGlobals(writer);
        EmitPrototypes(writer);
        writer.WriteLine();
        foreach (var definition in definitions)
        {
            writer.WriteBlock(definition.TrimEnd().Split('\n'));
            writer.WriteLine();
        }
        writer.WriteBlock(moduleInitializer.TrimEnd().Split('\n'));
        writer.WriteLine();
        EmitKeepSymbols(writer);
        writer.WriteLine();
        EmitMain(writer);
        return writer.ToString();
    }

    public string CTypeName(CType type) => type.Kind switch
    {
        CTypeKind.Void => "void", CTypeKind.Bool => "bool", CTypeKind.Byte or CTypeKind.Char => "uint8_t",
        CTypeKind.Sbyte => "int8_t", CTypeKind.Short => "int16_t", CTypeKind.Ushort => "uint16_t",
        CTypeKind.Int => "int32_t", CTypeKind.Uint => "uint32_t", CTypeKind.Float => "float",
        CTypeKind.String => "ct_string*", CTypeKind.Class => $"{NameMangler.Type(type.Symbol!)}*",
        CTypeKind.Struct or CTypeKind.Enum => NameMangler.Type(type.Symbol!), CTypeKind.Array => $"{NameMangler.Array(type.ElementType!)}*",
        CTypeKind.Pointer => $"{CTypeName(type.ElementType!)}*", CTypeKind.Null => "void*", _ => "int32_t",
    };

    public string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.Bool => "false", CTypeKind.Float => "0.0f",
        CTypeKind.String or CTypeKind.Class or CTypeKind.Array or CTypeKind.Pointer or CTypeKind.Null => "NULL",
        CTypeKind.Struct => $"({CTypeName(type)}){{0}}", _ => "0",
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

    private string EmitAccessor(PropertySymbol property, bool getter)
    {
        var syntax = getter ? property.Getter! : property.Setter!;
        var parameters = getter ? ImmutableArray<ParameterSymbol>.Empty : [new ParameterSymbol { Name = "value", Type = property.Type, Syntax = null }];
        var method = new MethodSymbol
        {
            Name = getter ? $"get_{property.Name}" : $"set_{property.Name}", ContainingType = property.ContainingType,
            Accessibility = property.Accessibility, IsStatic = property.IsStatic, Syntax = syntax,
            ReturnType = getter ? property.Type : CType.Void, Parameters = parameters, Body = syntax.Body,
        };
        var name = getter ? NameMangler.Getter(property) : NameMangler.Setter(property);
        return new MethodLowerer(this, method, name, property, getter).EmitDefinition();
    }

    private void RegisterDeclaredTypes()
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
        writer.WriteLine("/* Generated by C~ draft 0.3 for GNU C23. Do not edit. */");
        writer.WriteLine("#include <stdbool.h>");
        writer.WriteLine("#include <stddef.h>");
        writer.WriteLine("#include <stdint.h>");
        writer.WriteLine("#include <inttypes.h>");
        writer.WriteLine("#include <stdio.h>");
        writer.WriteLine("#include <stdlib.h>");
        writer.WriteLine("#include <string.h>");
        writer.WriteLine("#include <limits.h>");
        writer.WriteLine("#include <float.h>");
        writer.WriteLine();
        writer.WriteLine("static_assert(CHAR_BIT == 8, \"C~ requires 8-bit bytes\");");
        writer.WriteLine("static_assert(sizeof(int32_t) == 4 && sizeof(uint32_t) == 4, \"C~ requires exact 32-bit integers\");");
        writer.WriteLine("static_assert(sizeof(float) == 4 && FLT_RADIX == 2 && FLT_MANT_DIG == 24, \"C~ requires IEEE-754 binary32 float\");");
        writer.WriteLine("static_assert(INT32_MIN == (-2147483647 - 1), \"C~ requires two's-complement int32_t\");");
        writer.WriteLine();
        writer.WriteLine("typedef struct ct_string { int32_t Length; const uint8_t* Data; } ct_string;");
        writer.WriteLine("static const uint8_t ct_empty_bytes[1] = { 0 };");
        writer.WriteLine("static ct_string ct_empty_string = { 0, ct_empty_bytes };");
        writer.WriteLine();
        writer.WriteLine("static void ct_fail(const char* code, const char* file, int line)");
        writer.WriteLine("{");
        writer.WriteLine("    (void)fprintf(stderr, \"C~ runtime error %s at %s:%d\\n\", code, file, line);");
        writer.WriteLine("    exit(EXIT_FAILURE);");
        writer.WriteLine("}");
        writer.WriteLine("static void* ct_require_nonnull(void* value, const char* file, int line) { if (value == NULL) ct_fail(\"CTN0001\", file, line); return value; }");
        writer.WriteLine("static void* ct_alloc(size_t size, const char* file, int line) { void* value = calloc(1u, size == 0u ? 1u : size); if (value == NULL) ct_fail(\"CTM0001\", file, line); return value; }");
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
            writer.WriteLine($"static ct_string ct_sl_{pair.Value} = {{ {bytes.Length}, ct_slb_{pair.Value} }};");
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
            if (fields.Length == 0)
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
            {
                Diagnostics.Add("CT4100", $"Type '{type.FullName}' has a recursive value-type layout.", type.Syntax!.Source, type.Syntax.Span);
                yield break;
            }
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
            writer.WriteLine($"struct {name} {{ int32_t Length; {CTypeName(array.ElementType!)}* Data; }};");
            writer.WriteLine($"static {name}* ct_new_{name}(int32_t length, const char* file, int line) {{ {name}* value = ({name}*)ct_alloc(sizeof({name}), file, line); value->Length = length; value->Data = ({CTypeName(array.ElementType!)}*)ct_alloc_array(length, sizeof({CTypeName(array.ElementType!)}), file, line); return value; }}");
        }
        if (_arrayTypes.Count > 0)
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
        foreach (var type in Model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
        {
            foreach (var constructor in type.Constructors)
                writer.WriteLine(MethodSignature(constructor, prototype: true));
            foreach (var method in type.Methods)
                writer.WriteLine(MethodSignature(method, prototype: true));
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

    private string EmitModuleInitializer()
    {
        var writer = new CWriter();
        writer.WriteLine("static void ct_module_init(void)");
        writer.WriteLine("{");
        var initializerIndex = 0;
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Initializer is not null && field.Name != "<underlying>"))
        {
            var method = new MethodSymbol
            {
                Name = "<module_init>", ContainingType = field.ContainingType, Accessibility = Accessibility.Private,
                IsStatic = true, Syntax = field.Syntax, ReturnType = CType.Void, Parameters = [], Body = null,
            };
            var lowerer = new MethodLowerer(this, method, temporaryPrefix: $"_mi_{initializerIndex++}");
            var expression = lowerer.LowerStandalone(field.Initializer!);
            foreach (var line in expression.Prelude)
                writer.WriteLine("    " + line);
            var value = lowerer.ConvertStandalone(expression, field.Type, field.Initializer!);
            if (field.IsConst && !value.IsConstant)
                Diagnostics.Add("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer!.Source, field.Initializer.Span);
            foreach (var line in value.Prelude.Skip(expression.Prelude.Count))
                writer.WriteLine("    " + line);
            writer.WriteLine($"    {field.CName} = {value.Code};");
        }
        writer.WriteLine("}");
        return writer.ToString();
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
            "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint",
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
        };
        foreach (var name in runtime)
            writer.WriteLine($"    (void)&{name};");
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
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Name != "<underlying>"))
            writer.WriteLine($"    (void)&{field.CName};");
        writer.WriteLine("}");
    }
}
