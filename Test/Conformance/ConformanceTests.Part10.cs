using System.Globalization;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart10(ConformanceSuite suite)
    {
        suite.Run("draft 0.12 fused strings and terminated native paths", () =>
        {
            const string source = """
                using System;
                using System.IO;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int value = 42;
                        string path = "joined-" + value.ToString() + ".bin";
                        FileHandle file = File.Open(path, FileMode.Create, FileAccess.Write);
                        defer File.Close(file);
                        File.Write(file, "ok");
                        Console.WriteLine(path);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("data[length] = 0;", StringComparison.Ordinal), "Dynamic strings were not explicitly NUL-terminated.");
            Assert(generated.Contains("ct_string_build(", StringComparison.Ordinal), "The concatenation tree did not use the fused string builder.");
            var result = CompileAndRun(source, captureFile: "joined-42.bin");
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "joined-42.bin\n", result.StandardOutput);
            Assert(result.CapturedFile?.SequenceEqual("ok"u8.ToArray()) == true, "The concatenated native path did not name the expected file.");

            const string allocations = """
                using System;
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_total_allocations")]
                    [NoAlloc]
                    public static uint TotalAllocations();
                }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        uint before = Diagnostics.TotalAllocations();
                        string text = "value=" + 42.ToString() + ".";
                        uint after = Diagnostics.TotalAllocations();
                        Console.WriteLine(after - before);
                        Console.WriteLine(text);
                    }
                }
                """;
            var allocationResult = CompileAndRun(allocations, memoryDiagnostics: true);
            Assert(allocationResult.ExitCode == 0, allocationResult.StandardError);
            Assert(Normalize(allocationResult.StandardOutput) == "2\nvalue=42.\n", "A fused scalar concatenation did not use exactly one string object and one data allocation.");
        });

        suite.Run("draft 0.12 cleanup and reachability emission", () =>
        {
            const string source = """
                public static class Program
                {
                    private static int Leaf(int value) { return value + 1; }
                    private static int Unused() { return 99; }
                    [EntryPoint] public static void Main() { int result = Leaf(41); }
                }
                """;
            var generated = Emit(source);
            Assert(!generated.Contains("ct_keep_symbols", StringComparison.Ordinal), "Generated C retained ct_keep_symbols.");
            Assert(!generated.Contains("_6_Unused", StringComparison.Ordinal), "An unreachable user method was emitted.");
            var leafStart = generated.IndexOf("_4_Leaf_i32", StringComparison.Ordinal);
            Assert(leafStart >= 0, "The reachable leaf method was not emitted.");
            var leafEnd = generated.IndexOf("\n}\n", leafStart, StringComparison.Ordinal);
            var leaf = generated[leafStart..leafEnd];
            Assert(!leaf.Contains("ct_cleanup_", StringComparison.Ordinal), "A value-only leaf method accessed the cleanup stack.");
            Assert(!leaf.Contains("(void)u_5_value", StringComparison.Ordinal), "A used leaf parameter retained a blanket unused-value cast.");

            const string catchMutation = """
                using System;
                public static class Program
                {
                    private static int Probe(int value)
                    {
                        try { if (value < 0) throw new Exception("negative"); }
                        catch (Exception) { value = -value; }
                        return value;
                    }
                    [EntryPoint] public static void Main() { Console.WriteLine(Probe(-42)); }
                }
                """;
            var durableCatch = Emit(catchMutation);
            Assert(durableCatch.Contains("ct_state.ct_pp_0", StringComparison.Ordinal), "A parameter mutated in a catch handler was not placed in durable storage.");
            var durableCatchResult = CompileAndRun(catchMutation);
            Assert(durableCatchResult.ExitCode == 0 && Normalize(durableCatchResult.StandardOutput) == "42\n", "Catch-handler durable parameter storage changed runtime behavior.");
        });

        suite.Run("draft 0.12 reproducible source paths", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-source-root", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            var path = Path.Combine(root, "src", "Program.ct");
            const string source = "public static class Program { private static int Divide(int value) { return 1 / value; } [EntryPoint] public static void Main() { int value = Divide(1); } }";
            var defaultOutput = Emit(source, path: path);
            Assert(defaultOutput.Contains(path.Replace('\\', '/'), StringComparison.Ordinal), "Default hosted emission did not preserve the full source path.");
            var rootedOutput = Emit(source, new CompilationOptions(SourceRoot: root), path);
            Assert(rootedOutput.Contains("\"src/Program.ct\"", StringComparison.Ordinal), "Source-root emission did not use a normalized relative path.");
            Assert(!rootedOutput.Contains(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "Source-root emission leaked the absolute root.");

            Assert(Compile(source, new CompilationOptions(SourceRoot: "relative"), path).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4106"), "A relative API source root was accepted.");
            var outside = Path.Combine(Path.GetTempPath(), "outside.ct");
            Assert(Compile(source, new CompilationOptions(SourceRoot: root), outside).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4106"), "A source outside the configured root was accepted.");
            Assert(Compile(source, new CompilationOptions(CompilationTarget.EspIdf, root), path).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4106"), "ESP-IDF accepted a hosted source root.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                File.WriteAllText(path, source);
                File.WriteAllText(Path.Combine(root, "ctilde.json"), "{\"target\":\"hosted\",\"sources\":[\"src/*.ct\"]}");
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));

                var direct = RunProcess("dotnet", [cliDll, "src/Program.ct", "-o", "direct.c", "--source-root", "."], workingDirectory: root);
                Assert(direct.ExitCode == 0, direct.StandardError);
                var directOutput = File.ReadAllText(Path.Combine(root, "direct.c"));
                Assert(directOutput.Contains("\"src/Program.ct\"", StringComparison.Ordinal) && !directOutput.Contains(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "Direct CLI source-root emission was not reproducible.");

                var project = RunProcess("dotnet", [cliDll, "--project", "ctilde.json", "-o", "project.c", "--source-root", "."], workingDirectory: root);
                Assert(project.ExitCode == 0 && File.ReadAllText(Path.Combine(root, "project.c")).Contains("\"src/Program.ct\"", StringComparison.Ordinal), "Project CLI source-root emission failed.");

                var directory = RunProcess("dotnet", [cliDll, "--compile-directory", "src", "--source-root", "."], workingDirectory: root);
                Assert(directory.ExitCode == 0 && File.Exists(Path.Combine(root, "src", "Program.c")), "Compile-directory source-root emission failed.");

                var esp = RunProcess("dotnet", [cliDll, "src/Program.ct", "-o", "esp.c", "--target", "esp-idf", "--source-root", "."], workingDirectory: root);
                Assert(esp.ExitCode == 2, "ESP-IDF CLI source-root misuse was not a usage error.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        });
    }
}
