using System.Globalization;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart12(ConformanceSuite suite)
    {
        suite.Run("draft 0.14 catchable runtime faults and allocation-free OOM", () =>
        {
            const string source = """
                using System;
                using System.Runtime;

                public class First { public int Value; }
                public class Second { public int Value; }

                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_total_allocations")]
                    [NoAlloc]
                    public static uint TotalAllocations();
                }

                public static class Program
                {
                    private static int cleanupCount;

                    [NoAlloc]
                    private static int Divide(int value)
                    {
                        return 10 / value;
                    }

                    [NoAlloc]
                    private static void Cleanup()
                    {
                        cleanupCount++;
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        uint before = Diagnostics.TotalAllocations();
                        try { Divide(0); }
                        catch (DivideByZeroException error) { Console.WriteLine(error != null); }
                        Console.WriteLine(Diagnostics.TotalAllocations() == before);

                        try { string value = null; Console.WriteLine(value.Length); }
                        catch (NullReferenceException error) { Console.WriteLine(error != null); }

                        try { int[] values = new int[1]; Console.WriteLine(values[2]); }
                        catch (IndexOutOfRangeException error) { Console.WriteLine(error != null); }

                        try { object value = new First(); Second converted = (Second)value; Console.WriteLine(converted.Value); }
                        catch (InvalidCastException error) { Console.WriteLine(error != null); }

                        try { int length = -1; int[] values = new int[length]; Console.WriteLine(values.Length); }
                        catch (OverflowException error) { Console.WriteLine(error != null); }

                        try
                        {
                            string text = "a" + ((char)0).ToString() + "b";
                            NativeUtf8String native = NativeUtf8String.Borrow(text);
                            Console.WriteLine(native.ByteLength);
                        }
                        catch (ArgumentException error) { Console.WriteLine(error != null); }

                        try { Exception error = null; throw error; }
                        catch (NullReferenceException error) { Console.WriteLine(error != null); }

                        bool rethrown = false;
                        try
                        {
                            try { Divide(0); }
                            catch (DivideByZeroException error) { throw; }
                            finally { Cleanup(); }
                        }
                        catch (DivideByZeroException error) { rethrown = error != null; }
                        Console.WriteLine(rethrown && cleanupCount == 1);

                        Memory.TestFailAllocationAfter(0);
                        try { First value = new First(); Console.WriteLine(value.Value); }
                        catch (OutOfMemoryException error) { Console.WriteLine(error != null); }
                        finally { Memory.TestFailAllocationAfter(-1); }
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true, conformance: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == string.Concat(Enumerable.Repeat("True\n", 10)), result.StandardOutput);
        });

        suite.Run("draft 0.14 modular artifacts symbols and determinism", () =>
        {
            var firstTree = SyntaxTree.ParseText("namespace Alpha; public static class A { public static int Value() { return 7; } }", "alpha.ct");
            var secondTree = SyntaxTree.ParseText("using System; using Alpha; namespace Beta; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(A.Value()); } }", "beta.ct");
            var first = Compilation.Create([firstTree, secondTree]);
            var second = Compilation.Create([secondTree, firstTree]);
            var firstBundle = first.EmitCBundle();
            var secondBundle = second.EmitCBundle();
            Assert(firstBundle.Success && secondBundle.Success, string.Join(Environment.NewLine, firstBundle.Diagnostics));
            Assert(firstBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))
                .SequenceEqual(secondBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))), "Shuffled source order changed modular artifacts.");

            var kinds = firstBundle.Artifacts.Select(artifact => artifact.Kind).ToHashSet();
            Assert(kinds.Contains(GeneratedCArtifactKind.RuntimeSource) && kinds.Contains(GeneratedCArtifactKind.InternalHeader) &&
                kinds.Contains(GeneratedCArtifactKind.EntrySource) && kinds.Contains(GeneratedCArtifactKind.NamespaceSource) &&
                kinds.Contains(GeneratedCArtifactKind.SymbolMap) && kinds.Contains(GeneratedCArtifactKind.CMakeFragment), "The modular bundle omitted a required artifact category.");
            Assert(firstBundle.Artifacts.Count(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource) == 1, "The modular bundle emitted more than one runtime implementation.");

            using var mapWriter = new StringWriter(CultureInfo.InvariantCulture);
            var mapResult = first.EmitSymbolMap(mapWriter);
            Assert(mapResult.Success, string.Join(Environment.NewLine, mapResult.Diagnostics));
            var map = mapWriter.ToString();
            Assert(map.Contains("\"runtimeAbi\": 16", StringComparison.Ordinal) && map.Contains("method:Alpha.A::Value", StringComparison.Ordinal), "The symbol map omitted ABI or canonical identity data.");
            var compactNames = System.Text.RegularExpressions.Regex.Matches(map, "ct_[a-z]_([0-9a-f]{24})")
                .Select(match => match.Value)
                .ToArray();
            Assert(compactNames.Length != 0 && compactNames.All(name => name.Length <= 32), "A compact generated symbol exceeded its length budget.");

            var unreachable = Compilation.Create([SyntaxTree.ParseText("namespace Hidden; public static class Dead { public static void Never() { } }", "dead.ct"), secondTree, firstTree]);
            var unreachableBundle = unreachable.EmitCBundle();
            Assert(unreachableBundle.Success && !unreachableBundle.Artifacts.Any(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource && artifact.Content.Contains("Hidden", StringComparison.Ordinal)), "An unreachable namespace produced a modular source file.");
        });

        suite.Run("draft 0.14 native lifecycle finalization and panic callback", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static string retained = "value-" + 14.ToString();
                    [EntryPoint] public static void Main() { Console.WriteLine(retained); }
                }
                """;
            var generated = Emit(source)
                .Replace("int main(void)", "int ct_generated_main(void)", StringComparison.Ordinal);
            const string host = """

                static void ct_test_panic(const ct_panic_info* info, void* context)
                {
                    int* called = (int*)context;
                    *called = 1;
                    (void)fprintf(stderr, "panic-handler:%s\n", info->Code);
                }

                int main(void)
                {
                    uint32_t baseline = ct_memory_diagnostic_live_allocations();
                    int called = 0;
                    ct_runtime_config config = { sizeof(ct_runtime_config), ct_test_panic, &called };
                    ct_runtime_initialize(&config);
                    if (ct_memory_diagnostic_live_allocations() <= baseline) return 10;
                    ct_runtime_shutdown();
                    if (ct_memory_diagnostic_live_allocations() != baseline) return 11;
                    ct_runtime_shutdown();
                    return called == 0 ? 12 : 13;
                }
                """;
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-lifecycle-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            try
            {
                var cPath = Path.Combine(directory, "lifecycle.c");
                var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "lifecycle.exe" : "lifecycle");
                File.WriteAllText(cPath, generated + host, new System.Text.UTF8Encoding(false));
                var native = RunCompiler(cPath, executable, memoryDiagnostics: true);
                Assert(native.ExitCode == 0, native.StandardOutput + native.StandardError);
                var result = RunCompiledProgram(executable);
                Assert(result.ExitCode != 0 && result.StandardError.Contains("panic-handler:CTT0002", StringComparison.Ordinal) &&
                    result.StandardError.Contains("C~ runtime error CTT0002", StringComparison.Ordinal), "The configured panic callback was not invoked before fatal termination.");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }
}
