using System.Globalization;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart18(ConformanceSuite suite)
    {
        suite.Run("draft 0.20 endian values", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        be16 big = Endian.ToBigEndian((ushort)0x1234);
                        le32 little = Endian.ToLittleEndian((uint)0x12345678);
                        Console.WriteLine((int)Endian.FromBigEndian(big));
                        Console.WriteLine((int)Endian.FromLittleEndian(little));
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("ct_cpu_bswap16", StringComparison.Ordinal) && generated.Contains("typedef uint16_t ct_t_", StringComparison.Ordinal),
                "Endian intrinsic lowering or nominal wire type emission was missing.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && result.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "4660\n305419896",
                $"Endian round trip failed: {result.StandardOutput}{result.StandardError}");
        });

        suite.Run("draft 0.20 linker symbols and retained maps", () =>
        {
            const string source = """
                public static class Native
                {
                    [LinkerSymbol("_image_start")] public static unsafe readonly byte* ImageStart;
                    [Used] private static int retained;
                    [EntryPoint] public static unsafe void Main() { byte* value = ImageStart; retained = 1; }
                }
                """;
            var compilation = Compile(source);
            var generated = Emit(source);
            Assert(generated.Contains("extern unsigned char _image_start[];", StringComparison.Ordinal) && generated.Contains("(void*)_image_start", StringComparison.Ordinal),
                "Linker-address declaration was not emitted without owned storage.");
            using var mapWriter = new StringWriter(CultureInfo.InvariantCulture);
            Assert(compilation.EmitSymbolMap(mapWriter).Success, "Symbol map emission failed.");
            Assert(mapWriter.ToString().Contains("\"linkerRetained\": true", StringComparison.Ordinal), "Used metadata did not record final-image retention.\n" + mapWriter);

            const string retentionSource = "public static class Retained { [Used] private static int field; [Used] private static int Method() { return 1234567; } [Used][Export(\"ct_used_export\")] public static int Exported() { return 7; } } public static class Program { [EntryPoint] public static void Main() { } }";
            var retentionCompilation = Compile(retentionSource);
            using var retentionMapWriter = new StringWriter(CultureInfo.InvariantCulture);
            Assert(retentionCompilation.EmitSymbolMap(retentionMapWriter).Success, "Final-image retention symbol map emission failed.");
            using var retentionMap = System.Text.Json.JsonDocument.Parse(retentionMapWriter.ToString());
            var retainedSymbols = retentionMap.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            var retainedField = FindSymbol(retainedSymbols, "field", "Retained::field");
            var retainedMethod = FindSymbol(retainedSymbols, "method", "Retained::Method");
            var retainedExportImplementation = FindSymbol(retainedSymbols, "method", "Retained::Exported");
            var retainedImage = CompileAndInspectRetainedImage(retentionSource).Output;
            Assert(retainedImage.Contains(retainedField, StringComparison.Ordinal) && retainedImage.Contains(retainedMethod, StringComparison.Ordinal) &&
                retainedImage.Contains(retainedExportImplementation, StringComparison.Ordinal) && retainedImage.Contains("ct_used_export", StringComparison.Ordinal),
                "Dead-section elimination or LTO removed a [Used] field, method, export implementation, or export wrapper.\n" + retainedImage);

            var invalid = Compile("public static class Program { [LinkerSymbol(\"bad\")] public static uint Value; [EntryPoint] public static void Main() { } }");
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1296"), "Invalid linker symbol did not report CT1296.");
        });

        suite.Run("draft 0.20 bitfields and registers", () =>
        {
            const string source = """
                using System;
                [BitField(typeof(uint))]
                public struct ControlBits
                {
                    [Bit(0)] public bool Enabled;
                    [Bits(8, 15)] public byte Mode;
                    [Bits(16, 31)] public be16 Wire;
                }
                public static class Device
                {
                    [Register(0x60004000)] public static unsafe ControlBits Control;
                    [Export("read_control")] public static unsafe uint Read() { return (uint)Control; }
                    [Export("read_enabled")] public static unsafe bool ReadEnabled() { return Control.Enabled; }
                    [Export("set_enabled")] public static unsafe void SetEnabled() { Control.Enabled = true; }
                }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        ControlBits value = (ControlBits)(uint)0;
                        value.Enabled = true;
                        value.Mode = (byte)5;
                        value.Wire = Endian.ToBigEndian((ushort)0x1234);
                        Console.WriteLine(value.Enabled);
                        Console.WriteLine((int)value.Mode);
                        Console.WriteLine((int)Endian.FromBigEndian(value.Wire));
                        Console.WriteLine((long)(uint)value);
                    }
                }
                """;
            var generated = Emit(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            Assert(generated.Contains("typedef uint32_t ct_t_", StringComparison.Ordinal) && generated.Contains("C~ bitfield size mismatch", StringComparison.Ordinal), "Bitfield did not use scalar native storage.");
            Assert(generated.Contains("UINT64_C(0x60004000)", StringComparison.Ordinal) && !generated.Contains("ct_f_Device_Control", StringComparison.Ordinal),
                "Register access did not lower to its fixed address or emitted storage.");
            Assert(generated.Split("ct_mmio_barrier();", StringSplitOptions.None).Length - 1 == 6,
                "Whole-register and bit-view accesses did not emit exactly one barrier pair per ordered access.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && result.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "True\n5\n4660\n873596161",
                $"Bitfield view semantics failed: {result.StandardOutput}{result.StandardError}");

            var invalid = Compile("[BitField(typeof(uint))] public struct Bad { [Bit(32)] public bool Value; } public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code is "CT1297" or "CT2209"), "Out-of-range bit view was accepted.");

            var genericRegister = Compile("public static class Device<const nuint Address> { [Register(Address)] public static unsafe uint Value; } public static class Program { [EntryPoint] public static unsafe void Main() { uint value = Device<3u>.Value; } }");
            Assert(genericRegister.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2210"), "A misaligned constant-generic register specialization was accepted.");

            const string headerA = "[BitField(typeof(uint))] public struct Flags { [Bits(8, 15)] public byte Mode; } public static class Native { [Export(\"read_flags\")] public static Flags Read(Flags value) { return value; } } public static class Program { [EntryPoint] public static void Main() { } }";
            const string headerB = "[BitField(typeof(uint))] public struct Flags { [Bits(8, 14)] public byte Mode; } public static class Native { [Export(\"read_flags\")] public static Flags Read(Flags value) { return value; } } public static class Program { [EntryPoint] public static void Main() { } }";
            using var firstHeader = new StringWriter(CultureInfo.InvariantCulture);
            using var secondHeader = new StringWriter(CultureInfo.InvariantCulture);
            var firstHeaderCompilation = Compile(headerA);
            var secondHeaderCompilation = Compile(headerB);
            Assert(firstHeaderCompilation.EmitCHeader(firstHeader).Success && secondHeaderCompilation.EmitCHeader(secondHeader).Success,
                "Bitfield header emission failed.\n" + string.Join("\n", firstHeaderCompilation.GetDiagnostics().Concat(secondHeaderCompilation.GetDiagnostics())));
            Assert(firstHeader.ToString().Split('\n')[0] != secondHeader.ToString().Split('\n')[0], "The public-header signature hash ignored bit-view metadata.");
        });

        suite.Run("draft 0.20 source modules and panic policies", () =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ctilde-source-root"));
            var first = SyntaxTree.ParseText("namespace Shared; public static class A { public static int Value() { return 1; } }", Path.Combine(root, "a.ct"));
            var second = SyntaxTree.ParseText("using Shared; namespace Shared; public static class Program { [EntryPoint] public static void Main() { A.Value(); } }", Path.Combine(root, "b.ct"));
            var compilation = Compile([first, second], new CompilationOptions(SourceIdentityRoot: root));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var sources = bundle.Artifacts.Where(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource).ToArray();
            Assert(sources.Length >= 2 && sources.All(artifact => artifact.RelativePath.StartsWith("source_", StringComparison.Ordinal)) &&
                sources.Count(artifact => artifact.Content.Contains("ct_m_", StringComparison.Ordinal)) >= 2,
                "Reachable source files were not assigned stable source-owned modules.\n" + string.Join("\n", bundle.Artifacts.Select(artifact => artifact.RelativePath + ":" + artifact.Kind)));

            var editedFirst = SyntaxTree.ParseText("namespace Shared; public static class A { public static int Value() { return 2; } }", Path.Combine(root, "a.ct"));
            var editedBundle = Compile([editedFirst, second], new CompilationOptions(SourceIdentityRoot: root)).EmitCBundle();
            Assert(editedBundle.Success, string.Join(Environment.NewLine, editedBundle.Diagnostics));
            Assert(bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content ==
                editedBundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content,
                "A body-only edit changed the broad modular internal header.");
            var originalSources = sources.ToDictionary(artifact => artifact.RelativePath, artifact => artifact.Content, StringComparer.Ordinal);
            var editedSources = editedBundle.Artifacts.Where(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource)
                .ToDictionary(artifact => artifact.RelativePath, artifact => artifact.Content, StringComparer.Ordinal);
            Assert(originalSources.Keys.SequenceEqual(editedSources.Keys, StringComparer.Ordinal) &&
                originalSources.Count(pair => pair.Value != editedSources[pair.Key]) == 1,
                "A body-only edit did not change exactly one stable source-owned module.");

            var memoryA = SyntaxTree.ParseText("namespace MemoryA; public static class A { public static int Value() { return 1; } }", string.Empty);
            var memoryB = SyntaxTree.ParseText("using MemoryA; public static class Program { [EntryPoint] public static void Main() { A.Value(); } }", string.Empty);
            Assert(!Compile([memoryA, memoryB]).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4112"),
                "Distinct pathless syntax trees did not receive content-derived source identities.");

            var hosted = Compile("public static class Program { [EntryPoint] public static void Main() { } }",
                new CompilationOptions(PanicPolicy: EspIdfPanicPolicy.Restart));
            Assert(hosted.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4113"), "Hosted explicit panic policy was accepted.");
            var esp = Emit("public sealed class Box { public int Value; } public static class Program { [EntryPoint] public static void Main() { Box value = null; int unused = value.Value; } }",
                new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa, PanicPolicy: EspIdfPanicPolicy.Restart));
            Assert(esp.Contains("extern void esp_restart(void) __attribute__((noreturn));", StringComparison.Ordinal), "ESP-IDF restart declaration did not preserve its non-returning ABI contract.");
            Assert(esp.Contains("extern void esp_system_abort(const char* details) __attribute__((noreturn));", StringComparison.Ordinal), "ESP-IDF halt declaration did not preserve its non-returning ABI contract.");
            Assert(esp.Split("esp_restart();", StringSplitOptions.None).Length >= 3, "ESP-IDF restart policy was not emitted for both runtime faults and unhandled exceptions.");
        });
    }
}
