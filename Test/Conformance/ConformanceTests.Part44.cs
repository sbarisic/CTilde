using System.Collections.Immutable;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart44(ConformanceSuite suite)
    {
        suite.Run("draft 0.49 managed runtime faults initialize before process entry", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        try
                        {
                            int[] values = new int[1];
                            return values[2];
                        }
                        catch (IndexOutOfRangeException)
                        {
                            return 0;
                        }
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.RuntimeFault", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("ct_runtime_api = runtime; ct_runtime_faults_init(); return 0;", StringComparison.Ordinal),
                "Managed modules did not initialize catchable runtime faults while binding the resident runtime.");
        });

        suite.Run("draft 0.49 managed library metadata and checked imports", () =>
        {
            const string providerSource = """
                namespace Demo.Library;
                public struct Counter { public int Value; }
                public static class Calculator
                {
                    public static int Add(int left, int right) { return left + right; }
                }
                """;
            var providerConfiguration = new ManagedModuleConfiguration(
                ManagedModuleKind.Library, "Demo.Library", "1.0.0", [], 4096, 16384);
            var provider = Compile(providerSource, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: ManagedModuleKind.Library, ManagedModule: providerConfiguration));
            Assert(!provider.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, provider.GetDiagnostics()));
            using var metadataWriter = new StringWriter();
            Assert(provider.EmitManagedModuleMetadata(metadataWriter, providerConfiguration).Success,
                "Provider metadata emission failed.");
            var metadataPath = Path.Combine(Path.GetTempPath(), $"ctilde-{Guid.NewGuid():N}.ctmeta.json");
            File.WriteAllText(metadataPath, metadataWriter.ToString());
            try
            {
                var metadata = ManagedModuleMetadata.Load(metadataPath);
                Assert(metadata.SchemaVersion == 3 && metadata.ModuleAbi == CompilerContract.ManagedModuleAbiVersion &&
                    metadata.RuntimeAbi == CompilerContract.RuntimeAbiVersion &&
                    metadata.Declarations.Length == 2 && metadata.Exports.Any(export => export.Member == "Add"),
                    "Managed-library metadata omitted its v3 declarations or callable export.");

                var reference = new ManagedModuleReference(metadataPath, metadata.Name, metadata.Version,
                    metadata.BuildIdentity, metadata.ApiHash, metadata);
                var consumerConfiguration = new ManagedModuleConfiguration(
                    ManagedModuleKind.Application, "Demo.Consumer", "1.0.0", [reference], 4096, 16384);
                var owner = new SourceOwnerIdentity(metadata.Name, Path.GetTempPath(), Path.GetTempPath(), false,
                    metadata.BuildIdentity);
                var trees = metadata.Declarations.Select((declaration, index) => SyntaxTree.ParseManagedModuleReference(
                        SourceText.From(declaration.Source, Path.Combine(Path.GetTempPath(), $"reference-{index}.ct")), owner))
                    .Append(SyntaxTree.ParseText("""
                        using Demo.Library;
                        public static class Program
                        {
                            [EntryPoint]
                            public static int Main(string[] args) { return Calculator.Add(20, 22); }
                        }
                        """, Path.Combine(Path.GetTempPath(), "consumer.ct"), SourceOwnerIdentity.ImplicitRoot));
                var consumer = Compilation.Create(trees, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: ManagedModuleKind.Application, ManagedModule: consumerConfiguration));
                var bundle = consumer.EmitCBundle();
                Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
                var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
                Assert(generated.Contains("ct_managed_import_", StringComparison.Ordinal) &&
                    generated.Contains("ct_managed_import_v4", StringComparison.Ordinal) &&
                    !generated.Contains("EnterCall", StringComparison.Ordinal) && !generated.Contains("LeaveCall", StringComparison.Ordinal),
                    "Consumer did not emit provider-owned Module ABI 3 import dispatch.");
            }
            finally
            {
                File.Delete(metadataPath);
            }
        });

        suite.Run("draft 0.48 redirected process pipe surface", () =>
        {
            const string source = """
                using System.Diagnostics;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        ProcessStartInfo info = new ProcessStartInfo("child.ctm", new string[0]);
                        info.RedirectStandardInput = true;
                        info.RedirectStandardOutput = true;
                        Process child = Process.Start(info);
                        byte[] data = new byte[8];
                        int written;
                        child.StandardInput.TryWrite(data, 0, data.Length, 10u, out written);
                        child.StandardInput.Close();
                        return written;
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.Pipes", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("ct_managed_process_start_redirected", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_process_pipe_write", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_process_pipe_close", StringComparison.Ordinal),
                "Redirected process streams did not lower through Runtime ABI 22.");
        });
    }
}
