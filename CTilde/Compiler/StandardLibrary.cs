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
    Geometry = 16,
    PacketGeometry = 32,
    All = Vec2 | Vec3 | Vec4 | Simd | Geometry | PacketGeometry,
}

[Flags]
internal enum StandardFoundationTypes
{
    None = 0,
    TimeSpan = 1,
    Random = 2,
    Stopwatch = 4,
    Process = 8,
    All = TimeSpan | Random | Stopwatch | Process,
}

internal static class StandardLibrary
{
    private static readonly ConcurrentDictionary<(CompilationTarget Target, bool NativeIntegers, bool NativeUtf8, bool HostedIo, StandardVectorTypes Vectors, StandardFoundationTypes Foundations), ImmutableArray<SyntaxTree>> SyntaxTreeCache = new();

    public static ImmutableArray<SyntaxTree> GetSyntaxTrees(
        CompilationTarget target,
        bool includeNativeIntegers = false,
        bool includeNativeUtf8 = false,
        bool includeHostedIo = false,
        StandardVectorTypes vectors = StandardVectorTypes.None,
        StandardFoundationTypes foundations = StandardFoundationTypes.None)
    {
        return SyntaxTreeCache.GetOrAdd((target, includeNativeIntegers, includeNativeUtf8, includeHostedIo, vectors, foundations), key =>
        {
            var files = FilesFor(key.Target, key.HostedIo, key.Vectors, key.Foundations);
            return LoadSyntaxTrees(files, key.NativeIntegers, key.NativeUtf8, key.HostedIo, key.Target == CompilationTarget.Freestanding, null, null, applyTransforms: true);
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
        return LoadSyntaxTrees(FilesFor(target, includeHostedIo, vectors, StandardFoundationTypes.All), includeNativeIntegers, includeNativeUtf8,
            includeHostedIo, target == CompilationTarget.Freestanding, Path.GetFullPath(sourceRoot), overrides, applyTransforms);
    }

    private static IReadOnlyList<string> FilesFor(CompilationTarget target, bool includeHostedIo, StandardVectorTypes vectors, StandardFoundationTypes foundations)
    {
        if ((vectors & StandardVectorTypes.Geometry) != 0)
            vectors |= StandardVectorTypes.Vec2 | StandardVectorTypes.Vec3 | StandardVectorTypes.Vec4;
        if ((vectors & StandardVectorTypes.PacketGeometry) != 0)
            vectors |= StandardVectorTypes.Vec3 | StandardVectorTypes.Simd;
        var files = new List<string> { "Object.ct", "Exception.ct", "String.ct", "StringBuilder.ct", "Globalization.ct", "Parsing.ct", "Enum.ct", "Encoding.ct", "Console.ct", "Environment.ct", "Math.ct", target == CompilationTarget.Freestanding ? "MemoryFreestanding.ct" : "Memory.ct", "Endian.ct", "Target.ct", "Threading.ct" };
        if (target == CompilationTarget.Freestanding)
            files.Add("FreestandingFault.ct");
        if ((foundations & (StandardFoundationTypes.TimeSpan | StandardFoundationTypes.Stopwatch | StandardFoundationTypes.Process)) != 0)
            files.Add("TimeSpan.ct");
        if ((foundations & StandardFoundationTypes.Random) != 0)
            files.Add("Random.ct");
        if ((foundations & (StandardFoundationTypes.Stopwatch | StandardFoundationTypes.Process)) != 0)
            files.Add("Diagnostics.ct");
        files.Add("Generics.ct");
        files.Add("ArrayAlgorithms.ct");
        files.Add("Utf8.ct");
        files.Add("Iteration.ct");
        files.Add("LinearCollections.ct");
        files.Add("HashCollections.ct");
        files.Add("IteratorEnumerable.ct");
        if ((vectors & StandardVectorTypes.Vec2) != 0)
            files.Add("Vec2.ct");
        if ((vectors & StandardVectorTypes.Vec3) != 0)
            files.Add("Vec3.ct");
        if ((vectors & StandardVectorTypes.Vec4) != 0)
            files.Add("Vec4.ct");
        if ((vectors & StandardVectorTypes.Simd) != 0)
            files.Add("Simd.ct");
        if ((vectors & StandardVectorTypes.PacketGeometry) != 0)
            files.Add("Vec3x4.ct");
        if ((vectors & StandardVectorTypes.Geometry) != 0)
        {
            files.Add("Matrix3x2.ct");
            files.Add("Matrix4x4.ct");
            files.Add("Quaternion.ct");
        }
        if (includeHostedIo)
        {
            files.Add("HostedIO.ct");
            files.Add("HostedStreams.ct");
        }
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
                "Vec3x4" => StandardVectorTypes.PacketGeometry,
                "Matrix3x2" or "Matrix4x4" or "Quaternion" => StandardVectorTypes.Geometry,
                _ => StandardVectorTypes.None,
            };
        }
        return result;
    }

