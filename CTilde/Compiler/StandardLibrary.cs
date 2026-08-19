using System.Collections.Immutable;
using System.Text;

namespace CTilde;

internal static class StandardLibrary
{
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyCommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], false));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyNativeCommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], true));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyEspIdfSyntaxTrees = new(() => LoadSyntaxTrees(["EspIdf.ct"]));

    public static ImmutableArray<SyntaxTree> GetSyntaxTrees(CompilationTarget target, bool includeNativeIntegers = false)
    {
        var common = includeNativeIntegers ? LazyNativeCommonSyntaxTrees.Value : LazyCommonSyntaxTrees.Value;
        return target == CompilationTarget.EspIdf ? common.AddRange(LazyEspIdfSyntaxTrees.Value) : common;
    }

    private static ImmutableArray<SyntaxTree> LoadSyntaxTrees(IReadOnlyList<string> files, bool includeNativeIntegers = false)
    {
        var assembly = typeof(StandardLibrary).Assembly;
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(files.Count);

        foreach (var file in files)
        {
            var resourceName = $"CTilde.StandardLibrary.{file}";
            using var stream = assembly.GetManifestResourceStream(resourceName) ??
                throw new InvalidOperationException($"The embedded standard-library resource '{resourceName}' is missing.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (file == "Console.ct" && includeNativeIntegers)
                text = text.Replace("    // CTILDE_NATIVE_INTEGER_OVERLOADS", NativeIntegerConsoleOverloads, StringComparison.Ordinal);
            trees.Add(SyntaxTree.ParseText(text, $"stdlib/System/{file}"));
        }

        return trees.ToImmutable();
    }

    private const string NativeIntegerConsoleOverloads = """
        [Extern("ct_write_nint")]
        [NoAlloc]
        public static void Write(nint value);

        [Extern("ct_write_nuint")]
        [NoAlloc]
        public static void Write(nuint value);

        public static void WriteLine(nint value)
        {
            Write(value);
            WriteLine();
        }

        public static void WriteLine(nuint value)
        {
            Write(value);
            WriteLine();
        }
    """;
}
