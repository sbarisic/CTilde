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
    Simd = 8,
    All = Vec2 | Vec3 | Vec4 | Simd,
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
        includeHostedIo &= target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan;
        return SyntaxTreeCache.GetOrAdd((target, includeNativeIntegers, includeNativeUtf8, includeHostedIo, vectors), key =>
        {
            var files = FilesFor(key.Target, key.HostedIo, key.Vectors);
            return LoadSyntaxTrees(files, key.NativeIntegers, key.NativeUtf8, key.HostedIo, null, null, applyTransforms: true);
        });
    }

    internal static ImmutableArray<SyntaxTree> GetPhysicalSyntaxTrees(
        string sourceRoot,
        CompilationTarget target,
        bool includeNativeIntegers,
        bool includeNativeUtf8,
        bool includeHostedIo,
        StandardVectorTypes vectors,
        IReadOnlyDictionary<string, string>? overrides = null,
        bool applyTransforms = true)
    {
        includeHostedIo &= target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan;
        return LoadSyntaxTrees(FilesFor(target, includeHostedIo, vectors), includeNativeIntegers, includeNativeUtf8,
            includeHostedIo, Path.GetFullPath(sourceRoot), overrides, applyTransforms);
    }

    private static IReadOnlyList<string> FilesFor(CompilationTarget target, bool includeHostedIo, StandardVectorTypes vectors)
    {
        var files = target == CompilationTarget.Freestanding
            ? new List<string> { "Object.ct", "MemoryFreestanding.ct", "Endian.ct", "Target.ct" }
            : new List<string> { "Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Math.ct", "Memory.ct", "Endian.ct", "Target.ct", "Threading.ct" };
        if ((vectors & StandardVectorTypes.Vec2) != 0)
            files.Add("Vec2.ct");
        if ((vectors & StandardVectorTypes.Vec3) != 0)
            files.Add("Vec3.ct");
        if ((vectors & StandardVectorTypes.Vec4) != 0)
            files.Add("Vec4.ct");
        if ((vectors & StandardVectorTypes.Simd) != 0)
            files.Add("Simd.ct");
        if (includeHostedIo)
            files.Add("HostedIO.ct");
        if (target == CompilationTarget.EspIdf)
            files.Add("EspIdf.ct");
        return files;
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
                "F32x4" or "I32x4" or "U32x4" or "Mask32x4" => StandardVectorTypes.Simd,
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
        var names = target switch
        {
            CompilationTarget.EspIdf => new[] { "System.docs.xml", "EspIdf.docs.xml" },
            CompilationTarget.Hosted or CompilationTarget.Cosmopolitan => new[] { "System.docs.xml", "HostedIO.docs.xml" },
            _ => new[] { "System.docs.xml" },
        };
        var assembly = typeof(StandardLibrary).Assembly;
        return [.. names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream($"CTilde.StandardLibrary.{name}") ??
                throw new InvalidOperationException($"The embedded standard-library documentation resource '{name}' is missing.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        })];
    }

    private static ImmutableArray<SyntaxTree> LoadSyntaxTrees(
        IReadOnlyList<string> files,
        bool includeNativeIntegers,
        bool includeNativeUtf8,
        bool includeHostedIo,
        string? sourceRoot,
        IReadOnlyDictionary<string, string>? overrides,
        bool applyTransforms)
    {
        var assembly = typeof(StandardLibrary).Assembly;
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(files.Count);

        foreach (var file in files)
        {
            string text;
            string path;
            if (sourceRoot is null)
            {
                var resourceName = $"CTilde.StandardLibrary.{file}";
                using var stream = assembly.GetManifestResourceStream(resourceName) ??
                    throw new InvalidOperationException($"The embedded standard-library resource '{resourceName}' is missing.");
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
                path = $"stdlib/System/{file}";
            }
            else
            {
                path = Path.Combine(sourceRoot, file == "EspIdf.ct" ? Path.Combine("Esp", "Idf", file) : Path.Combine("System", file));
                text = overrides is not null && overrides.TryGetValue(Path.GetFullPath(path), out var openText)
                    ? openText
                    : File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            if (applyTransforms && file == "Console.ct" && includeNativeIntegers)
                text = text.Replace("    // CTILDE_NATIVE_INTEGER_OVERLOADS", NativeIntegerConsoleOverloads, StringComparison.Ordinal);
            if (applyTransforms && file == "Console.ct" && includeHostedIo)
                text = text.Replace("    // CTILDE_HOSTED_INPUT_MEMBERS", HostedConsoleInputMembers, StringComparison.Ordinal);
            if (applyTransforms && file == "Memory.ct" && includeNativeUtf8)
                text = text.Replace("// CTILDE_NATIVE_UTF8_DECLARATION", NativeUtf8Declaration, StringComparison.Ordinal);
            trees.Add(SyntaxTree.ParseStandardLibrary(SourceText.From(text, path)));
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
