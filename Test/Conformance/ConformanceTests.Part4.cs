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

        suite.Run("draft 0.9 opaque ownership and UTF-8 views", () =>
        {
            const string valid = """
                using System.Runtime;
                [NativeType("uintptr_t", "stdint.h")]
                public opaque Handle;
                public static class Native
                {
                    [Extern("native_create")] [ReturnsOwned] public static Handle Create();
                    [Extern("native_read")] public static int Read([Borrowed] Handle value);
                    [Extern("native_release")] public static void Release([Consumes] Handle value);
                    [Extern("native_text")] public static uint Text(NativeUtf8String value);
                    [Extern("native_optional_text")] public static uint OptionalText([Nullable] NativeUtf8String value);
                }
                public static class Program
                {
                    private static void Use()
                    {
                        Handle value = Native.Create();
                        defer Native.Release(value);
                        int result = Native.Read(value);
                        NativeUtf8String text = NativeUtf8String.Borrow("ctilde");
                        uint length = Native.Text(text);
                        uint optional = Native.OptionalText(NativeUtf8String.Null);
                    }
                    [EntryPoint] public static void Main() { Use(); }
                }
                """;
            var validCompilation = Compile(valid);
            Assert(!validCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, validCompilation.GetDiagnostics()));
            var generated = Emit(valid);
            Assert(generated.Contains("typedef struct ct_native_utf8_string", StringComparison.Ordinal), "Native UTF-8 runtime view was not emitted.");
            Assert(generated.Contains("ct_native_utf8_borrow", StringComparison.Ordinal) && generated.Contains("CTS0003", StringComparison.Ordinal), "Native UTF-8 validation was not emitted.");
            Assert(generated.Contains("ct_require_nonnull((void*)", StringComparison.Ordinal) && generated.Contains(".Data", StringComparison.Ordinal), "Non-null native UTF-8 arguments were not checked.");

            const string completionSource = "using System.Runtime; public static class Program { private static void Use() { NativeUtf8String text = NativeUtf8String.Borrow(\"x\"); text. } [EntryPoint] public static void Main() { } }";
            var completionService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(completionSource, "draft09-completion.ct")]);
            var completionPosition = completionSource.IndexOf("text. }", StringComparison.Ordinal) + "text.".Length;
            var completions = completionService.GetCompletions("draft09-completion.ct", completionPosition);
            Assert(completions.Any(item => item.Label == "ByteLength") && completions.Any(item => item.Label == "Pointer"), "Native UTF-8 member completion was incomplete.");

            const string invalid = """
                using System.Runtime;
                [NativeType("uintptr_t", "stdint.h")] public opaque Handle;
                public class Holder { public Handle Stored; public NativeUtf8String Text; }
                public static class Native
                {
                    [Extern("native_create")] [ReturnsOwned] public static Handle Create();
                    [Extern("native_release")] public static void Release([Consumes] Handle value);
                }
                public static class Program
                {
                    private static NativeUtf8String Escape(NativeUtf8String value) { return value; }
                    private static void Leak() { Handle value = Native.Create(); }
                    private static void MoveTwice() { Handle value = Native.Create(); Native.Release(value); Native.Release(value); }
                    private static void Discard() { Native.Create(); }
                    [EntryPoint] public static void Main() { NativeUtf8String text = NativeUtf8String.Borrow("a\0b"); }
                }
                """;
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1242"), "Expected opaque-storage diagnostics.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1254"), "Expected use-after-move diagnostics.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1255"), "Expected discarded-owned-result diagnostics.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1258"), "Expected unresolved-ownership diagnostics: " + string.Join(Environment.NewLine, diagnostics));
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1265"), "Expected NativeUtf8String field-escape diagnostics.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1266"), "Expected NativeUtf8String return-escape diagnostics.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CTS0003"), "Expected literal embedded-NUL diagnostics.");
        });

        suite.Run("draft 0.9 exports headers and synchronous delegates", () =>
        {
            const string source = """
                public delegate int Transformer(int value);
                public static class Native
                {
                    [Extern("native_invoke")]
                    public static int Invoke([SynchronousCallback] Transformer callback, int value);
                }
                public static class Program
                {
                    private static int AddOne(int value) { return value + 1; }
                    [Export("ctilde_add")] public static int Add(int left, int right) { return left + right; }
                    [EntryPoint] public static void Main() { Transformer callback = AddOne; int value = Native.Invoke(callback, 41); }
                }
                """;
            var compilation = Compile(source);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            using var firstWriter = new StringWriter();
            using var secondWriter = new StringWriter();
            Assert(compilation.EmitCHeader(firstWriter).Success && compilation.EmitCHeader(secondWriter).Success, "Header emission failed.");
            Assert(firstWriter.ToString() == secondWriter.ToString(), "Header emission was not deterministic.");
            Assert(firstWriter.ToString().Contains("extern \"C\"", StringComparison.Ordinal) && firstWriter.ToString().Contains("int32_t ctilde_add(int32_t u_4_left, int32_t u_5_right);", StringComparison.Ordinal), "Export header omitted its C/C++ declaration.");
            var generated = Emit(source);
            Assert(generated.Contains("int32_t (*u_8_callback)(int32_t, void*), void* u_8_callback_context", StringComparison.Ordinal), "Synchronous delegate ABI did not place context adjacent to the callback.");
            Assert(generated.Contains("ct_delegate_callback_", StringComparison.Ordinal), "Synchronous delegate adapter was not emitted.");
            Assert(generated.Contains("ct_require_attached_task", StringComparison.Ordinal) && generated.Contains("CTT0001", StringComparison.Ordinal), "Same-task native-entry validation was not emitted.");
            Assert(generated.Contains("int32_t ctilde_add(int32_t u_4_left, int32_t u_5_right)", StringComparison.Ordinal), "Export wrapper was not emitted.");

            var invalid = Compile("public static class Program { [Export(\"same\")] public static string Managed() { return \"x\"; } [Export(\"same\")] public static int Duplicate() { return 1; } [EntryPoint] public static void Main() { } }").GetDiagnostics();
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT1267"), "Expected managed export-signature diagnostics.");
            Assert(invalid.Any(diagnostic => diagnostic.Code == "CT4101"), "Expected duplicate export-symbol diagnostics.");
        });
    }
}
