namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitSectionSupport(CWriter writer)
    {
        var hasUsed = _reachableMethods.Any(method => method.IsUsed) ||
            EmittedTypes.SelectMany(type => type.Fields).Any(field => field.IsUsed);
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("#define CT_USED");
        writer.WriteLine($"#elif (defined(__GNUC__) || defined(__clang__)) && defined(__ELF__)");
        writer.WriteLine("#define CT_USED __attribute__((used, retain))");
        writer.WriteLine("#elif defined(__GNUC__) || defined(__clang__)");
        writer.WriteLine(hasUsed
            ? "#error CT4111: [Used] final-image retention requires an ELF GNU-compatible toolchain"
            : "#define CT_USED");
        writer.WriteLine("#else");
        writer.WriteLine(hasUsed
            ? "#error CT4111: [Used] final-image retention is unsupported by this toolchain"
            : "#define CT_USED");
        writer.WriteLine("#endif");
        writer.WriteLine();
        if (_reachableMethods.Any(method => method.IsOverlay))
        {
            writer.WriteLine("#if defined(__GNUC__) || defined(__clang__)");
            writer.WriteLine("#define CT_OVERLAY_BODY(name) __attribute__((section(\".ctilde.overlay.\" name \".text\"), used, noinline))");
            writer.WriteLine("#else");
            writer.WriteLine("#define CT_OVERLAY_BODY(name)");
            writer.WriteLine("#endif");
            writer.WriteLine();
        }
        var sections = _reachableMethods.Where(method => method.SectionName is not null)
            .Select(method => (Name: method.SectionName!, Kind: NativeSectionKind.Code))
            .Concat(EmittedTypes.SelectMany(type => type.Fields)
                .Where(field => field.IsStatic && field.Name != "<underlying>" && field.SectionName is not null)
                .Select(field => (Name: field.SectionName!, Kind: field.IsConstInit ? NativeSectionKind.ReadOnlyData : NativeSectionKind.Data)))
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
