using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart4(ConformanceSuite suite)
    {
        suite.Run("native-sized integer rules", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static int Pick(int value) { return 1; }
                    private static int Pick(nint value) { return 2; }
                    [EntryPoint]
                    public static void Main()
                    {
                        nint signed = (nint)9223372036854775807L;
                        nuint unsigned = (nuint)18446744073709551615UL;
                        Console.WriteLine((signed + (nint)1) - signed);
                        Console.WriteLine(unsigned >> 100);
                        Console.WriteLine(Pick((nint)1));
                        nint boxedValue = 42;
                        object boxed = boxedValue;
                        Console.WriteLine((nint)boxed);
                        Console.WriteLine(boxedValue.ToString());
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            var expectedShift = IntPtr.Size == 8 ? "268435455" : "0";
            Assert(Normalize(result.StandardOutput) == $"1\n{expectedShift}\n2\n42\n42\n", $"Unexpected native-sized integer output: {result.StandardOutput}");

            var invalid = Compile("""
                public enum Invalid : nint { Zero }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        nint signed = 1;
                        nuint unsigned = 1U;
                        long tooLarge = 4294967296L;
                        nint needsCast = tooLarge;
                        nint mixed = signed + unsigned;
                    }
                }
                """).GetDiagnostics();
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT1208"), "Expected native enum-underlying-type diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2137"), "Expected explicit-conversion diagnostics for nonportable native integer assignment.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2130"), "Expected mixed-sign native arithmetic diagnostics.");
        });

        suite.Run("by-reference callable ABI", () =>
        {
            const string source = """
                using System;
                public delegate void Adjuster(ref int value, in int add, out uint result);
                public static class Program
                {
                    private static void Adjust(ref int value, in int add, out uint result)
                    {
                        value = value + add;
                        result = (uint)value;
                    }

                    [Extern("native_adjust")]
                    private static void NativeAdjust(ref int value, in int add, out uint result);

                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        readonly int add = 2;
                        int first = 40;
                        uint firstResult;
                        Adjuster managed = Adjust;
                        managed(ref first, in add, out firstResult);
                        Console.WriteLine(firstResult);

                        int second = 40;
                        uint secondResult;
                        delegate* unmanaged<ref int, in int, out uint, void> pointer = &Adjust;
                        pointer(ref second, in add, out secondResult);
                        Console.WriteLine(secondResult);

                        int third = 40;
                        uint thirdResult;
                        NativeAdjust(ref third, in add, out thirdResult);
                        Console.WriteLine(thirdResult);
                    }
                }
                """;
            const string native = """

                void native_adjust(int32_t* value, const int32_t* add, uint32_t* result)
                {
                    *value += *add;
                    *result = (uint32_t)*value;
                }
                """;
            var result = CompileAndRun(source, nativeSuffix: native);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n42\n42\n", $"Unexpected by-reference ABI output: {result.StandardOutput}");

            var generated = Emit(source);
            Assert(generated.Contains("int32_t* u_5_value, const int32_t* u_3_add, uint32_t* u_6_result", StringComparison.Ordinal), "By-reference C declarations did not use pointer/const-pointer ABI mappings.");
            Assert(generated.Contains("Adjust_refi32_ini32_outu32", StringComparison.Ordinal), "By-reference passing kinds were not encoded in mangled names.");
        });

        suite.Run("draft 0.8 native ABI foundations", () =>
        {
            const string source = """
                using System;
                using System.Runtime;

                public static class Program
                {
                    private static void Adjust(ref int value, in int add, out uint result)
                    {
                        value = value + add;
                        result = (uint)value;
                    }

                    [NoAlloc]
                    private static unsafe void Fill(NativeBuffer<byte> data)
                    {
                        data[0] = (byte)42;
                    }

                    [Extern("native_sum")]
                    [NoAlloc]
                    private static unsafe uint NativeSum(ReadOnlyNativeBuffer<byte> data);

                    [NoAlloc]
                    private static unsafe uint StackSum()
                    {
                        NativeBuffer<byte> data = stackalloc byte[1];
                        Fill(data);
                        ReadOnlyNativeBuffer<byte> readable = data;
                        return NativeSum(readable);
                    }

                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        nint native = 40;
                        nuint unsignedNative = 2U;
                        Console.WriteLine(native + (nint)unsignedNative);

                        int value = 40;
                        readonly int add = 2;
                        uint result;
                        Adjust(ref value, in add, out result);
                        Console.WriteLine(result);

                        Console.WriteLine(StackSum());

                        NativeBuffer<byte> buffer = stackalloc byte[1];
                        Fill(buffer);
                        void* raw = buffer.Pointer;
                        byte* pointer = (byte*)raw;
                        Console.WriteLine((int)pointer[0]);
                    }
                }
                """;
            const string native = """

                uint32_t native_sum(const uint8_t* data, size_t length)
                {
                    uint32_t result = 0;
                    for (size_t index = 0; index < length; index++) result += data[index];
                    return result;
                }
                """;
            var result = CompileAndRun(source, nativeSuffix: native);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n42\n42\n42\n", $"Unexpected Draft 0.8 output: {result.StandardOutput}");

            var invalid = Compile("""
                using System.Runtime;
                public static class Program
                {
                    private static void Bad(out int value) { int copy = value; }
                    [EntryPoint] public static unsafe void Main()
                    {
                        ReadOnlyNativeBuffer<byte> data = new ReadOnlyNativeBuffer<byte>((byte*)null, 0U);
                        data[0] = 1;
                        while (false) { NativeBuffer<byte> loop = stackalloc byte[1]; }
                    }
                }
                """).GetDiagnostics();
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2174"), "Expected out-before-assignment diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2175"), "Expected required out-assignment diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2179"), "Expected readonly-buffer diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2182"), "Expected loop stackalloc diagnostics.");
        });

        suite.Run("native buffer safety and editor services", () =>
        {
            const string source = """
                using System.Runtime;
                public static class Program
                {
                    private static unsafe void Fill(NativeBuffer<byte> buffer) { buffer[0] = (byte)42; }
                    [EntryPoint] public static unsafe void Main()
                    {
                        nint offset = 1;
                        NativeBuffer<byte> buffer = stackalloc byte[(nuint)2];
                        Fill(buffer);
                        void* raw = buffer.Pointer + offset;
                    }
                }
                """;
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "draft08-editor.ct")]);
            Assert(!service.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, service.Diagnostics));
            const string completionSource = "using System.Runtime; public static class Program { [EntryPoint] public static unsafe void Main() { NativeBuffer<byte> buffer = stackalloc byte[1]; buffer. } }";
            var completionService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(completionSource, "draft08-completion.ct")]);
            var completionPosition = completionSource.IndexOf("buffer.", StringComparison.Ordinal) + "buffer.".Length;
            var completions = completionService.GetCompletions("draft08-completion.ct", completionPosition);
            Assert(completions.Any(item => item.Label == "Length"), "Native-buffer completion omitted Length.");
            Assert(completions.Any(item => item.Label == "Pointer"), "Native-buffer completion omitted Pointer.");
            var hoverPosition = source.IndexOf("nint offset", StringComparison.Ordinal) + 1;
            Assert(service.GetHover("draft08-editor.ct", hoverPosition)?.Contents.Contains("nint", StringComparison.Ordinal) == true, "Native-integer hover did not expose nint.");
            var tokens = service.GetSemanticTokens("draft08-editor.ct");
            var bufferTypePosition = source.IndexOf("NativeBuffer<byte>", StringComparison.Ordinal);
            Assert(tokens.Any(token => token.Span.Start == bufferTypePosition && token.Kind == LanguageSemanticTokenKind.Struct && token.Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary)), "Intrinsic buffer type was not semantically classified.");

            var invalid = Compile("""
                using System.Runtime;
                public class Holder { public NativeBuffer<byte> Field; }
                public static class Program
                {
                    private static NativeBuffer<byte> Escape(NativeBuffer<byte> value) { return value; }
                    private static void RefBuffer(ref NativeBuffer<byte> value) { }
                    [EntryPoint] public static unsafe void Main()
                    {
                        NativeBuffer<string> managed = stackalloc string[1];
                        NativeBuffer<byte> value = stackalloc byte[1];
                        RefBuffer(ref value);
                        void* raw = value.Pointer;
                        byte item = *raw;
                    }
                }
                """).GetDiagnostics();
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2185"), "Expected buffer-field escape diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2186"), "Expected buffer-return escape diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2187"), "Expected by-reference buffer diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2177"), "Expected managed buffer-element diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT2180"), "Expected void-pointer dereference diagnostics.");
        });
    }
}
