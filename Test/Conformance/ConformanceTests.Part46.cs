using System.Text.RegularExpressions;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart46(ConformanceSuite suite)
    {
        suite.Run("draft 0.50 size optimization profile", () =>
        {
            var root = CreateNativeProfileProject();
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                WriteNativeProfileManifest(manifest, "size", "baseline", "precise", "off", "build/pgo");
                var build = CTildeProjectFile.Load(manifest).Configuration.Build!;
                Assert(build.Optimization == NativeOptimization.Size, "The size optimization profile was not loaded.");

                var compiled = RunNativeProfileCli(root, manifest, "--build", "--trace", "--optimization", "size");
                Assert(compiled.ExitCode == 0, compiled.StandardOutput + compiled.StandardError);
                var trace = compiled.StandardOutput + compiled.StandardError;
                Assert(trace.Contains(OperatingSystem.IsWindows() ? "/O1" : "-Os", StringComparison.Ordinal),
                    "The native compiler did not receive the Draft 0.50 size optimization flag.");

                File.WriteAllText(manifest, """
                    {
                      "target": "cosmopolitan",
                      "architecture": "x64",
                      "sources": ["*.ct"],
                      "cosmopolitan": { "mode": "tiny" },
                      "build": { "configuration": "release", "optimization": "size" }
                    }
                    """);
                var tiny = RunNativeProfileCli(root, manifest, "--check");
                Assert(tiny.ExitCode == 0, "Cosmopolitan tiny rejected its explicit size profile.\n" +
                    tiny.StandardOutput + tiny.StandardError);

                var schema = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "editors", "vscode", "schemas", "ctilde.schema.json")));
                Assert(schema.Contains("\"enum\": [\"size\", \"speed\", \"aggressive\"]", StringComparison.Ordinal),
                    "The editor schema does not publish the size optimization profile.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        suite.Run("draft 0.50 private overlay helper inference", () =>
        {
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.InferredOverlay", "1.0.0", [], 4096, 16384);
            var exclusive = Compile("""
                public static class Program
                {
                    private static int Helper(int value) { return value * 2; }
                    [Overlay("work")]
                    public static int Work(int value) { return Helper(value); }
                    [EntryPoint]
                    public static int Main(string[] args) { return Work(21); }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var exclusiveBundle = exclusive.EmitCBundle();
            Assert(exclusiveBundle.Success, string.Join(Environment.NewLine, exclusiveBundle.Diagnostics));
            var exclusiveC = string.Join('\n', exclusiveBundle.Artifacts.Select(artifact => artifact.Content));
            Assert(Regex.Matches(exclusiveC, "CT_OVERLAY_BODY\\(\"work\"\\)").Count == 2,
                "A private helper used only by one overlay did not inherit that overlay.");

            var shared = Compile("""
                public static class Program
                {
                    private static int Helper(int value) { return value * 2; }
                    [Overlay("first")]
                    public static int First(int value) { return Helper(value); }
                    [Overlay("second")]
                    public static int Second(int value) { return Helper(value); }
                    [EntryPoint]
                    public static int Main(string[] args) { return First(10) + Second(11); }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var sharedBundle = shared.EmitCBundle();
            Assert(sharedBundle.Success, string.Join(Environment.NewLine, sharedBundle.Diagnostics));
            var sharedC = string.Join('\n', sharedBundle.Artifacts.Select(artifact => artifact.Content));
            Assert(Regex.Matches(sharedC, "CT_OVERLAY_BODY\\(\"first\"\\)").Count == 1 &&
                Regex.Matches(sharedC, "CT_OVERLAY_BODY\\(\"second\"\\)").Count == 1,
                "A helper shared by multiple overlay groups was duplicated or assigned to one group.");
        });

        suite.Run("draft 0.50 Xtensa atomic compare-and-swap synchronization", () =>
        {
            var generated = Emit("public static class Program { [EntryPoint] public static void Main() { object value = new object(); } }",
                new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa));
            Assert(generated.Contains("#elif defined(__XTENSA__)", StringComparison.Ordinal) &&
                generated.Contains("wsr.scompare1 %2\\n rsync\\n s32c1i", StringComparison.Ordinal),
                "The Xtensa atomic compare-and-swap primitive does not synchronize SCOMPARE1 before S32C1I.");
            Assert(generated.Contains("ct_atomic_xtensa_compare_set", StringComparison.Ordinal) &&
                generated.Contains("ct_atomic_fetch_sub_release", StringComparison.Ordinal),
                "The Xtensa ARC atomics do not use the synchronized compare-and-swap primitive.");

            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.AtomicModule", "1.0.0", [], 4096, 16384);
            var moduleCompilation = Compile("""
                using System.Threading;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args) {
                        object value = new object(); if (value == null) return 1;
                        Thread worker = new Thread(() => { }); worker.Start(); worker.Join();
                        Atomic<int> state = new Atomic<int>(0);
                        return state.CompareExchange(1, 0, MemoryOrder.SequentiallyConsistent, MemoryOrder.Relaxed);
                    }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var moduleBundle = moduleCompilation.EmitCBundle();
            Assert(moduleBundle.Success, string.Join(Environment.NewLine, moduleBundle.Diagnostics));
            var moduleC = string.Join('\n', moduleBundle.Artifacts.Select(artifact => artifact.Content));
            Assert(moduleC.Contains("ctilde_managed_atomic_compare_exchange_u32", StringComparison.Ordinal) &&
                !moduleC.Contains("static CT_INLINE uint32_t ct_atomic_xtensa_compare_set", StringComparison.Ordinal),
                "Managed Xtensa modules must execute CAS through resident firmware instead of their executable D/IRAM bank.");
            Assert(moduleC.Contains("case 4u: return ct_atomic_scalar_compare_exchange_u32", StringComparison.Ordinal) &&
                moduleC.Contains("(void)ctilde_managed_atomic_compare_exchange_u32((volatile uint32_t*)storage, &expected, desired)", StringComparison.Ordinal) &&
                !moduleC.Contains("defined(CT_MANAGED_MODULE)", StringComparison.Ordinal) &&
                moduleC.Contains("if (size == 1u || size == 2u) return ct_atomic_scalar_compare_exchange_subword", StringComparison.Ordinal),
                "Managed 32-bit scalar CAS bypassed the resident firmware helper.");
            Assert(moduleC.Contains("ctilde_managed_thread_payload_allocate(sizeof(*payload), &done)", StringComparison.Ordinal) &&
                moduleC.Contains("ctilde_managed_thread_payload_free(payload)", StringComparison.Ordinal) &&
                moduleC.Contains("ctilde_managed_thread_exit();", StringComparison.Ordinal),
                "Managed workers must retain native cleanup ownership and exit through resident firmware.");
        });

        suite.Run("draft 0.50 managed unhandled-exception fallback stays self-contained", () =>
        {
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ExceptionModule", "1.0.0", [], 4096, 16384);
            var compilation = Compile("""
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args) { throw new InvalidOperationException(); }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("ct_runtime_api->RuntimeFault", StringComparison.Ordinal) &&
                !generated.Contains("ct_string* message =", StringComparison.Ordinal),
                "The managed fallback must not retain the pruned System.Exception.Message getter.");
        });
    }
}
