using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart24(ConformanceSuite suite)
    {
        suite.Run("draft 0.26 source-owner identity", () =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ctilde-owner-root"));
            var owner = new SourceOwnerIdentity("example.com/root", root, root, true, null);
            var tree = SyntaxTree.ParseText("public static class Program { [EntryPoint] public static void Main() { } }", Path.Combine(root, "Program.ct"), owner);
            Assert(tree.SourceOwner == owner, "A parsed user tree did not retain its source owner.");
            Assert(!Compile([tree]).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A valid root source owner was rejected.");
            Assert(SyntaxTree.ParseText("public class A { }").SourceOwner == SourceOwnerIdentity.ImplicitRoot, "The compatibility parser did not assign the implicit root owner.");
        });

        suite.Run("draft 0.27 binary64 syntax semantics and runtime", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        double wide = 1.25d + 2;
                        double exponent = 1.0e2D;
                        double root = Math.Sqrt(9.0d);
                        object boxed = wide;
                        Console.WriteLine(wide);
                        Console.WriteLine(exponent);
                        Console.WriteLine(root);
                        Console.WriteLine(Math.Pi64 > 3.14d);
                        Console.WriteLine(((double)boxed).ToString());
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "double.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var generated = Emit(source);
            Assert(generated.Contains("static_assert(sizeof(double) == 8", StringComparison.Ordinal), "Binary64 layout validation was not emitted.");
            Assert(generated.Contains("ct_math_sqrt_double", StringComparison.Ordinal), "Binary64 math support was not available.");
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0, run.StandardError);
            Assert(run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal) == "3.25\n100\n3\nTrue\n3.25\n", $"Unexpected binary64 output: {run.StandardOutput}");

            var malformed = SyntaxTree.ParseText("public static class P { public static double X = 1eD; }");
            Assert(malformed.Diagnostics.Any(diagnostic => diagnostic.Code == "CT0002"), "A malformed binary64 exponent was not diagnosed.");
        });

        suite.Run("draft 0.28 fixed-width SIMD scalar semantics", () =>
        {
            const string source = """
                using System;
                using System.Simd;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        F32x4 left = F32x4.Create(1.0f, 2.0f, 3.0f, 4.0f);
                        F32x4 right = F32x4.Splat(2.0f);
                        F32x4 result = (left + right).Shuffle<3, 2, 1, 0>();
                        Mask32x4 mask = F32x4.CompareLessThan(left, right);
                        F32x4 selected = F32x4.Select(mask, right, left);
                        Console.WriteLine(sizeof(F32x4));
                        Console.WriteLine(sizeof(I32x4));
                        Console.WriteLine(sizeof(U32x4));
                        Console.WriteLine(sizeof(Mask32x4));
                        Console.WriteLine(result.GetLane<0>());
                        Console.WriteLine(selected.GetLane<0>());
                        U32x4 integers = U32x4.Create(1u, 2u, 3u, 4u) * U32x4.Splat(3u);
                        Console.WriteLine(integers.GetLane<3>());
                    }
                }
                """;
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0 && run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "16\n16\n16\n16\n6\n2\n12", $"SIMD scalar behavior or layout changed: {run.StandardOutput}{run.StandardError}");
            var invalid = Compile(source.Replace("GetLane<0>()", "GetLane<4>()", StringComparison.Ordinal));
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2220"), "An invalid SIMD lane was not diagnosed.");

            const string featureQuery = "using System.Runtime; static assert(Target.HasFeature(CpuFeature.Simd128), \"simd128 selected\"); public static class P { [EntryPoint] public static void Main() { } }";
            var enabledOptions = new CompilationOptions(Architecture: CompilationArchitecture.X64, CpuFeatures: [CpuFeature.Simd128]);
            var enabled = Compile(featureQuery, enabledOptions);
            Assert(!enabled.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, enabled.GetDiagnostics()));
            Assert(Emit(featureQuery, enabledOptions).Contains("#define CTILDE_CPU_SIMD128 1", StringComparison.Ordinal), "The explicit SIMD CPU contract was not recorded in generated C.");
            var enabledSimd = Emit(source, enabledOptions);
            Assert(enabledSimd.Contains("_mm_add_ps", StringComparison.Ordinal) && enabledSimd.Contains("ct_simd", StringComparison.Ordinal), "The explicit SIMD CPU feature did not select fixed-width hardware lowering.");
            var hardwareRun = CompileAndRun(source, enabledOptions);
            Assert(hardwareRun.ExitCode == 0 && hardwareRun.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "16\n16\n16\n16\n6\n2\n12", $"Hardware SIMD behavior changed: {hardwareRun.StandardOutput}{hardwareRun.StandardError}");
            Assert(!Emit(source).Contains("CTILDE_CPU_SIMD128", StringComparison.Ordinal), "Scalar-default SIMD unexpectedly enabled a CPU feature.");
            var unsupported = Compile(featureQuery, new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa, CpuFeatures: [CpuFeature.Simd128]));
            Assert(unsupported.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4120"), "An unsupported SIMD CPU feature/architecture pair was accepted.");
        });

        suite.Run("draft 0.29 Unicode rune literals and UTF-8 runtime", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        rune face = r'🙂';
                        rune accent = r'é';
                        rune converted = (rune)0x1f642u;
                        object boxed = accent;
                        Console.WriteLine(sizeof(rune));
                        Console.WriteLine(face);
                        Console.WriteLine(accent.ToString());
                        Console.WriteLine("rune=" + converted.ToString());
                        Console.WriteLine((uint)face);
                        Console.WriteLine(boxed.ToString());
                        Console.WriteLine(face == converted);
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "rune.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0, run.StandardError);
            Assert(run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "4\n🙂\né\nrune=🙂\n128578\né\nTrue", $"Rune UTF-8 behavior changed: {run.StandardOutput}{run.StandardError}");

            var malformed = SyntaxTree.ParseText("public static class P { public static rune X = r'ab'; }");
            Assert(malformed.Diagnostics.Any(diagnostic => diagnostic.Code == "CT0009"), "A multi-scalar rune literal was not diagnosed.");
            var invalidScalar = Compile("public static class P { public static rune X = (rune)0xd800u; [EntryPoint] public static void Main() { } }");
            Assert(invalidScalar.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2221"), "A surrogate scalar cast was not diagnosed.");
        });

        suite.Run("draft 0.30 immutable owner-relative embedded resources", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-embed", Guid.NewGuid().ToString("N"));
            var assetDirectory = Path.Combine(root, "assets");
            Directory.CreateDirectory(assetDirectory);
            try
            {
                File.WriteAllBytes(Path.Combine(assetDirectory, "payload.bin"), [0x43, 0x7e, 0x00, 0xff]);
                const string source = """
                    using System;
                    using System.Runtime;
                    public static class Assets
                    {
                        [Embed("assets/payload.bin")]
                        public static unsafe readonly ReadOnlyNativeBuffer<byte> Payload;
                    }
                    public static class Program
                    {
                        [EntryPoint] public static unsafe void Main()
                        {
                            Console.WriteLine(Assets.Payload.Length);
                            Console.WriteLine(Assets.Payload[0]);
                            Console.WriteLine(Assets.Payload[1]);
                            Console.WriteLine(Assets.Payload[3]);
                        }
                    }
                    """;
                var sourcePath = Path.Combine(root, "Program.ct");
                var owner = new SourceOwnerIdentity("example.com/assets", root, root, true, null);
                var tree = SyntaxTree.ParseText(source, sourcePath, owner);
                var compilation = Compile([tree]);
                var diagnostics = compilation.GetDiagnostics();
                Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
                var generated = Emit([tree]);
                Assert(generated.Contains("UINT8_C(0x43), UINT8_C(0x7E), UINT8_C(0x00), UINT8_C(0xFF)", StringComparison.Ordinal), "Embedded bytes were not emitted verbatim.");
                Assert(!generated.Contains(root, StringComparison.OrdinalIgnoreCase), "The generated resource leaked its absolute content root.");
                var run = CompileAndRun([tree]);
                Assert(run.ExitCode == 0 && run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "4\n67\n126\n255", $"Embedded resource behavior changed: {run.StandardOutput}{run.StandardError}");

                var escaping = SyntaxTree.ParseText(source.Replace("assets/payload.bin", "../outside.bin", StringComparison.Ordinal), sourcePath, owner);
                Assert(Compile([escaping]).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2222"), "An embedded resource escaped its owner's content root.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        });

        suite.Run("draft 0.31 captureless lambdas", () =>
        {
            const string source = """
                using System;
                public delegate int Transform(int value);
                public static class Program
                {
                    private static int Apply(Transform transform, int value) { return transform(value); }
                    [EntryPoint] public static void Main()
                    {
                        Transform twice = value => value * 2;
                        Transform addThree = (int value) => { return value + 3; };
                        Console.WriteLine(twice(6));
                        Console.WriteLine(addThree(7));
                        Console.WriteLine(Apply(item => item - 1, 10));
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "lambdas.ct");
            Assert(tree.Diagnostics.IsEmpty && tree.Tokens.Count(token => token.Kind == SyntaxKind.EqualsGreaterToken) == 3, string.Join(Environment.NewLine, tree.Diagnostics));
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0 && run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "12\n10\n9", $"Captureless lambda behavior changed: {run.StandardOutput}{run.StandardError}");

            const string capturing = "public delegate int D(int x); public static class P { [EntryPoint] public static void Main() { int offset = 2; D value = x => x + offset; } }";
            Assert(Compile(capturing).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2223"), "An implicit lambda capture was accepted.");
            const string noAlloc = "public delegate int D(int x); public static class P { [NoAlloc] private static D Make() { return x => x; } [EntryPoint] public static void Main() { } }";
            Assert(Compile(noAlloc).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2155" && diagnostic.Message.Contains("delegate", StringComparison.OrdinalIgnoreCase)), "Captureless delegate allocation did not participate in NoAlloc analysis.");
        });

        suite.Run("draft 0.32 explicit value captures and ARC closures", () =>
        {
            const string source = """
                using System;
                public delegate int Transform(int value);
                public sealed class Box
                {
                    public int Value;
                    public Box(int value) { Value = value; }
                }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_objects")] [NoAlloc]
                    public static uint LiveObjects();
                }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        uint baseline = Diagnostics.LiveObjects();
                        Transform closure;
                        {
                            int offset = 4;
                            Box box = new Box(6);
                            closure = [copy = offset, box] (int value) => value + copy + box.Value;
                            offset = 100;
                            box.Value = 7;
                        }
                        Console.WriteLine(closure(1));
                        closure = null;
                        Console.WriteLine(Diagnostics.LiveObjects() == baseline);
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "closures.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var generated = Emit(source);
            Assert(generated.Contains("creation of ARC closure", StringComparison.Ordinal) == false, "Compiler-internal effect text leaked into generated C.");
            Assert(generated.Contains("ct_release_fast((ct_object*)(void*)", StringComparison.Ordinal), "Closure environment ownership transfer did not use the managed ARC fast path.");
            var run = CompileAndRun(source, memoryDiagnostics: true);
            Assert(run.ExitCode == 0 && run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "12\nTrue", $"ARC closure behavior changed: {run.StandardOutput}{run.StandardError}");

            const string duplicate = "public delegate int D(int x); public static class P { [EntryPoint] public static void Main() { int n = 1; D d = [n, n] x => x + n; } }";
            Assert(Compile(duplicate).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2224"), "Duplicate explicit captures were accepted.");
            const string omitted = "public delegate int D(int x); public static class P { [EntryPoint] public static void Main() { int a = 1; int b = 2; D d = [a] x => x + a + b; } }";
            Assert(Compile(omitted).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2223"), "An outer value omitted from the explicit capture list was accepted.");
        });

        suite.Run("draft 0.33 and 0.34 exact repository module lifecycle", () =>
        {
            static void Git(string directory, params string[] arguments)
            {
                var start = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments)
                    start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output}{error}");
            }

            var root = Path.Combine(Path.GetTempPath(), "ctilde-modules", Guid.NewGuid().ToString("N"));
            var repository = Path.Combine(root, "repository");
            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(project);
            try
            {
                Git(repository, "init", "-b", "main");
                Git(repository, "config", "user.email", "ctilde-tests@example.invalid");
                Git(repository, "config", "user.name", "C~ Tests");
                File.WriteAllText(Path.Combine(repository, "Library.ct"), "public static class LockedLibrary { public static int Value() { return 1; } }");
                Git(repository, "add", "Library.ct");
                Git(repository, "commit", "-m", "first");
                File.WriteAllText(Path.Combine(project, "Program.ct"), "public static class Program { [EntryPoint] public static void Main() { int value = LockedLibrary.Value(); } }");
                var manifestPath = Path.Combine(project, "ctilde.json");
                File.WriteAllText(manifestPath, $$"""
                    {
                      "target": "hosted",
                      "sources": ["Program.ct"],
                      "modules": [{
                        "path": "example.com/locked/library",
                        "repository": {{System.Text.Json.JsonSerializer.Serialize(repository)}},
                        "selector": "main",
                        "alias": "library",
                        "sources": ["**/*.ct"]
                      }]
                    }
                    """);
                var references = CTildeProjectFile.ReadModuleReferences(manifestPath).Modules;
                RepositoryModules.Restore(project, references, update: false);
                var loaded = CTildeProjectFile.Load(manifestPath);
                Assert(loaded.SourceFiles.Length == 2 && loaded.SourceOwners.Count == 1, "The exact module graph was not loaded with source ownership.");
                var moduleOwner = loaded.SourceOwners.Single().Value;
                Assert(moduleOwner.ModulePath == "example.com/locked/library" && moduleOwner.LockedRevision is { Length: 40 }, "The locked module identity was not propagated.");

                RepositoryModules.Vendor(project, references);
                Assert(CTildeProjectFile.Load(manifestPath).SourceFiles.Single(path => path.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).Length > 0,
                    "Verified vendor content was not preferred over the cache.");

                var replacement = Path.Combine(root, "replacement");
                Directory.CreateDirectory(replacement);
                File.WriteAllText(Path.Combine(replacement, "Replacement.ct"), "public static class LockedLibrary { public static int Value() { return 2; } }");
                File.WriteAllText(Path.Combine(project, RepositoryModules.LocalFileName), System.Text.Json.JsonSerializer.Serialize(new
                {
                    replacements = new Dictionary<string, string> { ["example.com/locked/library"] = replacement },
                }));
                var replaced = CTildeProjectFile.Load(manifestPath);
                Assert(replaced.SourceOwners.Single().Value.LockedRevision!.StartsWith("local:", StringComparison.Ordinal), "Local replacement did not take precedence over verified vendor content.");
                File.Delete(Path.Combine(project, RepositoryModules.LocalFileName));

                File.WriteAllText(Path.Combine(repository, "Library.ct"), "public static class LockedLibrary { public static int Value() { return 3; } }");
                Git(repository, "add", "Library.ct");
                Git(repository, "commit", "-m", "second");
                Directory.Delete(Path.Combine(project, "vendor"), recursive: true);
                RepositoryModules.Restore(project, references, update: true);
                Assert(CTildeProjectFile.Load(manifestPath).SourceFiles.Length == 2, "Explicit module update did not produce a usable exact lock and cache.");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }
}
