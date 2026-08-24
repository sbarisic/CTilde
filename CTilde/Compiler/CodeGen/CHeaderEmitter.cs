using System.Security.Cryptography;
using System.Text;

namespace CTilde;

internal sealed class CHeaderEmitter(BoundProgram program)
{
    private CompilationModel Model => program.Model;

    public string Emit()
    {
        var exports = Model.UserTypes.SelectMany(type => type.Methods)
            .Where(method => method.ExportName is not null)
            .OrderBy(method => method.ExportName, StringComparer.Ordinal)
            .ToArray();
        var externFields = Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.ExternName is not null && field.Accessibility == Accessibility.Public)
            .OrderBy(field => field.ExternName, StringComparer.Ordinal).ToArray();
        var signatureText = $"draft-{CompilerContract.DraftVersion}\n" + string.Join("\n", exports.Select(method => method.ExportName + ":" + NameMangler.Method(method) + ":" + method.SectionName + ":" + method.TaskStackSize)) +
            "\n" + string.Join("\n", externFields.Select(field => field.ExternName + ":" + NameMangler.CanonicalType(field.Type) + ":" + field.IsReadonly + ":" + field.IsNativeVolatile));
        var guard = "CTILDE_EXPORTS_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureText)))[..16] + "_H";
        var writer = new StringBuilder();
        writer.Append("#ifndef ").Append(guard).Append('\n');
        writer.Append("#define ").Append(guard).Append("\n\n");
        writer.Append("#include <stdbool.h>\n#include <stddef.h>\n#include <stdint.h>\n");
        foreach (var header in ExportTypes(exports).Where(type => type.Kind == CTypeKind.Opaque).Select(type => type.Symbol!.NativeHeader!).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            writer.Append("#include <").Append(header).Append(">\n");
        if (ExportTypes(exports).Any(type => type.Kind == CTypeKind.EspError))
            writer.Append("#include <esp_err.h>\n");
        writer.Append("#if defined(__cplusplus)\n#define CT_ALIGNOF(type) alignof(type)\n#elif defined(_MSC_VER)\n#define CT_ALIGNOF(type) __alignof(type)\n#else\n#define CT_ALIGNOF(type) _Alignof(type)\n#endif\n");
        var codeSections = exports.Where(method => method.SectionName is not null).Select(method => method.SectionName!).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        foreach (var section in codeSections)
            foreach (var line in NativeSection.MacroDefinition(NativeSectionKind.Code, section))
                writer.Append(line).Append('\n');
        writer.Append("\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n");
        writer.Append("#define CTILDE_RUNTIME_ABI_VERSION UINT32_C(").Append(CompilerContract.RuntimeAbiVersion).Append(")\n\n");
        foreach (var method in exports.Where(method => method.TaskStackSize is not null))
            writer.Append("#define CTILDE_TASK_STACK_").Append(TaskMacroIdentifier(method.ExportName!)).Append(" UINT32_C(").Append(method.TaskStackSize!.Value).Append(")\n");
        if (exports.Any(method => method.TaskStackSize is not null))
            writer.Append('\n');
        writer.Append("typedef struct ct_object ct_object;\n");
        writer.Append("typedef struct ct_panic_info { const char* Code; const char* File; int32_t Line; } ct_panic_info;\n");
        writer.Append("typedef void (*ct_panic_handler)(const ct_panic_info* info, void* context);\n");
        writer.Append("typedef struct ct_runtime_config { uint32_t Size; ct_panic_handler PanicHandler; void* PanicContext; } ct_runtime_config;\n\n");
        writer.Append("void ct_runtime_initialize(const ct_runtime_config* config);\n");
        writer.Append("void ct_runtime_shutdown(void);\n");
        writer.Append("void ct_thread_attach(void);\n");
        writer.Append("void ct_thread_detach(void);\n");
        writer.Append("void ct_retain(ct_object* value);\n");
        writer.Append("void ct_release(ct_object* value);\n\n");
        writer.Append("#if defined(CTILDE_CONFORMANCE)\n");
        writer.Append("void ct_runtime_test_fail_allocation_after(int32_t successful_allocations);\n");
        writer.Append("#endif\n\n");

        foreach (var type in ExportTypes(exports).Where(type => type.Kind == CTypeKind.Enum).Select(type => type.Symbol!).Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var underlying = type.Fields.Single(field => field.Name == "<underlying>").Type;
            writer.Append("typedef ").Append(CTypeName(underlying)).Append(' ').Append(NameMangler.Type(type)).Append(";\n");
        }
        var exportedStructs = ExportTypes(exports).Where(type => type.Kind == CTypeKind.Struct).Select(type => type.Symbol!).Distinct().ToArray();
        foreach (var type in exportedStructs.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var tag = type.AggregateLayout == AggregateLayoutKind.Union ? "union" : "struct";
            writer.Append("typedef ").Append(tag).Append(' ').Append(NameMangler.Type(type)).Append(' ').Append(NameMangler.Type(type)).Append(";\n");
        }
        foreach (var type in OrderStructs(exportedStructs))
            AggregateLayout.EmitValueTypeDefinition(type, line => writer.Append(line).Append('\n'), (fieldType, name) => $"{CTypeName(fieldType)} {name}", includeTypedef: false);
        if (exports.Length != 0)
            writer.Append('\n');
        foreach (var method in exports)
        {
            writer.Append("/* C~: ").Append(method.ReturnType.DisplayName).Append(' ').Append(method.ContainingType.FullName).Append('.').Append(method.Name).Append(" */\n");
            var ownership = OwnershipComment(method);
            if (ownership.Length != 0)
                writer.Append("/* ownership: ").Append(ownership).Append(" */\n");
            if (method.SectionName is not null)
                writer.Append(NativeSection.MacroName(NativeSectionKind.Code, method.SectionName)).Append(' ');
            writer.Append(CTypeName(method.ReturnType)).Append(' ').Append(method.ExportName).Append('(');
            var parameters = method.Parameters.SelectMany(ParameterDeclarations).ToArray();
            writer.Append(parameters.Length == 0 ? "void" : string.Join(", ", parameters));
            writer.Append(");\n");
        }
        foreach (var field in externFields)
        {
            writer.Append("extern ");
            if (field.IsReadonly)
                writer.Append("const ");
            if (field.IsNativeVolatile)
                writer.Append("volatile ");
            writer.Append(CTypeName(field.Type)).Append(' ').Append(field.ExternName).Append(";\n");
        }
        writer.Append("\n#undef CT_ALIGNOF\n");
        foreach (var section in codeSections)
            writer.Append("#undef ").Append(NativeSection.MacroName(NativeSectionKind.Code, section)).Append('\n');
        writer.Append("#ifdef __cplusplus\n}\n#endif\n\n#endif\n");
        return writer.ToString();
    }

