namespace CTilde;

internal static class RuntimeAbiContract
{
    public static string Declarations { get; } = ReadDeclarations();
    public static uint CapabilityId(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Declarations,
            @"(?m)^#define " + System.Text.RegularExpressions.Regex.Escape(name) + @" UINT32_C\((\d+)\)$");
        if (!match.Success) throw new InvalidOperationException($"Runtime capability '{name}' is missing.");
        return uint.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadDeclarations()
    {
        using var stream = typeof(RuntimeAbiContract).Assembly.GetManifestResourceStream("CTilde.RuntimeContract.h")
            ?? throw new InvalidOperationException("The shared runtime ABI contract is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
