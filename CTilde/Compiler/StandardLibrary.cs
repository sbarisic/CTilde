using System.Collections.Immutable;
using System.Text;

namespace CTilde;

internal static class StandardLibrary
{
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyCommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], false));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyNativeCommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], true));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyUtf8CommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], false, true));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyNativeUtf8CommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"], true, true));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyEspIdfSyntaxTrees = new(() => LoadSyntaxTrees(["EspIdf.ct"]));

    public static ImmutableArray<SyntaxTree> GetSyntaxTrees(CompilationTarget target, bool includeNativeIntegers = false, bool includeNativeUtf8 = false)
    {
        var common = (includeNativeIntegers, includeNativeUtf8) switch
        {
            (true, true) => LazyNativeUtf8CommonSyntaxTrees.Value,
            (true, false) => LazyNativeCommonSyntaxTrees.Value,
            (false, true) => LazyUtf8CommonSyntaxTrees.Value,
            _ => LazyCommonSyntaxTrees.Value,
        };
        return target == CompilationTarget.EspIdf ? common.AddRange(LazyEspIdfSyntaxTrees.Value) : common;
    }

    private static ImmutableArray<SyntaxTree> LoadSyntaxTrees(IReadOnlyList<string> files, bool includeNativeIntegers = false, bool includeNativeUtf8 = false)
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
            if (file == "Memory.ct" && includeNativeUtf8)
                text = text.Replace("// CTILDE_NATIVE_UTF8_DECLARATION", NativeUtf8Declaration, StringComparison.Ordinal);
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

    private const string NativeUtf8Declaration = """
    public readonly struct NativeUtf8String
    {
        [Extern("ct_native_utf8_borrow")]
        [NoAlloc]
        public static NativeUtf8String Borrow(string value);

        [Extern("ct_native_utf8_null")]
        [NoAlloc]
        private static NativeUtf8String GetNull();

        [NoAlloc]
        public static NativeUtf8String Null
        {
            get { return GetNull(); }
        }

        [NoAlloc]
        public nuint ByteLength
        {
            get { return 0; }
        }

        [NoAlloc]
        public unsafe byte* Pointer
        {
            get { return null; }
        }
    }
    """;
}
