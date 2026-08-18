using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

var failures = new List<string>();

Run("deterministic C emission", () =>
{
    const string source = """
        using System;
        namespace Tests;
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int value = 2 + 3 * 4;
                Console.WriteLine(value);
            }
        }
        """;
    var first = Emit(source);
    var second = Emit(source);
    Assert(first == second, "Repeated compilation did not produce byte-identical C.");
    Assert(first.Contains("int main(void)", StringComparison.Ordinal), "C entry point was not emitted.");
    Assert(first.Contains("for GNU C23", StringComparison.Ordinal), "Generated C does not identify the default GNU C23 dialect.");
    Assert(first.Contains("static_assert(CHAR_BIT == 8", StringComparison.Ordinal), "Generated C does not use the C23 static_assert spelling.");
});

Run("structured syntax diagnostic", () =>
{
    var tree = SyntaxTree.ParseText("public class Broken {", "broken.ct");
    Assert(tree.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("CT0", StringComparison.Ordinal)), "Expected a syntax diagnostic.");
});

Run("semantic diagnostics", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int value;
                Console.WriteLine(value);
            }
        }
        """;
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT3108"), "Expected a definite-assignment diagnostic.");
});

Run("multi-file namespaces and using", () =>
{
    var library = SyntaxTree.ParseText("namespace Library; public static class Numbers { public static int Add(int left, int right) { return left + right; } }", "library.ct");
    var program = SyntaxTree.ParseText("using System; using Library; namespace Application; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(Numbers.Add(2, 3)); } }", "program.ct");
    var compilation = Compilation.Create([program, library]);
    using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var result = compilation.EmitC(writer);
    Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
});

Run("access and unsafe diagnostics", () =>
{
    const string source = """
        public sealed class Box
        {
            public int Value { get; private set; }
        }
        public static class Program
        {
            public static int* Expose(int* value) { return value; }
            [EntryPoint]
            public static void Main()
            {
                Box box = new Box();
                box.Value = 4;
            }
        }
        """;
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1110"), "Expected a private-setter diagnostic.");
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2141") >= 2, "Expected unsafe pointer-signature diagnostics.");
});

Run("EntryPoint and Extern validation", () =>
{
    var noEntry = Compile("public static class Library { public static int Value() { return 1; } }").GetDiagnostics();
    Assert(noEntry.Any(diagnostic => diagnostic.Code == "CT1300"), "Expected a missing EntryPoint diagnostic.");

    const string external = "public static class Program { [Extern(\"native_add\")] public static int Add(int a, int b); [EntryPoint] public static void Main() { } }";
    var generated = Emit(external);
    Assert(generated.Contains("extern int32_t native_add", StringComparison.Ordinal), "Extern declaration was not emitted.");
});

Run("readonly flow analysis", () =>
{
    const string valid = "public static class Program { [EntryPoint] public static void Main() { readonly int value; if (true) value = 1; else value = 2; int copy = value; } }";
    Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A valid delayed readonly assignment was rejected.");

    const string invalid = "public static class Program { [EntryPoint] public static void Main() { readonly int value; value = 1; value = 2; } }";
    Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3130"), "Expected a duplicate readonly assignment diagnostic.");
});

Run("numeric promotion and compound assignment", () =>
{
    const string source = "using System; public static class Program { [EntryPoint] public static void Main() { byte value = (byte)250; value += 10; Console.WriteLine((int)value); } }";
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "4\n", result.StandardOutput);

    const string invalid = "public static class Program { [EntryPoint] public static void Main() { uint value = 1u; uint result = -value; } }";
    Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2145"), "Expected unary minus on uint to be rejected.");
});

Run("left-to-right native execution", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            private static int state = 0;
            private static int Next() { state += 1; return state; }
            private static int Pack(int left, int right) { return left * 10 + right; }

            [EntryPoint]
            public static void Main()
            {
                Console.WriteLine(Pack(Next(), Next()));
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "12\n", $"Unexpected output: {result.StandardOutput}");
});

Run("constant folding in switch", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                const int Expected = 1 + 1;
                switch (2)
                {
                    case Expected:
                        Console.WriteLine("constant");
                        break;
                    default:
                        Console.WriteLine("wrong");
                        break;
                }
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "constant\n", result.StandardOutput);
});

Run("while do break and continue", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int index = 0;
                int total = 0;
                while (index < 5)
                {
                    index++;
                    if (index == 2) continue;
                    if (index == 5) break;
                    total += index;
                }
                do { total += 1; } while (false);
                Console.WriteLine(total);
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "9\n", result.StandardOutput);
});

Run("short circuit and string equality", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            private static int state = 0;
            private static bool Touch() { state += 1; return true; }
            [EntryPoint]
            public static void Main()
            {
                bool first = false && Touch();
                bool second = true || Touch();
                string left = "same";
                string right = "sa" + "me";
                Console.WriteLine(state);
                Console.WriteLine(left == right);
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "0\nTrue\n", result.StandardOutput);
});

Run("lexical forms and standard library overloads", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int café = 1_000_000;
                int @class = 0xFF;
                int binary = 0b1010_0110;
                Console.WriteLine(café);
                Console.WriteLine(@class);
                Console.WriteLine(binary);
                Console.WriteLine('A');
                Console.WriteLine(42u);
                Console.WriteLine(1.5f);
                Console.WriteLine(true);
                Console.WriteLine();
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "1000000\n255\n166\nA\n42\n1.5\nTrue\n\n", result.StandardOutput);
});

Run("bundled standard library", () =>
{
    var tree = SyntaxTree.ParseText("public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(1); } }", "program.ct");
    var compilation = Compilation.Create([tree]);
    using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var result = compilation.EmitC(writer);
    Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    Assert(compilation.SyntaxTrees.Length == 1 && ReferenceEquals(compilation.SyntaxTrees[0], tree), "Bundled library trees leaked into Compilation.SyntaxTrees.");
    Assert(writer.ToString().Contains("extern void ct_write_int", StringComparison.Ordinal), "The Console extern declaration was not loaded from the bundled library.");
});

Run("scalar ToString", () =>
{
    const string source = """
        using System;
        public static class Program
        {
            private static int calls = 0;
            private static int Next() { calls++; return 7; }

            [EntryPoint]
            public static void Main()
            {
                byte unsignedByte = (byte)255;
                sbyte signedByte = (sbyte)(-128);
                short signedShort = (short)(-32768);
                ushort unsignedShort = (ushort)65535;
                int signedInt = -2147483647 - 1;
                uint unsignedInt = 4294967295u;
                int zero = 0;
                float number = 1.5f;
                bool flag = true;
                char character = 'A';
                string text = "text";

                Console.WriteLine(unsignedByte.ToString());
                Console.WriteLine(signedByte.ToString());
                Console.WriteLine(signedShort.ToString());
                Console.WriteLine(unsignedShort.ToString());
                Console.WriteLine(signedInt.ToString());
                Console.WriteLine(unsignedInt.ToString());
                Console.WriteLine(zero.ToString());
                Console.WriteLine(number.ToString());
                Console.WriteLine(flag.ToString());
                Console.WriteLine(character.ToString());
                Console.WriteLine(text.ToString());
                Console.WriteLine(text.ToString() == text);
                Console.WriteLine(Next().ToString());
                Console.WriteLine(calls);
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "255\n-128\n-32768\n65535\n-2147483648\n4294967295\n0\n1.5\nTrue\nA\ntext\nTrue\n7\n1\n", result.StandardOutput);
});

