using System.Globalization;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart16(ConformanceSuite suite)
    {
        suite.Run("draft 0.18 target queries static if and assertions", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public struct Header { public int Value; static assert(sizeof(Header) == 4, "Header size"); }
                static assert(Target.PointerSize == 8);
                public static class Program
                {
                    private static void X64() { Console.WriteLine(64); }
                    [EntryPoint] public static void Main()
                    {
                        static if (Target.Architecture == TargetArchitecture.X64) { X64(); }
                        else { MissingOnX64(); }
                    }
                }
                """;
            var options = new CompilationOptions(Architecture: CompilationArchitecture.X64);
            var generated = Emit(source, options);
            Assert(generated.Contains("CT2201: Header size", StringComparison.Ordinal), "Symbolic layout assertion was not emitted.");
            Assert(!generated.Contains("MissingOnX64", StringComparison.Ordinal), "Inactive static-if branch was bound or emitted.");
            var runtime = CompileAndRun(source);
            Assert(runtime.ExitCode == 0 && runtime.StandardOutput.Trim() == "64", "Compile-time selection or native assertion changed runtime behavior.");

            var failed = Compile("static assert(false, \"broken\"); public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(failed.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2201"), "False static assertion did not report CT2201.");
            var malformed = Compile("static assert(1); public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(malformed.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2200"), "Non-Boolean static assertion did not report CT2200.");
        });

        suite.Run("draft 0.18 used extern data and MMIO emission", () =>
        {
            const string source = """
                using System.Runtime;
                public static class Native
                {
                    [Extern("system_state")][NativeVolatile] public static unsafe uint State;
                    [Extern("firmware_blob")] public static readonly unsafe byte Firmware;
                    [Used] private static int retained;
                    [Used] private static void RetainedMethod() { retained = 1; }
                    [EntryPoint] public static unsafe void Main()
                    {
                        uint value = State;
                        Mmio.WriteRelaxed<uint>((nuint)4096, value);
                        value = Mmio.Read<uint>((nuint)4096);
                    }
                }
                """;
            var generated = Emit(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            Assert(generated.Contains("extern volatile uint32_t system_state;", StringComparison.Ordinal), "Native volatile extern data declaration was not emitted.");
            Assert(generated.Contains("extern const uint8_t firmware_blob;", StringComparison.Ordinal), "Readonly extern data declaration was not emitted.");
            Assert(generated.Contains("CT_USED", StringComparison.Ordinal) && generated.Contains("ct_mmio_barrier();", StringComparison.Ordinal), "Used retention or ordered MMIO lowering was not emitted.");
            Assert(generated.Contains("*(volatile uint32_t*)(uintptr_t)", StringComparison.Ordinal), "Exact-width volatile MMIO access was not emitted.");

            var invalid = Compile("using System.Runtime; public static class Program { [EntryPoint] public static unsafe void Main() { Mmio.Read<bool>((nuint)4); } }");
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2203"), "Invalid MMIO element did not report CT2203.");
        });

        suite.Run("draft 0.18 ESP task entry", () =>
        {
            const string source = """
                public static class Program
                {
                    [TaskEntry(StackSize = 8192)]
                    [Export("network_worker")]
                    public static unsafe void Worker(void* context) { }
                    [EntryPoint] public static unsafe void Main() { System.Runtime.Mmio.Barrier(); }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa);
            var compilation = Compile(source, options);
            using var header = new StringWriter(CultureInfo.InvariantCulture);
            var headerResult = compilation.EmitCHeader(header);
            Assert(headerResult.Success, string.Join(Environment.NewLine, headerResult.Diagnostics));
            var generated = Emit(source, options);
            Assert(header.ToString().Contains("CTILDE_TASK_STACK_NETWORK_WORKER UINT32_C(8192)", StringComparison.Ordinal), "Task stack header constant was not emitted.");
            Assert(generated.IndexOf("#include <freertos/FreeRTOS.h>", StringComparison.Ordinal) < generated.IndexOf("#include <freertos/task.h>", StringComparison.Ordinal), "Task entry emitted FreeRTOS headers in an invalid order.");
            Assert(generated.Contains("ct_thread_attach();", StringComparison.Ordinal) && generated.Contains("ct_thread_detach();", StringComparison.Ordinal) && generated.Contains("vTaskDelete(NULL);", StringComparison.Ordinal), "Task entry lifecycle wrapper was incomplete.");
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var internalHeader = string.Join('\n', bundle.Artifacts.Where(artifact => artifact.Kind is GeneratedCArtifactKind.InternalHeader or GeneratedCArtifactKind.DependencyHeader).Select(artifact => artifact.Content));
            Assert(internalHeader.Contains("static inline void ct_mmio_barrier(void)", StringComparison.Ordinal) &&
                !internalHeader.Contains("extern inline void ct_mmio_barrier(void)", StringComparison.Ordinal), "Modular MMIO barrier lost its inline definition.");

            var hosted = Compile(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            Assert(hosted.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1292"), "Hosted TaskEntry did not report CT1292.");
        });
    }
}
