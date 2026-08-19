using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart6(ConformanceSuite suite)
    {
        suite.Run("XML documentation syntax and diagnostics", () =>
        {
            const string source = """
                /// <summary>Provides documented arithmetic.</summary>
                public static class Calculator
                {
                    /// <summary>Computes a value.</summary>
                    /// <param name="left">Left input.</param>
                    /// <param name="right">Right input.</param>
                    /// <returns>A deterministic result.</returns>
                    public static int Add(int left, int right) { return left + right; }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "docs.ct");
            Assert(tree.ToFullString() == source, "Documentation trivia did not round-trip exactly.");
            Assert(tree.Tokens.Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxTriviaKind.DocumentationComment)), "Documentation comments were not classified as documentation trivia.");

            var service = LanguageServiceSnapshot.Create([tree]);
            Assert(!service.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("CT50", StringComparison.Ordinal)), string.Join(Environment.NewLine, service.Diagnostics));
            var addUse = source.LastIndexOf("Add(int", StringComparison.Ordinal) + 1;
            var hover = service.GetHover("docs.ct", addUse) ?? throw new InvalidOperationException("Documented hover was missing.");
            var hoverDocumentation = hover.Sections.Single().Documentation ?? throw new InvalidOperationException("Hover documentation was missing.");
            Assert(hoverDocumentation.Summary == "Computes a value.", "Hover did not expose the method summary.");
            Assert(hoverDocumentation.Parameters[1].Text == "Right input.", "Hover did not expose parameter documentation.");

            const string completionSource = source + "\npublic static class Program { [EntryPoint] public static void Main() { Calculator. } }";
            var completionService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(completionSource, "completion-docs.ct")]);
            var completionPosition = completionSource.LastIndexOf("Calculator.", StringComparison.Ordinal) + "Calculator.".Length;
            var completion = completionService.GetCompletions("completion-docs.ct", completionPosition).Single(item => item.Label == "Add");
            var documentationId = completion.DocumentationId ?? throw new InvalidOperationException("Documented completion did not carry a stable documentation ID.");
            Assert(completionService.GetDocumentation(documentationId)?.Returns == "A deterministic result.", "Lazy completion documentation did not resolve.");

            const string intrinsicSource = "public static class Program { [EntryPoint] public static unsafe void Main() { NativeBuffer<byte> buffer = stackalloc byte[1]; buffer. } }";
            var intrinsicService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(intrinsicSource, "intrinsic-docs.ct")]);
            var intrinsicPosition = intrinsicSource.IndexOf("buffer. }", StringComparison.Ordinal) + "buffer.".Length;
            var lengthCompletion = intrinsicService.GetCompletions("intrinsic-docs.ct", intrinsicPosition).Single(item => item.Label == "Length");
            Assert(lengthCompletion.DocumentationId is not null && intrinsicService.GetDocumentation(lengthCompletion.DocumentationId)?.Summary.Contains("view length", StringComparison.Ordinal) == true, "Synthetic intrinsic completion documentation was unavailable.");

            const string invalid = """
                /// <summary>orphan</summary>

                // breaks attachment
                /// <summary><unknown/></summary>
                public class Broken
                {
                    /// <param name="missing">bad</param>
                    /// <summary>one</summary><summary>two</summary>
                    public void Run(int value) { }
                }
                """;
            var warnings = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(invalid, "invalid-docs.ct")]).Diagnostics;
            Assert(warnings.Any(diagnostic => diagnostic.Code == "CT5006" && diagnostic.Severity == DiagnosticSeverity.Warning), "Orphan documentation did not produce CT5006 warning.");
            Assert(warnings.Any(diagnostic => diagnostic.Code == "CT5001"), "Unsupported XML did not produce CT5001 warning.");
            Assert(warnings.Any(diagnostic => diagnostic.Code == "CT5002"), "Duplicate documentation section did not produce CT5002 warning.");
            Assert(warnings.Any(diagnostic => diagnostic.Code == "CT5003"), "Unknown parameter documentation did not produce CT5003 warning.");

            const string malformed = "/// <summary>unterminated\npublic class Malformed { }\n/// <summary><see cref=\"Missing\"/></summary>\npublic class UnknownReference { }\n/// <inheritdoc/>\npublic struct InvalidInheritance { }";
            var malformedWarnings = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(malformed, "malformed-docs.ct")]).Diagnostics;
            Assert(malformedWarnings.Any(diagnostic => diagnostic.Code == "CT5000"), "Malformed XML did not produce CT5000 warning.");
            Assert(malformedWarnings.Any(diagnostic => diagnostic.Code == "CT5004"), "Unresolved cref did not produce CT5004 warning.");
            Assert(malformedWarnings.Any(diagnostic => diagnostic.Code == "CT5005"), "Invalid inheritdoc did not produce CT5005 warning.");

            const string entity = "/// <!DOCTYPE doc [<!ENTITY unsafe SYSTEM \"file:///never-read\">]><summary>&unsafe;</summary>\npublic class Entity { }";
            var entityWarnings = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(entity, "entity-docs.ct")]).Diagnostics;
            Assert(entityWarnings.Any(diagnostic => diagnostic.Code == "CT5000"), "DTD or external entity input was not rejected as malformed XML documentation.");

            const string warningOnly = "/// <summary>orphan</summary>\n\npublic static class Program { [EntryPoint] public static void Main() { } }";
            var warningCompilation = Compile(warningOnly, path: "warning-only-docs.ct");
            using var generated = new StringWriter();
            var warningResult = warningCompilation.EmitC(generated);
            Assert(warningResult.Success && warningResult.Diagnostics.Any(diagnostic => diagnostic.Code == "CT5006"), "Documentation warnings incorrectly blocked C emission.");
        });

        suite.Run("XML documentation references and explicit inheritance", () =>
        {
            const string source = """
                using System;
                public class Base
                {
                    /// <summary>Reads through <see cref="Base.Read(int)"/> for <paramref name="value"/>.</summary>
                    /// <param name="value">The base input.</param>
                    /// <returns>The input.</returns>
                    /// <exception cref="System.Exception">When reading fails.</exception>
                    public virtual int Read(int value) { return value; }
                    public int Read(string value) { return value.Length; }
                }
                public class Derived : Base
                {
                    /// <inheritdoc/>
                    public override int Read(int renamed) { return renamed; }
                }
                public class MoreDerived : Derived
                {
                    /// <inheritdoc/>
                    public override int Read(int finalName) { return finalName; }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main() { MoreDerived value = new MoreDerived(); value.Read(1); }
                }
                """;
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "inherit-docs.ct")]);
            Assert(!service.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("CT50", StringComparison.Ordinal)), string.Join(Environment.NewLine, service.Diagnostics));
            var callPosition = source.LastIndexOf("Read(1)", StringComparison.Ordinal) + 1;
            var hover = service.GetHover("inherit-docs.ct", callPosition);
            var documentation = hover?.Sections.FirstOrDefault(section => section.Signature.Contains("MoreDerived.Read", StringComparison.Ordinal))?.Documentation ?? throw new InvalidOperationException("Inherited hover documentation was missing.");
            Assert(documentation.Summary.Contains("`Base.Read(int)`", StringComparison.Ordinal), "Overload-specific cref was not resolved and rendered as inline code.");
            Assert(documentation.Parameters.Single().Name == "finalName" && documentation.Parameters.Single().Text == "The base input.", "Inherited parameter documentation was not remapped by ordinal through an override chain.");
            Assert(documentation.Exceptions.Single().TypeName == "System.Exception", "Exception documentation was not resolved.");

            var signature = service.GetSignatureHelp("inherit-docs.ct", source.LastIndexOf("Read(1)", StringComparison.Ordinal) + "Read(".Length);
            Assert(signature?.Signatures.FirstOrDefault(item => item.Label.Contains("MoreDerived.Read", StringComparison.Ordinal))?.Parameters.Single().Documentation == "The base input.", "Signature help did not include active parameter documentation.");
        });
    }
}
