using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart17(ConformanceSuite suite)
    {
        suite.Run("draft 0.19 constant generics and inline arrays", () =>
        {
            const string source = """
                using System;
                public struct Buffer<T, const int Capacity>
                {
                    public T[Capacity] Items;
                }
                public static class Program
                {
                    private static int Add<const int Amount>(int value) { return value + Amount; }
                    [EntryPoint] public static void Main()
                    {
                        Buffer<byte, 4> buffer = new Buffer<byte, 4>();
                        buffer.Items[0] = (byte)38;
                        Console.WriteLine(Add<4>(buffer.Items[0]));
                        Console.WriteLine(buffer.Items.Length);
                    }
                }
                """;
            var runtime = CompileAndRun(source);
            Assert(runtime.ExitCode == 0 && runtime.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "42\n4",
                $"Constant specialization or inline-array value semantics changed runtime behavior: exit={runtime.ExitCode}, output='{runtime.StandardOutput}'.");
            var generated = Emit(source);
            Assert(generated.Contains("Data[4]", StringComparison.Ordinal), "Inline-array wrapper did not use the closed constant length.");

            var missing = Compile("public static class Program { private static int F<const int N>() { return N; } [EntryPoint] public static void Main() { int x = F(); } }");
            Assert(missing.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A constant method argument was inferred or omitted.");
            var invalid = Compile("public struct Bad<const float N> { public int Value; } public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2202"), "Invalid constant-parameter type did not report CT2202.");
        });

        suite.Run("draft 0.19 alignment newtypes and CPU intrinsics", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public newtype FileDescriptor : int;
                [Align(64)] public struct CacheLine { public byte[64] Data; }
                public static class Program
                {
                    [Align(32)] private static byte[32] storage;
                    [EntryPoint] public static void Main()
                    {
                        [Align(16)] uint value = Cpu.ByteSwap((uint)0x01020304);
                        CacheLine cache = new CacheLine();
                        cache.Data[0] = (byte)1;
                        FileDescriptor descriptor = (FileDescriptor)3;
                        Console.WriteLine((int)descriptor + (int)Cpu.PopCount(value) + cache.Data[0] - 1);
                        Console.WriteLine(Cpu.LeadingZeroCount((uint)0));
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("CT_ALIGN(64)", StringComparison.Ordinal) && generated.Contains("CT_ALIGN(32)", StringComparison.Ordinal),
                "General alignment was not carried into native declarations.");
            var runtime = CompileAndRun(source);
            Assert(runtime.ExitCode == 0 && runtime.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "8\n32",
                $"Newtype casts or portable CPU intrinsics changed runtime behavior: exit={runtime.ExitCode}, output='{runtime.StandardOutput}'.");

            var implicitConversion = Compile("public newtype Id : int; public static class Program { [EntryPoint] public static void Main() { Id id = 1; } }");
            Assert(implicitConversion.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2205"), "Implicit newtype conversion did not report CT2205.");

            const string nativeSurface = """
                public newtype PacketId : uint;
                [Align(16)] public struct Packet { public byte[4] Bytes; public PacketId Id; }
                public static class Program
                {
                    [Export("packet_echo")] public static Packet Echo(Packet value) { return value; }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var nativeCompilation = Compile(nativeSurface);
            using var headerWriter = new StringWriter();
            Assert(nativeCompilation.EmitCHeader(headerWriter).Success, string.Join(Environment.NewLine, nativeCompilation.GetDiagnostics()));
            var header = headerWriter.ToString();
            Assert(header.IndexOf("typedef uint32_t", StringComparison.Ordinal) < header.IndexOf("packet_echo", StringComparison.Ordinal) &&
                header.Contains("Data[4]", StringComparison.Ordinal) && header.Contains("CT_ALIGN(16)", StringComparison.Ordinal),
                "Public newtype, inline-array, or alignment ABI declarations were missing or out of order.");
        });

        suite.Run("draft 0.19 no recursion analysis", () =>
        {
            const string recursive = "public static class Program { [NoRecursion] private static void A() { B(); } private static void B() { A(); } [EntryPoint] public static void Main() { A(); } }";
            var attributed = Compile(recursive);
            Assert(attributed.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "NoRecursion did not reject a mutual cycle.");

            const string projectRecursive = "public static class Program { private static void A() { A(); } [EntryPoint] public static void Main() { A(); } }";
            var project = Compile(projectRecursive, new CompilationOptions(NoRecursion: true));
            Assert(project.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "Project-wide no-recursion did not reject a reachable cycle.");

            const string unknown = "public delegate void Work(); public static class Program { private static void Done() { } [NoRecursion] private static void Run(Work work) { work(); } [EntryPoint] public static void Main() { Work work = Done; Run(work); } }";
            Assert(Compile(unknown).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2206"), "NoRecursion accepted open delegate dispatch.");
        });
    }
}
