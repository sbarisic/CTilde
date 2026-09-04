using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart43(ConformanceSuite suite)
    {
        suite.Run("managed shell quoted command-line parsing", () =>
        {
            var parser = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "ShellCommandLine.ct"));
            const string harness = """
                using System;
                using Examples.ManagedShell;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        string[] values;
                        bool background;
                        bool ok = ShellCommandLine.TryParse("program.ctm 1 \"argument 2\" 3", out values, out background);
                        Console.WriteLine(ok && !background && values.Length == 4 && values[0] == "program.ctm"
                            && values[1] == "1" && values[2] == "argument 2" && values[3] == "3");
                        ok = ShellCommandLine.TryParse("tool.ctm \"\" pre\"two words\"post", out values, out background);
                        Console.WriteLine(ok && values.Length == 3 && values[1] == ""
                            && values[2] == "pretwo wordspost");
                        ok = ShellCommandLine.TryParse("tool.ctm \"&\"", out values, out background);
                        Console.WriteLine(ok && !background && values.Length == 2 && values[1] == "&");
                        ok = ShellCommandLine.TryParse("tool.ctm &", out values, out background);
                        Console.WriteLine(ok && background && values.Length == 1);
                        ok = ShellCommandLine.TryParse("tool.ctm a\\tb\\n", out values, out background);
                        Console.WriteLine(ok && values.Length == 2 && values[1].Length == 4);
                        ok = ShellCommandLine.TryParse("tool.ctm a\\\"b c\\\\d", out values, out background);
                        Console.WriteLine(ok && values.Length == 3 && values[1] == "a\"b"
                            && values[2] == "c\\d");
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \"", out values, out background));
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \\q", out values, out background));
                        Console.WriteLine(!ShellCommandLine.TryParse("bad \\", out values, out background));
                    }
                }
                """;
            var result = CompileAndRun([
                SyntaxTree.ParseText(parser, "ShellCommandLine.ct"),
                SyntaxTree.ParseText(harness, "Program.ct")]);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) ==
                "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n",
                $"ManagedShell command-line parsing failed ({result.ExitCode}).\n{result.StandardOutput}\n{result.StandardError}");
        });

        suite.Run("managed nano buffer and ANSI input parsing", () =>
        {
            var bufferSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "Nano", "NanoBuffer.ct"));
            var inputSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "Nano", "NanoInput.ct"));
            bufferSource = bufferSource.Replace("[Overlay(\"buffer\")]\r\n", "", StringComparison.Ordinal)
                .Replace("[Overlay(\"buffer\")]\n", "", StringComparison.Ordinal);
            inputSource = inputSource.Replace("[Overlay(\"editor\")]\r\n", "", StringComparison.Ordinal)
                .Replace("[Overlay(\"editor\")]\n", "", StringComparison.Ordinal);
            const string harness = """
                using System;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        byte[] original = new byte[8];
                        original[0] = (byte)0xef; original[1] = (byte)0xbb; original[2] = (byte)0xbf;
                        original[3] = (byte)97; original[4] = (byte)13; original[5] = (byte)10;
                        original[6] = (byte)0xc3; original[7] = (byte)0xa9;
                        NanoBuffer buffer = new NanoBuffer(original);
                        Console.WriteLine(buffer.HasBom && buffer.WasNormalized && buffer.Length == 4
                            && buffer.GetByte(0) == (byte)97 && buffer.GetByte(1) == (byte)10);
                        buffer.MoveTo(4);
                        Console.WriteLine(buffer.Backspace() && buffer.Length == 2 && buffer.Cursor == 2);
                        Console.WriteLine(buffer.Insert((byte)0xc3, (byte)0xa9, (byte)0, (byte)0, 2)
                            && buffer.Length == 4);
                        buffer.MoveTo(0);
                        Console.WriteLine(buffer.NextPosition(2) == 4 && buffer.PreviousPosition(4) == 2);

                        bool rejected = false;
                        try
                        {
                            byte[] invalid = new byte[2]; invalid[0] = (byte)0xc0; invalid[1] = (byte)0x80;
                            NanoBuffer unused = new NanoBuffer(invalid);
                        }
                        catch (FormatException) { rejected = true; }
                        Console.WriteLine(rejected);

                        NanoInput input = new NanoInput();
                        NanoKey key;
                        input.Feed(27, out key); input.Feed(91, out key);
                        Console.WriteLine(input.Feed(68, out key) && key.Kind == NanoKeyKind.Left);
                        input.Feed(27, out key); input.Feed(91, out key);
                        input.Feed(50, out key); input.Feed(48, out key); input.Feed(48, out key);
                        Console.WriteLine(input.Feed(126, out key) && key.Kind == NanoKeyKind.PasteStart);
                        input.Feed(27, out key); input.Feed(91, out key); input.Feed(56, out key);
                        input.Feed(59, out key); input.Feed(50, out key); input.Feed(52, out key);
                        input.Feed(59, out key); input.Feed(49, out key); input.Feed(48, out key);
                        input.Feed(48, out key);
                        Console.WriteLine(input.Feed(116, out key) && key.Kind == NanoKeyKind.Resize
                            && key.Rows == 24 && key.Columns == 100);
                    }
                }
                """;
            var result = CompileAndRun([
                SyntaxTree.ParseText(bufferSource, "NanoBuffer.ct"),
                SyntaxTree.ParseText(inputSource, "NanoInput.ct"),
                SyntaxTree.ParseText(harness, "Program.ct")]);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) ==
                "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n",
                $"Nano buffer/input behavior failed ({result.ExitCode}).\n{result.StandardOutput}\n{result.StandardError}");
        });

        suite.Run("managed module utility definitions retain valid GNU attributes", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        if (args.Length == 0)
                            return -1;
                        return args[0].IndexOf(':');
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Tests.UtilityAttributes", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var combined = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(!combined.Contains("CT_GENERATED_LOCAL {", StringComparison.Ordinal),
                "Managed-module externalization attached a visibility attribute to a function body.");
        });

        suite.Run("draft 0.46 storage surface and managed filesystem services", () =>
        {
            var configuration = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.Storage", "1.0.0", [], 8192, 65536);
            var source = """
                using System.IO;
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        string current = Directory.GetCurrentDirectory();
                        if (!File.Exists(current + "/probe.bin"))
                            return Directory.GetFileSystemEntries(current).Length;
                        FileStream stream = File.OpenRead(current + "/probe.bin");
                        defer stream.Dispose();
                        return (int)stream.Length;
                    }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.EspIdf,
                Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: configuration.Kind, ManagedModule: configuration);
            var compilation = Compile(source, options);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var combined = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            var types = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_types.h").Content;
            var runtimeHeader = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_runtime_internal.h").Content;
            var runtime = bundle.Artifacts.Single(artifact => artifact.RelativePath == "ctilde_runtime.c").Content;
            Assert(combined.Contains("ct_runtime_api_v22", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(32)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(48)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(53)", StringComparison.Ordinal) &&
                combined.Contains("Service(UINT32_C(56)", StringComparison.Ordinal),
                "Managed System.IO did not lower through Runtime ABI 22 filesystem services.");
            Assert(!combined.Contains("fopen(path", StringComparison.Ordinal),
                "Managed System.IO retained a private libc filesystem implementation.");
            var hasNativeUtf8 = types.Contains("typedef struct ct_native_utf8_string", StringComparison.Ordinal);
            var hasServiceResultBridge = runtime.Contains("ct_managed_io_result", StringComparison.Ordinal);
            var hasDataPath = runtime.Contains("path.Data", StringComparison.Ordinal);
            var hasLegacyPath = runtime.Contains("path.Pointer", StringComparison.Ordinal);
            var leaksFreeRtos = types.Contains("freertos/FreeRTOS.h", StringComparison.Ordinal);
            var hasEspErrorDeclaration = runtimeHeader.Contains("esp_err_to_name(int code)", StringComparison.Ordinal);
            Assert(hasNativeUtf8 && hasServiceResultBridge && hasDataPath && !hasLegacyPath &&
                !leaksFreeRtos && hasEspErrorDeclaration,
                $"Managed System.IO omitted or malformed its native UTF-8 and service-failure bridge " +
                $"(utf8={hasNativeUtf8}, resultBridge={hasServiceResultBridge}, data={hasDataPath}, " +
                $"legacy={hasLegacyPath}, freertos={leaksFreeRtos}, espError={hasEspErrorDeclaration}).");
        });

        suite.Run("draft 0.46 ESP storage ownership surface", () =>
        {
            const string source = """
                using System.Storage;
                using Esp.Idf.Storage;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        RemovableSdCardMonitor monitor = new RemovableSdCardMonitor(SdSpiConfiguration.TCan485);
                        defer monitor.Dispose();
                        monitor.AddFatMount(-1, "/sd", 8);
                        monitor.Start();
                        ulong generation = monitor.Generation;
                    }
                }
                """;
            var compilation = Compile(source, new CompilationOptions(CompilationTarget.EspIdf,
                Architecture: CompilationArchitecture.Xtensa));
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            using var writer = new StringWriter();
            var result = compilation.EmitC(writer);
            Assert(result.Success && writer.ToString().Contains("ct_storage_monitor_add_fat", StringComparison.Ordinal),
                "The ESP removable-storage surface did not emit its native adapter calls.");
        });
    }
}
