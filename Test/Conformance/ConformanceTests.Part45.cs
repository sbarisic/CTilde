using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart45(ConformanceSuite suite)
    {
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
                generated.Contains("ct_managed_call_target_v3", StringComparison.Ordinal) &&
                generated.Contains("EnterManagedCall", StringComparison.Ordinal) &&
                generated.Contains("ct_leave_managed_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_cleanup_push(&ct_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_module_text_anchor", StringComparison.Ordinal),
                "Overlay bodies were not separated behind cleanup-safe resident stubs.");

            using var metadataWriter = new StringWriter();
            Assert(compilation.EmitManagedModuleMetadata(metadataWriter, module).Success,
                "Overlay metadata emission failed.");
            Assert(metadataWriter.ToString().Contains("\"hasOverlays\": true", StringComparison.Ordinal) &&
                metadataWriter.ToString().Contains("\"name\": \"render\"", StringComparison.Ordinal) &&
                metadataWriter.ToString().Contains("\"targetIndex\": 0", StringComparison.Ordinal) &&
                !metadataWriter.ToString().Contains("\"name\": \"unused\"", StringComparison.Ordinal),
                "Schema-3 metadata omitted deterministic reachable placement or retained an unreachable overlay.");
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
    }
}
