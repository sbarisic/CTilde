using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text;

namespace CTilde;

[Flags]
internal enum StandardVectorTypes
{
    None = 0,
    Vec2 = 1,
    Vec3 = 2,
    Vec4 = 4,
    All = Vec2 | Vec3 | Vec4,
}

internal static class StandardLibrary
{
    private static readonly ConcurrentDictionary<(CompilationTarget Target, bool NativeIntegers, bool NativeUtf8, bool HostedIo, StandardVectorTypes Vectors), ImmutableArray<SyntaxTree>> SyntaxTreeCache = new();

    public static ImmutableArray<SyntaxTree> GetSyntaxTrees(
        CompilationTarget target,
        bool includeNativeIntegers = false,
        bool includeNativeUtf8 = false,
        bool includeHostedIo = false,
        StandardVectorTypes vectors = StandardVectorTypes.None)
    {
        includeHostedIo &= target == CompilationTarget.Hosted;
        return SyntaxTreeCache.GetOrAdd((target, includeNativeIntegers, includeNativeUtf8, includeHostedIo, vectors), key =>
        {
            var files = new List<string> { "Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Math.ct", "Memory.ct", "Threading.ct" };
            if ((key.Vectors & StandardVectorTypes.Vec2) != 0)
                files.Add("Vec2.ct");
            if ((key.Vectors & StandardVectorTypes.Vec3) != 0)
                files.Add("Vec3.ct");
            if ((key.Vectors & StandardVectorTypes.Vec4) != 0)
                files.Add("Vec4.ct");
            if (key.HostedIo)
                files.Add("HostedIO.ct");
            if (key.Target == CompilationTarget.EspIdf)
                files.Add("EspIdf.ct");
            return LoadSyntaxTrees(files, key.NativeIntegers, key.NativeUtf8, key.HostedIo);
        });
    }

    public static StandardVectorTypes RequiredVectors(IEnumerable<SyntaxTree> trees)
    {
        var result = StandardVectorTypes.None;
        foreach (var token in trees.SelectMany(tree => tree.Tokens).Where(token => token.Kind == SyntaxKind.IdentifierToken))
        {
            result |= token.Text switch
            {
                "Vec2" => StandardVectorTypes.Vec2,
                "Vec3" => StandardVectorTypes.Vec3,
                "Vec4" => StandardVectorTypes.Vec4,
                _ => StandardVectorTypes.None,
            };
        }
        return result;
    }

    public static bool RequiresHostedIo(IEnumerable<SyntaxTree> trees)
    {
        var declaresIoNamespace = false;
        var usesIoName = false;
        foreach (var tree in trees)
        {
            var tokens = tree.Tokens.Where(token => token.Kind != SyntaxKind.EndOfFileToken).ToArray();
            usesIoName |= tokens.Any(token => token.Kind == SyntaxKind.IdentifierToken && token.Text is "File" or "FileHandle" or "FileMode" or "FileAccess" or "IOException");
            for (var index = 0; index + 2 < tokens.Length; index++)
            {
                if (tokens[index].Text == "Console" && tokens[index + 1].Kind == SyntaxKind.DotToken && tokens[index + 2].Text is "Read" or "ReadLine")
                    return true;
                if (tokens[index].Text != "System" || tokens[index + 1].Kind != SyntaxKind.DotToken || tokens[index + 2].Text != "IO")
                    continue;
                if (index > 0 && tokens[index - 1].Kind == SyntaxKind.UsingKeyword)
                    return true;
                declaresIoNamespace |= index > 0 && tokens[index - 1].Kind == SyntaxKind.NamespaceKeyword;
                if (index + 4 < tokens.Length && tokens[index + 3].Kind == SyntaxKind.DotToken && tokens[index + 4].Text is "File" or "FileHandle" or "FileMode" or "FileAccess" or "IOException")
                    return true;
            }
        }
        return declaresIoNamespace && usesIoName;
    }

    public static ImmutableArray<string> GetDocumentationXml(CompilationTarget target)
    {
        var names = target == CompilationTarget.EspIdf
            ? new[] { "System.docs.xml", "EspIdf.docs.xml" }
            : new[] { "System.docs.xml", "HostedIO.docs.xml" };
        var assembly = typeof(StandardLibrary).Assembly;
        return [.. names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream($"CTilde.StandardLibrary.{name}") ??
                throw new InvalidOperationException($"The embedded standard-library documentation resource '{name}' is missing.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        })];
    }

    private static ImmutableArray<SyntaxTree> LoadSyntaxTrees(IReadOnlyList<string> files, bool includeNativeIntegers = false, bool includeNativeUtf8 = false, bool includeHostedIo = false)
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
            if (file == "Console.ct" && includeHostedIo)
                text = text.Replace("    // CTILDE_HOSTED_INPUT_MEMBERS", HostedConsoleInputMembers, StringComparison.Ordinal);
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

    private const string HostedConsoleInputMembers = """
        [Extern("ct_console_read")]
        public static int Read();

        [Extern("ct_console_read_line")]
        public static string ReadLine();
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
