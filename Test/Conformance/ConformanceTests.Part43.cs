using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart43(ConformanceSuite suite)
    {
        suite.Run("managed shell quoted command-line parsing", () =>
        {
            var parser = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "ShellCommandLine.ct"));
            const string harness = """
                using System;
                using Examples.ManagedShell;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        string[] values;
                        bool background;
                        bool ok = ShellCommandLine.TryParse("program.ctm 1 \"argument 2\" 3", out values, out background);
                        Console.WriteLine(ok && !background && values.Length == 4 && values[0] == "program.ctm"
                            && values[1] == "1" && values[2] == "argument 2" && values[3] == "3");
                        ok = ShellCommandLine.TryParse("tool.ctm \"\" pre\"two words\"post", out values, out background);
                        Console.WriteLine(ok && values.Length == 3 && values[1] == ""
                            && values[2] == "pretwo wordspost");
                        ok = ShellCommandLine.TryParse("tool.ctm \"&\"", out values, out background);
                        Console.WriteLine(ok && !background && values.Length == 2 && values[1] == "&");
                        ok = ShellCommandLine.TryParse("tool.ctm &", out values, out background);
                        Console.WriteLine(ok && background && values.Length == 1);
                        ok = ShellCommandLine.TryParse("tool.ctm a\\tb\\n", out values, out background);
                        Console.WriteLine(ok && values.Length == 2 && values[1].Length == 4);
                        ok = ShellCommandLine.TryParse("tool.ctm a\\\"b c\\\\d", out values, out background);
                        Console.WriteLine(ok && values.Length == 3 && values[1] == "a\"b"
                            && values[2] == "c\\d");
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \"", out values, out background));
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \\q", out values, out background));
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \\", out values, out background));
                    }
                }
                """;
            var result = CompileAndRun([
                SyntaxTree.ParseText(parser, "ShellCommandLine.ct"),
                SyntaxTree.ParseText(harness, "Program.ct")]);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) ==
                "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n",
                $"ManagedShell command-line parsing failed ({result.ExitCode}).\n{result.StandardOutput}\n{result.StandardError}");
        });

        suite.Run("managed module utility definitions retain valid GNU attributes", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        if (args.Length == 0)
                            return -1;
                        return args[0].IndexOf(':');
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Tests.UtilityAttributes", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var combined = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(!combined.Contains("CT_GENERATED_LOCAL {", StringComparison.Ordinal),
                "Managed-module externalization attached a visibility attribute to a function body.");
        });

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
