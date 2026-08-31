namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitNativeImportSupport(CWriter writer)
    {
        if (!HasNativeImports)
            return;

        var imports = _nativeImportUses
            .OrderBy(item => item.Value.Method.NativeImportLibrary, StringComparer.Ordinal)
            .ThenBy(item => item.Value.Method.NativeImportSymbol, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new NativeImportEmission(item.Key, item.Value.Method))
            .ToArray();
        var libraries = imports
            .GroupBy(import => import.Method.NativeImportLibrary!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select((group, index) => new NativeImportLibraryEmission(group.Key, index, group.ToArray()))
            .ToArray();

        foreach (var import in imports)
            writer.WriteLine($"static {NativeImportSlotDeclaration(import.Method, import.SlotName)} = NULL;");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine($"static HMODULE ct_native_import_libraries[{libraries.Length}] = {{0}};");
        writer.WriteLine("#elif defined(__linux__)");
        writer.WriteLine($"static void* ct_native_import_libraries[{libraries.Length}] = {{0}};");
        writer.WriteLine("#endif");
        writer.WriteLine();

        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("CT_NORETURN static void ct_native_import_fail(const char* code, const char* logical, const char* mapped, const char* symbol, const char* file, int line, DWORD native_code)");
        writer.WriteLine("{");
        writer.WriteLine("    char native_text[512] = {0};");
        writer.WriteLine("    (void)FormatMessageA(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, native_code, 0u, native_text, (DWORD)sizeof(native_text), NULL);");
        writer.WriteLine("    (void)fprintf(stderr, \"%s: native import failed: library='%s', mapped='%s', symbol='%s', declaration=%s:%d, native=%lu: %s\\n\", code, logical, mapped, symbol == NULL ? \"<library>\" : symbol, file, line, (unsigned long)native_code, native_text);");
        writer.WriteLine("    ct_fail(code, file, line);");
        writer.WriteLine("}");
        writer.WriteLine("#elif defined(__linux__)");
        writer.WriteLine("CT_NORETURN static void ct_native_import_fail(const char* code, const char* logical, const char* mapped, const char* symbol, const char* file, int line, const char* native_text)");
        writer.WriteLine("{");
        writer.WriteLine("    (void)fprintf(stderr, \"%s: native import failed: library='%s', mapped='%s', symbol='%s', declaration=%s:%d, native=%s\\n\", code, logical, mapped, symbol == NULL ? \"<library>\" : symbol, file, line, native_text == NULL ? \"<no loader error>\" : native_text);");
        writer.WriteLine("    ct_fail(code, file, line);");
        writer.WriteLine("}");
        writer.WriteLine("#endif");
        writer.WriteLine();

        writer.WriteLine("static void ct_native_imports_init(void)");
        writer.WriteLine("{");
        foreach (var library in libraries)
        {
            var first = library.Imports[0].Method;
            var source = first.Syntax!;
            var path = EscapeCString(source.Source.FilePath.Replace('\\', '/'));
            var line = source.Source.GetLocation(source.Span).Line;
            var logical = EscapeCString(library.LogicalName);
            var windowsName = EscapeCString(library.LogicalName + ".dll");
            var linuxName = EscapeCString("lib" + library.LogicalName + ".so");
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine($"    ct_native_import_libraries[{library.Index}] = LoadLibraryExW(L\"{windowsName}\", NULL, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);");
            writer.WriteLine($"    if (ct_native_import_libraries[{library.Index}] == NULL) ct_native_import_fail(\"CTI0001\", \"{logical}\", \"{windowsName}\", NULL, \"{path}\", {line}, GetLastError());");
            foreach (var import in library.Imports)
            {
                var symbol = EscapeCString(import.Method.NativeImportSymbol!);
                writer.WriteLine($"    FARPROC ct_native_address_{library.Index}_{import.SlotName} = GetProcAddress(ct_native_import_libraries[{library.Index}], \"{symbol}\");");
                writer.WriteLine($"    if (ct_native_address_{library.Index}_{import.SlotName} == NULL) ct_native_import_fail(\"CTI0002\", \"{logical}\", \"{windowsName}\", \"{symbol}\", \"{EscapeCString(import.Method.Syntax!.Source.FilePath.Replace('\\', '/'))}\", {import.Method.Syntax.Source.GetLocation(import.Method.Syntax.Span).Line}, GetLastError());");
                writer.WriteLine($"    static_assert(sizeof({import.SlotName}) == sizeof(ct_native_address_{library.Index}_{import.SlotName}), \"C~ native function pointers must match the platform loader address size\");");
                writer.WriteLine($"    (void)memcpy(&{import.SlotName}, &ct_native_address_{library.Index}_{import.SlotName}, sizeof({import.SlotName}));");
            }
            writer.WriteLine("#elif defined(__linux__)");
            writer.WriteLine("    (void)dlerror();");
            writer.WriteLine($"    ct_native_import_libraries[{library.Index}] = dlopen(\"{linuxName}\", RTLD_NOW | RTLD_LOCAL);");
            writer.WriteLine($"    if (ct_native_import_libraries[{library.Index}] == NULL) ct_native_import_fail(\"CTI0001\", \"{logical}\", \"{linuxName}\", NULL, \"{path}\", {line}, dlerror());");
            foreach (var import in library.Imports)
            {
                var symbol = EscapeCString(import.Method.NativeImportSymbol!);
                writer.WriteLine("    (void)dlerror();");
                writer.WriteLine($"    void* ct_native_address_{library.Index}_{import.SlotName} = dlsym(ct_native_import_libraries[{library.Index}], \"{symbol}\");");
                writer.WriteLine($"    const char* ct_native_error_{library.Index}_{import.SlotName} = dlerror();");
                writer.WriteLine($"    if (ct_native_error_{library.Index}_{import.SlotName} != NULL) ct_native_import_fail(\"CTI0002\", \"{logical}\", \"{linuxName}\", \"{symbol}\", \"{EscapeCString(import.Method.Syntax!.Source.FilePath.Replace('\\', '/'))}\", {import.Method.Syntax.Source.GetLocation(import.Method.Syntax.Span).Line}, ct_native_error_{library.Index}_{import.SlotName});");
                writer.WriteLine($"    static_assert(sizeof({import.SlotName}) == sizeof(ct_native_address_{library.Index}_{import.SlotName}), \"C~ native function pointers must match the platform loader address size\");");
                writer.WriteLine($"    (void)memcpy(&{import.SlotName}, &ct_native_address_{library.Index}_{import.SlotName}, sizeof({import.SlotName}));");
            }
            writer.WriteLine("#endif");
        }
        writer.WriteLine("}");
        writer.WriteLine();

        writer.WriteLine("static void ct_native_imports_fini(void)");
        writer.WriteLine("{");
        foreach (var library in libraries.Reverse())
        {
            var first = library.Imports[0].Method;
            var source = first.Syntax!;
            var path = EscapeCString(source.Source.FilePath.Replace('\\', '/'));
            var line = source.Source.GetLocation(source.Span).Line;
            var logical = EscapeCString(library.LogicalName);
            var windowsName = EscapeCString(library.LogicalName + ".dll");
            var linuxName = EscapeCString("lib" + library.LogicalName + ".so");
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine($"    if (ct_native_import_libraries[{library.Index}] != NULL && !FreeLibrary(ct_native_import_libraries[{library.Index}])) ct_native_import_fail(\"CTI0003\", \"{logical}\", \"{windowsName}\", NULL, \"{path}\", {line}, GetLastError());");
            writer.WriteLine($"    ct_native_import_libraries[{library.Index}] = NULL;");
            writer.WriteLine("#elif defined(__linux__)");
            writer.WriteLine("    (void)dlerror();");
            writer.WriteLine($"    if (ct_native_import_libraries[{library.Index}] != NULL && dlclose(ct_native_import_libraries[{library.Index}]) != 0) ct_native_import_fail(\"CTI0003\", \"{logical}\", \"{linuxName}\", NULL, \"{path}\", {line}, dlerror());");
            writer.WriteLine($"    ct_native_import_libraries[{library.Index}] = NULL;");
            writer.WriteLine("#endif");
        }
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private string NativeImportSlotDeclaration(MethodSymbol method, string slotName)
    {
        var parameters = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            var parameterName = NameMangler.Identifier(parameter.Name);
            parameters.Add(CParameterDeclaration(parameter, parameterName));
            if (parameter.IsSynchronousCallback)
                parameters.Add($"void* {parameterName}_context");
        }
        return CFunctionDeclaration(method.ReturnType, $"(*{slotName})", parameters);
    }

    private sealed record NativeImportEmission(string SlotName, MethodSymbol Method);
    private sealed record NativeImportLibraryEmission(string LogicalName, int Index, NativeImportEmission[] Imports);
}
