using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart29(ConformanceSuite suite)
    {
        suite.Run("C~ formatter syntax trivia and idempotence", () =>
        {
            const string source = "namespace N; [A] [B] public static class P { private int a; private int b; [A] public static void M(int a,int b) { if(a<b) a=a+b; else { a--; } } }";
            const string expected = """
                namespace N;

                [A]
                [B]
                public static class P
                {
                    private int a;
                    private int b;

                    [A]
                    public static void M(int a, int b)
                    {
                        if (a < b)
                            a = a + b;
                        else
                        {
                            a--;
                        }
                    }
                }
                """;
            var formatted = CTildeFormatter.Format(SourceText.From(source));
            Assert(formatted == expected.Replace("\r", string.Empty, StringComparison.Ordinal) + "\n", formatted);
            Assert(CTildeFormatter.Format(SourceText.From(formatted)) == formatted, "C~ formatting was not idempotent.");
            var before = SyntaxTree.ParseText(source).Tokens.Where(token => token.Kind != SyntaxKind.EndOfFileToken).Select(token => (token.Kind, token.Text));
            var after = SyntaxTree.ParseText(formatted).Tokens.Where(token => token.Kind != SyntaxKind.EndOfFileToken).Select(token => (token.Kind, token.Text));
            Assert(before.SequenceEqual(after), "C~ formatting changed a non-trivia token.");

            const string emissionSource = "public static class Entry { [EntryPoint] public static void Main() { int value=1; if(value<2) value=value+1; } }";
            var emissionFormatted = CTildeFormatter.Format(SourceText.From(emissionSource));
            Assert(Emit(emissionSource) == Emit(emissionFormatted), "Formatting changed non-debug unity C emission.");
            var originalBundle = Compile(emissionSource).EmitCBundle();
            var formattedBundle = Compile(emissionFormatted).EmitCBundle();
            Assert(originalBundle.Success && formattedBundle.Success, "Formatting emission bundle generation failed.");
            var originalModules = originalBundle.Artifacts
                .Where(artifact => artifact.Kind is not GeneratedCArtifactKind.SymbolMap and not GeneratedCArtifactKind.DebugMap)
                .Select(artifact => (artifact.RelativePath, artifact.Content));
            var formattedModules = formattedBundle.Artifacts
                .Where(artifact => artifact.Kind is not GeneratedCArtifactKind.SymbolMap and not GeneratedCArtifactKind.DebugMap)
                .Select(artifact => (artifact.RelativePath, artifact.Content));
            Assert(originalModules.SequenceEqual(formattedModules), "Formatting changed non-debug modular C emission.");

            const string advancedSource = "using System; using System.Collections; public class Advanced<T> { public T[4] Items;\n/// <summary>Gets an item.</summary>\npublic int this[int index] { get { return index; } set { } } public static IEnumerable<int> Values() { yield return 1; yield break; } public static unsafe void Flow([Nullable] int* pointer) { try { switch (*pointer) { case 0: break; default: throw new Exception(); } } catch (Exception error) { } finally { } } }";
            var advancedFormatted = CTildeFormatter.Format(SourceText.From(advancedSource));
            Assert(advancedFormatted.Contains("public T[4] Items;", StringComparison.Ordinal) &&
                advancedFormatted.Contains("public int this[int index]\n    {", StringComparison.Ordinal) &&
                advancedFormatted.Contains("case 0:\n", StringComparison.Ordinal) &&
                advancedFormatted.Contains("case 0:\n                    break;", StringComparison.Ordinal) &&
                advancedFormatted.Contains("yield return 1;\n", StringComparison.Ordinal) &&
                advancedFormatted.Contains("catch (Exception error)\n", StringComparison.Ordinal) &&
                advancedFormatted.Contains("Flow(\n        [Nullable]", StringComparison.Ordinal) &&
                advancedFormatted.Contains("[Nullable]\n        int* pointer", StringComparison.Ordinal) &&
                advancedFormatted.Contains("/// <summary>Gets an item.</summary>\n    public int this", StringComparison.Ordinal), advancedFormatted);
            Assert(CTildeFormatter.Format(SourceText.From(advancedFormatted)) == advancedFormatted,
                "Advanced C~ formatting was not idempotent.");

            const string assembly = "public static class A { public static unsafe void M() { // keep\n asm () {\nmovl %eax, %eax  // raw\n } } }";
            var assemblyFormatted = CTildeFormatter.Format(SourceText.From(assembly));
            Assert(assemblyFormatted.Contains("\nmovl %eax, %eax  // raw\n ", StringComparison.Ordinal), "Raw assembly text changed during formatting.");
            Assert(assemblyFormatted.Contains("// keep", StringComparison.Ordinal), "A source comment was lost during formatting.");
            var malformed = SyntaxTree.ParseText("public class Broken {");
            var rejected = false;
            try { CTildeFormatter.Format(malformed); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Malformed C~ source was formatted.");
        });

        suite.Run("C~ formatter CLI and repository contract", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-format", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sourcePath = Path.Combine(root, "Program.ct");
                var invalidPath = Path.Combine(root, "Invalid.ct");
                var templatePath = Path.Combine(root, "Template.ct");
                var excludedDirectory = Path.Combine(root, "bin");
                Directory.CreateDirectory(excludedDirectory);
                File.WriteAllText(sourcePath, "public static class P { public static void M() { string marker=\"// ctilde-format: preserve\"; int x=1; } }", new UTF8Encoding(true));
                File.WriteAllText(invalidPath, "public class Broken {");
                File.WriteAllText(templatePath, "namespace $safeprojectname$; public class CTildeTemplatePlaceholder0 { }");
                var excludedPath = Path.Combine(excludedDirectory, "Ignored.ct");
                File.WriteAllText(excludedPath, "public class I { }");
                var original = File.ReadAllBytes(sourcePath);
                var cli = CliPath();

                var atomicFailure = RunProcess("dotnet", [cli, "format", root], workingDirectory: root);
                Assert(atomicFailure.ExitCode == 1 && File.ReadAllBytes(sourcePath).SequenceEqual(original), "A syntax error allowed a partial formatting write.");
                File.Delete(invalidPath);
                var checkFailure = RunProcess("dotnet", [cli, "format", "--check", sourcePath, sourcePath], workingDirectory: root);
                Assert(checkFailure.ExitCode == 1 && checkFailure.StandardError.Contains("formatting required", StringComparison.Ordinal), "Format check did not report a dirty file.");
                var write = RunProcess("dotnet", [cli, "format", root], workingDirectory: root);
                Assert(write.ExitCode == 0, write.StandardError);
                var bytes = File.ReadAllBytes(sourcePath);
                Assert(!(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) && !bytes.Contains((byte)'\r'), "Formatting did not normalize UTF-8 and LF endings.");
                Assert(File.ReadAllText(sourcePath).Contains("public static class P\n{", StringComparison.Ordinal),
                    "A preserve-marker string literal incorrectly disabled syntax formatting.");
                var template = File.ReadAllText(templatePath);
                Assert(template.Contains("namespace $safeprojectname$;", StringComparison.Ordinal) &&
                    template.Contains("class CTildeTemplatePlaceholder0", StringComparison.Ordinal), "Formatting changed a template placeholder or colliding source identifier.");
                Assert(File.ReadAllText(excludedPath) == "public class I { }", "Formatting entered an excluded output directory.");
                var timestamp = File.GetLastWriteTimeUtc(sourcePath);
                var secondWrite = RunProcess("dotnet", [cli, "format", sourcePath], workingDirectory: root);
                Assert(secondWrite.ExitCode == 0 && File.GetLastWriteTimeUtc(sourcePath) == timestamp, "Formatting rewrote an unchanged file.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var physicalSources = new[] { "CTilde", "CTilde.Cli", "editors", "examples" }
                .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.ct", SearchOption.AllDirectories))
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj" or ".artifacts" or "artifacts" or "node_modules" or ".vscode-test"))
                .ToArray();
            Assert(physicalSources.Length == 64, $"Expected 64 physical C~ sources, found {physicalSources.Length}.");
            var repositoryCheck = RunProcess("dotnet", [CliPath(), "format", "--check", "CTilde", "CTilde.Cli", "editors", "examples"], workingDirectory: repositoryRoot);
            Assert(repositoryCheck.ExitCode == 0, repositoryCheck.StandardError);
        });

        static string CliPath()
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
        }
    }
}
