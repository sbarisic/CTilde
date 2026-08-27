using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart21(ConformanceSuite suite)
    {
        suite.Run("draft 0.23 interrupt entry emission and residency", () =>
        {
            const string source = """
                public static class Device
                {
                    public static volatile uint Counter;

                    [InterruptSafe]
                    [Extern("native_irq_data")]
                    [NativeVolatile]
                    public static unsafe uint NativeData;

                    [InterruptSafe]
                    [Extern("native_ack")]
                    [NoRuntime]
                    [NoBlock]
                    public static unsafe void Ack([Nullable] void* context);

                    private static unsafe void Increment<const uint Amount>([Nullable] void* context)
                    {
                        Counter = Counter + Amount + NativeData;
                        Ack(context);
                    }

                    private static unsafe void Hint()
                    {
                        [InterruptSafe] [NoRuntime] [NoBlock] asm { nop }
                    }

                    [Interrupt]
                    [Export("ct_test_isr")]
                    public static unsafe void Handler(void* context)
                    {
                        Increment<1u>(context);
                        Hint();
                    }

                    [EntryPoint]
                    public static void Main() { }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa);
            var compilation = Compile(source, options);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));

            var generated = Emit(source, options);
            Assert(generated.Contains("#include <esp_attr.h>", StringComparison.Ordinal), "ESP interrupt output did not include the placement definitions.");
            Assert(generated.Contains("IRAM_ATTR void ct_test_isr(void* u_", StringComparison.Ordinal), "The interrupt entry was not emitted directly in IRAM.");
            Assert(generated.Contains("IRAM_ATTR void ct_m_", StringComparison.Ordinal), "The private interrupt closure was not placed in IRAM.");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(generated, "DRAM_ATTR[^\\n]*uint32_t"), "Compiler-owned interrupt data was not placed in DRAM.");
            Assert(!generated.Contains("ct_runtime_require_ready();\n    ct_test_isr", StringComparison.Ordinal), "The interrupt entry retained an ordinary export wrapper.");

            var instrumented = Emit(source, options with { DebugInformation = DebugInformationMode.Instrumented });
            var interruptStart = instrumented.IndexOf("IRAM_ATTR void ct_test_isr(", StringComparison.Ordinal);
            var interruptEnd = instrumented.IndexOf("\n}\n", interruptStart, StringComparison.Ordinal);
            Assert(interruptStart >= 0 && interruptEnd > interruptStart &&
                !instrumented[interruptStart..interruptEnd].Contains("ct_debug_", StringComparison.Ordinal),
                "Debug instrumentation leaked into an interrupt entry.");

            using var header = new StringWriter();
            Assert(compilation.EmitCHeader(header).Success, "Interrupt header emission failed.");
            Assert(header.ToString().Contains("IRAM_ATTR void ct_test_isr(", StringComparison.Ordinal), "The public native ISR prototype did not carry IRAM placement.");

            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var modularText = string.Join("\n", bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(modularText.Contains("IRAM_ATTR void ct_test_isr(void* u_", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(modularText, "DRAM_ATTR[^\\n]*uint32_t"), "Modular output lost interrupt placement.");
            var internalHeader = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
            Assert(!internalHeader.Contains("DRAM_ATTR extern", StringComparison.Ordinal), "Definition-only DRAM placement leaked onto an extern declaration.");
            Assert(!internalHeader.Contains("extern IRAM_ATTR", StringComparison.Ordinal), "ESP-IDF's definition-only IRAM placement leaked onto an internal prototype.");

            using var symbolWriter = new StringWriter();
            Assert(compilation.EmitSymbolMap(symbolWriter).Success, "Interrupt symbol-map emission failed.");
            using var document = JsonDocument.Parse(symbolWriter.ToString());
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            var entry = symbols.Single(symbol => symbol.TryGetProperty("interrupt", out var interrupt) && interrupt.GetBoolean());
            Assert(entry.GetProperty("codeResidency").GetString() == "iram", "The ISR symbol map did not record IRAM residency.");
            Assert(symbols.Any(symbol => symbol.TryGetProperty("dataResidency", out var residency) && residency.GetString() == "dram"), "The symbol map did not record DRAM residency.");
        });

        suite.Run("draft 0.23 interrupt effect and residency diagnostics", () =>
        {
            const string source = """
                using System;
                using System.Threading;

                public static class Native
                {
                    [Extern("unsafe_irq_call")]
                    [NoRuntime]
                    [NoBlock]
                    public static unsafe void UnsafeCall(void* context);
                }

                public static class Device
                {
                    [Section(".custom")]
                    private static unsafe void Custom(void* context) { }

                    private static unsafe void UntrustedAssembly()
                    {
                        [NoRuntime] [NoBlock] asm { nop }
                    }

                    [Interrupt]
                    [Export("ct_bad_isr")]
                    public static unsafe void Handler(void* context)
                    {
                        Native.UnsafeCall(context);
                        Custom(context);
                        UntrustedAssembly();
                        Thread.Sleep(1u);
                        Console.WriteLine("flash");
                    }

                    [EntryPoint]
                    public static void Main() { }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa);
            var first = Compile(source, options).GetDiagnostics();
            var second = Compile(source, options).GetDiagnostics();
            Assert(first.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Location))
                .SequenceEqual(second.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Location))), "Interrupt diagnostics were not deterministic.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2215" && diagnostic.Message.Contains("NoBlock", StringComparison.Ordinal)), "A blocking ISR did not violate its implicit NoBlock profile.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2215" && diagnostic.Message.Contains("NoRuntime", StringComparison.Ordinal)), "A runtime-using ISR did not violate its implicit NoRuntime profile.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2216" && diagnostic.Message.Contains("extern without InterruptSafe", StringComparison.Ordinal)), "An untrusted extern was accepted from interrupt code.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2216" && diagnostic.Message.Contains("custom code section", StringComparison.Ordinal)), "A custom section was accepted in an interrupt closure.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2216" && diagnostic.Message.Contains("inline assembly", StringComparison.Ordinal)), "Untrusted inline assembly was accepted in an interrupt closure.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2216" && diagnostic.Message.Contains("string literal", StringComparison.Ordinal)), "Flash-backed literal data was accepted in interrupt code.");
            Assert(first.Any(diagnostic => diagnostic.Message.Contains("Handler", StringComparison.Ordinal) && diagnostic.Message.Contains("Custom", StringComparison.Ordinal)), "A residency diagnostic omitted its deterministic call path.");
        });

        suite.Run("draft 0.23 interrupt declaration and call restrictions", () =>
        {
            const string malformed = """
                public static class Device
                {
                    [InterruptSafe] public static uint LocalData;
                    [InterruptSafe] public static void LocalMethod() { }

                    [Interrupt]
                    [Export("wrong")]
                    public static void Wrong() { }

                    [Interrupt]
                    [Export("sectioned")]
                    [Section(".text.user")]
                    public static unsafe void Sectioned(void* context) { }

                    [EntryPoint] public static void Main() { }
                }
                """;
            var espOptions = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa);
            var malformedDiagnostics = Compile(malformed, espOptions).GetDiagnostics();
            Assert(malformedDiagnostics.Count(diagnostic => diagnostic.Code == "CT1306") >= 4, "Malformed interrupt declarations did not report CT1306.");

            const string hosted = """
                public static class Device
                {
                    [Interrupt] [Export("host_irq")]
                    public static unsafe void Handler(void* context) { }
                    [EntryPoint] public static void Main() { }
                }
                """;
            Assert(Compile(hosted, new CompilationOptions(Architecture: CompilationArchitecture.X64)).GetDiagnostics()
                .Any(diagnostic => diagnostic.Code == "CT4117"), "A hosted interrupt entry did not report CT4117.");

            const string nativeOnly = """
                public static class Device
                {
                    [Interrupt] [Export("native_irq")]
                    public static unsafe void Handler(void* context) { }
                    [EntryPoint] public static unsafe void Main() { Handler((void*)0); }
                }
                """;
            Assert(Compile(nativeOnly, espOptions).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2215" && diagnostic.Message.Contains("native-only", StringComparison.Ordinal)),
                "C~ code was allowed to invoke an interrupt entry directly.");
        });

        suite.Run("draft 0.23 interrupt language services", () =>
        {
            const string source = "public static class Device { [Interrupt] [Export(\"irq\")] public static unsafe void Handler(void* context) { } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "interrupt.ct")],
                new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa));
            Assert(service.GetCompletions("interrupt.ct", source.IndexOf("[Interrupt]", StringComparison.Ordinal)).Any(item => item.Label == "InterruptSafe"),
                "Interrupt attributes were missing from completion.");
            var hover = service.GetHover("interrupt.ct", source.IndexOf("Interrupt]", StringComparison.Ordinal) + 2);
            Assert(hover?.Contents.Contains("interrupt", StringComparison.OrdinalIgnoreCase) == true, "Interrupt attribute hover was unavailable.");
        });
    }
}
