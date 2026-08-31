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
    }
}
