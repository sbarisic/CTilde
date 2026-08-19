using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart7(ConformanceSuite suite)
    {
        suite.Run("hosted I/O target and documentation", () =>
        {
            const string source = "using System; using System.IO; public static class Program { [EntryPoint] public static void Main() { Console.ReadLine(); } }";
            var hosted = Compile(source);
            Assert(!hosted.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, hosted.GetDiagnostics()));
            var importOnly = Compile("using System.IO; public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!importOnly.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "An unused hosted System.IO import was rejected.");
            var esp = Compile(source, new CompilationOptions(CompilationTarget.EspIdf));
            Assert(esp.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Hosted I/O was available to ESP-IDF.");

            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText("using System.IO; public static class P { public void M() { File. } }", "hosted-io.ct")]);
            var text = "using System.IO; public static class P { public void M() { File. } }";
            var completions = service.GetCompletions("hosted-io.ct", text.IndexOf("File.", StringComparison.Ordinal) + "File.".Length);
            var open = completions.Single(completion => completion.Label == "Open");
            Assert(open.DocumentationId is not null && service.GetDocumentation(open.DocumentationId)?.Summary.Contains("Opens", StringComparison.Ordinal) == true, "Hosted File.Open documentation was unavailable.");

            var espService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(text, "esp-io.ct")], new CompilationOptions(CompilationTarget.EspIdf));
            Assert(!espService.GetCompletions("esp-io.ct", text.IndexOf("File.", StringComparison.Ordinal) + "File.".Length).Any(completion => completion.Label == "Open"), "Hosted File completion appeared for ESP-IDF.");
        });

        suite.Run("hosted I/O ownership and reserved symbols", () =>
        {
            const string valid = """
                using System.IO;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        FileHandle file = File.Open("owned.bin", FileMode.Create, FileAccess.Write);
                        defer File.Close(file);
                        File.Write(file, "ok");
                    }
                }
                """;
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A deferred hosted file handle was rejected.");

            const string leak = "using System.IO; public static class P { [EntryPoint] public static void Main() { FileHandle file = File.Open(\"x\", FileMode.Open, FileAccess.Read); } }";
            Assert(Compile(leak).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1258"), "An unclosed hosted file handle was not diagnosed.");

            const string reserved = "public static class Native { [Extern(\"ct_host_file_open\")] public static int Call(); } public static class P { [EntryPoint] public static void Main() { } }";
            Assert(Compile(reserved).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A hosted runtime symbol conflict was not diagnosed.");
        });

        suite.Run("hosted ray tracer native render", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "HostedIo.Program.ct"));
            var generated = Emit(source);
            Assert(generated.Contains("ct_math_sqrt(", StringComparison.Ordinal), "The ray tracer did not emit its square-root dependency.");
            Assert(generated.Contains("ct_math_min(", StringComparison.Ordinal) && generated.Contains("ct_math_max(", StringComparison.Ordinal), "The ray tracer did not emit its color-clamping dependencies.");
            Assert(generated.Contains("ct_host_file_open(", StringComparison.Ordinal) && generated.Contains("ct_host_file_write_string(", StringComparison.Ordinal), "The ray tracer did not emit hosted file output.");
            Assert(!generated.Contains("ct_console_read()", StringComparison.Ordinal) && !generated.Contains("ct_console_read_line()", StringComparison.Ordinal), "The ray tracer unexpectedly reads console input.");

            var first = CompileAndRun(source, captureFile: "image.ppm");
            var second = CompileAndRun(source, captureFile: "image.ppm");
            Assert(first.ExitCode == 0, first.StandardError);
            Assert(second.ExitCode == 0, second.StandardError);
            Assert(Normalize(first.StandardOutput) == "Rendering image.ppm...\nDone: 256x144.\n", first.StandardOutput);
            Assert(Normalize(second.StandardOutput) == Normalize(first.StandardOutput), second.StandardOutput);
            var firstImage = first.CapturedFile ?? throw new InvalidOperationException("The first ray-tracer run did not produce image.ppm.");
            var secondImage = second.CapturedFile ?? throw new InvalidOperationException("The second ray-tracer run did not produce image.ppm.");
            Assert(firstImage.SequenceEqual(secondImage), "Repeated renders were not byte-identical.");

            var tokens = Encoding.ASCII.GetString(firstImage).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert(tokens.Length == 4 + 256 * 144 * 3, $"The PPM contained {tokens.Length} tokens instead of {4 + 256 * 144 * 3}.");
            Assert(tokens[0] == "P3" && tokens[1] == "256" && tokens[2] == "144" && tokens[3] == "255", "The PPM header was incorrect.");

            var components = new int[256 * 144 * 3];
            for (var index = 0; index < components.Length; index++)
            {
                Assert(int.TryParse(tokens[index + 4], NumberStyles.None, CultureInfo.InvariantCulture, out components[index]), $"PPM component {index} was not an integer.");
                Assert(components[index] is >= 0 and <= 255, $"PPM component {index} was outside 0 through 255.");
            }

            var sky = ReadPixel(components, 256, 128, 0);
            Assert(sky.Blue == 255 && sky.Blue > sky.Green && sky.Green > sky.Red, "The top-center pixel did not contain the expected blue sky gradient.");
            var sphere = ReadPixel(components, 256, 128, 72);
            Assert(sphere.Red is >= 110 and <= 145 && sphere.Green is >= 110 and <= 145 && sphere.Blue is >= 240 and <= 255, "The center pixel did not contain the expected sphere-normal color.");
            var ground = ReadPixel(components, 256, 128, 120);
            Assert(ground.Green >= 240 && ground.Red is >= 110 and <= 145 && ground.Blue is >= 110 and <= 145, "The lower-center pixel did not contain the expected ground-normal color.");
        });

        suite.Run("hosted console EOF and I/O exceptions", () =>
        {
            const string source = """
                using System;
                using System.IO;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(Console.Read());
                        Console.WriteLine(Console.Read());
                        try
                        {
                            FileHandle file = File.Open("missing-file.bin", FileMode.Open, FileAccess.Read);
                            defer File.Close(file);
                        }
                        catch (IOException error)
                        {
                            Console.WriteLine(error.ErrorCode != 0);
                        }
                        try
                        {
                            FileHandle file = File.Open("bad.bin", FileMode.Append, FileAccess.ReadWrite);
                            defer File.Close(file);
                        }
                        catch (IOException error)
                        {
                            Console.WriteLine(error.ErrorCode != 0);
                        }
                    }
                }
                """;
            var result = CompileAndRun(source, standardInput: "A");
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "65\n-1\nTrue\nTrue\n", result.StandardOutput);
        });

        suite.Run("hosted console line edge cases", () =>
        {
            const string lines = """
                using System;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        string first = Console.ReadLine();
                        string second = Console.ReadLine();
                        string third = Console.ReadLine();
                        string fourth = Console.ReadLine();
                        Console.WriteLine(first);
                        Console.WriteLine(second.Length);
                        Console.WriteLine(third);
                        Console.WriteLine(fourth == null);
                    }
                }
                """;
            var lineResult = CompileAndRun(lines, standardInput: "alpha\r\n\nlast");
            Assert(lineResult.ExitCode == 0, lineResult.StandardError);
            Assert(Normalize(lineResult.StandardOutput) == "alpha\n0\nlast\nTrue\n", lineResult.StandardOutput);

            const string invalid = """
                using System;
                using System.IO;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        try { Console.ReadLine(); }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                }
                """;
            var invalidResult = CompileAndRun(invalid, standardInputBytes: [0xf0, 0x28, 0x8c, 0x28, 0x0a]);
            Assert(invalidResult.ExitCode == 0, invalidResult.StandardError);
            Assert(Normalize(invalidResult.StandardOutput) == "True\n", invalidResult.StandardOutput);
        });

        suite.Run("hosted file mode and buffer writes", () =>
        {
            const string source = """
                using System;
                using System.IO;
                using System.Runtime;
                public static class Program
                {
                    private static unsafe void WriteByte(FileMode mode, FileAccess access, byte value)
                    {
                        FileHandle file = File.Open("modes.bin", mode, access);
                        defer File.Close(file);
                        NativeBuffer<byte> buffer = stackalloc byte[1];
                        buffer[0u] = value;
                        File.Write(file, buffer);
                    }
                    private static unsafe void Print()
                    {
                        FileHandle file = File.Open("modes.bin", FileMode.Open, FileAccess.Read);
                        defer File.Close(file);
                        NativeBuffer<byte> buffer = stackalloc byte[4];
                        nuint count = File.Read(file, buffer);
                        nuint index = 0u;
                        while (index < count) { Console.Write((char)buffer[index]); index++; }
                        Console.WriteLine();
                    }
                    private static void Invalid(FileMode mode, FileAccess access)
                    {
                        try
                        {
                            FileHandle file = File.Open("modes.bin", mode, access);
                            defer File.Close(file);
                        }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                    private static void InvalidPath()
                    {
                        try
                        {
                            FileHandle file = File.Open("bad\0name.bin", FileMode.Create, FileAccess.Write);
                            defer File.Close(file);
                        }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                    [EntryPoint] public static unsafe void Main()
                    {
                        WriteByte(FileMode.Create, FileAccess.Write, (byte)'A');
                        WriteByte(FileMode.Open, FileAccess.Write, (byte)'B');
                        WriteByte(FileMode.Append, FileAccess.Write, (byte)'C');
                        Print();
                        WriteByte(FileMode.Create, FileAccess.ReadWrite, (byte)'D');
                        WriteByte(FileMode.Open, FileAccess.ReadWrite, (byte)'E');
                        Print();
                        Invalid(FileMode.Create, FileAccess.Read);
                        Invalid(FileMode.Append, FileAccess.Read);
                        Invalid(FileMode.Append, FileAccess.ReadWrite);
                        InvalidPath();
                    }
                }
                """;
            var result = CompileAndRun(source, standardInput: string.Empty);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "BC\nE\nTrue\nTrue\nTrue\nTrue\n", result.StandardOutput);
        });

        suite.Run("hosted I/O emission isolation", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(42); } }";
            var hosted = Emit(source);
            var esp = Emit(source, new CompilationOptions(CompilationTarget.EspIdf));
            foreach (var symbol in new[] { "ct_console_read", "ct_console_read_line", "ct_host_file_open", "ct_host_file_read" })
            {
                Assert(!hosted.Contains(symbol, StringComparison.Ordinal), $"Unused hosted I/O symbol '{symbol}' changed hosted output.");
                Assert(!esp.Contains(symbol, StringComparison.Ordinal), $"Hosted I/O symbol '{symbol}' changed ESP output.");
            }
        });
    }

    private static (int Red, int Green, int Blue) ReadPixel(int[] components, int width, int x, int y)
    {
        var offset = (y * width + x) * 3;
        return (components[offset], components[offset + 1], components[offset + 2]);
    }
}
