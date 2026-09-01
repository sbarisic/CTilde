using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart35(ConformanceSuite suite)
    {
        suite.Run("draft 0.41 ordinal string operations and split segments", () =>
        {
            const string source = """
                using System;
                using System.Text;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        string value = "--a::beta::a--";
                        Console.WriteLine(value.Length);
                        Console.WriteLine(value.Contains("beta") && value.StartsWith("--") && value.EndsWith("--"));
                        Console.WriteLine(value.IndexOf('a') == 2 && value.LastIndexOf('a') == 11 && value.IndexOf("::", 3, 8) == 3);
                        Console.WriteLine(value.Substring(2, 1) == "a" && value.Insert(3, "!") == "--a!::beta::a--");
                        Console.WriteLine(value.Remove(0, 2) == "a::beta::a--" && value.Replace("a", "xy") == "--xy::betxy::xy--");
                        Console.WriteLine(value.Trim('-') == "a::beta::a" && "xxdata".TrimStart('x') == "data" && "dataxx".TrimEnd('x') == "data");
                        char[] copy = value.ToCharArray(); char[] slice = new char[4]; value.CopyTo(5, slice, 0, 4);
                        Console.WriteLine(copy.Length == value.Length && copy[2] == 'a' && slice[0] == 'b' && slice[3] == 'a');
                        string[] all = "::a::::b::".Split("::");
                        string[] nonempty = "::a::::b::".Split("::", 8, StringSplitOptions.RemoveEmptyEntries);
                        string[] limited = "a:b:c".Split(':', 2, StringSplitOptions.None);
                        Console.WriteLine(all.Length == 5 && all[0] == "" && all[2] == "" && all[4] == "");
                        Console.WriteLine(nonempty.Length == 2 && nonempty[0] == "a" && nonempty[1] == "b");
                        Console.WriteLine(limited.Length == 2 && limited[0] == "a" && limited[1] == "b:c");
                        int segments = 0;
                        foreach (StringSegment segment in "x--y--z".EnumerateSplit("--"))
                        {
                            if (segments == 1) Console.WriteLine(segment.Source == "x--y--z" && segment.Start == 3 && segment.Length == 1 && segment[0] == 'y');
                            segments++;
                        }
                        Console.WriteLine(segments == 3);
                        Console.WriteLine(String.CompareOrdinal(null, "") < 0 && String.CompareOrdinal("a", "b") < 0);
                        string[] joined = new string[3]; joined[0] = "a"; joined[1] = "b"; joined[2] = "c";
                        Console.WriteLine(String.Join("/", joined) == "a/b/c");
                        string unicode = "éλ"; string embedded = "a\0b";
                        Console.WriteLine(unicode.Length == 4 && unicode.IndexOf("λ") == 2 && embedded.Length == 3 && embedded.Contains("\0"));
                        try { value.Contains((string)null); } catch (ArgumentNullException) { Console.WriteLine("null"); }
                        try { value.Substring(-1); } catch (ArgumentOutOfRangeException) { Console.WriteLine("range"); }
                        try { value.Split(""); } catch (ArgumentException) { Console.WriteLine("separator"); }
                        Console.WriteLine(Ascii.IsWhiteSpace('\t') && Ascii.IsLetter('z') && Ascii.IsDigit('7') &&
                            Ascii.ToUpper('q') == 'Q' && Ascii.ToLower('R') == 'r' && Ascii.EqualsIgnoreCase("Alpha", "aLPHA"));
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "14", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True",
                "True", "null", "range", "separator", "True");
        });

        suite.Run("draft 0.41 invariant formatting and StringBuilder", () =>
        {
            const string source = """
                using System;
                using System.Text;

                public struct Custom : IFormattable
                {
                    private int marker;
                    public string ToString(string format) { return "custom-" + format; }
                }

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(String.Format("{{{0:D4}}}:{1:x4}:{2,7:F2}", -42, 255u, 1.5f));
                        Console.WriteLine(String.Format("{0:G}", 0.1f));
                        Console.WriteLine(String.Format("{0:G}", -0.0d));
                        Console.WriteLine(String.Format("{0:g3}", Math.Pow(10.0d, 20.0d)));
                        Console.WriteLine(String.Format("{0}:{1}:{2}", Math.Sqrt(-1.0f), Math.Exp(10000.0d), -Math.Exp(10000.0d)));
                        Console.WriteLine(String.Format("{0:tag}", new Custom()));
                        IFormattable text = "identity";
                        Console.WriteLine(text.ToString(""));
                        StringBuilder builder = new StringBuilder(1);
                        builder.Append("a").Append(12).AppendLine().AppendFormat("{0:X2}", 15);
                        Console.WriteLine(builder.Length == 6 && builder.Capacity >= 6 && builder.ToString() == "a12\n0F");
                        builder.Clear().Append("reused");
                        Console.WriteLine(builder.ToString());
                        try { String.Format("{0:F100}", 1.0d); }
                        catch (FormatException) { Console.WriteLine("precision"); }
                        try { String.Format("{", 1); }
                        catch (FormatException) { Console.WriteLine("syntax"); }
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "{-0042}:00ff:   1.50", "0.1", "-0", "1e+20", "NaN:Infinity:-Infinity",
                "custom-tag", "identity", "True", "reused", "precision", "syntax");
        });

        suite.Run("draft 0.41 checked native UTF-8 conversion", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Text;

                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        NativeBuffer<byte> missingStorage = stackalloc byte[3]; byte* missing = missingStorage.Pointer; missing[0] = (byte)'a'; missing[1] = (byte)'b'; missing[2] = (byte)'c';
                        string result;
                        Console.WriteLine(!Utf8.TryGetString(missing, 3, out result) && result == null);
                        try { Utf8.GetString(missing, 3); } catch (ArgumentException) { Console.WriteLine("unterminated"); }
                        NativeBuffer<byte> invalidStorage = stackalloc byte[3]; byte* invalid = invalidStorage.Pointer; invalid[0] = (byte)237; invalid[1] = (byte)160; invalid[2] = (byte)128;
                        ReadOnlyNativeBuffer<byte> invalidBuffer = invalidStorage;
                        Console.WriteLine(!Utf8.TryGetString(invalidBuffer, out result) && result == null);
                        try { Utf8.GetString(invalidBuffer); } catch (ArgumentException) { Console.WriteLine("invalid"); }
                        NativeBuffer<byte> validStorage = stackalloc byte[3]; byte* valid = validStorage.Pointer; valid[0] = (byte)'a'; valid[1] = (byte)0; valid[2] = (byte)'b';
                        ReadOnlyNativeBuffer<byte> exact = validStorage;
                        string embedded = Utf8.GetString(exact);
                        Console.WriteLine(embedded.Length == 3 && embedded[1] == '\0' && Utf8.GetByteCount(embedded) == 3);
                        byte* nullPointer = null;
                        Console.WriteLine(Utf8.GetString(nullPointer, 100) == null && Utf8.TryGetString(nullPointer, 100, out result) && result == null);
                        NativeBuffer<byte> destination = stackalloc byte[4]; nuint written;
                        Console.WriteLine(Utf8.TryCopyTo(embedded, destination, true, out written) && written == 4 && destination[1] == 0 && destination[3] == 0);
                        NativeBuffer<byte> shortDestination = stackalloc byte[3];
                        Console.WriteLine(!Utf8.TryCopyTo(embedded, shortDestination, true, out written) && written == 0);
                        try { Utf8.GetByteCount(null); } catch (ArgumentNullException) { Console.WriteLine("null"); }
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "unterminated", "True", "invalid", "True", "True", "True", "True", "null");
        });

        suite.Run("draft 0.41 string surface and native boundary validation", () =>
        {
            const string invalidSurface = "namespace System; public class String { public int Value; public String() { } } public static class Program { [EntryPoint] public static void Main() { } }";
            var surfaceDiagnostics = Compile(invalidSurface).GetDiagnostics();
            Assert(surfaceDiagnostics.Any(diagnostic => diagnostic.Code is "CT1320" or "CT1100" or "CT1104"),
                "A user-defined System.String storage surface was accepted.\n" + string.Join(Environment.NewLine, surfaceDiagnostics));

            const string directImport = "using System; public static class Native { [NativeImport(\"demo\")] public static string Read(string value); } public static class Program { [EntryPoint] public static void Main() { string value = Native.Read(\"x\"); } }";
            var diagnostics = Compile(directImport).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code is "CT1279" or "CT1314" or "CT1284"),
                "NativeImport accepted automatic managed-string marshalling.\n" + string.Join(Environment.NewLine, diagnostics));
        });

        suite.Run("draft 0.41 native UTF-8 fixture ownership", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Text;
                public static class Native
                {
                    [NativeImport("ctilde_string_fixture", "ctilde_valid")] public static unsafe byte* Valid();
                    [NativeImport("ctilde_string_fixture", "ctilde_null")] public static unsafe byte* Null();
                    [NativeImport("ctilde_string_fixture", "ctilde_invalid")] public static unsafe byte* Invalid();
                    [NativeImport("ctilde_string_fixture", "ctilde_unterminated")] public static unsafe byte* Unterminated();
                    [NativeImport("ctilde_string_fixture", "ctilde_embedded")] public static unsafe byte* Embedded();
                    [NativeImport("ctilde_string_fixture", "ctilde_owned")] public static unsafe byte* Owned();
                    [NativeImport("ctilde_string_fixture", "ctilde_free")] public static unsafe void Free(byte* value);
                }
                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        Console.WriteLine(Utf8.GetString(Native.Valid(), 6) == "valid");
                        Console.WriteLine(Utf8.GetString(Native.Null(), 8) == null);
                        string result;
                        Console.WriteLine(!Utf8.TryGetString(Native.Invalid(), 2, out result) && result == null);
                        Console.WriteLine(!Utf8.TryGetString(Native.Unterminated(), 3, out result) && result == null);
                        ReadOnlyNativeBuffer<byte> embedded = new ReadOnlyNativeBuffer<byte>(Native.Embedded(), 3);
                        result = Utf8.GetString(embedded);
                        Console.WriteLine(result.Length == 3 && result[1] == '\0');
                        byte* owned = Native.Owned();
                        result = Utf8.GetString(owned, 6);
                        Native.Free(owned);
                        Console.WriteLine(result == "owned");
                    }
                }
                """;
            const string fixture = """
                #include <stdint.h>
                #include <stdlib.h>
                #include <string.h>
                #if defined(_WIN32)
                #define CT_FIXTURE_EXPORT __declspec(dllexport)
                #else
                #define CT_FIXTURE_EXPORT __attribute__((visibility("default")))
                #endif
                CT_FIXTURE_EXPORT uint8_t* ctilde_valid(void) { static uint8_t value[] = { 'v', 'a', 'l', 'i', 'd', 0 }; return value; }
                CT_FIXTURE_EXPORT uint8_t* ctilde_null(void) { return NULL; }
                CT_FIXTURE_EXPORT uint8_t* ctilde_invalid(void) { static uint8_t value[] = { 0xc0u, 0u }; return value; }
                CT_FIXTURE_EXPORT uint8_t* ctilde_unterminated(void) { static uint8_t value[] = { 'a', 'b', 'c' }; return value; }
                CT_FIXTURE_EXPORT uint8_t* ctilde_embedded(void) { static uint8_t value[] = { 'a', 0, 'b' }; return value; }
                CT_FIXTURE_EXPORT uint8_t* ctilde_owned(void) { uint8_t* value = (uint8_t*)malloc(6u); if (value != NULL) memcpy(value, "owned", 6u); return value; }
                CT_FIXTURE_EXPORT void ctilde_free(uint8_t* value) { free(value); }
                """;
            var result = CompileAndRunNativeImportFixture(source, fixture, "ctilde_string_fixture");
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True", "True", "True", "True", "True");
        });

        suite.Run("draft 0.41 string language services and utility pruning", () =>
        {
            const string memberSource = "using System; public static class P { public static void M() { string text = \"x\"; text. } }";
            var memberService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(memberSource, "string-completion.ct")]);
            var memberPosition = memberSource.IndexOf("text.", StringComparison.Ordinal) + "text.".Length;
            var members = memberService.GetCompletions("string-completion.ct", memberPosition);
            Assert(members.Any(item => item.Label == "Contains") && members.Any(item => item.Label == "Split") && members.Any(item => item.Label == "Length"),
                "Built-in string completion omitted the System.String surface.");
            var contains = members.Single(item => item.Label == "Contains");
            Assert(contains.DocumentationId is not null && memberService.GetDocumentation(contains.DocumentationId)?.Summary.Contains("ordinal", StringComparison.OrdinalIgnoreCase) == true,
                "String completion documentation was unavailable.");
            var stringPosition = memberSource.IndexOf("string text", StringComparison.Ordinal) + 1;
            Assert(memberService.GetDefinition("string-completion.ct", stringPosition)?.FilePath == "stdlib/System/String.ct",
                "The built-in string keyword did not navigate to System.String.");

            const string staticSource = "using System; using System.Text; public static class P { public static void M() { String.; Utf8. } }";
            var staticService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(staticSource, "string-static-completion.ct")]);
            var stringMember = staticSource.IndexOf("String.", StringComparison.Ordinal) + "String.".Length;
            var utf8Member = staticSource.IndexOf("Utf8.", StringComparison.Ordinal) + "Utf8.".Length;
            Assert(staticService.GetCompletions("string-static-completion.ct", stringMember).Any(item => item.Label == "Format") &&
                staticService.GetCompletions("string-static-completion.ct", utf8Member).Any(item => item.Label == "GetString"),
                "Static string or UTF-8 completion omitted Draft 0.41 APIs.");

            var unused = Emit("public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!unused.Contains("int32_t ct_string_index_string(ct_string* value", StringComparison.Ordinal) &&
                !unused.Contains("d2s_buffered_n", StringComparison.Ordinal) &&
                !unused.Contains("ct_utf8_validate_bytes", StringComparison.Ordinal),
                "Unused string, Ryu, or UTF-8 conversion support entered generated output.");
        });

        suite.Run("draft 0.41 string profile availability", () =>
        {
            const string body = """
                StringBuilder builder = new StringBuilder();
                builder.Append("a,b");
                string value = builder.ToString();
                string[] parts = value.Split(',');
                return parts.Length + String.Format("{0:D2}", 1).Length;
                """;
            var application = $$"""
                using System;
                using System.Text;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int result = UseStrings();
                    }
                    private static int UseStrings() { {{body}} }
                }
                """;
            foreach (var target in new[] { CompilationTarget.Hosted, CompilationTarget.Cosmopolitan, CompilationTarget.EspIdf })
            {
                var options = new CompilationOptions(target, Architecture: target == CompilationTarget.EspIdf
                    ? CompilationArchitecture.Xtensa
                    : CompilationArchitecture.X64);
                var diagnostics = Compile(application, options).GetDiagnostics();
                Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                    $"The Draft 0.41 string surface was unavailable for {target}.\n" + string.Join(Environment.NewLine, diagnostics));
            }

            var freestanding = $$"""
                using System;
                using System.Runtime;
                using System.Text;
                public static class Kernel
                {
                    [RuntimeImpl(Runtime.Allocate)] [NoAlloc]
                    private static unsafe void* Allocate(nuint size) { return null; }
                    [RuntimeImpl(Runtime.Free)] [NoAlloc]
                    private static unsafe void Free(void* value) { }
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc]
                    private static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }
                    [Export("kernel_main")]
                    public static int Main() { {{body}} }
                }
                """;
            var freestandingOptions = new CompilationOptions(CompilationTarget.Freestanding,
                Architecture: CompilationArchitecture.X64);
            var freestandingDiagnostics = Compile(freestanding, freestandingOptions).GetDiagnostics();
            Assert(!freestandingDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                "The Draft 0.41 string surface was unavailable for freestanding.\n" +
                string.Join(Environment.NewLine, freestandingDiagnostics));
            var freestandingOutput = Emit(freestanding, freestandingOptions);
            Assert(freestandingOutput.Contains("ct_string_format_builtin", StringComparison.Ordinal) &&
                freestandingOutput.Contains("ct_runtime_allocate_bridge", StringComparison.Ordinal),
                "Freestanding string allocation or formatting support was not emitted through runtime roles.");
        });

        suite.Run("draft 0.41 inline runtime hot paths", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-inline-hot-paths", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Program.ct"), """
                    using System;
                    public static class Program
                    {
                        private static long Read(int[] values, int index, long seed)
                        {
                            int value = values[index];
                            return ((seed + value) * 3L) - (seed << 1);
                        }
                        [EntryPoint] public static void Main()
                        {
                            int[] values = new int[1]; values[0] = 41;
                            Console.WriteLine(Read(values, 0, 1L));
                        }
                    }
                    """);
                var compiler = Environment.GetEnvironmentVariable("CTILDE_CC") ?? "auto";
                File.WriteAllText(Path.Combine(root, "ctilde.json"), $$"""
                    {
                      "target": "hosted",
                      "architecture": "x64",
                      "sources": ["*.ct"],
                      "build": {
                        "cLayout": "modules",
                        "generatedDirectory": "build/generated",
                        "generatedHeader": "build/generated/ctilde_exports.h",
                        "configuration": "release",
                        "lto": false,
                        "optimization": "speed",
                        "compiler": "{{compiler}}",
                        "executable": "build/hot-paths.exe"
                      }
                    }
                    """);
                var run = RunNativeProfileCli(root, Path.Combine(root, "ctilde.json"), "--run");
                Assert(run.ExitCode == 0 && Normalize(run.StandardOutput).Contains("124\n", StringComparison.Ordinal), run.StandardOutput + run.StandardError);
                var header = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "build", "generated"), "*.h")
                    .Order(StringComparer.Ordinal).Select(File.ReadAllText));
                var runtime = File.ReadAllText(Path.Combine(root, "build", "generated", "ctilde_runtime.c"));
                Assert(header.Contains("static CT_INLINE int64_t ct_i64_add(", StringComparison.Ordinal) &&
                    header.Contains("static CT_INLINE int64_t ct_i64_mul(", StringComparison.Ordinal) &&
                    header.Contains("static CT_INLINE void* ct_require_nonnull(", StringComparison.Ordinal) &&
                    header.Contains("static CT_INLINE void ct_bounds(", StringComparison.Ordinal),
                    "The modular internal header omitted a reachable inline hot-path helper.");
                Assert(!header.Contains("extern int64_t ct_i64_add(", StringComparison.Ordinal) &&
                    !header.Contains("extern void ct_bounds(", StringComparison.Ordinal) &&
                    runtime.Contains("static CT_INLINE int64_t ct_i64_add(", StringComparison.Ordinal) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(runtime, @"(?m)^int64_t ct_i64_add\("),
                    "A modular inline helper retained an external call or definition.");

                const string collision = "public static class Native { [Extern(\"ct_retain_fast\")] public static void Call(); } public static class Program { [EntryPoint] public static void Main() { } }";
                Assert(Compile(collision).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"),
                    "A native declaration was allowed to collide with an internal ARC fast path.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        suite.Run("draft 0.41 ARC fast paths and lazy identity hash", () =>
        {
            const string source = """
                using System;
                using System.Threading;
                public sealed class Item { }
                public static class Program
                {
                    private static Item shared;
                    private static int firstHash;
                    private static int secondHash;
                    private static void First() { firstHash = shared.GetHashCode(); }
                    private static void Second() { secondHash = shared.GetHashCode(); }
                    [EntryPoint] public static void Main()
                    {
                        Item first = new Item();
                        Item other = new Item();
                        shared = first;
                        Thread one = new Thread(First); Thread two = new Thread(Second);
                        one.Start(); two.Start(); one.Join(); two.Join();
                        Console.WriteLine(firstHash != 0 && firstHash == secondHash && first.GetHashCode() == firstHash);
                        int otherHash = other.GetHashCode();
                        Console.WriteLine(otherHash != 0 && otherHash != firstHash);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("ct_atomic_u32 IdentityHash", StringComparison.Ordinal) &&
                generated.Contains("ct_atomic_store_relaxed(&object->IdentityHash, 0u)", StringComparison.Ordinal) &&
                generated.Contains("ct_object_identity_hash(value)", StringComparison.Ordinal) &&
                !generated.Contains("do { identity = ct_atomic_fetch_add_relaxed(&ct_next_identity", StringComparison.Ordinal),
                "Managed allocation still assigned an eager identity hash or lost atomic lazy storage.");
            Assert(generated.Contains("static CT_INLINE void ct_retain_fast(", StringComparison.Ordinal) &&
                generated.Contains("static CT_INLINE void ct_release_fast(", StringComparison.Ordinal) &&
                generated.Contains("void ct_retain(ct_object* object) { (void)ct_thread_require_attached();", StringComparison.Ordinal) &&
                generated.Contains("void ct_release(ct_object* object) { ct_thread_state* state = ct_thread_require_attached();", StringComparison.Ordinal),
                "Generated ARC fast paths or strict public wrappers were not emitted.");
            var result = CompileAndRun(source, memoryDiagnostics: true, threads: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True");
        });

        suite.Run("draft 0.41 public ARC attachment contract", () =>
        {
            const string source = """
                public static class Native { [Extern("ct_test_arc_unattached")] public static void Invoke(); }
                public static class Program { [EntryPoint] public static void Main() { Native.Invoke(); } }
                """;
            foreach (var operation in new[] { "ct_retain(NULL);", "ct_release(NULL);" })
            {
                var native = $$"""

                    #if defined(_WIN32)
                    #include <windows.h>
                    static DWORD WINAPI ct_test_arc_worker(LPVOID raw) { (void)raw; {{operation}} return 0; }
                    void ct_test_arc_unattached(void) { HANDLE thread = CreateThread(NULL, 0, ct_test_arc_worker, NULL, 0, NULL); if (thread == NULL) abort(); (void)WaitForSingleObject(thread, INFINITE); (void)CloseHandle(thread); }
                    #else
                    #include <pthread.h>
                    static void* ct_test_arc_worker(void* raw) { (void)raw; {{operation}} return NULL; }
                    void ct_test_arc_unattached(void) { pthread_t thread; if (pthread_create(&thread, NULL, ct_test_arc_worker, NULL) != 0) abort(); if (pthread_join(thread, NULL) != 0) abort(); }
                    #endif
                    """;
                var result = CompileAndRun(source, nativeSuffix: native, threads: true);
                Assert(result.ExitCode != 0 && result.StandardError.Contains("CTT0001", StringComparison.Ordinal),
                    operation + Environment.NewLine + result.StandardOutput + result.StandardError);
            }
        });

        suite.Run("draft 0.41 sealed receiver devirtualization", () =>
        {
            const string source = """
                using System;
                public delegate int Reader();
                public class Base
                {
                    public virtual int Read() { return 1; }
                    public virtual int Value { get { return 2; } }
                    public virtual int this[int index] { get { return index; } }
                }
                public sealed class Closed : Base
                {
                    public override int Read() { return 10; }
                    public override int Value { get { return 20; } }
                    public override int this[int index] { get { return 30 + index; } }
                }
                public class Locked : Base { public sealed override int Read() { return 40; } }
                public class Open : Base { public override int Read() { return 50; } }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        Closed closed = new Closed(); Locked locked = new Locked(); Base open = new Open();
                        Reader closedReader = closed.Read; Reader openReader = open.Read;
                        Console.WriteLine(closed.Read() + closed.Value + closed[1] + closedReader());
                        Console.WriteLine(locked.Read());
                        Console.WriteLine(open.Read() + openReader());
                    }
                }
                """;
            var compilation = Compile(source);
            var generatedWriter = new StringWriter();
            Assert(compilation.EmitC(generatedWriter).Success, "Sealed-dispatch C emission failed.");
            var mapWriter = new StringWriter();
            Assert(compilation.EmitSymbolMap(mapWriter).Success, "Sealed-dispatch symbol-map emission failed.");
            var generated = generatedWriter.ToString();
            var mainName = Draft41SymbolName(mapWriter.ToString(), "method:Program::Main(", "method");
            var closedRead = Draft41SymbolName(mapWriter.ToString(), "method:Closed::Read(", "method");
            var lockedRead = Draft41SymbolName(mapWriter.ToString(), "method:Locked::Read(", "method");
            var closedValue = Draft41SymbolName(mapWriter.ToString(), "getter:Closed::Value", "getter");
            var closedIndexer = Draft41SymbolName(mapWriter.ToString(), "getter:Closed::Item", "getter");
            var mainBody = Draft41FunctionBody(generated, "void " + mainName + "(");
            Assert(mainBody.Contains(closedRead + "(", StringComparison.Ordinal) &&
                mainBody.Contains(lockedRead + "(", StringComparison.Ordinal) &&
                mainBody.Contains(closedValue + "(", StringComparison.Ordinal) &&
                mainBody.Contains(closedIndexer + "(", StringComparison.Ordinal) &&
                mainBody.Contains("->Type->VTable->", StringComparison.Ordinal),
                "Sealed direct calls or the required open-receiver virtual call were emitted incorrectly.\n" + mainBody);
            Assert(System.Text.RegularExpressions.Regex.IsMatch(generated,
                    @"return " + System.Text.RegularExpressions.Regex.Escape(closedRead) + @"\([^\n]*ct_target"),
                "A delegate bound to a sealed receiver retained virtual dispatch.");
            Assert(generated.Contains("target->Type->VTable->", StringComparison.Ordinal),
                "A delegate bound through an open base receiver lost virtual dispatch.");
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "71", "40", "100");
        });
    }

    private static string Draft41SymbolName(string map, string identityFragment, string kind)
    {
        using var document = System.Text.Json.JsonDocument.Parse(map);
        foreach (var symbol in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (symbol.GetProperty("kind").GetString() == kind &&
                symbol.GetProperty("identity").GetString()!.Contains(identityFragment, StringComparison.Ordinal))
                return symbol.GetProperty("name").GetString()!;
        }
        throw new InvalidOperationException($"Symbol map omitted {kind} '{identityFragment}'.");
    }

    private static string Draft41FunctionBody(string generated, string signature)
    {
        var declaration = generated.IndexOf(signature, StringComparison.Ordinal);
        var definition = generated.IndexOf(signature, declaration + signature.Length, StringComparison.Ordinal);
        if (definition < 0)
            throw new InvalidOperationException($"Generated C omitted definition '{signature}'.");
        var end = generated.IndexOf("\n}\n", definition, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"Generated C did not terminate definition '{signature}'.");
        return generated[definition..(end + 3)];
    }
}