Run("ToString diagnostics", () =>
{
    const string source = """
        public sealed class Box { }
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int value = 1;
                string invalidArguments = value.ToString(2);
                Box box = new Box();
                string unsupportedObject = box.ToString();
                int[] values = new int[0];
                string unsupportedArray = values.ToString();
            }
        }
        """;
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2122") >= 2, "Expected ToString overload diagnostics for arguments and class receivers.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2121"), "Expected an unsupported array receiver diagnostic.");
});

Run("null string ToString failure", () =>
{
    const string source = "public static class Program { [EntryPoint] public static void Main() { string text = null; string copy = text.ToString(); } }";
    var result = CompileAndRun(source);
    Assert(result.ExitCode != 0, "Null string ToString returned success.");
    Assert(result.StandardError.Contains("CTN0001", StringComparison.Ordinal), result.StandardError);
});

Run("Environment Exit", () =>
{
    const string source = "using System; public static class Program { [EntryPoint] public static void Main() { Environment.Exit(7); Console.WriteLine(1); } }";
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 7, $"Expected exit code 7, got {result.ExitCode}. {result.StandardError}");
    Assert(result.StandardOutput.Length == 0, result.StandardOutput);
});

Run("objects arrays strings and control flow", () =>
{
    const string source = """
        using System;

        public sealed class Counter
        {
            public Counter(int initial) { Value = initial; }
            public int Value { get; private set; }
            public void Increment() { Value++; }
        }

        public sealed class Defaults
        {
            public int Value = 8;
        }

        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                Counter counter = new Counter(2);
                counter.Increment();
                int[] values = new int[3];
                for (int index = 0; index < values.Length; index++)
                    values[index] = index + 1;
                int total = 0;
                foreach (int value in values)
                    total += value;
                string text = null + "ok";
                byte small = (byte)7;
                Defaults defaults = new Defaults();
                Console.WriteLine(counter.Value);
                Console.WriteLine(total);
                Console.WriteLine(text);
                Console.WriteLine(small);
                Console.WriteLine(defaults.Value);
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "3\n6\nok\n7\n8\n", $"Unexpected output: {result.StandardOutput}");
});

Run("complete feature example", () =>
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "Features.ct"));
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "14\n4\n12\n6\neast\n2\nA\n10\n", $"Unexpected output: {result.StandardOutput}");
});

Run("feature C symbol snapshot", () =>
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "Features.ct"));
    var generated = Emit(source);
    var projection = string.Join('\n', generated.Split('\n').Where(line =>
        line.StartsWith("typedef struct ct_t", StringComparison.Ordinal) ||
        line.StartsWith("typedef struct ct_a", StringComparison.Ordinal) ||
        line.StartsWith("typedef uint", StringComparison.Ordinal) && line.Contains("ct_t_", StringComparison.Ordinal) ||
        line.StartsWith("typedef int", StringComparison.Ordinal) && line.Contains("ct_t_", StringComparison.Ordinal) ||
        line.StartsWith("struct ct_t", StringComparison.Ordinal) ||
        line.StartsWith("struct ct_a", StringComparison.Ordinal) ||
        line.StartsWith("static ", StringComparison.Ordinal) && (line.Contains(" ct_ctor_", StringComparison.Ordinal) || line.Contains(" ct_m_", StringComparison.Ordinal) || line.Contains(" ct_get_", StringComparison.Ordinal) || line.Contains(" ct_set_", StringComparison.Ordinal)) ||
        line == "int main(void)")) + "\n";
    var expected = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "features.symbols.txt")));
    Assert(projection == expected, "Generated C symbol snapshot changed.");
});

Run("bounds failure", () =>
{
    const string source = """
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int[] values = new int[1];
                values[1] = 4;
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode != 0, "Bounds failure returned success.");
    Assert(result.StandardError.Contains("CTA0003", StringComparison.Ordinal), result.StandardError);
});

