using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart23(ConformanceSuite suite)
    {
        var freestanding = new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X86);

        suite.Run("draft 0.25 assembly function syntax and emission", () =>
        {
            const string source = """
                using System.Runtime;

                public static class Ports
                {
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc]
                    private static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }

                    [NoRuntime] [NoBlock] [Export("debug_write")]
                    public static unsafe asm void Write(char value)
                        (in("a") value as output, clobber("cc"))
                    {
                        outb output, $0xe9
                    }

                    [NoRuntime] [NoBlock] [Used]
                    public static unsafe asm uint Read()
                        (out("a") result as value)
                    {
                        inl $0xf4, value
                    }

                    [Naked] [NoAlloc] [Export("_start")]
                    public static unsafe asm void Start()
                    {
                        call debug_write
                        hlt
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "assembly-functions.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            Assert(tree.ToFullString() == source, "Assembly-function syntax did not round-trip exactly.");
            var methods = Descendants(tree.Root).OfType<MethodDeclarationSyntax>().Where(method => method.AssemblyBody is not null).ToArray();
            Assert(methods.Length == 3, "Assembly-function bodies were not represented on method syntax.");
            Assert(methods[0].AssemblyBody!.References.Select(reference => reference.Name).SequenceEqual(["output"]), "Assembly-function raw references were not isolated.");

            var compilation = Compile(source, freestanding);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var generated = Emit(source, freestanding);
            Assert(generated.Contains("__asm__ volatile (", StringComparison.Ordinal), "A normal assembly function did not emit GNU extended assembly.");
            Assert(generated.Contains("ct_asm_result", StringComparison.Ordinal) && generated.Contains("return ct_asm_result", StringComparison.Ordinal), "An assembly result did not receive compiler-owned result storage and return emission.");
            Assert(generated.Contains("__attribute__((naked, noreturn, used))", StringComparison.Ordinal) && generated.Contains("call debug_write", StringComparison.Ordinal), "A naked assembly function did not retain its complete raw body.");
            Assert(generated == Emit(source, freestanding), "Assembly-function emission was not deterministic.");
            var firstBundle = compilation.EmitCBundle();
            var secondBundle = Compile(source, freestanding).EmitCBundle();
            Assert(firstBundle.Success && secondBundle.Success && firstBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))
                .SequenceEqual(secondBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))),
                "Modular assembly-function emission was not deterministic.");

            using var mapWriter = new StringWriter();
            Assert(compilation.EmitSymbolMap(mapWriter).Success, "Assembly-function symbol-map emission failed.");
            using var map = JsonDocument.Parse(mapWriter.ToString());
            Assert(map.RootElement.GetProperty("symbols").EnumerateArray().Any(symbol =>
                symbol.TryGetProperty("assemblyFunction", out var marker) && marker.GetBoolean()), "Assembly functions were not identified in the symbol map.");

            var service = LanguageServiceSnapshot.Create([tree], freestanding);
            var operand = source.IndexOf("output, $0xe9", StringComparison.Ordinal);
            Assert(service.GetHover("assembly-functions.ct", operand)?.Contents.Contains("value", StringComparison.Ordinal) == true,
                "Assembly-function operand hover did not resolve its parameter.");
        });

        suite.Run("draft 0.25 assembly function validation", () =>
        {
            const string invalid = """
                public static class Bad
                {
                    public asm void MissingUnsafe(int value) (in value) { nop }
                    public static unsafe asm int MissingResult(int value) (in value) { nop }
                    public static unsafe asm void VoidResult() (out result) { nop }
                    public static unsafe asm int DuplicateResult() (out result as a, out result as b) { nop }
                    public static unsafe asm int MissingParameter(int value) (out result) { nop }
                    public static unsafe asm int WrongRole(out int value) (in value, out result) { nop }
                }
                """;
            var diagnostics = Compile(invalid, freestanding).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1307"), "An invalid assembly-function declaration shape was accepted.");
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2217") >= 5, "Assembly-function operand/result invariants were not enforced.");

            const string effects = """
                public static class Effects
                {
                    public static unsafe asm void Unknown() { nop }
                    [NoAlloc] public static unsafe void Caller() { Unknown(); }
                }
                """;
            Assert(Compile(effects, freestanding).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2155"),
                "An unannotated assembly boundary satisfied a NoAlloc caller.");
            Assert(Compile(effects).UsesInlineAssembly, "Assembly functions did not select the GNU inline-assembly toolchain path.");
        });

        suite.Run("draft 0.25 assembly function GNU runtime", () =>
        {
            var compiler = Environment.GetEnvironmentVariable("CTILDE_CC") ?? string.Empty;
            if (!compiler.Contains("gcc", StringComparison.OrdinalIgnoreCase) && !compiler.Contains("clang", StringComparison.OrdinalIgnoreCase))
                return;
            const string source = """
                using System;
                public static class Program
                {
                    public static unsafe asm int Value() (out result as output) { movl $42, output }
                    [EntryPoint] public static unsafe void Main() { if (Value() != 42) Environment.Exit(7); }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.25 ConstInit structured data", () =>
        {
            const string source = """
                [Packed(4)]
                public readonly struct Header
                {
                    public readonly uint Magic;
                    public readonly uint Flags;
                    public readonly uint Checksum;

                    public Header(uint magic, uint flags, uint checksum)
                    {
                        Magic = magic;
                        Flags = flags;
                        Checksum = checksum;
                    }
                }

                public readonly struct NestedHeader
                {
                    public readonly Header Header;
                    public readonly uint Tag;
                    public NestedHeader(Header header, uint tag) { Header = header; Tag = tag; }
                }

                public static class Image
                {
                    [ConstInit] [Used] [Section(".multiboot")] [Align(4)]
                    public static readonly Header BootHeader = new Header(
                        0x1BADB002u,
                        0x00000003u,
                        0u - 0x1BADB002u - 0x00000003u);

                    [ConstInit] [Used]
                    public static readonly NestedHeader Nested = new NestedHeader(
                        new Header(1u, 2u, 3u), 4u);

                    public static uint Read() { return BootHeader.Magic; }
                }
                """;
            var compilation = Compile(source, freestanding);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var generated = Emit(source, freestanding);
            Assert(generated.Contains("const ", StringComparison.Ordinal) && generated.Contains("UINT32_C(464367618)", StringComparison.Ordinal), "ConstInit data was not emitted as native constant storage.");
            Assert(generated.Contains("UINT32_C(3830599675)", StringComparison.Ordinal), "ConstInit wrapping checksum evaluation was incorrect.");
            Assert(generated.Contains("{ { UINT32_C(1), UINT32_C(2), UINT32_C(3) }, UINT32_C(4) }", StringComparison.Ordinal), "Nested ConstInit construction was not emitted as a positional aggregate.");
            Assert(generated.Contains("CT_SECTION_READONLYDATA_", StringComparison.Ordinal), "ConstInit custom data did not use a read-only section category.");
            Assert(!generated.Contains("ct_module_init", StringComparison.Ordinal), "ConstInit-only data incorrectly required module lifecycle emission.");
            Assert(!generated.Contains("BootHeader =", StringComparison.Ordinal) || generated.IndexOf("BootHeader =", StringComparison.Ordinal) == generated.LastIndexOf("BootHeader =", StringComparison.Ordinal),
                "ConstInit data also received a runtime assignment.");
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success && bundle.Artifacts.Any(artifact => artifact.Content.Contains("{ { UINT32_C(1), UINT32_C(2), UINT32_C(3) }, UINT32_C(4) }", StringComparison.Ordinal)),
                "Modular emission omitted nested ConstInit data.");

            using var mapWriter = new StringWriter();
            Assert(compilation.EmitSymbolMap(mapWriter).Success, "ConstInit symbol-map emission failed.");
            using var map = JsonDocument.Parse(mapWriter.ToString());
            Assert(map.RootElement.GetProperty("symbols").EnumerateArray().Any(symbol =>
                symbol.TryGetProperty("constInit", out var marker) && marker.GetBoolean()), "ConstInit data was not identified in the symbol map.");
        });

        suite.Run("draft 0.25 ConstInit restrictions", () =>
        {
            const string invalidShape = """
                public static class Bad
                {
                    [ConstInit] public static int Mutable = 1;
                    [ConstInit(1)] public static readonly int Arguments = 1;
                    [ConstInit] public static readonly int* Pointer = null;
                }
                """;
            var shapeDiagnostics = Compile(invalidShape, freestanding).GetDiagnostics();
            Assert(shapeDiagnostics.Count(diagnostic => diagnostic.Code == "CT1308") >= 3, "Invalid ConstInit field shapes were accepted.");

            const string badConstructor = """
                public struct Pair
                {
                    public uint A;
                    public uint B;
                    public Pair(uint value) { A = value; if (value != 0u) { B = value; } }
                }
                public static class Data
                {
                    [ConstInit] public static readonly Pair Value = new Pair(1u);
                }
                """;
            Assert(Compile(badConstructor, freestanding).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2218"),
                "General constructor execution was accepted for ConstInit.");

            const string writes = """
                public struct Pair { public uint A; public Pair(uint value) { A = value; } public void Change() { A = 2u; } }
                public static class Data
                {
                    [ConstInit] public static readonly Pair Value = new Pair(1u);
                    public static unsafe void Bad() { Value.A = 2u; uint* pointer = &Value.A; Value.Change(); }
                }
                """;
            Assert(Compile(writes, freestanding).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT2219") >= 3,
                "ConstInit storage accepted mutation, address-taking, or a direct instance call.");

            const string mixedSection = """
                public static class Sections
                {
                    [Section(".same")] public static uint Mutable;
                    [ConstInit] [Section(".same")] public static readonly uint Immutable = 1u;
                }
                """;
            Assert(Compile(mixedSection, freestanding).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4107"),
                "Writable and ConstInit data were accepted in the same custom section.");
        });
    }
}
