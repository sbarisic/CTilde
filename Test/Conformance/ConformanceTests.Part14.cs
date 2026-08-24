using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart14(ConformanceSuite suite)
    {
        suite.Run("draft 0.16 aggregate layout syntax and diagnostics", () =>
        {
            const string syntax = "[Packed(2)] public struct H { public byte A; public int B; } public struct R { [FieldOffset(0)] public uint V; [FieldOffset(2)] public ushort H; } public union U { public int I; public float F; } public static class P { [EntryPoint] public static void Main() { nuint a = sizeof(R); nuint b = alignof(U); nuint c = offsetof(R, H); } }";
            var tree = SyntaxTree.ParseText(syntax, "draft16-layout.ct");
            Assert(tree.ToFullString() == syntax, "Draft 0.16 layout syntax did not round-trip exactly.");
            Assert(tree.Tokens.Any(token => token.Kind == SyntaxKind.UnionKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.SizeofKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.AlignofKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.OffsetofKeyword), "Draft 0.16 layout keywords were not classified.");
            Assert(!Compile(syntax).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, Compile(syntax).GetDiagnostics()));
            var service = LanguageServiceSnapshot.Create([tree]);
            var completions = service.GetCompletions("draft16-layout.ct", syntax.IndexOf("nuint a", StringComparison.Ordinal) - 1);
            Assert(completions.Any(item => item.Label == "sizeof") && completions.Any(item => item.Label == "alignof") && completions.Any(item => item.Label == "offsetof"), "Layout operators were missing from completion.");
            var typeUse = syntax.IndexOf("sizeof(R)", StringComparison.Ordinal) + "sizeof(".Length;
            Assert(service.GetHover("draft16-layout.ct", typeUse)?.Contents.Contains("layout: explicit", StringComparison.Ordinal) == true, "Explicit layout metadata was missing from hover.");
            var fieldUse = syntax.IndexOf("offsetof(R, H)", StringComparison.Ordinal) + "offsetof(R, ".Length;
            Assert(service.GetHover("draft16-layout.ct", fieldUse)?.Contents.Contains("offset: 2", StringComparison.Ordinal) == true, "Field offset metadata was missing from hover.");
            const string implicitNativeInteger = "using System; public struct OnlyLayout { public int Value; } public static class ImplicitNativeInteger { [EntryPoint] public static void Main() { Console.WriteLine(sizeof(OnlyLayout)); } }";
            Assert(Emit(implicitNativeInteger).Contains("ct_write_nuint", StringComparison.Ordinal), "A layout operator did not load the native-integer standard-library surface without an explicit nuint token.");

            const string invalid = "[Packed(3)] public struct BadPack { public int X; } [Packed(2)] public struct PackedAddress { public int X; } public struct Missing { [FieldOffset(0)] public int X; public int Y; } [Packed(1)] public struct Managed { public string Text; } public union BadUnion { public int X; public int Read() { return X; } } public static class P { [EntryPoint] public static unsafe void Main() { PackedAddress p = new PackedAddress(); int* q = &p.X; } }";
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1280"), "Invalid packing was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1283"), "A missing explicit field offset was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1285"), "Managed packed storage was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1282"), "An instance union method was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2190"), "A packed field address was accepted.");

            const string additionalInvalid = "[Packed(1)] public class BadTarget { } public struct Negative { [FieldOffset(-1)] public int X; } public struct Duplicate { [FieldOffset(0)][FieldOffset(1)] public int X; } public struct Initialized { [FieldOffset(0)] public int X = 1; } public struct Automatic { [FieldOffset(0)] public int X; public int Y { get; set; } } [Packed(1)] public struct VolatileStorage { public volatile int X; } [Packed(1)] public struct GenericStorage<T> { public T Value; } public union BadMembers { public int X; public int P { get { return X; } } public BadMembers() { X = 0; } } public static class More { private static void Take(ref int value) { } private static nuint PointerSize() { return sizeof(int*); } [EntryPoint] public static void Main() { PackedAddress p = new PackedAddress(); Take(ref p.X); } }";
            var additionalDiagnostics = Compile(invalid + additionalInvalid).GetDiagnostics();
            Assert(additionalDiagnostics.Any(diagnostic => diagnostic.Code == "CT1213"), "Packed was accepted on a class.");
            Assert(additionalDiagnostics.Count(diagnostic => diagnostic.Code == "CT1281") >= 1, "A negative field offset was accepted.");
            Assert(additionalDiagnostics.Any(diagnostic => diagnostic.Code == "CT1214"), "Duplicate field offsets were accepted.\n" + string.Join("\n", additionalDiagnostics));
            Assert(additionalDiagnostics.Any(diagnostic => diagnostic.Code == "CT1284"), "An explicit field initializer was accepted.");
            Assert(additionalDiagnostics.Any(diagnostic => diagnostic.Code == "CT1283"), "An explicit-layout auto-property was accepted.");
            Assert(additionalDiagnostics.Count(diagnostic => diagnostic.Code == "CT1285") >= 2, "Volatile or unconstrained generic packed storage was accepted.");
            Assert(additionalDiagnostics.Count(diagnostic => diagnostic.Code == "CT1282") >= 2, "Forbidden union members were accepted.");
            Assert(additionalDiagnostics.Count(diagnostic => diagnostic.Code == "CT2190") >= 2, "Packed field by-reference use was accepted.");
            Assert(additionalDiagnostics.Any(diagnostic => diagnostic.Message.Contains("unsafe", StringComparison.OrdinalIgnoreCase)), "A pointer layout operand was accepted outside unsafe context.");
        });

        suite.Run("draft 0.16 aggregate layouts and operators runtime", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                [Packed(2)]
                public struct Header { public byte Kind; public int Length; }
                [Packed(1)] public struct Pack1 { public byte A; public int B; }
                [Packed(2)] public struct Pack2 { public byte A; public int B; }
                [Packed(4)] public struct Pack4 { public byte A; public int B; }
                [Packed(8)] public struct Pack8 { public byte A; public int B; }
                [Packed(16)] public struct Pack16 { public byte A; public int B; }
                [Packed(1)] public struct Cell<T> where T : unmanaged { public byte Tag; public T Value; }
                [Packed(2)] public struct Nested { public byte Tag; public Header Value; }
                public struct Register
                {
                    [FieldOffset(0)] public uint Value;
                    [FieldOffset(0)] public ushort Low;
                    [FieldOffset(2)] public ushort High;
                }
                [Packed(1)]
                public struct Unaligned { [FieldOffset(1)] public int Value; }
                public union NumberBits { public int Integer; public float Float; }
                public static class Program
                {
                    private const nuint numberSize = sizeof(NumberBits);
                    [EntryPoint] public static unsafe void Main()
                    {
                        const nuint registerSize = sizeof(Register);
                        const bool registerIsFour = sizeof(Register) == 4u;
                        const nuint transformed = ((sizeof(Register) + alignof(Register)) * 2u) / 2u % 7u;
                        const int castSize = (int)sizeof(Register);
                        const bool combined = sizeof(Register) == 4u && alignof(Register) == 4u;
                        NativeBuffer<byte> storage = stackalloc byte[sizeof(Register)];
                        Header header = new Header();
                        header.Length = 9;
                        Register register = new Register();
                        register.Value = 131073u;
                        NumberBits bits = new NumberBits();
                        bits.Integer = 1065353216;
                        Unaligned unaligned = new Unaligned();
                        unaligned.Value = 7;
                        Console.WriteLine(sizeof(Header));
                        Console.WriteLine(alignof(Header));
                        Console.WriteLine(offsetof(Header, Length));
                        Console.WriteLine(registerSize);
                        Console.WriteLine(alignof(Register));
                        Console.WriteLine(offsetof(Register, High));
                        Console.WriteLine(sizeof(Unaligned));
                        Console.WriteLine(alignof(Unaligned));
                        Console.WriteLine(numberSize);
                        Console.WriteLine(offsetof(NumberBits, Float));
                        Console.WriteLine(registerIsFour);
                        Console.WriteLine(transformed);
                        Console.WriteLine(castSize);
                        Console.WriteLine(combined);
                        Console.WriteLine(storage.Length);
                        Console.WriteLine(sizeof(Pack1));
                        Console.WriteLine(alignof(Pack1));
                        Console.WriteLine(sizeof(Pack2));
                        Console.WriteLine(alignof(Pack2));
                        Console.WriteLine(sizeof(Pack4));
                        Console.WriteLine(alignof(Pack4));
                        Console.WriteLine(sizeof(Pack8));
                        Console.WriteLine(alignof(Pack8));
                        Console.WriteLine(sizeof(Pack16));
                        Console.WriteLine(alignof(Pack16));
                        Console.WriteLine(sizeof(Cell<long>));
                        Console.WriteLine(offsetof(Cell<long>, Value));
                        Console.WriteLine(sizeof(Nested));
                        Console.WriteLine(header.Length);
                        Console.WriteLine(register.Low);
                        Console.WriteLine(register.High);
                        Console.WriteLine(bits.Float);
                        Console.WriteLine(unaligned.Value);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("#pragma pack(push, 2)", StringComparison.Ordinal) && generated.Contains("union ct_t_", StringComparison.Ordinal), "Packed or union C layout was not emitted.");
            Assert(generated.Contains("ct_slot_", StringComparison.Ordinal) && generated.Contains("C~ explicit field offset mismatch", StringComparison.Ordinal), "Explicit-layout carriers or assertions were not emitted.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "6\n2\n2\n4\n4\n2\n5\n1\n4\n0\nTrue\n1\n4\nTrue\n4\n5\n1\n6\n2\n8\n4\n8\n4\n8\n4\n9\n1\n8\n9\n1\n2\n1\n7\n", result.StandardOutput);
        });

        suite.Run("draft 0.16 exported aggregate layouts", () =>
        {
            const string source = "[Packed(2)] public union Word { public uint Value; public float Float; } public struct Pair { [FieldOffset(0)] public Word Left; [FieldOffset(4)] public Word Right; } public static class Program { [Export(\"pair_size\")] public static nuint Size(Pair value) { return sizeof(Pair); } [EntryPoint] public static void Main() { } }";
            var compilation = Compile(source);
            using var header = new StringWriter();
            Assert(compilation.EmitCHeader(header).Success, string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            var text = header.ToString();
            Assert(text.Contains("typedef union", StringComparison.Ordinal) && text.Contains("ct_layout", StringComparison.Ordinal) && text.Contains("pair_size", StringComparison.Ordinal), "The public header omitted exported union or explicit layouts.");
            Assert(text.Contains("#pragma pack(push, 2)", StringComparison.Ordinal) && text.Contains("C~ aggregate pack mismatch", StringComparison.Ordinal), "The public header omitted balanced packing or its assertion.");
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var internalHeader = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
            Assert(internalHeader.Contains("ct_layout", StringComparison.Ordinal) && internalHeader.Contains("#pragma pack(push, 2)", StringComparison.Ordinal), "Modular output did not use the aggregate layout renderer.");
            using var debugMap = new StringWriter();
            var debugCompilation = Compile(source, new CompilationOptions(DebugInformation: DebugInformationMode.Instrumented));
            Assert(debugCompilation.EmitDebugMap(debugMap).Success, "Debug-map emission failed for aggregate layouts.");
            var debugText = debugMap.ToString();
            Assert(debugText.Contains("\"layout\": \"union\"", StringComparison.Ordinal) && debugText.Contains("\"pack\": 2", StringComparison.Ordinal) && debugText.Contains("\"offset\": 4", StringComparison.Ordinal), "Debug metadata omitted aggregate layout details.");
        });
    }
}
