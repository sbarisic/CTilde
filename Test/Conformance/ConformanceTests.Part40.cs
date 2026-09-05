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
            Assert(combined.Contains("ct_managed_module_v4", StringComparison.Ordinal) &&
                combined.Contains(".ctilde.manifest", StringComparison.Ordinal) &&
                combined.Contains("ct_runtime_api_v23", StringComparison.Ordinal), "Managed ELF ABI records were not emitted.");
            Assert(combined.Contains("uint64_t FingerprintHigh; uint64_t FingerprintLow;", StringComparison.Ordinal),
                "Runtime ABI 22 type descriptors omitted their canonical fingerprint fields.");
            Assert(combined.Contains("ct_managed_module_static_state", StringComparison.Ordinal) &&
                combined.Contains("CurrentModuleState", StringComparison.Ordinal), "Mutable statics were not lowered through per-process module state.");
            Assert(combined.Contains("CT_GENERATED_LOCAL", StringComparison.Ordinal),
                "Managed module definitions were not hidden from the ELF dynamic symbol table.");
            Assert(combined.Contains("ct_runtime_api->Service(UINT32_C(16)", StringComparison.Ordinal) &&
                !combined.Contains("fwrite(value->Data, 1u, (size_t)value->Length, stdout)", StringComparison.Ordinal),
                "Managed Console output did not route through the shared Runtime ABI 22 service table.");

            var currentProcessSource = """
                using System.Diagnostics;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Process current = Process.Current;
                        if (current == null || current.Id == 0u)
                            return -1;
                        if (args.Length != 0)
                            return current.Receive().Length;
                        byte[] payload;
                        if (current.TryReceive(0u, out payload))
                            return payload.Length;
                        return 0;
                    }
                }
                """;
            var currentProcessCompilation = Compile(currentProcessSource, options);
            Assert(!currentProcessCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, currentProcessCompilation.GetDiagnostics()));
            var currentProcessBundle = currentProcessCompilation.EmitCBundle();
            var currentProcessC = string.Join('\n', currentProcessBundle.Artifacts
                .Where(artifact => artifact.RelativePath.EndsWith(".c", StringComparison.Ordinal))
                .Select(artifact => artifact.Content));
            Assert(currentProcessBundle.Success &&
                currentProcessC.Contains("ct_managed_process_current", StringComparison.Ordinal) &&
                currentProcessC.Contains("ct_managed_process_receive", StringComparison.Ordinal) &&
                currentProcessC.Contains("System.InvalidOperationException", StringComparison.Ordinal),
                "Managed mailbox receive omitted its current-process ownership guard.");

            using var first = new StringWriter();
            using var second = new StringWriter();
            Assert(compilation.EmitManagedModuleMetadata(first, configuration).Success && compilation.EmitManagedModuleMetadata(second, configuration).Success,
                "Managed public metadata emission failed.");
            Assert(first.ToString() == second.ToString(), "Managed public metadata was not deterministic.");
            var metadata = JsonSerializer.Deserialize<JsonElement>(first.ToString());
            Assert(metadata.GetProperty("schemaVersion").GetInt32() == 3 &&
                metadata.GetProperty("runtimeAbi").GetInt32() == CompilerContract.RuntimeAbiVersion &&
                metadata.GetProperty("moduleAbi").GetInt32() == CompilerContract.ManagedModuleAbiVersion &&
                metadata.GetProperty("name").GetString() == "Demo.App" && metadata.GetProperty("apiHash").GetString()!.Length == 64,
                "Managed public metadata omitted its exact ABI identity.");
            var capabilities = metadata.GetProperty("requiredCapabilities").EnumerateArray().ToArray();
            Assert(capabilities.Length == 2 && capabilities[0].GetProperty("id").GetUInt32() == 1 &&
                capabilities[1].GetProperty("id").GetUInt32() == 2 &&
                capabilities.All(item => item.GetProperty("majorVersion").GetUInt32() == 1),
                "Managed metadata omitted the required core and buffer capability versions.");
            Assert(combined.Contains("runtime->GetCapability", StringComparison.Ordinal) &&
                combined.IndexOf("if (core == NULL || buffer == NULL)", StringComparison.Ordinal) <
                combined.IndexOf("ct_core_api = core", StringComparison.Ordinal),
                "Managed runtime binding must validate capabilities before publishing their pointers.");

            var ordinary = Compile("public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!ordinary.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Ordinary firmware entry behavior changed.");
            var unavailableProcess = Compile("using System.Diagnostics; public static class Program { [EntryPoint] public static void Main() { bool cancelled = Process.IsCancellationRequested; } }");
            Assert(unavailableProcess.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6206"),
                "The ESP-IDF-only managed Process surface was accepted by a hosted compilation.");
            var availableStopwatch = Compile("using System.Diagnostics; public static class Program { [EntryPoint] public static void Main() { Stopwatch timer = Stopwatch.StartNew(); timer.Stop(); } }");
            Assert(!availableStopwatch.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6206"),
                "Loading the shared diagnostics source made an unrelated hosted Stopwatch program depend on the ESP-IDF process host.");
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
                var dependency = new ManagedModuleMetadata(3, CompilerContract.DraftVersion, CompilerContract.RuntimeAbiVersion,
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

        suite.Run("draft 0.45 managed-module ABI identity capacities", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-managed-capacity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var maximumName = "M" + new string('a', 62);
                var maximumReferenceName = "R" + new string('a', 62);
                var maximumDependencyName = "D" + new string('a', 62);
                var overflowName = "M" + new string('a', 63);
                var maximumVersion = "1.0.0-" + new string('a', 25);
                var overflowVersion = "1.0.0-" + new string('a', 26);
                var emptyApiHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
                var buildIdentity = new string('a', 64);
                var programPath = Path.Combine(directory, "Program.ct");
                File.WriteAllText(programPath, "public static class Program { [EntryPoint] public static int Main(string[] args) { return args.Length; } }");

                string WriteProject(string fileName, string name, string version, params string[] references)
                {
                    var path = Path.Combine(directory, fileName);
                    File.WriteAllText(path, JsonSerializer.Serialize(new
                    {
                        target = "esp-idf",
                        architecture = "xtensa",
                        sources = new[] { "**/*.ct" },
                        espIdf = new { artifact = "managed-module" },
                        managedModule = new
                        {
                            kind = "application",
                            name,
                            version,
                            references,
                        },
                        build = new { cLayout = "modules" },
                    }));
                    return path;
                }

                var maximumDependency = new ManagedModuleDependencyMetadata(maximumDependencyName, maximumVersion,
                    buildIdentity, emptyApiHash);
                var maximumReference = new ManagedModuleMetadata(3, CompilerContract.DraftVersion,
                    CompilerContract.RuntimeAbiVersion, CompilerContract.ManagedModuleAbiVersion, "library",
                    maximumReferenceName, maximumVersion, buildIdentity, emptyApiHash, [maximumDependency], [], []);
                var maximumReferencePath = Path.Combine(directory, "maximum.ctmeta.json");
                File.WriteAllText(maximumReferencePath, maximumReference.ToDeterministicJson());

                var maximumProject = CTildeProjectFile.Load(WriteProject("maximum.json", maximumName, maximumVersion,
                    Path.GetFileName(maximumReferencePath)));
                Assert(maximumProject.Configuration.ManagedModule is
                {
                    Name.Length: 63,
                    Version.Length: 31,
                    References: [{ Name.Length: 63, Version.Length: 31 }],
                }, "Managed Module ABI 3 maximum-width identities were not preserved.");

                var maximumConfiguration = maximumProject.Configuration.ManagedModule!;
                var maximumCompilation = Compile(File.ReadAllText(programPath), new CompilationOptions(
                    CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: maximumConfiguration.Kind,
                    ManagedModule: maximumConfiguration));
                var maximumDiagnostics = maximumCompilation.GetDiagnostics();
                Assert(!maximumDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                    string.Join(Environment.NewLine, maximumDiagnostics));
                var maximumBundle = maximumCompilation.EmitCBundle();
                Assert(maximumBundle.Success && maximumBundle.Artifacts.Any(artifact =>
                        artifact.Content.Contains("char Name[64]; char Version[32]", StringComparison.Ordinal) &&
                        artifact.Content.Contains(maximumName, StringComparison.Ordinal) &&
                        artifact.Content.Contains(maximumVersion, StringComparison.Ordinal)),
                    "Maximum-width managed identities did not fit the generated ABI manifest.");

                void AssertProjectIdentityRejected(string fileName, string name, string version, string field)
                {
                    try
                    {
                        _ = CTildeProjectFile.Load(WriteProject(fileName, name, version));
                        throw new InvalidOperationException($"An over-capacity managed-module {field} reached C generation.");
                    }
                    catch (CTildeProjectException exception)
                    {
                        Assert(exception.Code == "CT6202" && exception.Message.Contains($"ABI {CompilerContract.ManagedModuleAbiVersion} limit", StringComparison.Ordinal),
                            $"An over-capacity project {field} did not report the CT6202 ABI limit diagnostic: {exception}");
                    }
                }

                AssertProjectIdentityRejected("overflow-name.json", overflowName, "1.0.0", "name");
                AssertProjectIdentityRejected("overflow-version.json", "Demo.Overflow", overflowVersion, "version");

                void AssertReferenceRejected(string fileName, ManagedModuleMetadata metadata, string field)
                {
                    var metadataPath = Path.Combine(directory, fileName + ".ctmeta.json");
                    File.WriteAllText(metadataPath, metadata.ToDeterministicJson());
                    try
                    {
                        _ = CTildeProjectFile.Load(WriteProject(fileName + ".json", "Demo.Consumer", "1.0.0",
                            Path.GetFileName(metadataPath)));
                        throw new InvalidOperationException($"An over-capacity referenced metadata {field} reached C generation.");
                    }
                    catch (CTildeProjectException exception)
                    {
                        Assert(exception.Code == "CT6201",
                            $"An over-capacity referenced metadata {field} did not report CT6201: {exception}");
                    }
                }

                AssertReferenceRejected("identity-name", maximumReference with { Name = overflowName }, "module name");
                AssertReferenceRejected("identity-version", maximumReference with { Version = overflowVersion }, "module version");
                AssertReferenceRejected("dependency-name", maximumReference with
                {
                    Dependencies = [maximumDependency with { Name = overflowName }],
                }, "dependency name");
                AssertReferenceRejected("dependency-version", maximumReference with
                {
                    Dependencies = [maximumDependency with { Version = overflowVersion }],
                }, "dependency version");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }
}
