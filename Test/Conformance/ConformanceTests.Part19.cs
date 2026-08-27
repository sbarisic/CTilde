using System.Globalization;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart19(ConformanceSuite suite)
    {
        var options = new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64);

        suite.Run("draft 0.21 freestanding profile and API boundary", () =>
        {
            const string source = """
                using System.Runtime;
                public static class Kernel
                {
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc]
                    static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }

                    [Export("kernel_main")]
                    public static int Main()
                    {
                        static if (Target.Profile == TargetProfile.Freestanding) { return Target.PointerSize; }
                        else { return 0; }
                    }
                }
                """;
            var generated = Emit(source, options);
            Assert(!generated.Contains("int main(", StringComparison.Ordinal) && !generated.Contains("app_main", StringComparison.Ordinal),
                "A freestanding compilation emitted hosted or ESP-IDF startup.");
            Assert(!generated.Contains("#include <stdio.h>", StringComparison.Ordinal) && !generated.Contains("#include <stdlib.h>", StringComparison.Ordinal) &&
                !generated.Contains("#include <string.h>", StringComparison.Ordinal) && !generated.Contains("_Thread_local", StringComparison.Ordinal),
                "Freestanding output retained a forbidden hosted header or TLS dependency.");
            Assert(generated.Contains("void ct_runtime_initialize(void)", StringComparison.Ordinal) && generated.Contains("ct_runtime_panic_bridge", StringComparison.Ordinal),
                "Explicit freestanding lifecycle or panic-role routing was not emitted.");

            var entry = Compile("public static class Program { [EntryPoint] public static void Main() { } }", options);
            Assert(entry.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4115"), "Freestanding [EntryPoint] was accepted.");
            var automatic = Compile(source, new CompilationOptions(CompilationTarget.Freestanding));
            Assert(automatic.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4108"), "Freestanding architecture auto-detection was accepted.");
            var console = Compile("using System; public static class K { [Export(\"k\")] public static void Run() { Console.WriteLine(1); } }", options);
            Assert(console.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Console was exposed to freestanding code.");
        });

        suite.Run("draft 0.21 runtime implementations", () =>
        {
            const string heapSource = """
                using System.Runtime;
                public sealed class Box { public int Value; }
                public static class Kernel
                {
                    [RuntimeImpl(Runtime.Allocate)] [NoAlloc]
                    static unsafe void* Allocate(nuint size) { return null; }
                    [RuntimeImpl(Runtime.Free)] [NoAlloc]
                    static unsafe void Free(void* value) { }
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc]
                    static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }
                    [Export("kernel_main")] public static int Main() { Box box = new Box(); box.Value = 7; return box.Value; }
                }
                """;
            var compilation = Compile(heapSource, options);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var generated = Emit(heapSource, options);
            Assert(generated.Contains("ct_runtime_allocate_bridge", StringComparison.Ordinal) && generated.Contains("ct_runtime_free_bridge", StringComparison.Ordinal) &&
                generated.Contains("ct_memset(value, 0, payload);", StringComparison.Ordinal),
                "Managed freestanding storage did not route through and clear the user allocator result.");

            var missing = Compile(heapSource.Replace("[RuntimeImpl(Runtime.Allocate)]", "", StringComparison.Ordinal), options);
            Assert(missing.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4114"), "A missing required allocator role was accepted.");
            var unsafeHook = Compile(heapSource.Replace("return null;", "Box value = new Box(); return null;", StringComparison.Ordinal), options);
            Assert(unsafeHook.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2211"), "A heap-allocating runtime hook closure was accepted.");
        });

        suite.Run("draft 0.21 narrow naked startup", () =>
        {
            const string source = """
                public static class Boot
                {
                    [Naked] [NoAlloc] [Export("_start")] [Section(".text.boot")]
                    public static unsafe void Start()
                    {
                        [NoAlloc] asm { hlt }
                    }
                }
                """;
            var compilation = Compile(source, options);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = compilation.EmitC(writer);
            Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var generated = writer.ToString();
            Assert(generated.Contains("__attribute__((naked, noreturn, used))", StringComparison.Ordinal) &&
                generated.Contains("__asm__(\" hlt \");", StringComparison.Ordinal) && !generated.Contains("void ct_runtime_initialize(void)", StringComparison.Ordinal),
                "The narrow naked export did not emit exact basic assembly or incorrectly exposed managed lifecycle startup.");
            using var header = new StringWriter(CultureInfo.InvariantCulture);
            Assert(compilation.EmitCHeader(header).Success && header.ToString().Contains("#define CTILDE_HAS_RUNTIME 0", StringComparison.Ordinal) &&
                !header.ToString().Contains("ct_retain", StringComparison.Ordinal) && !header.ToString().Contains("ct_release", StringComparison.Ordinal),
                "A naked-only header did not declare the runtime-free contract.");

            var invalid = Compile(source.Replace("[NoAlloc] asm { hlt }", "int value = 0; [NoAlloc] asm { hlt }", StringComparison.Ordinal), options);
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1302"), "A naked method with a local was accepted.");
            var hosted = Compile(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            Assert(hosted.GetDiagnostics().Any(diagnostic => diagnostic.Code is "CT1302" or "CT4116"), "Hosted naked startup was accepted.");
        });

        suite.Run("draft 0.21 freestanding project and CLI", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-freestanding-project-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.Combine(directory, "src"));
            Directory.CreateDirectory(Path.Combine(directory, "native"));
            try
            {
                File.WriteAllText(Path.Combine(directory, "src", "Kernel.ct"), "using System.Runtime; public static class Kernel { [RuntimeImpl(Runtime.Panic)] [NoAlloc] static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } } [Export(\"kernel_main\")] public static int Main() { return Target.PointerSize; } }");
                File.WriteAllText(Path.Combine(directory, "native", "kernel.ld"), "ENTRY(_start) SECTIONS { . = 0x400000; .text : { *(.text*) } .data : { *(.data*) } .bss : { *(.bss*) *(COMMON) } }");
                File.WriteAllText(Path.Combine(directory, "native", "first.c"), "int first(void) { return 1; }");
                File.WriteAllText(Path.Combine(directory, "native", "second.S"), ".text");
                File.WriteAllBytes(Path.Combine(directory, "native", "extra.o"), []);
                File.WriteAllBytes(Path.Combine(directory, "native", "extra.a"), []);
                var manifestPath = Path.Combine(directory, "ctilde.json");
                File.WriteAllText(manifestPath, """
                    {
                      "target": "freestanding",
                      "architecture": "x64",
                      "sources": ["src/**/*.ct"],
                      "build": { "cLayout": "modules", "compiler": "wsl:gcc", "configuration": "release", "image": "build/kernel.elf" },
                      "freestanding": {
                        "linkerScript": "native/kernel.ld",
                        "entrySymbol": "_start",
                        "nativeSources": ["native/first.c", "native/second.S"],
                        "objectFiles": ["native/extra.o"],
                        "libraries": ["native/extra.a"],
                        "compileOptions": ["-mno-red-zone", "-fno-pic"],
                        "linkOptions": ["-Wl,--build-id=none"]
                      }
                    }
                    """);

                var project = CTildeProjectFile.Load(manifestPath);
                var freestanding = project.Configuration.Freestanding!;
                Assert(project.Configuration.Target == CompilationTarget.Freestanding && project.Configuration.Architecture == CompilationArchitecture.X64,
                    "The freestanding target or architecture was not loaded from the manifest.");
                Assert(freestanding.NativeSources.Select(Path.GetFileName).SequenceEqual(["first.c", "second.S"]) &&
                    freestanding.CompileOptions.SequenceEqual(["-mno-red-zone", "-fno-pic"]),
                    "Freestanding manifest lists did not preserve their declared order.");

                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
                var check = RunProcess("dotnet", [cliDll, "--project", manifestPath, "--check"]);
                Assert(check.ExitCode == 0, $"Freestanding project CLI check failed: {check.StandardOutput}{check.StandardError}");
                var debug = RunProcess("dotnet", [cliDll, Path.Combine(directory, "src", "Kernel.ct"), "--target", "freestanding", "--architecture", "x64", "--prepare-debug", "launch"]);
                Assert(debug.ExitCode == 2 && debug.StandardError.Contains("unavailable for freestanding", StringComparison.Ordinal),
                    "Freestanding debug preparation was not rejected by the CLI.");

                File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"-fno-pic\"", "\"-oescape\"", StringComparison.Ordinal));
                var overrideRejected = false;
                try { CTildeProjectFile.Load(manifestPath); }
                catch (CTildeProjectException) { overrideRejected = true; }
                Assert(overrideRejected, "A freestanding option overriding compiler-owned output was accepted.");
                File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"-oescape\"", "\"-fno-pic\"", StringComparison.Ordinal)
                    .Replace("\"native/first.c\", \"native/second.S\"", "\"native/first.c\", \"native/first.c\"", StringComparison.Ordinal));
                var duplicateRejected = false;
                try { CTildeProjectFile.Load(manifestPath); }
                catch (CTildeProjectException) { duplicateRejected = true; }
                Assert(duplicateRejected, "A duplicate freestanding native input was accepted.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }
}
