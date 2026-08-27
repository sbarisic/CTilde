using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart20(ConformanceSuite suite)
    {
        suite.Run("draft 0.22 direct and transitive effect contracts", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Threading;

                public static class Native
                {
                    [Extern("trusted_native")] [NoThrow] [NoBlock] [NoRuntime]
                    public static unsafe void Trusted();
                    [Extern("unknown_native")]
                    public static void Unknown();
                }

                public static class Effects
                {
                    [NoThrow] static int SafeDivision(int value) { return value / 2; }
                    [NoThrow] static int UnsafeDivision(int value, int divisor) { return value / divisor; }
                    static int[] Allocate() { return new int[1]; }
                    [NoThrow] static int[] TransitiveThrow() { return Allocate(); }

                    [NoBlock] static void AcceptedNonBlocking(Mutex mutex) { mutex.TryEnter(); Thread.Yield(); Cpu.Pause(); }
                    [NoBlock] static void Blocking(Mutex mutex) { mutex.Enter(); Thread.Sleep(1u); Native.Unknown(); }

                    [NoRuntime] static unsafe void LowLevel()
                    {
                        Native.Trusted();
                        Cpu.MemoryBarrier();
                        Mmio.WriteRelaxed<uint>(0x1000u, 1u);
                    }
                    [NoRuntime] static string Managed(string value) { defer Native.Unknown(); return value; }

                    [NoThrow] [NoBlock] [NoRuntime]
                    static unsafe void TrustedAssembly() { [NoThrow] [NoBlock] [NoRuntime] asm { nop } }
                    [NoThrow] static unsafe void UnknownAssembly() { asm { nop } }

                    [EntryPoint] public static void Main() { }
                }
                """;
            var first = Compile(source).GetDiagnostics();
            var second = Compile(source).GetDiagnostics();
            Assert(first.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Location))
                .SequenceEqual(second.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Location))),
                "Effect diagnostics were not deterministic.");
            Assert(!first.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("SafeDivision", StringComparison.Ordinal)), string.Join(Environment.NewLine, first));
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("UnsafeDivision", StringComparison.Ordinal)), "A dynamic division was accepted under NoThrow.");
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("TransitiveThrow", StringComparison.Ordinal) && diagnostic.Message.Contains("Allocate", StringComparison.Ordinal)), "A transitive NoThrow violation did not include its call witness.");
            Assert(!first.Any(diagnostic => diagnostic.Code == "CT2213" && diagnostic.Message.Contains("AcceptedNonBlocking", StringComparison.Ordinal)), string.Join(Environment.NewLine, first));
            Assert(first.Count(diagnostic => diagnostic.Code == "CT2213" && diagnostic.Message.Contains("Blocking", StringComparison.Ordinal)) >= 3, "Blocking operations or the unknown native boundary were accepted.");
            Assert(!first.Any(diagnostic => diagnostic.Code == "CT2214" && diagnostic.Message.Contains("LowLevel", StringComparison.Ordinal)), string.Join(Environment.NewLine, first));
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2214" && diagnostic.Message.Contains("Managed", StringComparison.Ordinal)), "Managed values or defer were accepted under NoRuntime.");
            Assert(!first.Any(diagnostic => diagnostic.Code is "CT2212" or "CT2213" or "CT2214" && diagnostic.Message.Contains("TrustedAssembly", StringComparison.Ordinal)), string.Join(Environment.NewLine, first));
            Assert(first.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("UnknownAssembly", StringComparison.Ordinal)), "Uncontracted assembly was accepted under NoThrow.");
        });

        suite.Run("draft 0.22 callable contexts inheritance and malformed contracts", () =>
        {
            const string source = """
                public interface IReader { [NoThrow] int Read(); [NoBlock] int Value { get; } }
                public abstract class Base { [NoRuntime] public abstract unsafe void Poll(); }
                public sealed class Reader : Base, IReader
                {
                    public int Read() { return 1 / GetZero(); }
                    public int Value { get { while (false) { } return 1; } }
                    public override unsafe void Poll() { string value = "managed"; }
                    static int GetZero() { return 0; }
                }
                public struct Number
                {
                    public int Value;
                    [NoThrow] public Number(int value) { Value = value; }
                    [NoThrow] public static Number operator +(Number left, Number right) { return new Number(left.Value); }
                    public int Current { [NoThrow] get { return Value; } [NoBlock] set { Value = value; } }
                }
                public sealed class Bootstrap
                {
                    private int value;
                    [NoRuntime] public Bootstrap(int value) { this.value = value; }
                    [NoRuntime] public int Read() { return value; }
                }
                public static class Bad
                {
                    [NoThrow(1)] [NoBlock(1)] [NoRuntime(1)] static void Malformed() { }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("Reader.Read", StringComparison.Ordinal)), "An interface NoThrow contract was not inherited.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2214" && diagnostic.Message.Contains("Reader.Poll", StringComparison.Ordinal)), $"An abstract NoRuntime contract was not inherited.{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
            Assert(!diagnostics.Any(diagnostic => diagnostic.Code == "CT2214" && diagnostic.Message.Contains("Bootstrap", StringComparison.Ordinal)), string.Join(Environment.NewLine, diagnostics));
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1303") && diagnostics.Any(diagnostic => diagnostic.Code == "CT1304") && diagnostics.Any(diagnostic => diagnostic.Code == "CT1305"),
                "Malformed effect attributes did not use their dedicated diagnostics.");
            Assert(!diagnostics.Any(diagnostic => diagnostic.Code is "CT0103" or "CT1213" && diagnostic.Location.FilePath.EndsWith("test.ct", StringComparison.Ordinal)), string.Join(Environment.NewLine, diagnostics));
            Assert(!diagnostics.Any(diagnostic => diagnostic.Code == "CT1213" && diagnostic.Message.Contains("constructor", StringComparison.OrdinalIgnoreCase)), string.Join(Environment.NewLine, diagnostics));
        });

        suite.Run("draft 0.22 recursive generic and pruned effect inference", () =>
        {
            const string source = """
                using System.Runtime;
                using System.Threading;

                public static class Effects
                {
                    static int Recursive(int value)
                    {
                        if (value == 0) { int[] allocated = new int[1]; return allocated.Length; }
                        return Recursive(value - 1);
                    }

                    [NoThrow] static int RecursiveCaller() { return Recursive(2); }
                    static int Divide<const int Divisor>(int value) { return value / Divisor; }
                    [NoThrow] static int ClosedGeneric() { return Divide<2>(8); }

                    [NoBlock] static void Pruned()
                    {
                        static if (Target.Architecture == TargetArchitecture.X64) { Cpu.Pause(); }
                        else { Thread.Sleep(1u); }
                    }

                    [EntryPoint] public static void Main() { }
                }
                """;
            var diagnostics = Compile(source, new CompilationOptions(Architecture: CompilationArchitecture.X64)).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2212" &&
                diagnostic.Message.Contains("RecursiveCaller", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("Recursive", StringComparison.Ordinal)),
                "A recursively inferred allocation/throw effect did not reach its contracted caller.");
            Assert(!diagnostics.Any(diagnostic => diagnostic.Code == "CT2212" && diagnostic.Message.Contains("ClosedGeneric", StringComparison.Ordinal)),
                string.Join(Environment.NewLine, diagnostics));
            Assert(!diagnostics.Any(diagnostic => diagnostic.Code == "CT2213" && diagnostic.Message.Contains("Pruned", StringComparison.Ordinal)),
                "An inactive static-if branch contributed a blocking effect.");
        });

        suite.Run("draft 0.22 invalid effect targets", () =>
        {
            const string source = """
                [NoThrow] public class BadType { }
                public static class Bad
                {
                    [NoBlock] public static int Field;
                    [NoRuntime] public static string Managed(string value) { return value; }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1303"), "NoThrow used on a type did not report CT1303.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1304"), "NoBlock used on a field did not report CT1304.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1305"), "A managed NoRuntime signature did not report CT1305.");
        });

        suite.Run("draft 0.22 effect symbol maps and unchanged headers", () =>
        {
            const string source = """
                public static class Api
                {
                    [NoThrow] [NoBlock]
                    [Export("api_value")]
                    public static int Value() { return 7; }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var compilation = Compile(source);
            using var symbolWriter = new StringWriter();
            Assert(compilation.EmitSymbolMap(symbolWriter).Success, "Effect symbol-map emission failed.");
            using var document = JsonDocument.Parse(symbolWriter.ToString());
            var entry = document.RootElement.GetProperty("symbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("identity").GetString()!.Contains("method:Api::Value", StringComparison.Ordinal));
            Assert(entry.GetProperty("declaredEffects").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["NoBlock", "NoThrow"]),
                "Declared effects were not sorted in the symbol map.");
            Assert(entry.GetProperty("inferredEffects").GetArrayLength() == 0, "A pure method received inferred effects.");
            using var header = new StringWriter();
            Assert(compilation.EmitCHeader(header).Success && !header.ToString().Contains("NoThrow", StringComparison.Ordinal) && !header.ToString().Contains("NoBlock", StringComparison.Ordinal),
                "Analysis-only effects changed the public native header.");
        });

        suite.Run("draft 0.22 effect language services", () =>
        {
            const string source = "public static class Api { [NoRuntime] public static void Poll() { } public static void Use() { Pol } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "effects.ct")]);
            var completionPosition = source.IndexOf("Pol }", StringComparison.Ordinal) + 3;
            Assert(service.GetCompletions("effects.ct", completionPosition).Any(item => item.Label == "Poll" && item.Detail.Contains("[NoRuntime]", StringComparison.Ordinal)),
                "Effect contracts were missing from completion details.");
            Assert(service.GetCompletions("effects.ct", source.IndexOf("[NoRuntime]", StringComparison.Ordinal)).Any(item => item.Label == "NoThrow"),
                "Effect attributes were missing from completion.");
            var hoverPosition = source.IndexOf("NoRuntime", StringComparison.Ordinal) + 2;
            Assert(service.GetHover("effects.ct", hoverPosition)?.Contents.Contains("bootstrap-safe", StringComparison.Ordinal) == true,
                "Effect attribute hover was unavailable.");
        });
    }
}
