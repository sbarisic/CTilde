using System.Text.RegularExpressions;
using CTilde;
using CTilde.Cli;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart45(ConformanceSuite suite)
    {
        suite.Run("draft 0.51 SFTP rooted relative paths", () =>
        {
            var protocol = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "Protocol.ct"));
            var start = protocol.IndexOf("    internal static bool TryNormalizeSftpPath(", StringComparison.Ordinal);
            var end = protocol.IndexOf("    internal static bool IsAuthorizedP256Line(", start, StringComparison.Ordinal);
            Assert(start >= 0 && end > start, "Production path normalizer was not found.");
            var helper = "using System; internal static class SshWire {\n" + protocol[start..end] + "\n}";
            const string source = """
                using System;
                public static class Program
                {
                    private static void Check(string input, string expected)
                    {
                        string actual;
                        bool valid = SshWire.TryNormalizeSftpPath(input, out actual);
                        if (valid != (expected != null) || actual != expected)
                            throw new InvalidOperationException();
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        Check(".", "/sftp");
                        Check("", "/sftp");
                        Check("/", "/sftp");
                        Check("./docs//readme.txt", "/sftp/docs/readme.txt");
                        Check("/docs/./readme.txt", "/sftp/docs/readme.txt");
                        Check("../storage/ssh", null);
                        Check("/docs/../escape", null);
                        Check("docs\\escape", null);
                        Check("docs\0escape", null);
                        Check(null, null);
                        Console.WriteLine("SFTP_PATH_OK");
                    }
                }
                """;
            var result = CompileAndRun([SyntaxTree.ParseText(helper, "paths.ct"), SyntaxTree.ParseText(source, "test.ct")]);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SFTP_PATH_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 SFTP incremental request framing", () =>
        {
            var framer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "SftpFramer.ct")).Replace("[Overlay(\"sftp-core\")]", "", StringComparison.Ordinal);
            const string source = """
                using System;
                using System.Ssh;
                public static class Program
                {
                    [Extern("ct_memory_diagnostic_live_objects")]
                    [NoAlloc]
                    public static uint LiveObjects();
                    private static void Check(bool value)
                    {
                        if (!value) throw new InvalidOperationException();
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = LiveObjects();
                        Run();
                        Check(LiveObjects() == baseline);
                        Console.WriteLine("SFTP_FRAMER_OK");
                    }
                    private static void Run()
                    {
                        SftpFramer framer = new SftpFramer();
                        byte[] wire = new byte[35004];
                        wire[2] = (byte)136; wire[3] = (byte)184;
                        for (int index = 4; index < wire.Length; index++)
                            wire[index] = (byte)index;
                        for (int split = 0; split <= 4; split++)
                        {
                            byte[] prefix = new byte[split];
                            byte[] suffix = new byte[wire.Length - split];
                            for (int index = 0; index < split; index++) prefix[index] = wire[index];
                            for (int index = split; index < wire.Length; index++) suffix[index - split] = wire[index];
                            int position = 0;
                            Check(framer.Read(prefix, ref position) == null && position == split);
                            position = 0;
                            byte[] packet = framer.Read(suffix, ref position);
                            Check(packet.Length == 35000 && position == suffix.Length);
                            for (int index = 0; index < packet.Length; index++) Check(packet[index] == (byte)(index + 4));
                        }
                        byte[] bytewise = new byte[1];
                        for (int index = 0; index < wire.Length; index++)
                        {
                            bytewise[0] = wire[index];
                            int position = 0;
                            byte[] packet = framer.Read(bytewise, ref position);
                            Check(position == 1);
                            Check((packet != null) == (index == wire.Length - 1));
                        }
                        byte[] joined = new byte[70008];
                        for (int index = 0; index < joined.Length; index++) joined[index] = wire[index % wire.Length];
                        int offset = 0;
                        Check(framer.Read(joined, ref offset).Length == 35000 && offset == 35004);
                        Check(framer.Read(joined, ref offset).Length == 35000 && offset == joined.Length);
                        byte[] invalid = new byte[4];
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt == 1) { invalid[2] = (byte)136; invalid[3] = (byte)185; }
                            if (attempt == 2) { invalid[0] = (byte)255; invalid[1] = (byte)255; invalid[2] = (byte)255; invalid[3] = (byte)255; }
                            offset = 0;
                            bool rejected = false;
                            try { framer.Read(invalid, ref offset); }
                            catch (InvalidOperationException) { rejected = true; }
                            Check(rejected);
                            framer.Reset();
                        }
                        byte[] partial = new byte[5];
                        partial[3] = (byte)2; partial[4] = (byte)1;
                        offset = 0;
                        Check(framer.Read(partial, ref offset) == null);
                        framer.Reset();
                        partial[3] = (byte)1;
                        offset = 0;
                        Check(framer.Read(partial, ref offset)[0] == 1);
                    }
                }
                """;
            var result = CompileAndRun([
                SyntaxTree.ParseText(framer, "SftpFramer.ct"),
                SyntaxTree.ParseText(source, "test.ct")], memoryDiagnostics: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SFTP_FRAMER_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.49 overlay placement and resident call stubs", () =>
        {
            const string source = """
                [Overlay("render")]
                public class Renderer
                {
                    public static int Trace(int value) { return Shade(value) + 1; }
                    private static int Shade(int value) { return value * 2; }
                    [Resident]
                    public static int Report(int value) { return value; }
                }

                public static class Program
                {
                    [Overlay("unused")]
                    private static void Unused() { }

                    [EntryPoint]
                    public static int Main(string[] args) { return Renderer.Trace(20); }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.Overlay", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, diagnostics));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("CT_OVERLAY_BODY(\"render\")", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_call_target_v4", StringComparison.Ordinal) &&
                generated.Contains("EnterManagedCall", StringComparison.Ordinal) &&
                generated.Contains("ct_leave_managed_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_cleanup_push(&ct_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_module_text_anchor", StringComparison.Ordinal),
                "Overlay bodies were not separated behind cleanup-safe resident stubs.");

            using var metadataWriter = new StringWriter();
            Assert(compilation.EmitManagedModuleMetadata(metadataWriter, module).Success,
                "Overlay metadata emission failed.");
            var metadataText = metadataWriter.ToString();
            Assert(metadataText.Contains("\"hasOverlays\": true", StringComparison.Ordinal) &&
                metadataText.Contains("\"name\": \"render\"", StringComparison.Ordinal) &&
                metadataText.Contains("\"targetIndex\": 0", StringComparison.Ordinal) &&
                !metadataText.Contains("\"name\": \"unused\"", StringComparison.Ordinal),
                "Schema-3 metadata omitted deterministic reachable placement or retained an unreachable overlay.");
            var bodySymbols = Regex.Matches(metadataText, "\\\"bodySymbol\\\": \\\"([^\\\"]+)\\\"")
                .Select(match => match.Groups[1].Value).ToArray();
            var bound = (BoundProgram)typeof(Compilation).GetField("_boundProgram",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(compilation)!;
            var shade = bound.Model.ProjectTypes.Single(type => type.Name == "Renderer").Methods.Single(method => method.Name == "Shade");
            var shadeBody = CEmitter.OverlayBodyName(shade, shade.CName);
            Assert(bodySymbols.Length == 1 && !shade.RequiresOverlayEntry &&
                    !bodySymbols.Contains(shadeBody) && Regex.Matches(generated, $@"\b{Regex.Escape(shadeBody)}\s*\(").Count >= 3,
                "A proven same-overlay call did not target its typed overlay body directly.");
        });

        suite.Run("draft 0.49 Xtensa overlay instruction relocation audit", () =>
        {
            Assert(ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(8u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(10u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(14u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(20u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(49u) &&
                !ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(11u) &&
                !ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(50u),
                "The audited Xtensa instruction-relocation allowlist changed unexpectedly.");
            ManagedOverlayPackager.ValidateAuditedXtensaInstructionRelocation(
                "render", 20u, true, true, "render", true);
            AssertRejected(20u, true, true, "other", true);
            AssertRejected(20u, true, true, null, true);
            AssertRejected(20u, true, false, null, false);
            AssertRejected(11u, true, true, "render", true);
            AssertRejected(20u, false, true, "render", true);

            static void AssertRejected(uint type, bool originContains, bool targetDefined,
                string? targetOverlay, bool targetContains)
            {
                try
                {
                    ManagedOverlayPackager.ValidateAuditedXtensaInstructionRelocation(
                        "render", type, originContains, targetDefined, targetOverlay, targetContains);
                    throw new InvalidOperationException("Unsafe Xtensa overlay relocation was accepted.");
                }
                catch (NativeBuildException)
                {
                }
            }
        });

        suite.Run("draft 0.49 overlay constructors properties and delegates use stable stubs", () =>
        {
            const string source = """
                public delegate int Transform(int value);

                [Overlay("outer")]
                public class Worker
                {
                    private int number;

                    [Overlay("construction")]
                    public Worker() { number = 20; }

                    public int Value { get { return number; } set { number = value; } }

                    public static int Twice(int value) { return value * 2; }

                    [Resident]
                    public int ResidentValue() { return 2; }
                }

                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Worker worker = new Worker();
                        Transform transform = Worker.Twice;
                        return worker.Value + transform(worker.ResidentValue());
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.OverlayMembers", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("CT_OVERLAY_BODY(\"construction\")", StringComparison.Ordinal),
                "The overlay constructor body was not separated.");
            Assert(generated.Contains("CT_OVERLAY_BODY(\"outer\")", StringComparison.Ordinal),
                "The inherited property or method body was not separated.");
            Assert(generated.Contains("EnterManagedCall", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_call_targets_v3", StringComparison.Ordinal),
                "Constructors, properties, or managed delegates bypassed stable overlay stubs.");
        });

        suite.Run("draft 0.49 overlay placement rejects invalid and unsafe boundaries", () =>
        {
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.InvalidOverlay", "1.0.0", [], 4096, 16384);
            var invalid = Compile("""
                [Overlay("1invalid")]
                public class InvalidName { }

                [Overlay("outer")]
                public class Worker
                {
                    [Overlay("inner")]
                    [Resident]
                    public static void Conflict() { }

                    [Overlay("native")]
                    [Extern("native_work")]
                    public static void Native();
                }

                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args) { return 0; }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var invalidDiagnostics = invalid.GetDiagnostics();
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT6230") &&
                invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT6231"),
                "Invalid overlay names, conflicts, or native boundaries were accepted.");

            var pointer = Compile("""
                public static class Program
                {
                    [Overlay("work")]
                    public static int Work(int value) { return value; }

                    [EntryPoint]
                    public static unsafe int Main(string[] args)
                    {
                        delegate* unmanaged<int, int> address = &Work;
                        return address(1);
                    }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(pointer.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6234"),
                "An overlay body exposed a raw unmanaged function pointer.");

            var interrupt = Compile("""
                public static class Program
                {
                    [Overlay("work")]
                    public static void Work() { }

                    [Interrupt]
                    [Export("irq")]
                    public static unsafe void Handler(void* context) { Work(); }

                    [EntryPoint]
                    public static int Main(string[] args) { return 0; }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(interrupt.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6235"),
                "An interrupt call closure was allowed to enter overlay code.");
        });

        suite.Run("draft 0.49 overlay target and thread restrictions", () =>
        {
            const string source = """
                using System.Threading;
                public static class Program
                {
                    [Overlay("work")]
                    public static void Work() { }

                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Thread thread = new Thread(Work);
                        thread.Start();
                        return 0;
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ThreadedOverlay", "1.0.0", [], 4096, 16384);
            var xtensa = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(xtensa.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6233"),
                "Overlay-enabled dependency closure accepted Thread.Start.");

            var riscV = Compile("""
                public static class Program
                {
                    [Overlay("work")] public static void Work() { }
                    [EntryPoint] public static int Main(string[] args) { Work(); return 0; }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.RiscV32,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(riscV.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6232"),
                "ESP32-C3 did not receive the dedicated overlay target diagnostic.");

            var providerConfiguration = new ManagedModuleConfiguration(
                ManagedModuleKind.Library, "Demo.OverlayDependency", "1.0.0", [], 4096, 16384);
            var provider = Compile("""
                namespace Demo.OverlayDependency;
                public static class Work
                {
                    [Overlay("dependency")] public static void Run() { }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: providerConfiguration.Kind, ManagedModule: providerConfiguration));
            using var metadataWriter = new StringWriter();
            Assert(provider.EmitManagedModuleMetadata(metadataWriter, providerConfiguration).Success,
                "Overlay dependency metadata emission failed.");
            var metadataPath = Path.Combine(Path.GetTempPath(), $"ctilde-overlay-{Guid.NewGuid():N}.ctmeta.json");
            File.WriteAllText(metadataPath, metadataWriter.ToString());
            try
            {
                var metadata = ManagedModuleMetadata.Load(metadataPath);
                Assert(metadata.HasOverlays, "Provider metadata did not publish overlay capability.");
                var reference = new ManagedModuleReference(metadataPath, metadata.Name, metadata.Version,
                    metadata.BuildIdentity, metadata.ApiHash, metadata);
                var consumerConfiguration = new ManagedModuleConfiguration(
                    ManagedModuleKind.Application, "Demo.ThreadedConsumer", "1.0.0", [reference], 4096, 16384);
                var owner = new SourceOwnerIdentity(metadata.Name, Path.GetTempPath(), Path.GetTempPath(), false,
                    metadata.BuildIdentity);
                var trees = metadata.Declarations.Select((declaration, index) => SyntaxTree.ParseManagedModuleReference(
                        SourceText.From(declaration.Source, Path.Combine(Path.GetTempPath(), $"overlay-reference-{index}.ct")), owner))
                    .Append(SyntaxTree.ParseText("""
                        using System.Threading;
                        using Demo.OverlayDependency;
                        public static class Program
                        {
                            private static void Worker() { Work.Run(); }
                            [EntryPoint]
                            public static int Main(string[] args)
                            {
                                Thread thread = new Thread(Worker);
                                thread.Start();
                                return 0;
                            }
                        }
                        """, Path.Combine(Path.GetTempPath(), "overlay-consumer.ct"), SourceOwnerIdentity.ImplicitRoot));
                var consumer = Compilation.Create(trees, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: consumerConfiguration.Kind, ManagedModule: consumerConfiguration));
                Assert(consumer.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6233"),
                    "A consumer of an overlay-enabled dependency accepted Thread.Start.");
            }
            finally
            {
                File.Delete(metadataPath);
            }
        });

        suite.Run("draft 0.49 managed application exception boundary", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        throw new System.InvalidOperationException();
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ExceptionBoundary", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("C~ unhandled module exception\\n", StringComparison.Ordinal) &&
                generated.Contains("return -2;", StringComparison.Ordinal) &&
                generated.Contains("setjmp(ct_main_target)", StringComparison.Ordinal),
                "Managed application Main lacks its process-level exception boundary.");
        });

        suite.Run("draft 0.49 managed child tasks inherit process context", () =>
        {
            const string source = """
                using System.Threading;
                public static class Program
                {
                    private static void Worker() { Thread.Yield(); }
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Thread thread = new Thread(Worker);
                        thread.Start();
                        thread.Join();
                        return 0;
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ChildTask", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("ct_runtime_thread_attach_v23", StringComparison.Ordinal) &&
                generated.Contains("payload->Process = ct_runtime_api->CurrentProcess()", StringComparison.Ordinal) &&
                generated.Contains("ct_thread_attach_to(payload->Process)", StringComparison.Ordinal),
                "Generated managed workers do not inherit the creating process context.");
        });
    }
}
