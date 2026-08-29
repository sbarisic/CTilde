using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart11(ConformanceSuite suite)
    {
        suite.Run("draft 0.13 inline assembly syntax and emission", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        int value = 41;
                        int result;
                        int accumulator = 1;
                        [NoAlloc]
                        asm (in value as source, out("&r") result, ref accumulator, clobber("eax", "cc", "memory")) {
                            movl source, %eax
                            addl %eax, accumulator
                            movl %eax, result
                            push {r0-r3}
                            .ascii "$literal %eax value }"
                        }
                    }
                }
                """;
            var tree = SyntaxTree.ParseText(source, "asm.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            Assert(tree.ToFullString() == source, "Inline assembly syntax did not round-trip exactly.");
            var assembly = Descendants(tree.Root).OfType<InlineAssemblyStatementSyntax>().Single();
            Assert(assembly.Operands.Length == 3 && assembly.Clobbers.SequenceEqual(["eax", "cc", "memory"]), "Inline assembly operands or clobbers were not parsed.");
            Assert(assembly.References.Select(reference => reference.Name).SequenceEqual(["source", "accumulator", "result"]), "Inline assembly references were not isolated from registers and quoted text.");

            const string simple = "public static class Program { [EntryPoint] public static unsafe void Main() { asm { nop } } }";
            var simpleTree = SyntaxTree.ParseText(simple, "asm-simple.ct");
            Assert(simpleTree.Diagnostics.IsEmpty && simpleTree.ToFullString() == simple, "Operand-free inline assembly did not round-trip exactly.");
            Assert(Descendants(simpleTree.Root).OfType<InlineAssemblyStatementSyntax>().Single().Body == " nop ", "Operand-free inline assembly did not retain its raw body.");

            const string malformed = "public static class Program { [EntryPoint] public static unsafe void Main() { int value = 1; asm (in value { nop } } }";
            var malformedTree = SyntaxTree.ParseText(malformed, "asm-malformed.ct");
            Assert(!malformedTree.Diagnostics.IsEmpty, "Malformed inline assembly did not report a syntax diagnostic.");
            Assert(Descendants(malformedTree.Root).OfType<InlineAssemblyStatementSyntax>().Any(), "Malformed inline assembly did not recover as an assembly statement.");

            var generated = Emit(source);
            Assert(generated == Emit(source), "Repeated inline assembly emission was not byte-identical.");
            Assert(generated.Contains("__asm__ volatile (", StringComparison.Ordinal), "GNU volatile asm was not emitted.");
            Assert(generated.Contains("%[ct_asm_0]", StringComparison.Ordinal), "Input operand was not substituted.");
            Assert(generated.Contains("%%eax", StringComparison.Ordinal), "Raw GNU register percent signs were not escaped.");
            Assert(generated.Contains("[ct_asm_1] \"=&r\"", StringComparison.Ordinal), "Early-clobber output constraint was not emitted.");
            Assert(generated.Contains("[ct_asm_2] \"+r\"", StringComparison.Ordinal), "Read/write constraint was not emitted.");
            Assert(generated.Contains(": \"eax\", \"cc\", \"memory\");", StringComparison.Ordinal), "Clobbers were not emitted in source order.");

            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var bound = (BoundProgram?)typeof(Compilation).GetField("_boundProgram", flags)!.GetValue(compilation);
            var inlineIr = new TypedIrLowerer(bound!).Lower().Functions.SelectMany(function => function.Blocks)
                .SelectMany(block => block.Instructions).OfType<IrInlineAssembly>().Single();
            Assert(inlineIr.Operands.Length == 3, "Typed IR did not retain the inline assembly operands.");
        });

        suite.Run("draft 0.13 inline assembly flow and NoAlloc", () =>
        {
            const string trusted = """
                public static class Program
                {
                    [NoAlloc]
                    public static unsafe int Set()
                    {
                        int result;
                        [NoAlloc] asm (out result) { movl $42, result }
                        return result;
                    }
                    [EntryPoint] public static void Main() { }
                }
                """;
            Assert(!Compile(trusted).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Trusted asm did not assign its out operand or satisfy NoAlloc.");

            var untrusted = trusted.Replace("[NoAlloc] asm", "asm", StringComparison.Ordinal);
            Assert(Compile(untrusted).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2155"), "Untrusted asm was accepted in a NoAlloc method.");
            var safe = trusted.Replace("public static unsafe int Set()", "public static int Set()", StringComparison.Ordinal);
            Assert(Compile(safe).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2190"), "Asm outside an unsafe context was accepted.");
            var readBeforeAssigned = trusted.Replace("out result", "in result", StringComparison.Ordinal);
            Assert(Compile(readBeforeAssigned).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "An unassigned asm input was accepted.");
            var refBeforeAssigned = trusted.Replace("out result", "ref result", StringComparison.Ordinal);
            Assert(Compile(refBeforeAssigned).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "An unassigned asm ref operand was accepted.");
        });

        suite.Run("draft 0.13 inline assembly operand diagnostics", () =>
        {
            const string invalid = """
                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        readonly int value = 1;
                        string text = "bad";
                        float number = 1.0;
                        [NoAlloc] asm (out value, in text, in number, in missing, in value as same, in value as same, clobber("cc", "cc")) { same }
                    }
                }
                """;
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2192"), "A duplicate asm alias was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2194"), "A variable was bound to multiple asm operands.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2195"), "A managed asm operand was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2196"), "A float asm operand without an explicit constraint was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2198"), "A readonly asm output was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2199"), "A duplicate asm clobber was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1107"), "An unresolved asm operand was accepted.");
        });

        suite.Run("draft 0.13 inline assembly language services", () =>
        {
            const string source = "public static class Program { [EntryPoint] public static unsafe void Main() { int value = 1; [NoAlloc] asm (in value as source) { addl source, source } } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "asm-service.ct")]);
            var reference = source.IndexOf("source, source", StringComparison.Ordinal);
            var tokens = service.GetSemanticTokens("asm-service.ct");
            Assert(tokens.Any(token => token.Span.Start == reference && token.Kind == LanguageSemanticTokenKind.Variable), "An asm operand reference did not receive a semantic token.");
            Assert(service.GetHover("asm-service.ct", reference)?.Contents.Contains("value", StringComparison.Ordinal) == true, "Asm operand hover did not resolve the C~ variable.");
            Assert(service.GetDefinition("asm-service.ct", reference)?.Span.Start == source.IndexOf("value =", StringComparison.Ordinal), "Asm operand definition did not navigate to the local declaration.");
        });

        suite.Run("draft 0.13 inline assembly GNU runtime", () =>
        {
            var compiler = Environment.GetEnvironmentVariable("CTILDE_CC") ?? string.Empty;
            if (!compiler.Contains("gcc", StringComparison.OrdinalIgnoreCase) && !compiler.Contains("clang", StringComparison.OrdinalIgnoreCase))
                return;
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "InlineAssemblyWindows", "Program.ct"));
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "add: 42\nsubtract: 42\nmultiply: 42\nincrement: 42\nnegate: 42\nrotate-left: 3\n", result.StandardOutput);
        });
    }

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.ChildNodesAndTokens().Where(item => item.IsNode).Select(item => item.Node!))
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
