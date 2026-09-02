using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart33(ConformanceSuite suite)
    {
        suite.Run("draft 0.40 comparison operators and TimeSpan", () =>
        {
            const string source = """
                using System;

                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        TimeSpan negative = TimeSpan.FromMilliseconds(-1500L);
                        TimeSpan positive = TimeSpan.FromSeconds(2L);
                        TimeSpan total = negative + positive;
                        Console.WriteLine(sizeof(TimeSpan));
                        Console.WriteLine(negative.Nanoseconds);
                        Console.WriteLine(negative.WholeMicroseconds);
                        Console.WriteLine(negative.WholeMilliseconds);
                        Console.WriteLine(negative.WholeSeconds);
                        Console.WriteLine(total.Nanoseconds);
                        Console.WriteLine(total.TotalMilliseconds == 500.0d);
                        Console.WriteLine(total.TotalSeconds == 0.5d);
                        Console.WriteLine(negative < positive && negative <= negative && positive > negative && positive >= positive);
                        Console.WriteLine(negative == TimeSpan.FromNanoseconds(-1500000000L));
                        Console.WriteLine(negative != positive);
                        Console.WriteLine((-negative).Nanoseconds);
                        TimeSpan wrapped = TimeSpan.FromNanoseconds(9223372036854775807L) + TimeSpan.FromNanoseconds(1L);
                        Console.WriteLine(wrapped.Nanoseconds);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput, "8", "-1500000000", "-1500000", "-1500", "-1", "500000000",
                "True", "True", "True", "True", "True", "1500000000", "-9223372036854775808");

            const string invalid = "public struct Value { public static int operator ==(Value left, Value right) { return 0; } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1269"),
                "A comparison operator with a non-Boolean result was accepted.");
        });

        suite.Run("draft 0.40 deterministic Random", () =>
        {
            const string source = """
                using System;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Random zero = new Random();
                        Console.WriteLine(zero.NextUInt());
                        Console.WriteLine(zero.NextUInt());
                        Console.WriteLine(zero.NextUInt());
                        Console.WriteLine(zero.NextUInt());
                        Console.WriteLine(zero.NextUInt());

                        Random one = new Random(1UL);
                        Console.WriteLine(one.NextUInt());
                        Console.WriteLine(one.NextUInt());
                        Console.WriteLine(one.NextUInt());

                        one.Reseed(18446744073709551615UL);
                        Console.WriteLine(one.NextUInt());
                        Console.WriteLine(one.NextUInt());

                        Random bounded = new Random(42UL);
                        Console.WriteLine(bounded.NextUInt());
                        Console.WriteLine(bounded.NextUInt());
                        Console.WriteLine(bounded.NextUInt(10u));
                        Console.WriteLine(bounded.NextUInt(1u));

                        Random signed = new Random(42UL);
                        Console.WriteLine(signed.NextInt(-10, 10));
                        Console.WriteLine(signed.NextInt(-2147483648, 2147483647));
                        float sample = signed.NextFloat();
                        Console.WriteLine(sample >= 0.0f && sample < 1.0f);

                        try
                        {
                            bounded.NextUInt(0u);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            Console.WriteLine("uint-range");
                        }
                        try
                        {
                            bounded.NextInt(5, 5);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            Console.WriteLine("int-range");
                        }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput,
                "0", "1613493245", "3894649422", "2055130073", "2315086854",
                "0", "3114030964", "3308539156", "4293918721", "3933164268",
                "0", "1971522493", "4", "0", "3", "-1905394254", "True", "uint-range", "int-range");
        });

        suite.Run("draft 0.40 monotonic Stopwatch", () =>
        {
            const string source = """
                using System;
                using System.Diagnostics;
                using System.Threading;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        long first = Stopwatch.GetTimestampNanoseconds();
                        Stopwatch watch = Stopwatch.StartNew();
                        watch.Start();
                        Thread.Sleep(20u);
                        TimeSpan whileRunning = watch.Elapsed;
                        watch.Stop();
                        watch.Stop();
                        long stopped = watch.ElapsedNanoseconds;
                        Thread.Sleep(5u);
                        Console.WriteLine(Stopwatch.GetTimestampNanoseconds() >= first);
                        Console.WriteLine(whileRunning.Nanoseconds > 0L);
                        Console.WriteLine(watch.ElapsedMilliseconds >= 1L);
                        Console.WriteLine(watch.ElapsedNanoseconds == stopped);
                        Console.WriteLine(!watch.IsRunning);
                        watch.Reset();
                        Console.WriteLine(watch.ElapsedNanoseconds == 0L && !watch.IsRunning);
                        watch.Restart();
                        Console.WriteLine(watch.IsRunning);
                        Stopwatch copy = watch;
                        watch.Stop();
                        Console.WriteLine(copy.IsRunning && !watch.IsRunning);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("QueryPerformanceCounter", StringComparison.Ordinal) &&
                generated.Contains("clock_gettime(CLOCK_MONOTONIC", StringComparison.Ordinal) &&
                generated.Contains("esp_timer_get_time", StringComparison.Ordinal), "The platform monotonic-clock branches were not emitted.");
            Assert(generated.Contains("if (milliseconds != 0u && ticks == 0u) ticks = 1u", StringComparison.Ordinal),
                "ESP Thread.Sleep did not preserve a nonzero delay shorter than one FreeRTOS tick.");
            var unused = Emit("public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!unused.Contains("ct_monotonic_nanoseconds", StringComparison.Ordinal), "Unused monotonic-clock support was emitted.");
            var result = CompileAndRun(source, threads: true);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True", "True", "True", "True", "True", "True", "True");
        });

        suite.Run("draft 0.40 spin primitives", () =>
        {
            const string source = """
                using System;
                using System.Threading;

                public static class Program
                {
                    private static SpinLock gate = new SpinLock();
                    private static int counter;

                    private static void Work()
                    {
                        int index = 0;
                        while (index < 1000)
                        {
                            gate.Enter();
                            counter++;
                            gate.Exit();
                            index++;
                        }
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        SpinWait wait = new SpinWait();
                        int index = 0;
                        while (index < 10) { wait.SpinOnce(); index++; }
                        Console.WriteLine(wait.Count == 10 && wait.NextSpinWillYield);
                        wait.Reset();
                        Console.WriteLine(wait.Count == 0 && !wait.NextSpinWillYield);
                        Console.WriteLine(gate.TryEnter());
                        Console.WriteLine(gate.IsHeld);
                        gate.Exit();

                        Thread first = new Thread(Work);
                        Thread second = new Thread(Work);
                        first.Start(); second.Start(); first.Join(); second.Join();
                        Console.WriteLine(counter);
                        Console.WriteLine(!gate.IsHeld);
                    }
                }
                """;
            var result = CompileAndRun(source, threads: true);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True", "True", "True", "2000", "True");

            const string copied = "using System.Threading; public static class Program { [EntryPoint] public static void Main() { SpinLock first = new SpinLock(); SpinLock second = first; } }";
            Assert(Compile(copied).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1278"), "A SpinLock was copied by value.");
        });

        suite.Run("draft 0.40 scalar Math expansion", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static bool Near(float left, float right) { return Math.Abs(left - right) < 0.0001f; }
                    private static bool Near(double left, double right) { return Math.Abs(left - right) < 0.000000001d; }
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(Near(Math.Asin(1.0f), Math.Pi / 2.0f));
                        Console.WriteLine(Near(Math.Atan(1.0f), Math.Pi / 4.0f));
                        Console.WriteLine(Near(Math.Atan2(0.0f, -1.0f), Math.Pi));
                        Console.WriteLine(Near(Math.Exp(Math.Log(5.0f)), 5.0f));
                        Console.WriteLine(Near(Math.Log2(8.0f), 3.0f) && Near(Math.Log10(1000.0f), 3.0f));
                        Console.WriteLine(Near(Math.Pow(2.0f, 10.0f), 1024.0f));
                        Console.WriteLine(Near(Math.Round(1.5f), 2.0f) && Near(Math.Truncate(-1.9f), -1.0f));
                        Console.WriteLine(Near(Math.E, Math.Exp(1.0f)) && Near(Math.Tau, 2.0f * Math.Pi));
                        Console.WriteLine(Near(Math.Asin(1.0d), Math.Pi64 / 2.0d));
                        Console.WriteLine(Near(Math.Atan2(0.0d, -1.0d), Math.Pi64));
                        Console.WriteLine(Near(Math.Exp(Math.Log(5.0d)), 5.0d));
                        Console.WriteLine(Near(Math.Log2(8.0d), 3.0d) && Near(Math.Log10(1000.0d), 3.0d));
                        Console.WriteLine(Near(Math.Pow(2.0d, 10.0d), 1024.0d));
                        Console.WriteLine(Near(Math.Round(1.5d), 2.0d) && Near(Math.Truncate(-1.9d), -1.0d));
                        Console.WriteLine(Near(Math.E64, Math.Exp(1.0d)) && Near(Math.Tau64, 2.0d * Math.Pi64));
                        float nan = Math.Log(-1.0f);
                        Console.WriteLine(nan != nan);
                        float negativeZero = Math.Round(-0.0f);
                        Console.WriteLine(1.0f / negativeZero < 0.0f);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("return atan2f(left, right);", StringComparison.Ordinal) &&
                generated.Contains("return pow(left, right);", StringComparison.Ordinal), "The expanded math mappings were not emitted.");
            Assert(!Emit("public static class Program { [EntryPoint] public static void Main() { } }").Contains("float ct_math_pow(float left, float right) {", StringComparison.Ordinal),
                "Unused expanded math helpers were emitted.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            AssertOutputLines(result.StandardOutput, Enumerable.Repeat("True", 17).ToArray());
        });

        suite.Run("draft 0.40 freestanding utility availability", () =>
        {
            const string available = """
                using System;
                using System.Runtime;
                public static class Kernel
                {
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc] private static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }
                    [Export("kernel_main")] [NoAlloc] public static int Main()
                    {
                        TimeSpan value = TimeSpan.FromMilliseconds(2L);
                        Random random = new Random(1UL);
                        return(int)value.WholeMilliseconds + (int)(random.NextUInt() & 0u);
                    }
                }
                """;
            var options = new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64);
            Assert(!Compile(available, options).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, Compile(available, options).GetDiagnostics()));

            const string unavailable = "using System.Diagnostics; using System.Threading; public static class Kernel { [Export(\"kernel_main\")] public static int Main() { Stopwatch watch = new Stopwatch(); SpinLock gate = new SpinLock(); return 0; } }";
            Assert(Compile(unavailable, options).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                "Freestanding exposed Stopwatch or SpinLock.");
        });

        suite.Run("draft 0.40 standard-library language services", () =>
        {
            const string source = "using System; using System.Diagnostics; using System.Threading; public static class P { public static void M() { Stopwatch watch = Stopwatch.StartNew(); TimeSpan elapsed = watch.Elapsed; Random random = new Random(); SpinLock gate = new SpinLock(); Math.Pow(2.0f, 3.0f); } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "draft040-editor.ct")]);

            var stopwatchOffset = source.IndexOf("Stopwatch watch", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition("draft040-editor.ct", stopwatchOffset)?.FilePath == "stdlib/System/Diagnostics.ct",
                "Stopwatch did not navigate to its embedded standard-library declaration.");
            Assert(service.GetHover("draft040-editor.ct", stopwatchOffset)?.Sections.Any(section =>
                section.Documentation?.Summary.Contains("monotonic clock", StringComparison.Ordinal) == true) == true,
                "Stopwatch hover documentation was unavailable.");

            const string randomCompletionSource = "using System; public static class P { public static void M() { Random random = new Random(); random. } }";
            var randomService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(randomCompletionSource, "random-completion.ct")]);
            var randomPosition = randomCompletionSource.IndexOf("random.", StringComparison.Ordinal) + "random.".Length;
            var nextUInt = randomService.GetCompletions("random-completion.ct", randomPosition).Single(item => item.Label == "NextUInt");
            Assert(nextUInt.OverloadCount == 2 && nextUInt.DocumentationId is not null &&
                randomService.GetDocumentation(nextUInt.DocumentationId)?.Summary.Contains("Returns", StringComparison.Ordinal) == true,
                "Random.NextUInt completion or documentation was unavailable.");

            const string mathCompletionSource = "using System; public static class P { public static void M() { Math. } }";
            var mathService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(mathCompletionSource, "draft040-math-completion.ct")]);
            var mathPosition = mathCompletionSource.IndexOf("Math.", StringComparison.Ordinal) + "Math.".Length;
            var mathCompletions = mathService.GetCompletions("draft040-math-completion.ct", mathPosition);
            Assert(mathCompletions.Any(item => item.Label == "Tau" && item.Kind == LanguageCompletionKind.Field) &&
                mathCompletions.Any(item => item.Label == "Atan2" && item.OverloadCount == 2),
                "Draft 0.40 Math completion omitted Tau or Atan2 overloads.");
            Assert(mathService.GetDocumentation("M:System.Math.Atan2(float,float)")?.Summary.Contains("Cartesian coordinate", StringComparison.Ordinal) == true,
                "Math.Atan2 documentation was unavailable.");
            Assert(service.GetDocumentation("T:System.Threading.SpinLock")?.Remarks?.Contains("cannot be copied", StringComparison.Ordinal) == true,
                "SpinLock non-copyable documentation was unavailable.");
        });
    }

    private static void AssertOutputLines(string output, params string[] expected)
    {
        var actual = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"Unexpected output.{Environment.NewLine}Expected: {string.Join(" | ", expected)}{Environment.NewLine}Actual: {string.Join(" | ", actual)}");
    }
}
