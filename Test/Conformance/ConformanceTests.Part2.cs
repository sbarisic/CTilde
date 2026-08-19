using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart2(ConformanceSuite suite)
    {
        suite.Run("EntryPoint and Extern validation", () =>
        {
            var noEntry = Compile("public static class Library { public static int Value() { return 1; } }").GetDiagnostics();
            Assert(noEntry.Any(diagnostic => diagnostic.Code == "CT1300"), "Expected a missing EntryPoint diagnostic.");

            const string external = "public static class Program { [Extern(\"native_add\")] public static int Add(int a, int b); [EntryPoint] public static void Main() { } }";
            var generated = Emit(external);
            Assert(generated.Contains("extern int32_t native_add", StringComparison.Ordinal), "Extern declaration was not emitted.");
        });

        suite.Run("readonly flow analysis", () =>
        {
            const string valid = "public static class Program { [EntryPoint] public static void Main() { readonly int value; if (true) value = 1; else value = 2; int copy = value; } }";
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A valid delayed readonly assignment was rejected.");

            const string invalid = "public static class Program { [EntryPoint] public static void Main() { readonly int value; value = 1; value = 2; } }";
            Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3130"), "Expected a duplicate readonly assignment diagnostic.");

            const string singleDo = "public static class Program { [EntryPoint] public static void Main() { readonly int value; do { value = 1; } while (false); int copy = value; } }";
            Assert(!Compile(singleDo).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A one-shot do assignment to readonly storage was rejected.");

            const string repeatedDo = "public static class Program { [EntryPoint] public static void Main() { readonly int value; bool repeat = true; do { value = 1; } while (repeat); } }";
            Assert(Compile(repeatedDo).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3130"), "A repeatable do assignment to readonly storage was accepted.");

            const string repeatedField = "public class Box { public readonly int Value; public Box(bool repeat) { do { Value = 1; } while (repeat); } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(repeatedField).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3131"), "A repeatable constructor assignment to a readonly field was accepted.");
        });

        suite.Run("numeric promotion and compound assignment", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { byte value = (byte)250; value += 10; Console.WriteLine((int)value); } }";
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "4\n", result.StandardOutput);

            const string invalid = "public static class Program { [EntryPoint] public static void Main() { uint value = 1u; uint result = -value; } }";
            Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2145"), "Expected unary minus on uint to be rejected.");
        });

        suite.Run("left-to-right native execution", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static int state = 0;
                    private static int Next() { state += 1; return state; }
                    private static int Pack(int left, int right) { return left * 10 + right; }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(Pack(Next(), Next()));
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "12\n", $"Unexpected output: {result.StandardOutput}");
        });

        suite.Run("constant folding in switch", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        const int Expected = 1 + 1;
                        switch (2)
                        {
                            case Expected:
                                Console.WriteLine("constant");
                                break;
                            default:
                                Console.WriteLine("wrong");
                                break;
                        }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "constant\n", result.StandardOutput);
        });

        suite.Run("while do break and continue", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int index = 0;
                        int total = 0;
                        while (index < 5)
                        {
                            index++;
                            if (index == 2) continue;
                            if (index == 5) break;
                            total += index;
                        }
                        do { total += 1; } while (false);
                        Console.WriteLine(total);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "9\n", result.StandardOutput);
        });

        suite.Run("short circuit and string equality", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static int state = 0;
                    private static bool Touch() { state += 1; return true; }
                    [EntryPoint]
                    public static void Main()
                    {
                        bool first = false && Touch();
                        bool second = true || Touch();
                        string left = "same";
                        string right = "sa" + "me";
                        Console.WriteLine(state);
                        Console.WriteLine(left == right);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "0\nTrue\n", result.StandardOutput);
        });

        suite.Run("lexical forms and standard library overloads", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int café = 1_000_000;
                        int @class = 0xFF;
                        int binary = 0b1010_0110;
                        Console.WriteLine(café);
                        Console.WriteLine(@class);
                        Console.WriteLine(binary);
                        Console.WriteLine('A');
                        Console.WriteLine(42u);
                        Console.WriteLine(1.5f);
                        Console.WriteLine(true);
                        Console.WriteLine();
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "1000000\n255\n166\nA\n42\n1.5\nTrue\n\n", result.StandardOutput);
        });

        suite.Run("bundled standard library", () =>
        {
            var tree = SyntaxTree.ParseText("public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(1); } }", "program.ct");
            var compilation = Compilation.Create([tree]);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = compilation.EmitC(writer);
            Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert(compilation.SyntaxTrees.Length == 1 && ReferenceEquals(compilation.SyntaxTrees[0], tree), "Bundled library trees leaked into Compilation.SyntaxTrees.");
            Assert(writer.ToString().Contains("extern void ct_write_int", StringComparison.Ordinal), "The Console extern declaration was not loaded from the bundled library.");
        });

        suite.Run("System.Math standard library", () =>
        {
            const string source = "public static class Program { public static float Read() { return Math.Sqrt(Math.Pi); } [EntryPoint] public static void Main() { } }";
            var compilation = Compile(source);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, compilation.GetDiagnostics()));

            var editorSource = "public static class Program { public static float Read() { return Math.Sqrt(4.0f); } [EntryPoint] public static void Main() { } }";
            var completionSource = "public static class Program { public static void Read() { Math. } [EntryPoint] public static void Main() { } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(editorSource, "math-editor.ct"), SyntaxTree.ParseText(completionSource, "math-completion.ct")]);
            var completionPosition = completionSource.IndexOf("Math.", StringComparison.Ordinal) + "Math.".Length;
            var completions = service.GetCompletions("math-completion.ct", completionPosition);
            var piCompletion = completions.Single(item => item.Label == "Pi" && item.Kind == LanguageCompletionKind.Field);
            Assert(piCompletion.DocumentationId is not null && service.GetDocumentation(piCompletion.DocumentationId)?.Summary.Contains("single-precision value", StringComparison.Ordinal) == true, "Math.Pi documentation was unavailable.");
            var sqrtCompletion = completions.Single(item => item.Label == "Sqrt");
            Assert(sqrtCompletion.DocumentationId is not null && service.GetDocumentation(sqrtCompletion.DocumentationId)?.Summary.Contains("square root", StringComparison.Ordinal) == true, "Math.Sqrt documentation was unavailable.");
            var sqrtPosition = editorSource.IndexOf("Sqrt", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition("math-editor.ct", sqrtPosition)?.FilePath == "stdlib/System/Math.ct", "Math.Sqrt did not navigate to its embedded declaration.");

            var esp = Compile(source, new CompilationOptions(CompilationTarget.EspIdf));
            Assert(!esp.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "System.Math was unavailable to ESP-IDF.");
        });

        suite.Run("System.Math emission and native behavior", () =>
        {
            const string sqrtOnlySource = "public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(Math.Sqrt(9.0f)); } }";
            var first = Emit(sqrtOnlySource);
            var second = Emit(sqrtOnlySource);
            Assert(first == second, "System.Math emission was not byte-identical.");
            Assert(first.Contains("float ct_math_sqrt(float value) { return sqrtf(value); }", StringComparison.Ordinal), "Math.Sqrt did not emit its native wrapper.");
            foreach (var unused in new[] { "fabsf", "tanf", "fminf", "fmaxf", "sinf", "cosf", "floorf", "ceilf" })
                Assert(!first.Contains($"return {unused}(", StringComparison.Ordinal), $"Unused native math function '{unused}' was emitted.");

            const string behaviorSource = """
                using System;
                public static class Program
                {
                    [NoAlloc]
                    private static float ExerciseAll(float value)
                    {
                        value = Math.Sqrt(value);
                        value = Math.Abs(value);
                        value = Math.Tan(value);
                        value = Math.Min(value, Math.Pi);
                        value = Math.Max(value, -Math.Pi);
                        value = Math.Sin(value);
                        value = Math.Cos(value);
                        value = Math.Floor(value);
                        return Math.Ceiling(value);
                    }

                    private static bool Close(float left, float right)
                    {
                        return Math.Abs(left - right) < 0.0001f;
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        const float pi = Math.Pi;
                        Console.WriteLine(Close(pi, 3.1415927f));
                        Console.WriteLine(Math.Sqrt(9.0f) == 3.0f);
                        Console.WriteLine(Math.Abs(-2.5f) == 2.5f);
                        Console.WriteLine(Close(Math.Tan(Math.Pi / 4.0f), 1.0f));
                        Console.WriteLine(Close(Math.Sin(Math.Pi / 2.0f), 1.0f));
                        Console.WriteLine(Math.Cos(0.0f) == 1.0f);
                        Console.WriteLine(Math.Floor(-1.25f) == -2.0f);
                        Console.WriteLine(Math.Ceiling(-1.25f) == -1.0f);
                        float nan = 0.0f / 0.0f;
                        Console.WriteLine(Math.Min(nan, 2.0f) == 2.0f);
                        Console.WriteLine(Math.Max(2.0f, nan) == 2.0f);
                        float invalid = Math.Sqrt(-1.0f);
                        Console.WriteLine(invalid != invalid);
                        float infinity = 1.0f / 0.0f;
                        Console.WriteLine(Math.Sqrt(infinity) == infinity);
                        Console.WriteLine(ExerciseAll(1.0f) == 0.0f);
                    }
                }
                """;
            var result = CompileAndRun(behaviorSource);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == string.Concat(Enumerable.Repeat("True\n", 13)), result.StandardOutput);

            var generated = Emit(behaviorSource);
            foreach (var mapping in new[]
            {
                "ct_math_sqrt(float value) { return sqrtf(value); }",
                "ct_math_abs(float value) { return fabsf(value); }",
                "ct_math_tan(float value) { return tanf(value); }",
                "ct_math_min(float left, float right) { return fminf(left, right); }",
                "ct_math_max(float left, float right) { return fmaxf(left, right); }",
                "ct_math_sin(float value) { return sinf(value); }",
                "ct_math_cos(float value) { return cosf(value); }",
                "ct_math_floor(float value) { return floorf(value); }",
                "ct_math_ceiling(float value) { return ceilf(value); }",
            })
                Assert(generated.Contains(mapping, StringComparison.Ordinal), $"Generated C omitted math mapping '{mapping}'.");

            var espGenerated = Emit(behaviorSource, new CompilationOptions(CompilationTarget.EspIdf));
            Assert(espGenerated.Contains("return sqrtf(value);", StringComparison.Ordinal) && espGenerated.Contains("return tanf(value);", StringComparison.Ordinal), "ESP-IDF output omitted native math wrappers.");
        });

        suite.Run("System.Math runtime symbols are reserved", () =>
        {
            foreach (var symbol in new[] { "ct_math_sqrt", "ct_math_abs", "ct_math_tan", "ct_math_min", "ct_math_max", "ct_math_sin", "ct_math_cos", "ct_math_floor", "ct_math_ceiling" })
            {
                var source = $"public static class Native {{ [Extern(\"{symbol}\")] public static float Call(float value); }} public static class Program {{ [EntryPoint] public static void Main() {{ }} }}";
                Assert(Compile(source).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), $"Compiler-owned math symbol '{symbol}' was not reserved.");
            }
        });

        suite.Run("scalar ToString", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static int calls = 0;
                    private static int Next() { calls++; return 7; }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        byte unsignedByte = (byte)255;
                        sbyte signedByte = (sbyte)(-128);
                        short signedShort = (short)(-32768);
                        ushort unsignedShort = (ushort)65535;
                        int signedInt = -2147483647 - 1;
                        uint unsignedInt = 4294967295u;
                        int zero = 0;
                        float number = 1.5f;
                        bool flag = true;
                        char character = 'A';
                        string text = "text";
        
                        Console.WriteLine(unsignedByte.ToString());
                        Console.WriteLine(signedByte.ToString());
                        Console.WriteLine(signedShort.ToString());
                        Console.WriteLine(unsignedShort.ToString());
                        Console.WriteLine(signedInt.ToString());
                        Console.WriteLine(unsignedInt.ToString());
                        Console.WriteLine(zero.ToString());
                        Console.WriteLine(number.ToString());
                        Console.WriteLine(flag.ToString());
                        Console.WriteLine(character.ToString());
                        Console.WriteLine(text.ToString());
                        Console.WriteLine(text.ToString() == text);
                        Console.WriteLine(Next().ToString());
                        Console.WriteLine(calls);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "255\n-128\n-32768\n65535\n-2147483648\n4294967295\n0\n1.5\nTrue\nA\ntext\nTrue\n7\n1\n", result.StandardOutput);
        });

        suite.Run("ToString diagnostics", () =>
        {
            const string source = """
                public sealed class Box { }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int value = 1;
                        string invalidArguments = value.ToString(2);
                        Box box = new Box();
                        string unsupportedObject = box.ToString();
                        int[] values = new int[0];
                        string unsupportedArray = values.ToString();
                    }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2122") == 1, "Expected only the invalid ToString argument diagnostic.");
        });

        suite.Run("64-bit integers", () =>
        {
            const string source = """
                using System;
                public enum Wide : ulong { Maximum = 18446744073709551615UL }
                public static class Program
                {
                    private static int Pick(long value) { return 64; }
                    private static int Pick(ulong value) { return 65; }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        long signedMinimum = -9223372036854775808L;
                        ulong unsignedMaximum = 18446744073709551615lu;
                        long inferred = 4294967296;
                        long promoted = 1u + -2;
                        long wrapped = 9223372036854775807L + 1L;
                        long shifted = 1L << 63;
                        long shiftedCount = 1L << 65;
                        long dividedMinimum = signedMinimum / -1L;
                        long remainderMinimum = signedMinimum % -1L;
                        ulong wrappedUnsigned = 0UL - 1UL;
                        int truncated = (int)4294967297L;
                        ulong suffixUl = 1UL;
                        ulong suffixLu = 1LU;
                        ulong suffixLower = 1ul + 1lu;
                        long suffixLong = 1l;
                        long[] values = new long[1];
                        values[0] = inferred;
                        object boxed = unsignedMaximum;
                        Console.WriteLine(signedMinimum);
                        Console.WriteLine(unsignedMaximum);
                        Console.WriteLine(inferred);
                        Console.WriteLine(promoted);
                        Console.WriteLine(wrapped == shifted);
                        Console.WriteLine(Pick(4294967296));
                        Console.WriteLine(Pick(18446744073709551615UL));
                        Console.WriteLine((ulong)boxed == (ulong)Wide.Maximum);
                        Console.WriteLine(unsignedMaximum.ToString());
                        Console.WriteLine(shiftedCount);
                        Console.WriteLine(dividedMinimum == signedMinimum && remainderMinimum == 0L);
                        Console.WriteLine(wrappedUnsigned == unsignedMaximum);
                        Console.WriteLine(truncated);
                        Console.WriteLine(suffixUl + suffixLu + suffixLower + (ulong)suffixLong);
                        Console.WriteLine(values[0]);
                        switch ((Wide)unsignedMaximum) { case Wide.Maximum: Console.WriteLine("wide"); break; }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "-9223372036854775808\n18446744073709551615\n4294967296\n-1\nTrue\n64\n65\nTrue\n18446744073709551615\n2\nTrue\nTrue\n1\n5\n4294967296\nwide\n", result.StandardOutput);

            const string malformed = "public static class Program { [EntryPoint] public static void Main() { ulong a = 1UU; ulong b = 1LF; ulong c = 18446744073709551616UL; } }";
            var diagnostics = Compile(malformed).GetDiagnostics();
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT0002") == 2, "Malformed integer suffixes were not rejected.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2112"), "Overflowing ulong literal was not rejected.");

            var mixedSignedUnsigned = Compile("public static class Program { [EntryPoint] public static void Main() { ulong invalid = 1UL + -1L; } }").GetDiagnostics();
            Assert(mixedSignedUnsigned.Any(diagnostic => diagnostic.Code == "CT2130"), "ulong combined with a signed integral type was accepted.");
        });

        suite.Run("named delegates and ARC", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
        
                public delegate int Transformer(int value);
        
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
        
                public class Base
                {
                    public virtual int Transform(int value) { return value + 1; }
                }
        
                public class Derived : Base
                {
                    public override int Transform(int value) { return value * 2; }
                    public Transformer CaptureBase() { return base.Transform; }
                }
        
                public static class Program
                {
                    private static int StaticTransform(int value) { return value + 20; }
                    private static long StaticTransform(long value) { return value + 200L; }
                    private static void Run()
                    {
                        Transformer first = StaticTransform;
                        Derived receiver = new Derived();
                        Transformer second = receiver.Transform;
                        Transformer directBase = receiver.CaptureBase();
                        receiver = null;
                        Console.WriteLine(first(22));
                        Console.WriteLine(second(21));
                        Console.WriteLine(directBase(21));
                        Console.WriteLine(first == second);
                        first = null;
                        second = null;
                        directBase = null;
                    }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        Run();
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n42\n22\nFalse\nTrue\n", result.StandardOutput);

            const string nullInvoke = "public delegate int Reader(); public static class Program { [EntryPoint] public static void Main() { Reader value = null; int result = value(); } }";
            var failure = CompileAndRun(nullInvoke);
            Assert(failure.ExitCode != 0 && failure.StandardError.Contains("CTN0001", StringComparison.Ordinal), "Null delegate invocation did not report CTN0001.");

            const string containersAndExceptions = """
                using System;
                public delegate int Reader();
                public class Target { public int Read() { return 42; } }
                public struct Holder { public Reader Value; public Holder(Reader value) { Value = value; } }
                public static class Diagnostics { [Extern("ct_memory_diagnostic_live_allocations")] [NoAlloc] public static uint LiveAllocations(); }
                public static class Program
                {
                    private static int Throwing() { throw new Exception("through delegate"); }
                    private static void Run()
                    {
                        Target target = new Target();
                        Reader reader = target.Read;
                        Holder holder = new Holder(reader);
                        Reader[] readers = new Reader[1];
                        readers[0] = holder.Value;
                        object boxed = holder;
                        Holder copy = (Holder)boxed;
                        target = null;
                        reader = null;
                        Console.WriteLine(copy.Value());
                        Reader throwing = Throwing;
                        try { throwing(); }
                        catch (Exception error) { Console.WriteLine(error.Message); }
                    }
                    [EntryPoint] public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        Run();
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            var containers = CompileAndRun(containersAndExceptions, memoryDiagnostics: true);
            Assert(containers.ExitCode == 0, containers.StandardError);
            Assert(Normalize(containers.StandardOutput) == "42\nthrough delegate\nTrue\n", containers.StandardOutput);
        });

        suite.Run("unmanaged function pointers", () =>
        {
            const string source = """
                using System;
                public static class Native
                {
                    [Extern("ct_test_invoke_i64")]
                    public static unsafe long Invoke(delegate* unmanaged<long, long> callback, long value);
                    [Extern("ct_test_identity_i64")]
                    public static long Identity(long value);
                }
                public static class Program
                {
                    private static unsafe delegate* unmanaged<long, long> stored;
                    private static long Transform(long value) { return value * 2L; }
                    private static unsafe delegate* unmanaged<long, long> GetTransform() { return &Transform; }
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        stored = GetTransform();
                        delegate* unmanaged<long, long> callback = (delegate* unmanaged<long, long>)stored;
                        delegate* unmanaged<long, long> missing = (delegate* unmanaged<long, long>)null;
                        Console.WriteLine(callback(21L));
                        Console.WriteLine(Native.Invoke(callback, 21L));
                        Console.WriteLine(callback != null);
                        delegate* unmanaged<long, long> external = &Native.Identity;
                        Console.WriteLine(external(21L));
                    }
                }
                """;
            const string nativeSuffix = "\nint64_t ct_test_invoke_i64(int64_t (*callback)(int64_t), int64_t value) { return callback(value); }\nint64_t ct_test_identity_i64(int64_t value) { return value; }\n";
            var generated = Emit(source);
            Assert(generated.Contains("extern int64_t ct_test_invoke_i64(int64_t (*", StringComparison.Ordinal), "Extern function-pointer parameters did not use a C declarator.");
            Assert(generated.Contains("static CT_UNUSED int64_t (*ct_f_", StringComparison.Ordinal), "Function-pointer fields did not use a C declarator.");
            Assert(generated.Contains("int64_t (*ct_l_", StringComparison.Ordinal), "Function-pointer locals did not use a C declarator.");
            Assert(generated.Contains("static CT_UNUSED int64_t (*ct_m_", StringComparison.Ordinal), "Function-pointer returns did not use a C declarator.");
            Assert(generated.Contains("(int64_t (*)(int64_t))", StringComparison.Ordinal), "Function-pointer casts did not use an unnamed C declarator.");
            Assert(generated.Contains("&ct_test_identity_i64", StringComparison.Ordinal), "An extern method address did not use its native C symbol directly.");
            var result = CompileAndRun(source, nativeSuffix: nativeSuffix);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n42\nTrue\n21\n", result.StandardOutput);

            const string escapingException = "using System; public static class Native { [Extern(\"ct_test_invoke_i64\")] public static unsafe long Invoke(delegate* unmanaged<long, long> callback, long value); } public static class Program { private static long Fail(long value) { defer Console.WriteLine(\"callback cleanup\"); throw new Exception(\"callback\"); } [EntryPoint] public static unsafe void Main() { delegate* unmanaged<long, long> callback = &Fail; Native.Invoke(callback, 1L); } }";
            var failure = CompileAndRun(escapingException, nativeSuffix: nativeSuffix);
            Assert(failure.ExitCode != 0 && failure.StandardError.Contains("CTE0003", StringComparison.Ordinal) && Normalize(failure.StandardOutput) == "callback cleanup\n", "A callback exception crossed the native boundary or skipped C~ cleanup.");

            const string invalid = "public delegate int Reader(int value); public class Item { public int Instance(int value) { return value; } } public static class Program { private static int Static(int value) { return value; } [EntryPoint] public static void Main() { delegate* unmanaged<Item, int> invalid; Item item = new Item(); delegate* unmanaged<int, int> callback = &item.Instance; unsafe { Reader managed = Static; delegate* unmanaged<int, int> forbidden = managed; delegate* unmanaged<long, long> mismatch = &Static; } } }";
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2162"), "A managed function-pointer signature was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2139"), "Function-pointer operations were accepted outside unsafe context.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2163"), "An instance method address was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2137"), "A delegate implicitly converted to an unmanaged function pointer.");
        });

        suite.Run("System.Object inheritance dispatch and boxing", () =>
        {
            const string source = """
                using System;
                public class Animal
                {
                    protected int value;
                    public Animal(int value) { this.value = value; }
                    public virtual string Speak() { return "animal"; }
                    public virtual int Number { get { return value; } }
                    public override string ToString() { return "Animal"; }
                }
                public class Dog : Animal
                {
                    public Dog() : this(7) { }
                    private Dog(int value) : base(value) { }
                    public override string Speak() { return base.Speak() + " dog"; }
                    public override int Number { get { return value + 1; } }
                    public sealed override string ToString() { return "Dog"; }
                }
                public class Cat : Animal
                {
                    public Cat() : base(3) { }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Dog dog = new Dog();
                        Animal animal = dog;
                        object value = animal;
                        Console.WriteLine(animal.Speak());
                        Console.WriteLine(animal.Number);
                        Console.WriteLine(value.ToString());
                        Console.WriteLine(animal is Dog);
                        Dog cast = (Dog)animal;
                        Console.WriteLine(cast.Speak());
                        Cat missing = animal as Cat;
                        Console.WriteLine(missing == null);
                        object first = 42;
                        object second = 42;
                        Console.WriteLine(Object.Equals(first, second));
                        Console.WriteLine(Object.ReferenceEquals(first, second));
                        Console.WriteLine((int)first);
                        Console.WriteLine(first.ToString());
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "animal dog\n8\nDog\nTrue\nanimal dog\nTrue\nTrue\nFalse\n42\n42\n", result.StandardOutput);
        });

        suite.Run("constructor order and virtual dispatch", () =>
        {
            const string source = "using System; public class Base { protected int value = 1; public Base() { Console.WriteLine(Read()); Console.WriteLine(value); } public virtual int Read() { return value; } } public class Derived : Base { private int derived = 5; public Derived() : base() { Console.WriteLine(Read()); } public override int Read() { return derived; } } public static class Program { [EntryPoint] public static void Main() { Derived value = new Derived(); } }";
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "0\n1\n5\n", result.StandardOutput);
        });

        suite.Run("unsafe pointer object boxing", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { unsafe { int value = 9; int* pointer = &value; object boxed = pointer; Console.WriteLine(boxed is int*); int* copy = (int*)boxed; Console.WriteLine(*copy); } } }";
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n9\n", result.StandardOutput);
        });

        suite.Run("inheritance diagnostics", () =>
        {
            const string source = "public sealed class Closed { } public class Invalid : Closed { } public class Base { public virtual int Value() { return 1; } protected int field; public virtual int Property { get; protected set; } private virtual int Hidden() { return 0; } } public class Derived : Base { public int Value() { return 2; } private int field; public override int Property { get; set; } public sealed override string ToString() { return \"Derived\"; } } public class Further : Derived { public override string ToString() { return \"Further\"; } } public static class Program { [EntryPoint] public static void Main() { } }";
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1227"), "A sealed base was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1230"), "Inherited member hiding was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1229"), "A sealed virtual slot was overridden.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1228"), "A private virtual member was accepted.");
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT1229") >= 2, "An override changed accessor accessibility.");

            const string invalidAs = "public static class Program { [EntryPoint] public static void Main() { object value = 1 as object; } }";
            Assert(Compile(invalidAs).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2147"), "The as operator boxed a value-type source.");

            const string virtualStruct = "public struct Value { public virtual int Read() { return 1; } public virtual int Property { get; } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(virtualStruct).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1228") >= 2, "A structure declared an ordinary virtual member.");
        });

        suite.Run("object syntax surface", () =>
        {
            const string source = "public class Base { public Base(int value) { } } public class Derived : Base { public Derived() : this(1) { } private Derived(int value) : base(value) { } public bool Check(object value) { return value is Derived && value as Derived != null; } }";
            var tree = SyntaxTree.ParseText(source, "object-syntax.ct");
            Assert(tree.ToFullString() == source, "Draft 0.5 object syntax did not round-trip.");
            var derived = tree.Root.Types.Single(type => type.Name == "Derived");
            Assert(derived.BaseType?.Name == "Base", "The class base clause was not retained.");
            Assert(derived.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Initializer?.Kind == ConstructorInitializerKind.This), "A this constructor initializer was not retained.");
            Assert(derived.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Initializer?.Kind == ConstructorInitializerKind.Base), "A base constructor initializer was not retained.");
        });

        suite.Run("enum and struct object behavior", () =>
        {
            const string source = """
                using System;
                public enum State : int { None = 0, Ready = 2, Alias = 2 }
                public struct Pair
                {
                    public int X;
                    public Pair(int value) { X = value; }
                    public override string ToString() { return X.ToString(); }
                    public override bool Equals(object value)
                    {
                        if (!(value is Pair)) return false;
                        Pair other = (Pair)value;
                        return X == other.X;
                    }
                    public override int GetHashCode() { return X; }
                }
                public struct Plain
                {
                    public int X;
                    public string Text;
                    public Plain(int value, string text) { X = value; Text = text; }
                }
                public class Key
                {
                    private int value;
                    public Key(int value) { this.value = value; }
                    public override bool Equals(object other) { return other is Key && ((Key)other).value == value; }
                    public override int GetHashCode() { return value; }
                }
                public struct Inner
                {
                    public Key Key;
                    public Inner(Key key) { Key = key; }
                }
                public struct Outer
                {
                    public Inner Inner;
                    public Outer(Inner inner) { Inner = inner; }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(State.Ready.ToString());
                        Console.WriteLine(((State)3).ToString());
                        object left = new Pair(5);
                        object right = new Pair(5);
                        Console.WriteLine(left.ToString());
                        Console.WriteLine(Object.Equals(left, right));
                        Console.WriteLine(left.GetHashCode() == right.GetHashCode());
                        object plainLeft = new Plain(4, "same");
                        object plainRight = new Plain(4, "same");
                        Console.WriteLine(Object.Equals(plainLeft, plainRight));
                        Console.WriteLine(plainLeft.GetHashCode() == plainRight.GetHashCode());
                        object outerLeft = new Outer(new Inner(new Key(8)));
                        object outerRight = new Outer(new Inner(new Key(8)));
                        Console.WriteLine(Object.Equals(outerLeft, outerRight));
                        Console.WriteLine(outerLeft.GetHashCode() == outerRight.GetHashCode());
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "Ready\n3\n5\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n", result.StandardOutput);
        });

        suite.Run("object cast runtime failures", () =>
        {
            const string invalidReference = "public class Base { } public class Left : Base { } public class Right : Base { } public static class Program { [EntryPoint] public static void Main() { Base value = new Left(); Right invalid = (Right)value; } }";
            var cast = CompileAndRun(invalidReference);
            Assert(cast.ExitCode != 0 && cast.StandardError.Contains("CTO0001", StringComparison.Ordinal), cast.StandardError);

            const string nullUnbox = "public static class Program { [EntryPoint] public static void Main() { object value = null; int invalid = (int)value; } }";
            var nullResult = CompileAndRun(nullUnbox);
            Assert(nullResult.ExitCode != 0 && nullResult.StandardError.Contains("CTO0002", StringComparison.Ordinal), nullResult.StandardError);

            const string wrongUnbox = "public static class Program { [EntryPoint] public static void Main() { object value = 1u; int invalid = (int)value; } }";
            var wrongResult = CompileAndRun(wrongUnbox);
            Assert(wrongResult.ExitCode != 0 && wrongResult.StandardError.Contains("CTO0003", StringComparison.Ordinal), wrongResult.StandardError);
        });

        suite.Run("constructor and hierarchy cycles", () =>
        {
            const string inheritance = "public class A : B { } public class B : A { } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(inheritance).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1226"), "An inheritance cycle was accepted.");
            const string constructors = "public class Loop { public Loop() : this(1) { } private Loop(int value) : this() { } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(constructors).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1232"), "A constructor cycle was accepted.");
        });

        suite.Run("null string ToString failure", () =>
        {
            const string source = "public static class Program { [EntryPoint] public static void Main() { string text = null; string copy = text.ToString(); } }";
            var result = CompileAndRun(source);
            Assert(result.ExitCode != 0, "Null string ToString returned success.");
            Assert(result.StandardError.Contains("CTN0001", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("Environment Exit", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { try { Environment.Exit(7); } finally { Console.WriteLine(1); } } }";
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 7, $"Expected exit code 7, got {result.ExitCode}. {result.StandardError}");
            Assert(result.StandardOutput.Length == 0, result.StandardOutput);
        });

        suite.Run("objects arrays strings and control flow", () =>
        {
            const string source = """
                using System;
        
                public sealed class Counter
                {
                    public Counter(int initial) { Value = initial; }
                    public int Value { get; private set; }
                    public void Increment() { Value++; }
                }
        
                public sealed class Defaults
                {
                    public int Value = 8;
                }
        
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Counter counter = new Counter(2);
                        counter.Increment();
                        int[] values = new int[3];
                        for (int index = 0; index < values.Length; index++)
                            values[index] = index + 1;
                        int total = 0;
                        foreach (int value in values)
                            total += value;
                        string text = null + "ok";
                        byte small = (byte)7;
                        Defaults defaults = new Defaults();
                        Console.WriteLine(counter.Value);
                        Console.WriteLine(total);
                        Console.WriteLine(text);
                        Console.WriteLine(small);
                        Console.WriteLine(defaults.Value);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "3\n6\nok\n7\n8\n", $"Unexpected output: {result.StandardOutput}");
        });
    }
}