    public static StandardFoundationTypes RequiredFoundations(IEnumerable<SyntaxTree> trees)
    {
        var result = StandardFoundationTypes.None;
        foreach (var token in trees.SelectMany(tree => tree.Tokens).Where(token => token.Kind == SyntaxKind.IdentifierToken))
        {
            result |= token.Text switch
            {
                "TimeSpan" => StandardFoundationTypes.TimeSpan,
                "Random" => StandardFoundationTypes.Random,
                "Stopwatch" => StandardFoundationTypes.Stopwatch | StandardFoundationTypes.TimeSpan,
                "Process" or "ProcessState" => StandardFoundationTypes.Process | StandardFoundationTypes.TimeSpan,
                _ => StandardFoundationTypes.None,
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
            CompilationTarget.EspIdf => new[] { "System.docs.xml", "Generics.docs.xml", "Collections.docs.xml", "Geometry.docs.xml", "EspIdf.docs.xml" },
            CompilationTarget.Hosted or CompilationTarget.Cosmopolitan => new[] { "System.docs.xml", "Generics.docs.xml", "Collections.docs.xml", "Geometry.docs.xml", "HostedIO.docs.xml" },
            _ => new[] { "System.docs.xml", "Generics.docs.xml", "Collections.docs.xml", "Geometry.docs.xml", "HostedIO.docs.xml" },
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
        bool freestanding,
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
            if (applyTransforms && (file is "Memory.ct" or "MemoryFreestanding.ct") && includeNativeUtf8)
                text = text.Replace("// CTILDE_NATIVE_UTF8_DECLARATION", NativeUtf8Declaration, StringComparison.Ordinal);
            if (applyTransforms && file == "Simd.ct" && freestanding)
                text = StripRegions(text, "// CTILDE_CHECKED_SIMD_BEGIN", "// CTILDE_CHECKED_SIMD_END");
            if (applyTransforms && freestanding)
                text = RewriteFreestandingFaults(text);
            trees.Add(SyntaxTree.ParseStandardLibrary(SourceText.From(text, path)));
        }

        return trees.ToImmutable();
    }

    private static string RewriteFreestandingFaults(string text)
    {
        foreach (var (exception, helper) in new[]
                 {
                     ("ArgumentException", "Argument"),
                     ("ArgumentNullException", "ArgumentNull"),
                     ("ArgumentOutOfRangeException", "ArgumentOutOfRange"),
                     ("EndOfStreamException", "EndOfStream"),
                     ("IndexOutOfRangeException", "IndexOutOfRange"),
                     ("InvalidOperationException", "InvalidOperation"),
                     ("KeyNotFoundException", "KeyNotFound"),
                     ("ObjectDisposedException", "ObjectDisposed"),
                     ("OutOfMemoryException", "OutOfMemory"),
                     ("OverflowException", "Overflow"),
                 })
            text = text.Replace($"throw new System.{exception}();", $"System.Runtime.FreestandingFault.{helper}();", StringComparison.Ordinal);
        return text;
    }

    private static string StripRegions(string text, string begin, string end)
    {
        while (true)
        {
            var start = text.IndexOf(begin, StringComparison.Ordinal);
            if (start < 0)
                return text;
            var finish = text.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
            if (finish < 0)
                throw new InvalidOperationException($"Standard-library transform region '{begin}' is not terminated.");
            finish += end.Length;
            text = text.Remove(start, finish - start);
        }
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

        [Extern("ct_console_read_line_prompt")]
        public static string ReadLine(string prompt);
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
