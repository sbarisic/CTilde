using System.Security.Cryptography;
using System.Text;

namespace CTilde;

internal enum NativeSectionKind
{
    Code,
    Data,
}

internal static class NativeSection
{
    public static bool IsValidName(string value)
    {
        if (value.Length is < 1 or > 128 || !IsFirstCharacter(value[0]))
            return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '$' or '-');
    }

    public static string MacroName(NativeSectionKind kind, string name)
    {
        var identity = $"{kind}:{name}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"CT_SECTION_{kind.ToString().ToUpperInvariant()}_{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    public static IEnumerable<string> MacroDefinition(NativeSectionKind kind, string name)
    {
        var macro = MacroName(kind, name);
        yield return "#if defined(_MSC_VER)";
        if (kind == NativeSectionKind.Data)
            yield return $"#pragma section(\"{name}\", read, write)";
        yield return kind == NativeSectionKind.Code
            ? $"#define {macro} __declspec(code_seg(\"{name}\"))"
            : $"#define {macro} __declspec(allocate(\"{name}\"))";
        yield return "#elif defined(__GNUC__) || defined(__clang__)";
        yield return $"#define {macro} __attribute__((section(\"{name}\")))";
        yield return "#else";
        yield return "#error \"C~ Section requires MSVC, GCC, or Clang section-placement support.\"";
        yield return $"#define {macro}";
        yield return "#endif";
    }

    public static string StripDataDefinitionMacro(string declaration)
    {
        const string prefix = "CT_SECTION_DATA_";
        if (!declaration.StartsWith(prefix, StringComparison.Ordinal))
            return declaration;
        var separator = prefix.Length + 24;
        return separator < declaration.Length && declaration[separator] == ' '
            ? declaration[(separator + 1)..]
            : declaration;
    }

    private static bool IsFirstCharacter(char character) =>
        char.IsAsciiLetter(character) || character is '.' or '_' or '$';
}