Run("null failure", () =>
{
    const string source = """
        public sealed class Box { public int Value; }
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                Box box = null;
                int value = box.Value;
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode != 0, "Null failure returned success.");
    Assert(result.StandardError.Contains("CTN0001", StringComparison.Ordinal), result.StandardError);
});

Run("negative array length failure", () =>
{
    const string source = """
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int length = -1;
                int[] values = new int[length];
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode != 0, "Negative array length returned success.");
    Assert(result.StandardError.Contains("CTA0001", StringComparison.Ordinal), result.StandardError);
});

Run("integer division failure", () =>
{
    const string source = """
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                int zero = 0;
                int value = 4 / zero;
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode != 0, "Division by zero returned success.");
    Assert(result.StandardError.Contains("CTI0001", StringComparison.Ordinal), result.StandardError);
});

Run("allocation overflow guard emitted", () =>
{
    var generated = Emit("public static class Program { [EntryPoint] public static void Main() { int[] values = new int[0]; } }");
    Assert(generated.Contains("CTA0002", StringComparison.Ordinal), "Allocation overflow guard was not emitted.");
});

if (failures.Count == 0)
{
    Console.WriteLine("Conformance: all tests passed.");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine(failure);
return 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

static Compilation Compile(string source) => Compilation.Create([SyntaxTree.ParseText(source, "test.ct")]);

static string Emit(string source)
{
    var compilation = Compile(source);
    using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var result = compilation.EmitC(writer);
    Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    return writer.ToString();
}

static ProcessResult CompileAndRun(string source)
{
    var directory = Path.Combine(Path.GetTempPath(), "ctilde-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    try
    {
        var cPath = Path.Combine(directory, "program.c");
        var executablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "program.exe" : "program");
        File.WriteAllText(cPath, Emit(source), new UTF8Encoding(false));
        var compilerResult = RunCompiler(cPath, executablePath);
        Assert(compilerResult.ExitCode == 0, $"C compiler failed:{Environment.NewLine}{compilerResult.StandardOutput}{compilerResult.StandardError}");
        return RunProcess(executablePath, []);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}

static ProcessResult RunCompiler(string cPath, string executablePath)
{
    var configured = Environment.GetEnvironmentVariable("CTILDE_CC");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var compilerName = Path.GetFileNameWithoutExtension(configured);
        var arguments = compilerName.Equals("cl", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/nologo", "/std:clatest", "/W4", "/WX", $"/Fe:{executablePath}", cPath }
            : new[] { "-std=gnu23", "-Wall", "-Wextra", "-Werror", "-o", executablePath, cPath };
        return RunProcess(configured, arguments);
    }

    if (!OperatingSystem.IsWindows())
        return RunProcess("cc", ["-std=gnu23", "-Wall", "-Wextra", "-Werror", "-o", executablePath, cPath]);

    var vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
    Assert(File.Exists(vsWhere), "No C compiler was configured and vswhere.exe was not found.");
    var discovery = RunProcess(vsWhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]);
    Assert(discovery.ExitCode == 0 && !string.IsNullOrWhiteSpace(discovery.StandardOutput), "Visual Studio C tools were not found.");
    var installation = discovery.StandardOutput.Trim();
    var vcVars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
    var commandFile = Path.Combine(Path.GetDirectoryName(cPath)!, "compile.cmd");
    File.WriteAllText(commandFile, $"@echo off{Environment.NewLine}call \"{vcVars}\" >nul{Environment.NewLine}cl /nologo /std:clatest /W4 /WX /Fe:\"{executablePath}\" \"{cPath}\"{Environment.NewLine}", Encoding.ASCII);
    return RunProcess("cmd.exe", ["/d", "/c", commandFile]);
}

static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, standardOutput, standardError);
}

static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
