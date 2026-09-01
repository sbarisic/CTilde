using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart34(ConformanceSuite suite)
    {
        suite.Run("draft 0.41 native profile manifest contract", () =>
        {
            var root = CreateNativeProfileProject();
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "off", "build/pgo");
                var build = CTildeProjectFile.Load(manifest).Configuration.Build!;
                Assert(build.Optimization == NativeOptimization.Speed, "The speed optimization profile was not loaded.");
                Assert(build.CpuTarget == NativeCpuTarget.Baseline, "The baseline CPU target was not loaded.");
                Assert(build.FloatingPoint == NativeFloatingPointMode.Precise, "The precise floating-point mode was not loaded.");
                Assert(build.Pgo?.Mode == NativePgoMode.Off && build.Pgo.DirectoryPath == Path.Combine(root, "build", "pgo"),
                    "The PGO mode or project-relative directory was not loaded.");

                File.WriteAllText(manifest, "{ \"target\": \"hosted\", \"sources\": [\"*.ct\"] }");
                var defaults = CTildeProjectFile.Load(manifest).Configuration.Build!;
                Assert(defaults.Optimization is null && defaults.CpuTarget is null && defaults.FloatingPoint is null && defaults.Pgo is null,
                    "Omitted Draft 0.41 native profile properties did not preserve the legacy build defaults.");

                File.WriteAllText(manifest, "{ \"target\": \"hosted\", \"sources\": [\"*.ct\"], \"build\": { \"optimization\": \"maximum\" } }");
                AssertProjectFailure(manifest, "speed or aggressive");
                File.WriteAllText(manifest, "{ \"target\": \"hosted\", \"sources\": [\"*.ct\"], \"build\": { \"pgo\": { \"directory\": \"../escape\" } } }");
                AssertProjectFailure(manifest, "stay within");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        suite.Run("draft 0.41 target-specific native profile validation", () =>
        {
            var root = CreateNativeProfileProject();
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                File.WriteAllText(manifest, """
                    {
                      "target": "esp-idf",
                      "sources": ["*.ct"],
                      "build": {
                        "cLayout": "modules",
                        "generatedDirectory": "build/generated",
                        "generatedHeader": "build/generated/ctilde_exports.h",
                        "optimization": "aggressive",
                        "floatingPoint": "fast"
                      }
                    }
                    """);
                var esp = RunNativeProfileCli(root, manifest, "--trace");
                Assert(esp.ExitCode == 0, esp.StandardOutput + esp.StandardError);
                var cmake = File.ReadAllText(Path.Combine(root, "build", "generated", "ctilde_sources.cmake"));
                Assert(cmake.Contains("set_property(SOURCE ${CTILDE_GENERATED_SOURCES}", StringComparison.Ordinal) &&
                    cmake.Contains("\"-O3\"", StringComparison.Ordinal) && cmake.Contains("\"-ffast-math\"", StringComparison.Ordinal),
                    "ESP-IDF did not scope controlled flags to the generated-source list.");

                File.WriteAllText(manifest, """
                    {
                      "target": "cosmopolitan",
                      "architecture": "x64",
                      "sources": ["*.ct"],
                      "cosmopolitan": { "mode": "tiny" },
                      "build": { "configuration": "release", "optimization": "speed" }
                    }
                    """);
                var tiny = RunNativeProfileCli(root, manifest, "--check");
                Assert(tiny.ExitCode == 2 && tiny.StandardError.Contains("owns -Os", StringComparison.Ordinal),
                    "Cosmopolitan tiny did not report the expected explicit-optimization conflict.\n" + tiny.StandardOutput + tiny.StandardError);

                File.WriteAllText(manifest, """
                    {
                      "target": "freestanding",
                      "architecture": "x86",
                      "sources": ["*.ct"],
                      "build": { "configuration": "release", "optimization": "speed" },
                      "freestanding": { "compileOptions": ["-O3"] }
                    }
                    """);
                var conflict = RunNativeProfileCli(root, manifest, "--check");
                Assert(conflict.ExitCode == 2 && conflict.StandardError.Contains("conflicts with an explicitly controlled", StringComparison.Ordinal),
                    "Freestanding raw options silently overrode an explicit optimization profile.");

                File.WriteAllText(manifest, """
                    {
                      "target": "esp-idf",
                      "sources": ["*.ct"],
                      "build": {
                        "cLayout": "modules",
                        "generatedDirectory": "build/generated",
                        "pgo": { "mode": "generate", "directory": "build/pgo" }
                      }
                    }
                    """);
                var embeddedPgo = RunNativeProfileCli(root, manifest, "--check");
                Assert(embeddedPgo.ExitCode == 2 && embeddedPgo.StandardError.Contains("supported only for hosted", StringComparison.Ordinal),
                    "ESP-IDF silently accepted hosted PGO instrumentation.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        suite.Run("draft 0.41 native profile CLI precedence and flags", () =>
        {
            var root = CreateNativeProfileProject();
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "off", "build/pgo");
                var first = RunNativeProfileCli(root, manifest, "--build", "--trace");
                Assert(first.ExitCode == 0, first.StandardOutput + first.StandardError);
                var initialObjects = Directory.EnumerateFiles(Path.Combine(root, "build", ".ctilde-cache"), "*.*", SearchOption.TopDirectoryOnly).Count();

                var overridden = RunNativeProfileCli(root, manifest, "--build", "--trace", "--optimization", "aggressive",
                    "--cpu-target", "avx2", "--floating-point", "fast");
                Assert(overridden.ExitCode == 0, overridden.StandardOutput + overridden.StandardError);
                var trace = overridden.StandardOutput + overridden.StandardError;
                if (OperatingSystem.IsWindows())
                    Assert(trace.Contains("/O2", StringComparison.Ordinal) && trace.Contains("/Ob3", StringComparison.Ordinal) &&
                        trace.Contains("/arch:AVX2", StringComparison.Ordinal) && trace.Contains("/fp:fast", StringComparison.Ordinal) &&
                        trace.Contains("/Gy", StringComparison.Ordinal) && trace.Contains("/Gw", StringComparison.Ordinal) &&
                        trace.Contains("/GL", StringComparison.Ordinal) && trace.Contains("/LTCG", StringComparison.Ordinal) &&
                        trace.Contains("/OPT:REF,ICF", StringComparison.Ordinal), "MSVC did not receive the exact Draft 0.41 Release profile flags.");
                else
                    Assert(trace.Contains("-O3", StringComparison.Ordinal) && trace.Contains("-march=x86-64-v3", StringComparison.Ordinal) &&
                        trace.Contains("-mtune=generic", StringComparison.Ordinal) && trace.Contains("-ffast-math", StringComparison.Ordinal) &&
                        trace.Contains("-flto", StringComparison.Ordinal) && trace.Contains("-Wl,--gc-sections", StringComparison.Ordinal),
                        "GNU C did not receive the exact Draft 0.41 Release profile flags.");
                var separatedObjects = Directory.EnumerateFiles(Path.Combine(root, "build", ".ctilde-cache"), "*.*", SearchOption.TopDirectoryOnly).Count();
                Assert(separatedObjects > initialObjects, "Changing controlled native settings reused the previous object-cache identity.");

                var invalidArchitecture = RunNativeProfileCli(root, manifest, "--build", "--architecture", "x86", "--cpu-target", "avx2");
                Assert(invalidArchitecture.ExitCode == 2 && invalidArchitecture.StandardError.Contains("requires resolved architecture 'x64'", StringComparison.Ordinal),
                    "The CLI accepted AVX2 for x86.");
                var invalidDebug = RunNativeProfileCli(root, manifest, "--build", "--configuration", "debug", "--optimization", "speed");
                Assert(invalidDebug.ExitCode == 2 && invalidDebug.StandardError.Contains("requires a Release configuration", StringComparison.Ordinal),
                    "The CLI accepted controlled optimization in Debug.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        suite.Run("draft 0.41 hosted PGO identity and missing training data", () =>
        {
            var root = CreateNativeProfileProject();
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "use", "build/missing-pgo");
                var missing = RunNativeProfileCli(root, manifest, "--build");
                Assert(missing.ExitCode == 1 && missing.StandardError.Contains("Matching PGO training data is absent", StringComparison.Ordinal),
                    "PGO use did not reject absent identity-matched training data.");

                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "generate", "build/generated-pgo");
                var generated = RunNativeProfileCli(root, manifest, "--build", "--trace");
                Assert(generated.ExitCode == 0, generated.StandardOutput + generated.StandardError);
                var markers = Directory.EnumerateFiles(Path.Combine(root, "build", "generated-pgo"), "identity.txt", SearchOption.AllDirectories).ToArray();
                Assert(markers.Length == 1 && File.ReadAllText(markers[0]).Contains("draft-0.43", StringComparison.Ordinal),
                    "PGO generation did not create one Draft-versioned build identity.");

                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "generate", "build/stale-pgo");
                var staleGenerated = RunNativeProfileCli(root, manifest, "--build");
                Assert(staleGenerated.ExitCode == 0, staleGenerated.StandardOutput + staleGenerated.StandardError);
                var staleMarker = Directory.EnumerateFiles(Path.Combine(root, "build", "stale-pgo"), "identity.txt", SearchOption.AllDirectories).Single();
                File.WriteAllText(staleMarker, "stale identity");
                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "use", "build/stale-pgo");
                var stale = RunNativeProfileCli(root, manifest, "--build");
                Assert(stale.ExitCode == 1 && stale.StandardError.Contains("stale", StringComparison.OrdinalIgnoreCase),
                    "PGO use accepted stale identity metadata.");

                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "use", "build/generated-pgo");
                var untrained = RunNativeProfileCli(root, manifest, "--build");
                var expected = OperatingSystem.IsWindows() ? ".pgc" : "training";
                Assert(untrained.ExitCode == 1 && untrained.StandardError.Contains(expected, StringComparison.OrdinalIgnoreCase),
                    "PGO use accepted an instrumented build that had never been trained.");

                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "generate", "build/trained-pgo");
                var instrumented = RunNativeProfileCli(root, manifest, "--build");
                Assert(instrumented.ExitCode == 0, instrumented.StandardOutput + instrumented.StandardError);
                var trainingEnvironment = Directory.EnumerateFiles(Path.Combine(root, "build", "trained-pgo"), "training-environment.txt", SearchOption.AllDirectories).Single();
                var profileDirectory = Path.GetDirectoryName(trainingEnvironment)!;
                var trained = RunProcess(Path.Combine(root, "build", "profile-test.exe"), [], workingDirectory: root,
                    environment: new Dictionary<string, string> { ["VCPROFILE_PATH"] = profileDirectory });
                Assert(trained.ExitCode == 0, trained.StandardOutput + trained.StandardError);
                WriteNativeProfileManifest(manifest, "speed", "baseline", "precise", "use", "build/trained-pgo");
                var optimized = RunNativeProfileCli(root, manifest, "--build", "--trace");
                Assert(optimized.ExitCode == 0, optimized.StandardOutput + optimized.StandardError);
                Assert((optimized.StandardOutput + optimized.StandardError).Contains(OperatingSystem.IsWindows() ? "/USEPROFILE" : "-fprofile-use", StringComparison.Ordinal),
                    "The trained PGO use phase did not reach the native linker.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    private static string CreateNativeProfileProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "ctilde-draft041-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.ct"),
            "public static class Program { [EntryPoint] public static void Main() { } }");
        return root;
    }

    private static void WriteNativeProfileManifest(string path, string optimization, string cpu, string floatingPoint, string pgo, string directory)
    {
        File.WriteAllText(path, $$"""
            {
              "target": "hosted",
              "architecture": "x64",
              "sources": ["*.ct"],
              "build": {
                "cLayout": "unity",
                "generatedC": "build/generated/ctilde_program.c",
                "generatedHeader": "build/generated/ctilde_exports.h",
                "configuration": "release",
                "lto": true,
                "executable": "build/profile-test.exe",
                "optimization": "{{optimization}}",
                "cpuTarget": "{{cpu}}",
                "floatingPoint": "{{floatingPoint}}",
                "pgo": { "mode": "{{pgo}}", "directory": "{{directory}}" }
              }
            }
            """);
    }

    private static ProcessResult RunNativeProfileCli(string root, string manifest, params string[] arguments)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Debug" : "Release";
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
        return RunProcess("dotnet", [cli, "--project", manifest, .. arguments], workingDirectory: root);
    }

    private static void AssertProjectFailure(string manifest, string expected)
    {
        try
        {
            _ = CTildeProjectFile.Load(manifest);
            throw new InvalidOperationException("The invalid Draft 0.41 native profile manifest was accepted.");
        }
        catch (CTildeProjectException exception)
        {
            Assert(exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), exception.Message);
        }
    }
}
