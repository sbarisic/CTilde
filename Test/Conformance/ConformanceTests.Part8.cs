using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart8(ConformanceSuite suite)
    {
        suite.Run("draft 0.11 operator syntax and declarations", () =>
        {
            const string source = "public struct V { public int X; public V(int x) { X = x; } [NoAlloc] public static V operator +(V left, V right) { return new V(left.X + right.X); } public static V operator -(V value) { return new V(-value.X); } } public static class P { [EntryPoint] public static void Main() { } }";
            var tree = SyntaxTree.ParseText(source, "operators.ct");
            Assert(tree.Tokens.Any(token => token.Kind == SyntaxKind.OperatorKeyword), "operator was not classified as a keyword.");
            Assert(tree.Root.Types[0].Members.OfType<OperatorDeclarationSyntax>().Count() == 2, "Operator declarations were not represented in syntax.");
            Assert(tree.ToFullString() == source, "Operator syntax did not round-trip exactly.");
            var compilation = Compile(source);
            Assert(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, compilation.GetDiagnostics()));

            const string invalid = "public static class S { public static int operator +(int value); } public struct V { public int X; private V operator *(ref V left, V right) { return left; } public static void operator /(V left, V right) { } } public static class P { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalid).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1269") >= 3, "Invalid operator declarations did not produce CT1269.");

            string[] invalidContracts =
            [
                "public struct V { public static V operator *(V value) { return value; } }",
                "public struct V { public V operator +(V value) { return value; } }",
                "public struct V { internal static V operator +(V value) { return value; } }",
                "public struct V { public static V operator +(int value) { return new V(); } }",
                "public struct V { public static V operator +(V value); }",
                "public struct V { public virtual static V operator +(V value) { return value; } }",
                "public struct V { [Extern(\"bad\")] public static V operator +(V value) { return value; } }",
                "public struct V { public static V operator +([Retained] V value) { return value; } }",
            ];
            foreach (var declaration in invalidContracts)
            {
                var diagnostics = Compile(declaration + " public static class P { [EntryPoint] public static void Main() { } }").GetDiagnostics();
                Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1269"), $"Invalid operator contract was accepted: {declaration}");
            }

            const string malformed = "public struct V { public static V operator %(V left, V right) { return left; } }";
            var malformedTree = SyntaxTree.ParseText(malformed, "malformed-operator.ct");
            Assert(malformedTree.Diagnostics.Any(diagnostic => diagnostic.Code == "CT0108"), "An unsupported operator token did not recover with a syntax diagnostic.");
            Assert(malformedTree.ToFullString() == malformed, "Malformed operator syntax did not round-trip exactly.");
        });

        suite.Run("draft 0.11 operator lowering and mangling", () =>
        {
            const string source = """
                using System;
                public struct V
                {
                    public int X;
                    public V(int x) { X = x; }
                    [NoAlloc] public static V operator +(V left, V right) { return new V(left.X + right.X); }
                    [NoAlloc] public static V operator -(V left, V right) { return new V(left.X - right.X); }
                    [NoAlloc] public static V operator -(V value) { return new V(-value.X); }
                    [NoAlloc] public static V operator +(V value) { return value; }
                    [NoAlloc] public static V operator *(V value, int scale) { return new V(value.X * scale); }
                    [NoAlloc] public static V operator *(int scale, V value) { return value * scale; }
                    [NoAlloc] public static V operator /(V value, int scale) { return new V(value.X / scale); }
                }
                public static class Program
                {
                    [NoAlloc] private static V Calculate(V value)
                    {
                        value += new V(2);
                        value *= 3;
                        value -= new V(6);
                        value /= 2;
                        return +(-value);
                    }
                    [EntryPoint] public static void Main()
                    {
                        V result = Calculate(new V(10));
                        Console.WriteLine(result.X);
                        Console.WriteLine((2 * new V(4)).X);
                    }
                }
                """;
            var generated = Emit(source);
            Assert(generated.Contains("ct_o_", StringComparison.Ordinal), "Operator functions did not use compact operator mangling.");
            Assert(generated.StartsWith($"/* Generated by C~ draft {CompilerContract.DraftVersion}", StringComparison.Ordinal), "Operator output did not identify the current source language draft.");
            Assert(generated == Emit(source), "Operator emission was not deterministic.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "-15\n8\n", result.StandardOutput);

            var builtIn = Emit("public static class Program { [EntryPoint] public static void Main() { int value = 1 + 2; } }");
            Assert(builtIn.StartsWith($"/* Generated by C~ draft {CompilerContract.DraftVersion}", StringComparison.Ordinal), "A program without operator declarations did not identify the current draft.");
            Assert(!builtIn.Contains("ct_o_", StringComparison.Ordinal), "Built-in arithmetic emitted operator machinery.");
        });

        suite.Run("draft 0.11 operator resolution diagnostics", () =>
        {
            const string missing = "public struct V { public int X; } public static class P { [EntryPoint] public static void Main() { V x = new V(); V y = x + x; } }";
            Assert(Compile(missing).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2167"), "A missing user-defined operator did not produce CT2167.");

            const string duplicate = "public struct V { public int X; public static V operator +(V a, V b) { return a; } public static V operator +(V a, V b) { return b; } } public static class P { [EntryPoint] public static void Main() { } }";
            Assert(Compile(duplicate).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1105"), "A duplicate operator signature did not produce CT1105.");

            const string ambiguous = "public class A { public static int operator +(A left, B right) { return 1; } } public class B { public static int operator +(A left, B right) { return 2; } } public static class P { [EntryPoint] public static void Main() { A a = new A(); B b = new B(); int value = a + b; } }";
            Assert(Compile(ambiguous).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2168"), "Ambiguous operators did not produce CT2168.");

            const string inheritance = "public class Base { public static int operator +(Base left, int right) { return right; } } public class Derived : Base { } public static class P { [EntryPoint] public static void Main() { Derived value = new Derived(); int result = value + (byte)2; } }";
            Assert(!Compile(inheritance).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, Compile(inheritance).GetDiagnostics()));

            const string asymmetric = "public struct V { public int X; public static V operator *(V value, int scale) { return value; } } public static class P { [EntryPoint] public static void Main() { V value = new V(); V result = 2 * value; } }";
            Assert(Compile(asymmetric).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2167"), "Scalar symmetry was inferred without a matching declaration.");
        });

        suite.Run("draft 0.11 operator evaluation and ARC", () =>
        {
            const string source = """
                using System;
                public struct V
                {
                    public int X;
                    public V(int x) { X = x; }
                    public static V operator +(V left, V right)
                    {
                        Console.WriteLine("operator");
                        return new V(left.X + right.X);
                    }
                }
                public class Holder
                {
                    public V Value { get; set; }
                    public Holder(V value) { Value = value; }
                }
                public class Box
                {
                    public string Text;
                    public Box(string text) { Text = text; }
                    public static Box operator +(Box left, Box right)
                    {
                        if (left == null)
                            return right;
                        if (right == null)
                            throw new Exception("operator failure");
                        return new Box(left.Text + right.Text);
                    }
                }
                public struct Wrap
                {
                    public Box Value;
                    public Wrap(Box value) { Value = value; }
                    public static Wrap operator +(Wrap left, Wrap right) { return new Wrap(left.Value + right.Value); }
                }
                public static class Program
                {
                    private static Holder Current = new Holder(new V(10));
                    private static Box CurrentBox = new Box("old");
                    private static int ReceiverCalls;
                    private static int IndexCalls;
                    private static Holder Receiver() { ReceiverCalls++; Console.WriteLine("receiver"); return Current; }
                    private static int Index() { IndexCalls++; Console.WriteLine("index"); return 0; }
                    private static V Right() { Console.WriteLine("right"); return new V(2); }
                    private static Box ReplaceBox() { CurrentBox = new Box("new"); return CurrentBox; }
                    [EntryPoint] public static void Main()
                    {
                        Receiver().Value += Right();
                        Console.WriteLine(Current.Value.X);
                        V[] values = new V[1];
                        values[0] = new V(3);
                        values[Index()] += Right();
                        Console.WriteLine(values[0].X);
                        Console.WriteLine(ReceiverCalls);
                        Console.WriteLine(IndexCalls);
                        Box right = new Box("ok");
                        Box result = null + right;
                        Console.WriteLine(result.Text);
                        Box keptAlive = CurrentBox + ReplaceBox();
                        Console.WriteLine(keptAlive.Text);
                        Wrap wrapped = new Wrap(new Box("a")) + new Wrap(new Box("b"));
                        Console.WriteLine(wrapped.Value.Text);
                        try { Box failure = right + null; }
                        catch (Exception error) { Console.WriteLine(error.Message); }
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "receiver\nright\noperator\n12\nindex\nright\noperator\n5\n1\n1\nok\noldnew\nab\noperator failure\n", result.StandardOutput);
        });

        suite.Run("draft 0.11 operator language services", () =>
        {
            const string source = """
                public struct V
                {
                    public int X;
                    public static V operator +(V left, V right) { return left; }

                }
                public static class P
                {
                    [EntryPoint] public static void Main() { V left = new V(); V right = new V(); V result = left + right; }
                }
                """;
            const string path = "operator-services.ct";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, path)]);
            Assert(!service.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, service.Diagnostics));
            var typeBodyPosition = source.IndexOf("public static V operator", StringComparison.Ordinal) - 1;
            Assert(service.GetCompletions(path, typeBodyPosition).Any(completion => completion.Label == "operator"), "Type-member completion omitted operator.");
            var usePosition = source.LastIndexOf("+", StringComparison.Ordinal);
            Assert(service.GetHover(path, usePosition)?.Contents.Contains("operator +", StringComparison.Ordinal) == true, "Operator hover did not expose the selected overload.");
            var definition = service.GetDefinition(path, usePosition);
            Assert(definition is not null && source.Substring(definition.Span.Start, definition.Span.Length) == "+", "Operator go-to-definition did not select the declaration token.");
            Assert(service.GetDocumentSymbols(path).SelectMany(symbol => symbol.Children).Any(symbol => symbol.Name == "operator +"), "Document symbols omitted the operator declaration.");
            Assert(service.GetWorkspaceSymbols("operator +").Any(symbol => symbol.Name == "operator +"), "Workspace symbols omitted the operator declaration.");
            var memberSource = source.Replace("V result = left + right;", "V result = left + right; V.", StringComparison.Ordinal);
            var memberService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(memberSource, path)]);
            var memberPosition = memberSource.LastIndexOf("V.", StringComparison.Ordinal) + 2;
            Assert(!memberService.GetCompletions(path, memberPosition).Any(completion => completion.Label.Contains("operator", StringComparison.Ordinal)), "Ordinary member completion exposed operator symbols.");
        });
    }
}
