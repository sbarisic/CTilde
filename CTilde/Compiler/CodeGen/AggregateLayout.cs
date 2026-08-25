namespace CTilde;

internal static class AggregateLayout
{
    public static string StorageTypeName(TypeSymbol type) => $"{NameMangler.Type(type)}_layout";
    public static string SlotTypeName(FieldSymbol field) => $"{NameMangler.Type(field.ContainingType)}_slot_{field.CName}";
    public static string SlotName(FieldSymbol field) => $"ct_slot_{field.CName}";

    public static string OffsetExpression(TypeSymbol type, FieldSymbol field)
    {
        var typeName = NameMangler.Type(type);
        if (type.AggregateLayout != AggregateLayoutKind.Explicit)
            return $"offsetof({typeName}, {field.CName})";
        return $"(offsetof({typeName}, ct_layout) + offsetof({StorageTypeName(type)}, {SlotName(field)}) + offsetof({SlotTypeName(field)}, {field.CName}))";
    }

    public static void EmitValueTypeDefinition(
        TypeSymbol type,
        Action<string> writeLine,
        Func<CType, string, string> declaration,
        bool includeTypedef)
    {
        var name = NameMangler.Type(type);
        var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
        if (type.AggregateLayout == AggregateLayoutKind.Explicit)
        {
            foreach (var field in fields)
            {
                writeLine("#if defined(_MSC_VER)");
                writeLine("#pragma warning(push)");
                writeLine("#pragma warning(disable: 4121)");
                writeLine("#endif");
                writeLine("#pragma pack(push, 1)");
                writeLine($"typedef struct {SlotTypeName(field)} {{");
                if (field.Offset > 0)
                    writeLine($"    uint8_t ct_padding_{field.CName}[{field.Offset}];");
                writeLine($"    {Alignment(field.Alignment)}{declaration(field.Type, field.CName)};");
                writeLine($"}} {SlotTypeName(field)};");
                writeLine("#pragma pack(pop)");
                writeLine("#if defined(_MSC_VER)");
                writeLine("#pragma warning(pop)");
                writeLine("#endif");
            }
            PushPack(type, writeLine);
            writeLine($"typedef union {StorageTypeName(type)} {{");
            if (fields.Length == 0)
                writeLine("    uint8_t ct_empty;");
            foreach (var field in fields)
            {
                writeLine($"    {SlotTypeName(field)} {SlotName(field)};");
                writeLine($"    {declaration(field.Type, $"ct_align_{field.CName}")};");
            }
            writeLine($"}} {StorageTypeName(type)};");
            PopPack(type, writeLine);
            writeLine(includeTypedef ? $"typedef struct {Alignment(type.Alignment)}{name} {{" : $"struct {Alignment(type.Alignment)}{name}\n{{");
            writeLine($"    {StorageTypeName(type)} ct_layout;");
            writeLine(includeTypedef ? $"}} {name};" : "};");
            foreach (var field in fields)
                writeLine($"static_assert({OffsetExpression(type, field)} == (size_t){field.Offset}, \"C~ explicit field offset mismatch\");");
            AssertPack(type, name, writeLine);
            if (type.Alignment is int alignment)
                writeLine($"static_assert(CT_ALIGNOF({name}) >= (size_t){alignment}, \"C~ aggregate alignment mismatch\");");
            return;
        }

        PushPack(type, writeLine);
        var tag = type.AggregateLayout == AggregateLayoutKind.Union ? "union" : "struct";
        writeLine(includeTypedef ? $"typedef {tag} {Alignment(type.Alignment)}{name} {{" : $"{tag} {Alignment(type.Alignment)}{name}\n{{");
        if (fields.Length == 0)
            writeLine("    uint8_t ct_empty;");
        foreach (var field in fields)
            writeLine($"    {Alignment(field.Alignment)}{declaration(field.Type, field.CName)};");
        writeLine(includeTypedef ? $"}} {name};" : "};");
        PopPack(type, writeLine);
        if (type.AggregateLayout == AggregateLayoutKind.Union)
            foreach (var field in fields)
                writeLine($"static_assert(offsetof({name}, {field.CName}) == (size_t)0, \"C~ union field offset mismatch\");");
        AssertPack(type, name, writeLine);
        if (type.Alignment is int requestedAlignment)
            writeLine($"static_assert(CT_ALIGNOF({name}) >= (size_t){requestedAlignment}, \"C~ aggregate alignment mismatch\");");
    }

    private static string Alignment(int? alignment) => alignment is int value ? $"CT_ALIGN({value}) " : string.Empty;

    private static void PushPack(TypeSymbol type, Action<string> writeLine)
    {
        if (type.Pack is int pack)
        {
            writeLine("#if defined(_MSC_VER)");
            writeLine("#pragma warning(push)");
            writeLine("#pragma warning(disable: 4121)");
            writeLine("#endif");
            writeLine($"#pragma pack(push, {pack})");
        }
    }

    private static void PopPack(TypeSymbol type, Action<string> writeLine)
    {
        if (type.Pack is not null)
        {
            writeLine("#pragma pack(pop)");
            writeLine("#if defined(_MSC_VER)");
            writeLine("#pragma warning(pop)");
            writeLine("#endif");
        }
    }

    private static void AssertPack(TypeSymbol type, string name, Action<string> writeLine)
    {
        if (type.Pack is int pack)
            writeLine($"static_assert(CT_ALIGNOF({name}) <= (size_t){pack}, \"C~ aggregate pack mismatch\");");
    }
}
