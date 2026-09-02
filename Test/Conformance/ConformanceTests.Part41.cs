using System.Text.RegularExpressions;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart41(ConformanceSuite suite)
    {
        suite.Run("draft 0.45 modular extern signature type closure", () =>
        {
            const string source = """
                using System;
                using System.Diagnostics;

                public static class Program
                {
                    private static Process[] processes = new Process[1];

                    [EntryPoint]
                    public static void Main()
                    {
                        string[] words = new string[2];
                        words[0] = "managed";
                        words[1] = "shell";
                        Console.WriteLine(String.Join(" ", words));
                        processes[0] = Process.Start("/storage/modules/example.ctm", words);
                        Console.WriteLine(processes.Length);
                        uint value;
                        Console.WriteLine(uint.TryParse("18", out value));
                    }
                }
                """;
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf,
                Architecture: CompilationArchitecture.Xtensa));
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, diagnostics));

            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var types = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_types.h").Content;
            var runtimeHeader = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_runtime_internal.h").Content;
            var runtimeSource = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_runtime.c").Content;
            Assert(runtimeHeader.Contains("ct_managed_process_get_state", StringComparison.Ordinal) &&
                runtimeHeader.Contains("ct_parse_u32_style", StringComparison.Ordinal),
                "The regression did not retain the sibling extern prototypes that require additional enum types. " +
                $"process={runtimeHeader.Contains("ct_managed_process_get_state", StringComparison.Ordinal)}, " +
                $"parse={runtimeHeader.Contains("ct_parse_u32_style", StringComparison.Ordinal)}");

            var referencedTypes = Regex.Matches(runtimeHeader + runtimeSource, @"\bct_t_[0-9a-f]{24}\b")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var missingTypes = referencedTypes.Where(type => !types.Contains(type, StringComparison.Ordinal)).ToArray();
            Assert(missingTypes.Length == 0,
                "Modular runtime declarations referenced types absent from ctilde_types.h: " + string.Join(", ", missingTypes));
        });
    }
}
