using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart17(ConformanceSuite suite)
    {
        suite.Run("draft 0.19 constant generics and inline arrays", () =>
        {
            const string source = """
                using System;
                public struct Buffer<T, const int Capacity>
                {
                    public T[Capacity] Items;
                }
                public static class Program
                {
                    private static int Add<const int Amount>(int value) { return value + Amount; }
                    [EntryPoint] public static void Main()
                    {
                        Buffer<byte, 4> buffer = new Buffer<byte, 4>();
                        buffer.Items[0] = (byte)38;
                        Console.WriteLine(Add<4>(buffer.Items[0]));
                        Console.WriteLine(buffer.Items.Length);
                    }
                }
                """;
            var runtime = CompileAndRun(source);
            Assert(runtime.ExitCode == 0 && runtime.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "42\n4",
                $"Constant specialization or inline-array value semantics changed runtime behavior: exit={runtime.ExitCode}, output='{runtime.StandardOutput}'.");
            var generated = Emit(source);
            Assert(generated.Contains("Data[4]", StringComparison.Ordinal), "Inline-array wrapper did not use the closed constant length.");

            var missing = Compile("public static class Program { private static int F<const int N>() { return N; } [EntryPoint] public static void Main() { int x = F(); } }");
            Assert(missing.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A constant method argument was inferred or omitted.");
            var invalid = Compile("public struct Bad<const float N> { public int Value; } public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2202"), "Invalid constant-parameter type did not report CT2202.");
        });

        suite.Run("draft 0.19 alignment newtypes and CPU intrinsics", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public newtype FileDescriptor : int;
                [Align(64)] public struct CacheLine { public byte[64] Data; }
                public static class Program
                {
                    [Align(32)] private static byte[32] storage;
                    [EntryPoint] public static void Main()
                    {
                        [Align(16)] uint value = Cpu.ByteSwap((uint)0x01020304);
                        CacheLine cache = new CacheLine();
                        cache.Data[0] = (byte)1;
                        FileDescriptor descriptor = (FileDescriptor)3;
                        Console.WriteLine((int)descriptor + (int)Cpu.PopCount(value) + cache.Data[0] - 1);
                        Console.WriteLine(Cpu.LeadingZeroCount((uint)0));
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("CT_ALIGN(64)", StringComparison.Ordinal) && generated.Contains("CT_ALIGN(32)", StringComparison.Ordinal),
                "General alignment was not carried into native declarations.");
            var runtime = CompileAndRun(source);
            Assert(runtime.ExitCode == 0 && runtime.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "8\n32",
                $"Newtype casts or portable CPU intrinsics changed runtime behavior: exit={runtime.ExitCode}, output='{runtime.StandardOutput}'.");

            var implicitConversion = Compile("public newtype Id : int; public static class Program { [EntryPoint] public static void Main() { Id id = 1; } }");
            Assert(implicitConversion.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2205"), "Implicit newtype conversion did not report CT2205.");

            const string nativeSurface = """
                public newtype PacketId : uint;
                [Align(16)] public struct Packet { public byte[4] Bytes; public PacketId Id; }
                public static class Program
                {
                    [Export("packet_echo")] public static Packet Echo(Packet value) { return value; }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var nativeCompilation = Compile(nativeSurface);
            using var headerWriter = new StringWriter();
            Assert(nativeCompilation.EmitCHeader(headerWriter).Success, string.Join(Environment.NewLine, nativeCompilation.GetDiagnostics()));
            var header = headerWriter.ToString();
            Assert(header.IndexOf("typedef uint32_t", StringComparison.Ordinal) < header.IndexOf("packet_echo", StringComparison.Ordinal) &&
                header.Contains("Data[4]", StringComparison.Ordinal) && header.Contains("CT_ALIGN(16)", StringComparison.Ordinal),
                "Public newtype, inline-array, or alignment ABI declarations were missing or out of order.");
        });

        suite.Run("draft 0.19 no recursion analysis", () =>
        {
            const string recursive = "public static class Program { [NoRecursion] private static void A() { B(); } private static void B() { A(); } [EntryPoint] public static void Main() { A(); } }";
            var attributed = Compile(recursive);
            Assert(attributed.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "NoRecursion did not reject a mutual cycle.");

            const string projectRecursive = "public static class Program { private static void A() { A(); } [EntryPoint] public static void Main() { A(); } }";
            var project = Compile(projectRecursive, new CompilationOptions(NoRecursion: true));
            Assert(project.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "Project-wide no-recursion did not reject a reachable cycle.");

            const string unknown = "public delegate void Work(); public static class Program { private static void Done() { } [NoRecursion] private static void Run(Work work) { work(); } [EntryPoint] public static void Main() { Work work = Done; Run(work); } }";
            Assert(Compile(unknown).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "NoRecursion accepted open delegate dispatch.");
        });

        suite.Run("draft 0.44 stack usage contracts and metadata", () =>
        {
            const string valid = "public static class Native { [Extern(\"native_leaf\")][StackUsage(64)] public static void Leaf(); } public static class Program { [StackUsage(512)][EntryPoint] public static void Main() { Native.Leaf(); } }";
            var compilation = Compile(valid);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            using var map = new StringWriter();
            Assert(compilation.EmitSymbolMap(map).Success, "Stack-usage symbol map emission failed.");
            Assert(map.ToString().Contains("\"stackUsageBytes\": 512", StringComparison.Ordinal) &&
                map.ToString().Contains("\"stackUsageBytes\": 64", StringComparison.Ordinal) &&
                map.ToString().Contains("\"entryPoint\": true", StringComparison.Ordinal),
                "Stack contracts or native-root metadata were omitted from the symbol map.\n" + map);

            const string invalid = "public abstract class A { [StackUsage(8)] public abstract void M(); } public static class Program { [StackUsage(0)] [EntryPoint] public static void Main() { } }";
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT1323") >= 2,
                "Malformed or abstract StackUsage declarations did not report CT1323.");

            var optionRoot = Path.Combine(Path.GetTempPath(), "ctilde-stack-option-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(optionRoot);
            try
            {
                var input = Path.Combine(optionRoot, "Program.ct");
                File.WriteAllText(input, "public static class Program { [EntryPoint] public static void Main() { } }");
                Assert(CTilde.Cli.CommandLineOptions.TryParse(["--check", "--stack-report", Path.Combine(optionRoot, "stack.json"), input],
                    out var parsed, out var parseError, out _) && parseError is null, "Stack-report CLI options did not parse.");
                try
                {
                    _ = CTilde.Cli.BuildRequestResolver.Resolve(parsed!);
                    Assert(false, "A stack report was accepted without a native build.");
                }
                catch (CTilde.Cli.CommandLineException exception)
                {
                    Assert(exception.Message.Contains("native-build", StringComparison.Ordinal) ||
                        exception.Message.Contains("--build or --run", StringComparison.Ordinal), exception.Message);
                }
            }
            finally
            {
                if (Directory.Exists(optionRoot)) Directory.Delete(optionRoot, true);
            }
        });

        suite.Run("draft 0.44 GCC stack report analysis", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-stack-report-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var usage = Path.Combine(root, "program.su");
                var graph = Path.Combine(root, "program.ci");
                var symbols = Path.Combine(root, "symbols.json");
                var report = Path.Combine(root, "stack.json");
                File.WriteAllText(usage, "program.c:1:1:main\t16\tstatic\nprogram.c:2:1:ct_method\t32\tstatic\n");
                File.WriteAllText(graph, "graph: { title: \"program.c\"\nnode: { title: \"main\" label: \"main\" }\nnode: { title: \"ct_method\" label: \"ct_method\" }\nnode: { title: \"native_leaf\" label: \"native_leaf\" }\nedge: { sourcename: \"main\" targetname: \"ct_method\" }\nedge: { sourcename: \"ct_method\" targetname: \"native_leaf\" }\n}\n");
                void WriteSymbols(uint bound) => File.WriteAllText(symbols, System.Text.Json.JsonSerializer.Serialize(new
                {
                    symbols = new object[]
                    {
                        new { name = "ct_method", identity = "method:Program::Main()->void", kind = "method", entryPoint = true, used = false, stackUsageBytes = bound },
                        new { name = "native_leaf", identity = "method:Native::Leaf()->void", kind = "extern", stackUsageBytes = 8u },
                    }
                }));
                var request = new CTilde.Cli.BuildRequest([], CompilationTarget.Hosted, CompilationArchitecture.X64, null,
                    root, null, Path.Combine(root, "program.c"), null, false, false, true, false,
                    CTildeNativeBuildConfiguration.Release, "gcc", Path.Combine(root, "program"), null, null,
                    GeneratedCLayout.Unity, null, symbols, report, false);
                var native = new CTilde.Cli.NativeBuildOutcome(0, "gcc", "gcc", null, [usage, graph]);
                WriteSymbols(64);
                var passing = CTilde.Cli.StackUsageReporter.Analyze(request, native);
                Assert(!passing.ContractFailure && File.ReadAllText(report).Contains("\"worstCaseBytes\": 40", StringComparison.Ordinal),
                    "A complete trusted-boundary stack contract did not pass with the expected longest path.");
                WriteSymbols(16);
                var failing = CTilde.Cli.StackUsageReporter.Analyze(request, native);
                Assert(failing.ContractFailure && failing.Messages.Any(message => message.Contains("CT2226", StringComparison.Ordinal)),
                    "An exceeded native stack contract did not fail with CT2226 evidence.");

                WriteSymbols(64);
                File.WriteAllText(usage, File.ReadAllText(usage).Replace(":main\t", ":ct_managed_main\t"));
                File.WriteAllText(graph, File.ReadAllText(graph).Replace("\"main\"", "\"ct_managed_main\""));
                var managedRequest = request with
                {
                    Target = CompilationTarget.EspIdf,
                    ManagedModule = new ManagedModuleConfiguration(ManagedModuleKind.Application, "tests.stack", "1.0.0", [], 48, null),
                };
                var managed = CTilde.Cli.StackUsageReporter.Analyze(managedRequest, native);
                Assert(managed.ContractFailure && managed.Messages.Any(message => message.Contains("at least 56", StringComparison.Ordinal)),
                    "Managed entry must use ct_managed_main and enforce its configured stack.");
                File.AppendAllText(graph, "edge: { sourcename: \"ct_method\" targetname: \"__indirect_call\" }\n");
                var indirect = CTilde.Cli.StackUsageReporter.Analyze(managedRequest with
                {
                    ManagedModule = managedRequest.ManagedModule! with { MainTaskStackBytes = 4096 },
                }, native);
                Assert(indirect.ContractFailure && File.ReadAllText(report).Contains("__indirect_call:missing-frame", StringComparison.Ordinal),
                    "An indirect call must keep the stack bound unknown.");

                File.WriteAllText(usage, "program.c:1:1:worker\t8\tstatic\nprogram.c:2:1:ct_task\t24\tstatic\n");
                File.WriteAllText(graph, "graph: { title: \"program.c\"\nnode: { title: \"worker\" label: \"worker\" }\nnode: { title: \"ct_task\" label: \"ct_task\" }\nedge: { sourcename: \"worker\" targetname: \"ct_task\" }\n}\n");
                File.WriteAllText(symbols, System.Text.Json.JsonSerializer.Serialize(new
                {
                    symbols = new object[]
                    {
                        new { name = "ct_task", identity = "method:Program::Worker(:void*)->void", kind = "method", export = "worker", entryPoint = false, used = false, taskStackBytes = 64u },
                    }
                }));
                var task = CTilde.Cli.StackUsageReporter.Analyze(request, native);
                var taskReport = File.ReadAllText(report);
                Assert(!task.ContractFailure && taskReport.Contains("\"headroomBytes\": 32", StringComparison.Ordinal) &&
                    taskReport.Contains("\"status\": \"verified\"", StringComparison.Ordinal),
                    "A complete TaskEntry graph did not report verified byte headroom.");

                File.WriteAllText(usage, "program.c:1:1:ct_recursive\t12\tstatic\n");
                File.WriteAllText(graph, "graph: { title: \"program.c\"\nnode: { title: \"ct_recursive\" label: \"ct_recursive\" }\nedge: { sourcename: \"ct_recursive\" targetname: \"ct_recursive\" }\n}\n");
                File.WriteAllText(symbols, System.Text.Json.JsonSerializer.Serialize(new
                {
                    symbols = new object[]
                    {
                        new { name = "ct_recursive", identity = "method:Program::Recursive()->void", kind = "method", entryPoint = false, used = false, stackUsageBytes = 128u },
                    }
                }));
                var recursive = CTilde.Cli.StackUsageReporter.Analyze(request, native);
                Assert(recursive.ContractFailure && File.ReadAllText(report).Contains("recursive-cycle", StringComparison.Ordinal),
                    "A recursive stack contract was not reported as incomplete.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
