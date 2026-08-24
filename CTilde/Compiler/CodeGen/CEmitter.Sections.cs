namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitSectionSupport(CWriter writer)
    {
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("#define CT_USED");
        writer.WriteLine("#else");
        writer.WriteLine("#define CT_USED __attribute__((used))");
        writer.WriteLine("#endif");
        writer.WriteLine();
        var sections = _reachableMethods.Where(method => method.SectionName is not null)
            .Select(method => (Name: method.SectionName!, Kind: NativeSectionKind.Code))
            .Concat(EmittedTypes.SelectMany(type => type.Fields)
                .Where(field => field.IsStatic && field.Name != "<underlying>" && field.SectionName is not null)
                .Select(field => (Name: field.SectionName!, Kind: NativeSectionKind.Data)))
            .Distinct()
            .OrderBy(section => section.Name, StringComparer.Ordinal)
            .ThenBy(section => section.Kind)
            .ToArray();
        foreach (var section in sections)
        {
            foreach (var line in NativeSection.MacroDefinition(section.Kind, section.Name))
                writer.WriteLine(line);
        }
        if (sections.Length != 0)
            writer.WriteLine();
    }

    private static string SectionAnnotation(NativeSectionKind kind, string? name) =>
        name is null ? string.Empty : NativeSection.MacroName(kind, name) + " ";

    private static string UsedAnnotation(bool used) => used ? "CT_USED " : string.Empty;
}
