namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitExports(CWriter writer)
    {
        foreach (var method in Model.UserTypes.SelectMany(type => type.Methods)
                     .Where(method => method.ExportName is not null)
                     .OrderBy(method => method.ExportName, StringComparer.Ordinal))
        {
            var declarations = method.Parameters
                .SelectMany(parameter => ExportParameterDeclarations(parameter, NameMangler.Identifier(parameter.Name)))
                .ToArray();
            writer.WriteLine(SectionAnnotation(NativeSectionKind.Code, method.SectionName) + CFunctionDeclaration(method.ReturnType, method.ExportName!, declarations));
            writer.WriteLine("{");
            writer.WriteLine("    ct_runtime_require_ready();");
            writer.WriteLine("    jmp_buf ct_export_target;");
            writer.WriteLine("    ct_exception_frame ct_export_frame = { &ct_export_target, ct_exception_top, ct_cleanup_top };");
            writer.WriteLine("    if (setjmp(ct_export_target) != 0)");
            writer.WriteLine("    {");
            writer.WriteLine("        ct_object* ct_export_exception = ct_current_exception;");
            writer.WriteLine("        ct_current_exception = NULL;");
            writer.WriteLine("        ct_exception_top = ct_export_frame.Previous;");
            writer.WriteLine("        ct_release(ct_export_exception);");
            writer.WriteLine("        ct_fail(\"CTE0003\", \"<native-export>\", 0);");
            writer.WriteLine("    }");
            writer.WriteLine("    ct_exception_top = &ct_export_frame;");
            var arguments = new List<string>();
            foreach (var parameter in method.Parameters)
            {
                var name = NameMangler.Identifier(parameter.Name);
                if (parameter.Type.IsNativeBuffer)
                {
                    arguments.Add(name + "_data");
                    arguments.Add(name + "_length");
                }
                else if (parameter.Type.IsNativeUtf8String)
                {
                    var local = name + "_view";
                    writer.WriteLine($"    ct_native_utf8_string {local} = {{ NULL, (const uint8_t*)(const void*){name}, {name} == NULL ? 0u : strlen({name}) }};");
                    arguments.Add(local);
                }
                else
                    arguments.Add(name);
            }
            var call = $"{method.CName}({string.Join(", ", arguments)})";
            if (method.ReturnType == CType.Void)
            {
                writer.WriteLine($"    {call};");
                writer.WriteLine("    ct_exception_top = ct_export_frame.Previous;");
                writer.WriteLine("    return;");
            }
            else
            {
                writer.WriteLine($"    {CDeclaration(method.ReturnType, "ct_export_result")} = {call};");
                writer.WriteLine("    ct_exception_top = ct_export_frame.Previous;");
                writer.WriteLine("    return ct_export_result;");
            }
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    private IEnumerable<string> ExportParameterDeclarations(ParameterSymbol parameter, string name)
    {
        if (parameter.Type.IsNativeBuffer)
            return
            [
                $"{(parameter.Type.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.Type.ElementType!)}* {name}_data",
                $"size_t {name}_length",
            ];
        if (parameter.Type.IsNativeUtf8String)
            return [$"const char* {name}"];
        return [CParameterDeclaration(parameter, name)];
    }
}
