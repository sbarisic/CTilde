using System.Text.Json;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart25(ConformanceSuite suite)
    {
        suite.Run("instrumented constructors unwind debug cleanup frames", () =>
        {
            const string source = """
                public struct Counter
                {
                    public int Value;
                    public Counter(int value) { Value = value; }
                }
                public sealed class Box
                {
                    public int Value;
                    public Box(int value) { Value = value; }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Counter counter = new Counter(1);
                        Box box = new Box(2);
                    }
                }
                """;
            var generated = Emit(source, new CompilationOptions(DebugInformation: DebugInformationMode.Instrumented));
            Assert(System.Text.RegularExpressions.Regex.IsMatch(generated, @"ct_cleanup_unwind_to\(ct_cleanup_method\);\s*return ct_value;", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "An instrumented struct constructor returned with its debug cleanup frame still attached.");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(generated, @"ct_cleanup_unwind_to\(ct_cleanup_method\);\s*return ct_self;", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "An instrumented class constructor returned with its debug cleanup frame still attached.");
        });

        suite.Run("project clean removes only owned outputs and is idempotent", () =>
        {
            var root = CreateCleanProject("clean project with spaces", new
            {
                target = "hosted",
                sources = new[] { "src/**/*.ct" },
                hosted = new { nativeSources = new[] { "native/shim.c" } },
                build = new
                {
                    generatedC = "out/generated/program.c",
                    generatedHeader = "out/generated/exports.h",
                    symbolMap = "out/generated/symbols.json",
                    executable = "out/program.exe",
                },
            });
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "native"));
                File.WriteAllText(Path.Combine(root, "native", "shim.c"), "int shim(void) { return 0; }");
                var generated = Path.Combine(root, "out", "generated");
                Directory.CreateDirectory(generated);
                Directory.CreateDirectory(Path.Combine(root, "out", ".ctilde-cache"));
                Directory.CreateDirectory(Path.Combine(root, "out", ".ctilde"));
                foreach (var path in new[]
                {
                    Path.Combine(generated, "program.c"),
                    Path.Combine(generated, "exports.h"),
                    Path.Combine(generated, "symbols.json"),
                    Path.Combine(generated, "ctilde_debug.json"),
                    Path.Combine(root, "out", "program.exe"),
                    Path.Combine(root, "out", "program.exe.dbg"),
                    Path.Combine(root, "out", ".ctilde-cache", "cached.o"),
                    Path.Combine(root, "out", ".ctilde", "ctilde-debug-target.json"),
                })
                    File.WriteAllText(path, "owned");

                var first = RunClean(root, "--trace");
                Assert(first.ExitCode == 0, first.StandardError);
                Assert(first.StandardError.Contains("trace: clean removed file", StringComparison.Ordinal), "Clean trace did not report exact-file removal.");
                Assert(!File.Exists(Path.Combine(root, "out", "program.exe")), "Clean retained the executable output.");
                Assert(!Directory.Exists(Path.Combine(root, "out", ".ctilde-cache")), "Clean retained the compiler cache.");
                Assert(File.Exists(Path.Combine(root, "src", "Program.ct")), "Clean removed a source file.");
                Assert(File.Exists(Path.Combine(root, "native", "shim.c")), "Clean removed a hosted native source.");

                var second = RunClean(root, "--trace");
                Assert(second.ExitCode == 0 && second.StandardError.Contains("skipped missing", StringComparison.Ordinal), "A repeated clean was not a traced successful no-op.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        suite.Run("standard-library clean is a no-op", () =>
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var manifest = Path.Combine(repositoryRoot, "CTilde", "StandardLibrary", "ctilde.json");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var cliDll = Path.GetFullPath(Path.Combine(repositoryRoot, "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
            var result = RunProcess("dotnet", [cliDll, "clean", "--project", manifest, "--trace"]);
            Assert(result.ExitCode == 0 && result.StandardError.Contains("standard-library clean has no outputs", StringComparison.Ordinal),
                "Standard-library Clean was not a traced successful no-op.");
        });

        suite.Run("project clean rejects roots source collisions and escaping outputs", () =>
        {
            var rootTarget = CreateCleanProject("root-target", new
            {
                sources = new[] { "src/**/*.ct" },
                build = new { cLayout = "modules", generatedDirectory = ".", generatedHeader = "out/exports.h" },
            });
            var collision = CreateCleanProject("source-collision", new
            {
                sources = new[] { "src/**/*.ct" },
                build = new { cLayout = "modules", generatedDirectory = "src", generatedHeader = "out/exports.h" },
            });
            try
            {
                Directory.CreateDirectory(Path.Combine(rootTarget, "out"));
                File.WriteAllText(Path.Combine(rootTarget, "out", "exports.h"), "owned");
                var rootResult = RunClean(rootTarget, "--trace");
                Assert(rootResult.ExitCode == 1 && rootResult.StandardError.Contains("root paths", StringComparison.Ordinal), "Clean accepted the project root as a recursive target.");
                Assert(Directory.Exists(rootTarget), "Clean deleted the project root.");

                Directory.CreateDirectory(Path.Combine(collision, "out"));
                File.WriteAllText(Path.Combine(collision, "out", "exports.h"), "owned");
                var collisionResult = RunClean(collision, "--trace");
                Assert(collisionResult.ExitCode == 1 && collisionResult.StandardError.Contains("source or native input", StringComparison.Ordinal), "Clean accepted a generated directory containing source input.");
                Assert(File.Exists(Path.Combine(collision, "src", "Program.ct")), "Clean deleted a source-directory collision.");
                Assert(!File.Exists(Path.Combine(collision, "out", "exports.h")), "Clean did not continue exact-file cleanup after rejecting an unsafe directory.");

                var invalidManifest = Path.Combine(collision, "invalid.json");
                File.WriteAllText(invalidManifest, "{\"sources\":[\"src/**/*.ct\"],\"build\":{\"generatedC\":\"../outside.c\"}}");
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
                var escaping = RunProcess("dotnet", [cliDll, "clean", "--project", invalidManifest]);
                Assert(escaping.ExitCode == 1 && escaping.StandardError.Contains("must stay within", StringComparison.Ordinal), "Clean accepted an out-of-tree configured output.");
            }
            finally
            {
                Directory.Delete(rootTarget, recursive: true);
                Directory.Delete(collision, recursive: true);
            }
        });

        suite.Run("project clean rejects reparse-point trees", () =>
        {
            var root = CreateCleanProject("reparse", new
            {
                sources = new[] { "src/**/*.ct" },
                build = new { executable = "out/program.exe" },
            });
            var external = Path.Combine(Path.GetTempPath(), "ctilde-clean-external", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(external);
            try
            {
                File.WriteAllText(Path.Combine(external, "keep.txt"), "keep");
                var cache = Path.Combine(root, "out", ".ctilde-cache");
                Directory.CreateDirectory(cache);
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(cache, "external"), external);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    return;
                }

                var result = RunClean(root, "--trace");
                Assert(result.ExitCode == 1 && result.StandardError.Contains("reparse point", StringComparison.Ordinal), "Clean traversed a reparse-point directory.");
                Assert(File.Exists(Path.Combine(external, "keep.txt")), "Clean followed a reparse point outside the owned directory.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                Directory.Delete(external, recursive: true);
            }
        });
    }

    private static string CreateCleanProject(string name, object manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "ctilde-clean-tests", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Program.ct"), "public static class Program { [EntryPoint] public static void Main() { } }");
        File.WriteAllText(Path.Combine(root, "ctilde.json"), JsonSerializer.Serialize(manifest));
        return root;
    }

    private static ProcessResult RunClean(string root, params string[] additionalArguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
        var arguments = new List<string> { cliDll, "clean", "--project", Path.Combine(root, "ctilde.json") };
        arguments.AddRange(additionalArguments);
        return RunProcess("dotnet", arguments);
    }
}
