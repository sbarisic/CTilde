using System.Globalization;
using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    private const string SectionFixture = """
        using System;

        public class SectionGeneric<T> where T : unmanaged
        {
            [Section(".ctgdata")] public static int Count = 1;
            [Section(".ctgcode")] public static int Read() { return Count; }
        }

        public static class Program
        {
            [Section(".ctdata")] private static int placed = 40;
            [Section(".ctdata")] private static int secondPlaced;
            [Section(".ctreadonly")] private static readonly int readOnly = 1;
            [Section(".ctvolatile")] private static volatile int volatileValue = 1;

            [Section(".ctcode")]
            private static int Add(int value) { return value + placed + readOnly + volatileValue; }

            [Section(".ctcode")]
            private static int SecondCode() { return secondPlaced; }

            [Section(".ctmethod")]
            private static T Identity<T>(T value) where T : unmanaged { return value; }

            [Section(".ctexport")]
            [Export("ct_section_export")]
            public static int Exported() { return Add(0); }

            [Section(".ctentry")]
            [EntryPoint]
            public static void Main()
            {
                Console.WriteLine(Exported() == 42 && SectionGeneric<int>.Read() == 1 && Identity<int>(SecondCode()) == 0);
            }

            [Section(".ctdead")]
            private static void Dead() { }
        }
        """;

    public static void RegisterPart15(ConformanceSuite suite)
    {
        suite.Run("draft 0.17 section syntax targets and diagnostics", () =>
        {
            var diagnostics = Compile(SectionFixture).GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));

            var longName = "." + new string('a', 128);
            var malformed = $$"""
                public static class BadNames
                {
                    [Section] public static void Missing() { }
                    [Section(1)] public static void NotString() { }
                    [Section(".one", ".two")] public static void TooMany() { }
                    [Section("")] public static void Empty() { }
                    [Section("1bad")] public static void BadStart() { }
                    [Section(".bad/name")] public static void BadCharacter() { }
                    [Section(".däta")] public static void NonAscii() { }
                    [Section("{{longName}}")] public static void TooLong() { }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var malformedDiagnostics = Compile(malformed).GetDiagnostics();
            Assert(malformedDiagnostics.Count(diagnostic => diagnostic.Code == "CT1286") == 8,
                "Malformed section names did not produce CT1286 consistently.\n" + string.Join("\n", malformedDiagnostics));

            const string invalidTargets = """
                public abstract class InvalidMembers
                {
                    [Section(".field")] public int InstanceField;
                    [Section(".const")] public const int Constant = 1;
                    [Section(".managed")] public static string Managed;
                    [Section(".instance")] public void Instance() { }
                    [Section(".abstract")] public abstract void Abstract();
                    [Section(".ctor")] public InvalidMembers() { }
                    [Section(".property")] public static int Property { get; set; }
                }
                public struct Number
                {
                    public int Value;
                    [Section(".operator")] public static Number operator +(Number value) { return value; }
                }
                public class OpenStorage<T>
                {
                    [Section(".incomplete")] public static T Value;
                }
                public static class MoreInvalid
                {
                    [Section(".extern")][Extern("native_call")] public static void External();
                    [Section(".duplicate")][Section(".other")] public static void Duplicate() { }
                    [Section(".mixed")] public static int Data;
                    [Section(".mixed")] public static void Code() { }
                    [EntryPoint] public static void Main() { Code(); }
                }
                """;
            var targetDiagnostics = Compile(invalidTargets).GetDiagnostics();
            Assert(targetDiagnostics.Count(diagnostic => diagnostic.Code == "CT1287") >= 10,
                "Forbidden section targets did not produce CT1287.\n" + string.Join("\n", targetDiagnostics));
            Assert(targetDiagnostics.Any(diagnostic => diagnostic.Code == "CT1214"), "Duplicate Section attributes were accepted.");
            Assert(targetDiagnostics.Any(diagnostic => diagnostic.Code == "CT4107"), "A section shared by code and data was accepted.");
        });

        suite.Run("draft 0.17 section unity modular and header emission", () =>
        {
            var compilation = Compile(SectionFixture);
            var generated = Emit(SectionFixture);
            var second = Emit(SectionFixture);
            Assert(generated == second, "Repeated section emission was not deterministic.");

            var codeMacro = NativeSection.MacroName(NativeSectionKind.Code, ".ctcode");
            var dataMacro = NativeSection.MacroName(NativeSectionKind.Data, ".ctdata");
            var exportMacro = NativeSection.MacroName(NativeSectionKind.Code, ".ctexport");
            var entryMacro = NativeSection.MacroName(NativeSectionKind.Code, ".ctentry");
            var deadMacro = NativeSection.MacroName(NativeSectionKind.Code, ".ctdead");
            Assert(generated.Contains($"#define {codeMacro} __declspec(code_seg(\".ctcode\"))", StringComparison.Ordinal) &&
                generated.Contains($"#define {codeMacro} __attribute__((section(\".ctcode\")))", StringComparison.Ordinal), "The code-section portability macro was incomplete.");
            Assert(generated.Contains("#pragma section(\".ctdata\", read, write)", StringComparison.Ordinal) &&
                generated.Contains($"#define {dataMacro} __declspec(allocate(\".ctdata\"))", StringComparison.Ordinal), "The MSVC writable data-section declaration was incomplete.");
            Assert(generated.Contains("#pragma section(\".ctreadonly\", read, write)", StringComparison.Ordinal) &&
                generated.Contains("#pragma section(\".ctvolatile\", read, write)", StringComparison.Ordinal), "Readonly or volatile static storage was not declared writable for module initialization.");
            Assert(generated.Contains($"#define {dataMacro} __attribute__((section(\".ctdata\")))", StringComparison.Ordinal), "The GNU data-section macro was incomplete.");
            Assert(generated.Contains($"static CT_UNUSED {dataMacro} ", StringComparison.Ordinal),
                "The static field definition omitted its section annotation.\n" + string.Join("\n", Normalize(generated).Split('\n').Where(line => line.Contains("CT_SECTION_DATA", StringComparison.Ordinal))));
            Assert(Count(generated, codeMacro) >= 3, "The internal method prototype and definition were not both sectioned.");
            Assert(Count(generated, exportMacro) >= 4, "The exported wrapper and implementation were not both sectioned.");
            Assert(Count(generated, entryMacro) >= 3 && !generated.Contains($"{entryMacro} int main(void)", StringComparison.Ordinal), "EntryPoint placement leaked onto generated hosted startup.");
            Assert(!generated.Contains(deadMacro, StringComparison.Ordinal), "Section made an unreachable method a reachability root.");
            Assert(generated.Contains(NativeSection.MacroName(NativeSectionKind.Code, ".ctgcode"), StringComparison.Ordinal) &&
                generated.Contains(NativeSection.MacroName(NativeSectionKind.Data, ".ctgdata"), StringComparison.Ordinal), "A closed generic specialization lost section metadata.");
            Assert(generated.Contains(NativeSection.MacroName(NativeSectionKind.Code, ".ctmethod"), StringComparison.Ordinal), "A closed generic method specialization lost section metadata.");

            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var internalHeader = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
            var runtimeSource = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource).Content;
            var namespaceSources = string.Join("\n", bundle.Artifacts.Where(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource).Select(artifact => artifact.Content));
            var entrySource = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.EntrySource).Content;
            Assert(internalHeader.Contains("CTILDE_INTERNAL_DRAFT_018_H", StringComparison.Ordinal), "The modular internal-header guard was not derived from Draft 0.18.");
            Assert(internalHeader.Contains(codeMacro, StringComparison.Ordinal), "The internal code prototype lost its section annotation.");
            Assert(!internalHeader.Split('\n').Any(line => line.Contains("extern", StringComparison.Ordinal) && line.Contains(dataMacro, StringComparison.Ordinal)), "An extern data declaration retained a definition-only placement annotation.");
            Assert(runtimeSource.Contains(dataMacro, StringComparison.Ordinal), "The modular data definition lost its section annotation.");
            Assert(namespaceSources.Contains(codeMacro, StringComparison.Ordinal), "A modular method definition lost its section annotation.");
            Assert(entrySource.Contains(exportMacro, StringComparison.Ordinal), "The modular export wrapper lost its section annotation.");

            using var header = new StringWriter(CultureInfo.InvariantCulture);
            Assert(compilation.EmitCHeader(header).Success, string.Join(Environment.NewLine, compilation.GetDiagnostics()));
            var headerText = header.ToString();
            Assert(headerText.Contains(exportMacro, StringComparison.Ordinal) && headerText.Contains("ct_section_export", StringComparison.Ordinal), "The public export prototype omitted its section annotation.");
            Assert(headerText.Contains($"#undef {exportMacro}", StringComparison.Ordinal), "The public header leaked its generated section macro.");
            using var unsectionedHeader = new StringWriter(CultureInfo.InvariantCulture);
            var unsectionedExport = SectionFixture.Replace("[Section(\".ctexport\")]", string.Empty, StringComparison.Ordinal);
            Assert(Compile(unsectionedExport).EmitCHeader(unsectionedHeader).Success, "The comparison export header could not be emitted.");
            Assert(headerText.Split('\n')[0] != unsectionedHeader.ToString().Split('\n')[0], "The export-header signature hash ignored the section name.");

            using var mapWriter = new StringWriter(CultureInfo.InvariantCulture);
            Assert(compilation.EmitSymbolMap(mapWriter).Success, "Section symbol-map emission failed.");
            using var map = JsonDocument.Parse(mapWriter.ToString());
            Assert(map.RootElement.GetProperty("generator").GetString() == "C~ draft 0.18", "The symbol map did not advance to Draft 0.18.");

            var alpha = SyntaxTree.ParseText("namespace Alpha; public static class A { [Section(\".zdata\")] public static int Value = 1; [Section(\".zcode\")] public static int Read() { return Value; } }", "alpha-section.ct");
            var beta = SyntaxTree.ParseText("using Alpha; public static class P { [Section(\".acode\")][EntryPoint] public static void Main() { A.Read(); } }", "beta-section.ct");
            var ordered = Compilation.Create([alpha, beta]);
            var shuffled = Compilation.Create([beta, alpha]);
            Assert(Emit([alpha, beta]) == Emit([beta, alpha]), "Source order changed unity section output.");
            var orderedBundle = ordered.EmitCBundle();
            var shuffledBundle = shuffled.EmitCBundle();
            Assert(orderedBundle.Success && shuffledBundle.Success && orderedBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))
                .SequenceEqual(shuffledBundle.Artifacts.Select(artifact => (artifact.RelativePath, artifact.Content))), "Source order changed modular section output.");
        });

        suite.Run("draft 0.17 section runtime behavior", () =>
        {
            var result = CompileAndRun(SectionFixture);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

        suite.Run("draft 0.17 section native object placement", () =>
        {
            const string source = """
                public static class Program
                {
                    [Section(".ctdata")] private static int placed = 40;
                    [Section(".ctcode")] private static int Add(int value) { return value + placed; }
                    [Section(".ctexp")][Export("ct_section_export")] public static int Exported() { return Add(2); }
                    [EntryPoint] public static void Main() { Exported(); }
                }
                """;
            var compilation = Compile(source);
            using var symbolWriter = new StringWriter(CultureInfo.InvariantCulture);
            Assert(compilation.EmitSymbolMap(symbolWriter).Success, "Could not emit the object-inspection symbol map.");
            using var map = JsonDocument.Parse(symbolWriter.ToString());
            var symbols = map.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            var field = FindSymbol(symbols, "field", "Program::placed");
            var add = FindSymbol(symbols, "method", "Program::Add");
            var implementation = FindSymbol(symbols, "method", "Program::Exported");
            var inspection = CompileAndInspectObject(source);
            AssertObjectSection(inspection, ".ctdata", field);
            AssertObjectSection(inspection, ".ctcode", add);
            AssertObjectSection(inspection, ".ctexp", implementation);
            AssertObjectSection(inspection, ".ctexp", "ct_section_export");
        });
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string FindSymbol(IEnumerable<JsonElement> symbols, string kind, string identityFragment) =>
        symbols.Single(symbol => symbol.GetProperty("kind").GetString() == kind &&
            symbol.GetProperty("identity").GetString()!.Contains(identityFragment, StringComparison.Ordinal))
            .GetProperty("name").GetString()!;

    private static void AssertObjectSection(NativeObjectInspection inspection, string section, string symbol)
    {
        var output = Normalize(inspection.Output);
        if (inspection.Toolchain == "gnu")
        {
            var symbolLine = output.Split('\n').SingleOrDefault(line => line.Contains(symbol, StringComparison.Ordinal));
            Assert(symbolLine is not null && symbolLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(section, StringComparer.Ordinal),
                $"Native symbol '{symbol}' was not placed in '{section}'.\n{output}");
            return;
        }

        var sectionMatch = System.Text.RegularExpressions.Regex.Match(output,
            $@"SECTION HEADER #(?<number>[0-9A-F]+)\s+{System.Text.RegularExpressions.Regex.Escape(section)}\s+name",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert(sectionMatch.Success, $"Native object omitted section '{section}'.\n{output}");
        var sectionNumber = sectionMatch.Groups["number"].Value.TrimStart('0');
        if (sectionNumber.Length == 0)
            sectionNumber = "0";
        var msvcSymbolLine = output.Split('\n').SingleOrDefault(line => line.Contains("| " + symbol, StringComparison.Ordinal));
        Assert(msvcSymbolLine is not null && msvcSymbolLine.Contains("SECT" + sectionNumber, StringComparison.OrdinalIgnoreCase),
            $"Native symbol '{symbol}' was not placed in '{section}'.\n{output}");
    }
}
