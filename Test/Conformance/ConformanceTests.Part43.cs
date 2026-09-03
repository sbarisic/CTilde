using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart43(ConformanceSuite suite)
    {
        suite.Run("draft 0.46 storage surface and managed filesystem services", () =>
        {
            var configuration = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.Storage", "1.0.0", [], 8192, 65536);
            var source = """
                using System.IO;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        string current = Directory.GetCurrentDirectory();
                        if (!File.Exists(current + "/probe.bin"))
                            return Directory.GetFileSystemEntries(current).Length;
                        FileStream stream = File.OpenRead(current + "/probe.bin");
                        defer stream.Dispose();
                        return (int)stream.Length;
                    }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf,
                Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: configuration.Kind, ManagedModule: configuration);
            var compilation = Compile(source, options);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var combined = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(combined.Contains("ct_runtime_api_v19", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(32)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(48)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(53)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(56)", StringComparison.Ordinal),
                "Managed System.IO did not lower through Runtime ABI 19 filesystem services.");
            Assert(!combined.Contains("fopen(path", StringComparison.Ordinal),
                "Managed System.IO retained a private libc filesystem implementation.");
        });

        suite.Run("draft 0.46 ESP storage ownership surface", () =>
        {
            const string source = """
                using System.Storage;
                using Esp.Idf.Storage;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        RemovableSdCardMonitor monitor = new RemovableSdCardMonitor(SdSpiConfiguration.TCan485);
                        defer monitor.Dispose();
                        monitor.AddFatMount(-1, "/sd", 8);
                        monitor.Start();
                        ulong generation = monitor.Generation;
                    }
                }
                """;
            var compilation = Compile(source, new CompilationOptions(CompilationTarget.EspIdf,
                Architecture: CompilationArchitecture.Xtensa));
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            using var writer = new StringWriter();
            var result = compilation.EmitC(writer);
            Assert(result.Success && writer.ToString().Contains("ct_storage_monitor_add_fat", StringComparison.Ordinal),
                "The ESP removable-storage surface did not emit its native adapter calls.");
        });
    }
}