    private static string TaskMacroIdentifier(string name) => string.Concat(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_'));

    private static string OwnershipComment(MethodSymbol method)
    {
        var contracts = new List<string>();
        if (method.ReturnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
        {
            var ownership = method.ReturnsOwned ? "owned" : "borrowed";
            contracts.Add($"return={ownership}{(method.ReturnsNullable ? ", nullable" : string.Empty)}");
        }
        foreach (var parameter in method.Parameters.Where(parameter =>
                     parameter.Type.Kind is CTypeKind.Opaque or CTypeKind.Pointer ||
                     parameter.Type.IsNativeUtf8String || parameter.IsSynchronousCallback))
        {
            var ownership = parameter.NativeOwnership switch
            {
                NativeParameterOwnership.Consumes => "consumes",
                NativeParameterOwnership.Retained => "retained",
                NativeParameterOwnership.Creates => "creates",
                _ => "borrowed",
            };
            var qualifiers = new List<string>();
            if (parameter.IsNullable)
                qualifiers.Add("nullable");
            if (parameter.IsSynchronousCallback)
                qualifiers.Add("synchronous-callback");
            contracts.Add($"{parameter.Name}={ownership}{(qualifiers.Count == 0 ? string.Empty : ", " + string.Join(", ", qualifiers))}");
        }
        return string.Join("; ", contracts);
    }

    private static IEnumerable<CType> ExportTypes(IEnumerable<MethodSymbol> exports) => exports
        .SelectMany(method => method.Parameters.Select(parameter => parameter.Type).Append(method.ReturnType))
        .SelectMany(ExpandType)
        .Distinct();

    private static IEnumerable<CType> ExpandType(CType type)
    {
        yield return type;
        if (type.ElementType is not null)
            foreach (var nested in ExpandType(type.ElementType))
                yield return nested;
        if (type.Kind == CTypeKind.Struct)
            foreach (var field in type.Symbol!.Fields.Where(field => !field.IsStatic))
                foreach (var nested in ExpandType(field.Type))
                    yield return nested;
    }

    private static IEnumerable<string> ParameterDeclarations(ParameterSymbol parameter)
    {
        var name = NameMangler.Identifier(parameter.Name);
        if (parameter.Type.IsNativeBuffer)
        {
            yield return $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}* {name}_data";
            yield return $"size_t {name}_length";
            yield break;
        }
        if (parameter.Type.IsNativeUtf8String)
        {
            yield return $"const char* {name}";
            yield break;
        }
        yield return parameter.PassingKind switch
        {
            ParameterPassingKind.In => $"const {CTypeName(parameter.Type)}* {name}",
            ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter.Type)}* {name}",
            _ => $"{CTypeName(parameter.Type)} {name}",
        };
    }

    internal static string CTypeName(CType type) => type.Kind switch
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
        CTypeKind.Pointer => CTypeName(type.ElementType!) + "*",
        CTypeKind.Opaque => type.Symbol!.NativeTypeName!,
        CTypeKind.EspError => "esp_err_t",
        CTypeKind.Struct or CTypeKind.Enum => NameMangler.Type(type.Symbol!),
        _ => "void",
    };

    private static IEnumerable<TypeSymbol> OrderStructs(IEnumerable<TypeSymbol> source)
    {
        var available = source.ToHashSet();
        var emitted = new HashSet<TypeSymbol>();
        foreach (var type in available.OrderBy(type => type.FullName, StringComparer.Ordinal))
            foreach (var ordered in Visit(type))
                yield return ordered;

        IEnumerable<TypeSymbol> Visit(TypeSymbol type)
        {
            if (emitted.Contains(type))
                yield break;
            foreach (var dependency in type.Fields.Where(field => !field.IsStatic && field.Type.Kind == CTypeKind.Struct)
                         .Select(field => field.Type.Symbol!).Where(available.Contains).Distinct())
                foreach (var ordered in Visit(dependency))
                    yield return ordered;
            if (emitted.Add(type))
                yield return type;
        }
    }
}
