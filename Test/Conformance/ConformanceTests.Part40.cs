using System.Collections.Immutable;
using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart40(ConformanceSuite suite)
    {
        suite.Run("draft 0.45 managed-module entry points and ABI artifacts", () =>
        {
            var configuration = new ManagedModuleConfiguration(ManagedModuleKind.Application, "Demo.App", "1.2.3", [], 8192, 65536);
            var source = """
                public static class State
                {
                    public static int Starts;
                    public static readonly int Revision = 3;
                }
                public sealed class Payload
                {
                    public int Value;
                    public Payload(int value) { Value = value; }
                    public int Read() { return Value; }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        State.Starts++;
                        Console.WriteLine("managed console");
                        return args.Length + State.Starts + State.Revision;
                    }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: configuration.Kind, ManagedModule: configuration);
            var compilation = Compile(source, options);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var combined = string.Join('\n', bundle.Artifacts.Where(artifact => artifact.RelativePath.EndsWith(".c", StringComparison.Ordinal)).Select(artifact => artifact.Content));
            Assert(!combined.Contains("void app_main(void)", StringComparison.Ordinal), "A managed module emitted the firmware app_main entry.");
            Assert(combined.Contains("ct_managed_module_v1", StringComparison.Ordinal) &&
                combined.Contains(".ctilde.manifest", StringComparison.Ordinal) &&
                combined.Contains("ct_runtime_api_v18", StringComparison.Ordinal), "Managed ELF ABI records were not emitted.");
            Assert(combined.Contains("uint64_t FingerprintHigh; uint64_t FingerprintLow;", StringComparison.Ordinal),
                "Runtime ABI 18 type descriptors omitted their canonical fingerprint fields.");
            Assert(combined.Contains("ct_managed_module_static_state", StringComparison.Ordinal) &&
                combined.Contains("CurrentModuleState", StringComparison.Ordinal), "Mutable statics were not lowered through per-process module state.");
            Assert(combined.Contains("CT_GENERATED_LOCAL", StringComparison.Ordinal),
                "Managed module definitions were not hidden from the ELF dynamic symbol table.");
            Assert(combined.Contains("ct_runtime_api->Service(UINT32_C(16)", StringComparison.Ordinal) &&
                !combined.Contains("fwrite(value->Data, 1u, (size_t)value->Length, stdout)", StringComparison.Ordinal),
                "Managed Console output did not route through the shared Runtime ABI 18 service table.");

            using var first = new StringWriter();
            using var second = new StringWriter();
            Assert(compilation.EmitManagedModuleMetadata(first, configuration).Success && compilation.EmitManagedModuleMetadata(second, configuration).Success,
                "Managed public metadata emission failed.");
            Assert(first.ToString() == second.ToString(), "Managed public metadata was not deterministic.");
            var metadata = JsonSerializer.Deserialize<JsonElement>(first.ToString());
            Assert(metadata.GetProperty("runtimeAbi").GetInt32() == 18 && metadata.GetProperty("moduleAbi").GetInt32() == 1 &&
                metadata.GetProperty("name").GetString() == "Demo.App" && metadata.GetProperty("apiHash").GetString()!.Length == 64,
                "Managed public metadata omitted its exact ABI identity.");

            var ordinary = Compile("public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!ordinary.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Ordinary firmware entry behavior changed.");
            var unavailableProcess = Compile("using System.Diagnostics; public static class Program { [EntryPoint] public static void Main() { bool cancelled = Process.IsCancellationRequested; } }");
            Assert(unavailableProcess.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6206"),
                "The ESP-IDF-only managed Process surface was accepted by a hosted compilation.");
            var wrongEntry = Compile("public static class Program { [EntryPoint] public static void Main() { } }", options);
            Assert(wrongEntry.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1207"), "A firmware-shaped entry point was accepted for a managed application.");
            var libraryConfiguration = configuration with { Kind = ManagedModuleKind.Library, Name = "Demo.Library" };
            var library = Compile("public class Service { public int Get() { return 1; } }", options with
            {
                ManagedModuleKind = ManagedModuleKind.Library,
                ManagedModule = libraryConfiguration,
            });
            Assert(!library.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A managed library without an entry point was rejected.");
            var generic = Compile("public class Box<T> { public T Value; }", options with
            {
                ManagedModuleKind = ManagedModuleKind.Library,
                ManagedModule = libraryConfiguration,
            });
            Assert(generic.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6205"), "A generic public module API was accepted.");
        });

        suite.Run("draft 0.45 managed-module project manifest contract", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-managed-project-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program { [EntryPoint] public static int Main(string[] args) { return args.Length; } }");
                var emptyApiHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
                var dependency = new ManagedModuleMetadata(1, CompilerContract.DraftVersion, CompilerContract.RuntimeAbiVersion,
                    CompilerContract.ManagedModuleAbiVersion, "library", "Demo.Core", "2.0.0", new string('a', 64), emptyApiHash, [], [], []);
                File.WriteAllText(Path.Combine(directory, "Demo.Core.ctmeta.json"), dependency.ToDeterministicJson());
                File.WriteAllText(Path.Combine(directory, "ctilde.json"), """
                    {
                      "target": "esp-idf",
                      "architecture": "xtensa",
                      "sources": ["**/*.ct"],
                      "espIdf": { "artifact": "managed-module" },
                      "managedModule": {
                        "kind": "application",
                        "name": "Demo.ShellCommand",
                        "version": "1.0.0",
                        "references": ["Demo.Core.ctmeta.json"],
                        "mainTaskStackBytes": 4096,
                        "heapLimitBytes": 32768
                      },
                      "build": { "cLayout": "modules" }
                    }
                    """);
                var project = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                Assert(project.Configuration.EspIdfArtifact == EspIdfArtifact.ManagedModule &&
                    project.Configuration.ManagedModule is { Name: "Demo.ShellCommand", References.Length: 1, MainTaskStackBytes: 4096, HeapLimitBytes: 32768 },
                    "Managed-module manifest settings were not preserved.");

                File.WriteAllText(Path.Combine(directory, "invalid.json"), """
                    { "target": "esp-idf", "sources": ["**/*.ct"], "espIdf": { "artifact": "managed-module" },
                      "managedModule": { "kind": "application", "name": "bad/name", "version": "latest" } }
                    """);
                try
                {
                    _ = CTildeProjectFile.Load(Path.Combine(directory, "invalid.json"));
                    throw new InvalidOperationException("Invalid managed-module identity was accepted.");
                }
                catch (CTildeProjectException exception)
                {
                    Assert(exception.Code == "CT6202", "Invalid managed-module identity reported the wrong diagnostic.");
                }

                var stale = dependency with { DraftVersion = "0.44" };
                File.WriteAllText(Path.Combine(directory, "stale.ctmeta.json"), stale.ToDeterministicJson());
                File.WriteAllText(Path.Combine(directory, "stale.json"), """
                    { "target": "esp-idf", "sources": ["**/*.ct"], "espIdf": { "artifact": "managed-module" },
                      "managedModule": { "kind": "application", "name": "Demo.Stale", "version": "1.0.0", "references": ["stale.ctmeta.json"] },
                      "build": { "cLayout": "modules" } }
                    """);
                try
                {
                    _ = CTildeProjectFile.Load(Path.Combine(directory, "stale.json"));
                    throw new InvalidOperationException("Stale managed-module metadata was accepted.");
                }
                catch (CTildeProjectException exception)
                {
                    Assert(exception.Code == "CT6201", "Stale managed-module metadata reported the wrong diagnostic.");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }
}
