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
            Assert(generated.Contains("result->Data[length] = 0;", StringComparison.Ordinal), "Dynamic strings were not explicitly NUL-terminated.");
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
            var allocationOutput = Normalize(allocationResult.StandardOutput);
            Assert(allocationOutput == "1\nvalue=42.\n", $"A fused scalar concatenation did not use one contiguous result allocation. Output: {allocationOutput}");
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
            var compilation = Compile(source);
            using var map = new StringWriter();
            Assert(compilation.EmitSymbolMap(map).Success, "The reachability symbol map failed.");
            using var document = System.Text.Json.JsonDocument.Parse(map.ToString());
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            Assert(!symbols.Any(symbol => symbol.GetProperty("identity").GetString()!.Contains("::Unused", StringComparison.Ordinal)), "An unreachable user method was emitted.");
            var leafName = symbols.Single(symbol => symbol.GetProperty("identity").GetString()!.Contains("::Leaf", StringComparison.Ordinal)).GetProperty("name").GetString()!;
            var leafStart = generated.IndexOf(leafName, StringComparison.Ordinal);
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

        suite.Run("draft 0.14 deterministic C~ debug information", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-debug-info", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            var sourcePath = Path.Combine(root, "src", "Program.ct");
            const string source = """
                using System;
                public static class Program
                {
                    private static int Increment(int value)
                    {
                        int result = value + 1;
                        int[] items = new int[1];
                        foreach (int item in items)
                        {
                            result = result + item;
                        }
                        return result;
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        int answer = Increment(41);
                        if (answer > 0)
                        {
                            answer = answer - 1;
                        }
                        else
                        {
                            answer = 0;
                        }
                        defer Console.WriteLine("done");
                        try
                        {
                            throw new Exception("test");
                        }
                        catch (Exception error)
                        {
                            Console.WriteLine(error.Message);
                        }
                        finally
                        {
                            Console.WriteLine(answer);
                        }
                    }
                }
                """;
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            try
            {
                File.WriteAllText(sourcePath, source);
                var options = new CompilationOptions(SourceRoot: root, DebugInformation: DebugInformationMode.Source);
                var compilation = Compile(source, options, sourcePath);
                using var generatedWriter = new StringWriter();
                using var firstMapWriter = new StringWriter();
                using var secondMapWriter = new StringWriter();
                Assert(compilation.EmitC(generatedWriter).Success, "Debug C emission failed.");
                Assert(compilation.EmitDebugMap(firstMapWriter).Success, "Debug-map emission failed.");
                Assert(compilation.EmitDebugMap(secondMapWriter).Success, "Repeated debug-map emission failed.");
                var generated = generatedWriter.ToString();
                var debugMap = firstMapWriter.ToString();
                Assert(generated.Contains("#line 6 \"src/Program.ct\"", StringComparison.Ordinal), "An executable C~ statement was not mapped with #line.");
                Assert(generated.Contains("#line 1 \"<ctilde-generated>\"", StringComparison.Ordinal), "Generated runtime code did not reset source mapping.");
                Assert(generated.Contains("ct_debug_throw_hook", StringComparison.Ordinal) && generated.Contains("ct_debug_fatal_hook", StringComparison.Ordinal), "Debug runtime hooks were not emitted.");
                Assert(generated.Contains("uint32_t unhandled", StringComparison.Ordinal) && generated.Contains("ct_exception_top == NULL ? 1u : 0u", StringComparison.Ordinal), "The exception hook did not expose a stable 32-bit handled-state value.");
                Assert(!generated.Contains("ct_debug_control_block", StringComparison.Ordinal) && !generated.Contains("ct_debug_live_head", StringComparison.Ordinal), "Source-only debugging unexpectedly emitted instrumentation overhead.");
                var bundle = compilation.EmitCBundle();
                Assert(bundle.Success, "Modular debug C emission failed.");
                var internalHeader = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
                var runtimeSource = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource).Content;
                Assert(!internalHeader.Contains("CT_DEBUG_NOINLINE static", StringComparison.Ordinal) && !internalHeader.Contains("static CT_DEBUG_NOINLINE static", StringComparison.Ordinal), "The modular debug header emitted a duplicate or internal hook declaration.");
                Assert(runtimeSource.Contains("CT_DEBUG_NOINLINE void ct_debug_throw_hook", StringComparison.Ordinal), "The modular runtime omitted the external debug hook definition.");
                Assert(debugMap == secondMapWriter.ToString(), "Debug-map emission was not deterministic.");
                Assert(debugMap.Contains("\"version\": 2", StringComparison.Ordinal) && debugMap.Contains("\"instrumented\": false", StringComparison.Ordinal), "Source debug metadata did not use the v2 non-instrumented contract.");
                Assert(debugMap.Contains("\"displayName\": \"Program.Increment\"", StringComparison.Ordinal), "The debug map omitted the source method name.");
                Assert(debugMap.Contains("\"name\": \"result\"", StringComparison.Ordinal) && debugMap.Contains("\"storage\": \"ct_l_0\"", StringComparison.Ordinal), "The debug map omitted local storage metadata.");
                Assert(debugMap.Contains("\"file\": \"src/Program.ct\"", StringComparison.Ordinal), "The debug map did not use a reproducible project-relative source path.");
                Assert(!debugMap.Contains(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "The deterministic debug map leaked an absolute source root.");

                var instrumentedOptions = new CompilationOptions(SourceRoot: root, DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Objects);
                var instrumentedCompilation = Compile(source, instrumentedOptions, sourcePath);
                using var instrumentedWriter = new StringWriter();
                using var instrumentedMapWriter = new StringWriter();
                Assert(instrumentedCompilation.EmitC(instrumentedWriter).Success, "Instrumented C emission failed.");
                Assert(instrumentedCompilation.EmitDebugMap(instrumentedMapWriter).Success, "Instrumented debug-map emission failed.");
                var instrumented = instrumentedWriter.ToString();
                var instrumentedMap = instrumentedMapWriter.ToString();
                Assert(instrumented.Contains("ct_debug_control_block", StringComparison.Ordinal) && instrumented.Contains("ct_debug_site(UINT32_C(", StringComparison.Ordinal), "Instrumented emission omitted its logical-probe runtime or call sites.");
                Assert(instrumented.Contains("ct_debug_live_head", StringComparison.Ordinal) && instrumented.Contains("ct_debug_object_initialized", StringComparison.Ordinal), "Object memory diagnostics were not emitted.");
                Assert(instrumentedMap.Contains("\"instrumented\": true", StringComparison.Ordinal) && instrumentedMap.Contains("\"memoryDiagnostics\": \"objects\"", StringComparison.Ordinal), "Instrumented debug metadata omitted its mode contract.");
                Assert(instrumentedMap.Contains("\"kind\": \"entry\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"kind\": \"call\"", StringComparison.Ordinal), "Instrumented debug metadata omitted method-entry or call probe sites.");
                Assert(instrumentedMap.Contains("\"kind\": \"defer\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"kind\": \"catch\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"kind\": \"finally\"", StringComparison.Ordinal), "Instrumented debug metadata omitted cleanup or exception probe sites.");
                Assert(instrumentedMap.Contains("\"scopes\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"liveStart\"", StringComparison.Ordinal), "Instrumented debug metadata omitted lexical lifetime information.");
                Assert(instrumentedMap.Contains("\"runtimeControl\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"enabledOffset\"", StringComparison.Ordinal) && instrumentedMap.Contains("\"pointerSize\": 4", StringComparison.Ordinal) && instrumentedMap.Contains("\"pointerSize\": 8", StringComparison.Ordinal), "Instrumented debug metadata omitted its optional bulk control layouts.");
                Assert(instrumentedMap.Contains("\"runtimeSummary\"", StringComparison.Ordinal) && instrumented.Contains("ct_debug_runtime_summary_block", StringComparison.Ordinal) && instrumented.Contains("ct_debug_refresh_runtime_summary", StringComparison.Ordinal), "Instrumented debugging omitted its bulk runtime-inspection summary.");
                Assert(instrumented.Contains("(void)fflush(stdout);", StringComparison.Ordinal), "Instrumented Console.WriteLine did not flush debugger-visible output.");
                var espInstrumented = Emit(source, new CompilationOptions(CompilationTarget.EspIdf, DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Objects), sourcePath);
                Assert(espInstrumented.Contains("ct_debug_console_packet", StringComparison.Ordinal) && espInstrumented.Contains("esp_gdbstub_putchar", StringComparison.Ordinal), "Instrumented ESP output did not emit GDB remote console packets.");
                Assert(espInstrumented.Contains("ct_debug_control.SessionActive == 0u", StringComparison.Ordinal) && espInstrumented.Contains("esp_rom_uart_putc", StringComparison.Ordinal), "Instrumented ESP output did not restore the normal UART path outside a debug session.");
                using (var instrumentedDocument = System.Text.Json.JsonDocument.Parse(instrumentedMap))
                {
                    var locals = instrumentedDocument.RootElement.GetProperty("functions").EnumerateArray()
                        .SelectMany(function => function.GetProperty("locals").EnumerateArray()).ToArray();
                    var resultLocal = locals.Single(local => local.GetProperty("name").GetString() == "result");
                    var resultSource = resultLocal.GetProperty("source");
                    Assert(resultLocal.GetProperty("liveStart").GetInt32() == resultSource.GetProperty("spanStart").GetInt32() + resultSource.GetProperty("spanLength").GetInt32(), "An ordinary local became visible before its initializer completed.");
                    var answerLocal = locals.Single(local => local.GetProperty("name").GetString() == "answer");
                    var answerSource = answerLocal.GetProperty("source");
                    Assert(answerLocal.GetProperty("liveStart").GetInt32() == answerSource.GetProperty("spanStart").GetInt32() + answerSource.GetProperty("spanLength").GetInt32(), "A call-initialized local became visible during its initializer.");
                    var catchLocal = locals.Single(local => local.GetProperty("name").GetString() == "error");
                    Assert(catchLocal.GetProperty("liveStart").GetInt32() == catchLocal.GetProperty("source").GetProperty("spanStart").GetInt32(), "A catch local was not visible after its owned exception slot was initialized.");
                    var foreachLocal = locals.Single(local => local.GetProperty("name").GetString() == "item");
                    var foreachSource = foreachLocal.GetProperty("source");
                    Assert(foreachLocal.GetProperty("liveStart").GetInt32() > foreachSource.GetProperty("spanStart").GetInt32() && foreachLocal.GetProperty("liveEnd").GetInt32() <= foreachSource.GetProperty("spanStart").GetInt32() + foreachSource.GetProperty("spanLength").GetInt32(), "A foreach local escaped its active iteration body.");
                }
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(instrumented, @"if \([^\r\n]+\)\r?\n\s+(?:#line|ct_debug_)", System.Text.RegularExpressions.RegexOptions.CultureInvariant), "A structural block probe separated a generated if statement from its body.");
                var instrumentedBundle = instrumentedCompilation.EmitCBundle();
                Assert(instrumentedBundle.Success, "Instrumented modular C emission failed.");
                var instrumentedHeader = instrumentedBundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
                var instrumentedRuntime = instrumentedBundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource).Content;
                var instrumentedNamespaces = instrumentedBundle.Artifacts.Where(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource).Select(artifact => artifact.Content).ToArray();
                Assert(instrumentedHeader.Contains("extern ct_debug_control_block ct_debug_control;", StringComparison.Ordinal), "The modular debug header defined the shared control block instead of declaring it.");
                Assert(instrumentedHeader.Contains("extern ct_debug_runtime_summary_block ct_debug_runtime_summary;", StringComparison.Ordinal), "The modular debug header defined the runtime summary instead of declaring it.");
                Assert(instrumentedRuntime.Contains("ct_debug_control_block ct_debug_control =", StringComparison.Ordinal), "The modular runtime omitted the debug control-block definition.");
                Assert(instrumentedRuntime.Contains("ct_debug_runtime_summary_block ct_debug_runtime_summary;", StringComparison.Ordinal), "The modular runtime omitted the debug runtime-summary definition.");
                Assert(instrumentedNamespaces.All(content => !content.Contains("CT_DEBUG_USER_NOINLINE static ", StringComparison.Ordinal)), "An instrumented modular method retained internal linkage despite its shared declaration.");
                var instrumentationOnly = Emit(source, new CompilationOptions(SourceRoot: root, DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Off), sourcePath);
                Assert(instrumentationOnly.Contains("ct_debug_control_block", StringComparison.Ordinal) && !instrumentationOnly.Contains("ct_debug_live_head", StringComparison.Ordinal), "Instrumentation-only mode did not isolate optional ARC diagnostics.");
                var guarded = Emit(source, new CompilationOptions(SourceRoot: root, DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Guarded), sourcePath);
                Assert(guarded.Contains("UINT32_C(0xC71DE14D)", StringComparison.Ordinal) && guarded.Contains("ct_debug_quarantine_count > 16u", StringComparison.Ordinal) && guarded.Contains("ct_debug_quarantine_bytes > 32768u", StringComparison.Ordinal), "Guarded memory diagnostics omitted canaries or bounded quarantine checks.");
                const string reservedDebugSymbol = "public static class Native { [Extern(\"ct_debug_control\")] public static int Read(); } public static class P { [EntryPoint] public static void Main() { } }";
                Assert(Compile(reservedDebugSymbol).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A private debugger runtime symbol conflict was not diagnosed.");

                var ordinary = Emit(source, path: sourcePath);
                Assert(!ordinary.Contains("ct_debug_throw_hook", StringComparison.Ordinal) && !ordinary.Contains("<ctilde-generated>", StringComparison.Ordinal), "Ordinary emission unexpectedly enabled debug-only output.");
                var ordinaryEsp = Emit(source, new CompilationOptions(CompilationTarget.EspIdf), sourcePath);
                Assert(!ordinaryEsp.Contains("ct_debug_console_packet", StringComparison.Ordinal) && !ordinaryEsp.Contains("esp_gdbstub_putchar", StringComparison.Ordinal), "Ordinary ESP emission unexpectedly enabled debugger console packet output.");

                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
                var cli = RunProcess("dotnet", [cliDll, "src/Program.ct", "-o", "cli-debug.c", "--source-root", ".", "--debug-info", "--debug-map", "cli-debug.json"], workingDirectory: root);
                Assert(cli.ExitCode == 0, $"CLI debug-info emission failed: {cli.StandardError}");
                Assert(File.ReadAllText(Path.Combine(root, "cli-debug.c")).Contains("ct_debug_throw_hook", StringComparison.Ordinal), "CLI --debug-info did not enable debug C emission.");
                Assert(File.ReadAllText(Path.Combine(root, "cli-debug.json")).Contains("\"entryPoint\"", StringComparison.Ordinal), "CLI --debug-map did not write the deterministic debug map.");
                var incompatible = RunProcess("dotnet", [cliDll, "src/Program.ct", "--check", "--debug-info"], workingDirectory: root);
                Assert(incompatible.ExitCode == 2, "CLI --check accepted debug emission options.");
                var invalidMemoryMode = RunProcess("dotnet", [cliDll, "src/Program.ct", "-o", "invalid.c", "--debug-memory", "objects"], workingDirectory: root);
                Assert(invalidMemoryMode.ExitCode == 2, "CLI accepted --debug-memory outside debug Launch preparation.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        });
    }
}
