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

Run("full fidelity syntax round trip", () =>
{
    const string valid = "// lead\r\npublic static class Program { /* body */ [EntryPoint] public static void Main() { } }\r\n";
    var validTree = SyntaxTree.ParseText(valid, "valid.ct");
    Assert(validTree.ToFullString() == valid, "Valid syntax did not round-trip exactly.");
    Assert(validTree.Root.ToFullString() == valid, "The compilation-unit node did not round-trip exactly.");
    Assert(validTree.Root.ChildNodesAndTokens().Any(item => item.IsNode) && validTree.Root.ChildNodesAndTokens().Any(item => item.IsToken), "Node/token traversal did not expose both child forms.");
    Assert(validTree.Tokens.Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxTriviaKind.SingleLineComment)), "Single-line comment trivia was not retained.");
    Assert(validTree.Tokens.Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxTriviaKind.BlockComment)), "Block comment trivia was not retained.");
    Assert(validTree.Tokens.Any(token => token.TrailingTrivia.Length > 0), "Trailing trivia was not retained.");

    const string invalid = "public static class Program { @ [EntryPoint] public static void Main( { } }";
    var invalidTree = SyntaxTree.ParseText(invalid, "invalid.ct");
    Assert(invalidTree.ToFullString() == invalid, "Invalid syntax did not round-trip exactly.");
    Assert(invalidTree.Tokens.Any(token => token.IsMissing), "Parser recovery did not retain a missing token.");
    Assert(invalidTree.SkippedTokens.Length > 0, "Parser recovery did not retain skipped tokens.");
});

Run("conversion and recursive unsafe safety", () =>
{
    const string source = """
        public class A { }
        public class B { }
        public struct Holder { public unsafe int* Pointer; }
        public static class Program
        {
            public static int*[] Expose(int*[] value) { return value; }
            public static Holder Echo(Holder value) { return value; }
            [EntryPoint]
            public static void Main()
            {
                A a = new A();
                B b = (B)a;
                string text = "x";
                int[] values = (int[])text;
                int*[] pointers = new int*[1];
            }
        }
        """;
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2137") >= 2, "Unrelated reference casts were not rejected.");
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2141") >= 4, "Pointer-containing public signatures were not recursively unsafe-checked.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2139"), "Pointer-containing local uses were not recursively unsafe-checked.");

    const string valid = "public static class Program { public static unsafe int** Convert(int** value) { return (int**)value; } [EntryPoint] public static void Main() { unsafe { int*[] values = new int*[1]; } } }";
    Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Valid pointer casts and pointer arrays were rejected in unsafe contexts.");
});

Run("integral only operators", () =>
{
    const string source = "public static class Program { [EntryPoint] public static void Main() { float a = 5.0; float b = a % 2.0; a %= 2.0; float c = ~a; } }";
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2149") == 2, "float remainder forms were not rejected.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2148"), "Bitwise complement on float was not rejected.");
});

Run("C float literal formatting", () =>
{
    const string source = "using System; public static class Program { [EntryPoint] public static void Main() { float whole = 5.0; float negativeZero = -0.0; float infinity = 1.0 / 0.0; float notANumber = 0.0 / 0.0; Console.WriteLine((0.0 / 0.0) == (0.0 / 0.0)); } }";
    var generated = Emit(source);
    Assert(generated.Contains("5.0f", StringComparison.Ordinal), "An integral-valued float literal was not emitted with a decimal point.");
    Assert(generated.Contains("-0.0f", StringComparison.Ordinal), "Negative zero was not preserved.");
    Assert(generated.Contains("INFINITY", StringComparison.Ordinal), "A folded infinity was not emitted with the C macro.");
    Assert(generated.Contains("NAN", StringComparison.Ordinal), "A folded NaN was not emitted with the C macro.");
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) == "False\n", "Folded NaN equality did not use IEEE semantics.");
});

Run("pairwise overload ambiguity", () =>
{
    const string source = "public static class Program { private static void Pick(short a, float b) { } private static void Pick(int a, uint b) { } [EntryPoint] public static void Main() { byte a = 1; ushort b = 2; Pick(a, b); } }";
    Assert(Compile(source).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2123"), "Cross-argument overload preferences were not reported as ambiguous.");
});

Run("do and switch control flow", () =>
{
    const string assigned = "public static class Program { [EntryPoint] public static void Main() { int value; do { value = 1; } while (false); int copy = value; } }";
    Assert(!Compile(assigned).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "Assignment through a do body was not preserved.");

    const string broken = "public static class Program { [EntryPoint] public static void Main() { int value; do { if (true) break; value = 1; } while (false); int copy = value; } }";
    Assert(Compile(broken).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "An early do break incorrectly assigned a local.");

    const string returning = "public static class Program { private static int Pick(int value) { switch (value) { case 0: return 1; default: return 2; } } [EntryPoint] public static void Main() { } }";
    Assert(!Compile(returning).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3100"), "A fully returning switch was rejected.");

    const string incomplete = "public static class Program { private static int Pick(int value) { switch (value) { case 0: break; default: return 2; } } [EntryPoint] public static void Main() { } }";
    Assert(Compile(incomplete).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3100"), "A switch break incorrectly completed a non-void return.");
});

Run("switch case conversion", () =>
{
    const string duplicates = "public static class Program { [EntryPoint] public static void Main() { byte value = 0; switch (value) { case 1: break; case (byte)1: break; case 300: break; default: break; } } }";
    var diagnostics = Compile(duplicates).GetDiagnostics();
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT3109"), "Duplicate converted case labels were not rejected.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2108"), "Out-of-range case label was not rejected.");
});

Run("extern ABI validation", () =>
{
    const string identical = "public static class A { [Extern(\"native_value\")] public static int Get(int value); } public static class B { [Extern(\"native_value\")] public static int Read(int value); [EntryPoint] public static void Main() { } }";
    var emitted = Emit(identical);
    Assert(emitted.Split("extern int32_t native_value", StringSplitOptions.None).Length == 2, "Identical extern aliases did not emit exactly one prototype.");

    const string incompatible = "public static class A { [Extern(\"native_value\")] public static int Get(int value); } public static class B { [Extern(\"native_value\")] public static uint Read(uint value); [EntryPoint] public static void Main() { } }";
    Assert(Compile(incompatible).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4102" && diagnostic.RelatedLocation is not null), "Incompatible extern aliases did not report the earlier declaration.");

    const string reserved = "public static class Program { [Extern(\"main\")] public static int Native(); [EntryPoint] public static void Main() { } }";
    Assert(Compile(reserved).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "Reserved external main was not rejected.");

    const string runtime = "public static class Program { [Extern(\"ct_alloc\")] public static int Native(); [EntryPoint] public static void Main() { } }";
    Assert(Compile(runtime).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A runtime external collision was not rejected.");

    const string generated = "public static class Program { private static void Helper() { } [Extern(\"ct_m__7_Program_6_Helper\")] public static int Native(); [EntryPoint] public static void Main() { } }";
    Assert(Compile(generated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated external collision was not rejected.");

    const string dynamicGenerated = "public static class Program { [Extern(\"ct_new_ct_a_i32\")] public static int Native(); [EntryPoint] public static void Main() { int[] values = new int[1]; } }";
    Assert(Compile(dynamicGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated array-allocator collision was not rejected.");

    const string objectGenerated = "public class Value { public virtual int Read() { return 1; } } public static class Program { [Extern(\"ct_vtable_u_5_Value\")] public static int Native(); [EntryPoint] public static void Main() { object value = new Value(); } }";
    Assert(Compile(objectGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated object-vtable collision was not rejected.");

    const string boxGenerated = "public static class Program { [Extern(\"ct_box_value_i32\")] public static int Native(); [EntryPoint] public static void Main() { object value = 1; } }";
    Assert(Compile(boxGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated boxing-helper collision was not rejected.");
});

Run("target validation precedes output", () =>
{
    const string source = "public struct Recursive { public Recursive Value; } public static class Program { [EntryPoint] public static void Main() { } }";
    var compilation = Compile(source);
    Assert(compilation.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4100"), "A recursive value layout was not rejected during analysis.");
    using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var result = compilation.EmitC(writer);
    Assert(!result.Success && writer.GetStringBuilder().Length == 0, "Target validation wrote partial C output.");
});

Run("directory mode output safety", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "ctilde-directory-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(Path.Combine(directory, "valid.ct"), "public static class Program { [EntryPoint] public static void Main() { } }");
        File.WriteAllText(Path.Combine(directory, "generated.ct"), "public static class Broken {");
        File.WriteAllText(Path.Combine(directory, "generated.c"), "/* Generated by C~ old output. Do not edit. */\nold");
        File.WriteAllText(Path.Combine(directory, "handwritten.ct"), "public static class Broken {");
        File.WriteAllText(Path.Combine(directory, "handwritten.c"), "/* handwritten */\nint value;");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
        Assert(File.Exists(cliDll), $"CLI test dependency was not found at {cliDll}.");
        var result = RunProcess("dotnet", [cliDll, "--compile-directory", directory]);
        Assert(result.ExitCode == 1, "Directory mode did not report invalid siblings.");
        Assert(File.Exists(Path.Combine(directory, "valid.c")), "A valid sibling did not produce C output.");
        Assert(!File.Exists(Path.Combine(directory, "generated.c")), "Stale generated output was not removed.");
        Assert(File.ReadAllText(Path.Combine(directory, "handwritten.c")) == "/* handwritten */\nint value;", "Handwritten C output was modified.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
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

    const string singleDo = "public static class Program { [EntryPoint] public static void Main() { readonly int value; do { value = 1; } while (false); int copy = value; } }";
    Assert(!Compile(singleDo).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A one-shot do assignment to readonly storage was rejected.");

    const string repeatedDo = "public static class Program { [EntryPoint] public static void Main() { readonly int value; bool repeat = true; do { value = 1; } while (repeat); } }";
    Assert(Compile(repeatedDo).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3130"), "A repeatable do assignment to readonly storage was accepted.");

    const string repeatedField = "public class Box { public readonly int Value; public Box(bool repeat) { do { Value = 1; } while (repeat); } } public static class Program { [EntryPoint] public static void Main() { } }";
    Assert(Compile(repeatedField).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3131"), "A repeatable constructor assignment to a readonly field was accepted.");
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
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2122") == 1, "Expected only the invalid ToString argument diagnostic.");
});

Run("System.Object inheritance dispatch and boxing", () =>
{
    const string source = """
        using System;
        public class Animal
        {
            protected int value;
            public Animal(int value) { this.value = value; }
            public virtual string Speak() { return "animal"; }
            public virtual int Number { get { return value; } }
            public override string ToString() { return "Animal"; }
        }
        public class Dog : Animal
        {
            public Dog() : this(7) { }
            private Dog(int value) : base(value) { }
            public override string Speak() { return base.Speak() + " dog"; }
            public override int Number { get { return value + 1; } }
            public sealed override string ToString() { return "Dog"; }
        }
        public class Cat : Animal
        {
            public Cat() : base(3) { }
        }
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                Dog dog = new Dog();
                Animal animal = dog;
                object value = animal;
                Console.WriteLine(animal.Speak());
                Console.WriteLine(animal.Number);
                Console.WriteLine(value.ToString());
                Console.WriteLine(animal is Dog);
                Dog cast = (Dog)animal;
                Console.WriteLine(cast.Speak());
                Cat missing = animal as Cat;
                Console.WriteLine(missing == null);
                object first = 42;
                object second = 42;
                Console.WriteLine(Object.Equals(first, second));
                Console.WriteLine(Object.ReferenceEquals(first, second));
                Console.WriteLine((int)first);
                Console.WriteLine(first.ToString());
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "animal dog\n8\nDog\nTrue\nanimal dog\nTrue\nTrue\nFalse\n42\n42\n", result.StandardOutput);
});

Run("constructor order and virtual dispatch", () =>
{
    const string source = "using System; public class Base { protected int value = 1; public Base() { Console.WriteLine(Read()); Console.WriteLine(value); } public virtual int Read() { return value; } } public class Derived : Base { private int derived = 5; public Derived() : base() { Console.WriteLine(Read()); } public override int Read() { return derived; } } public static class Program { [EntryPoint] public static void Main() { Derived value = new Derived(); } }";
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "0\n1\n5\n", result.StandardOutput);
});

Run("unsafe pointer object boxing", () =>
{
    const string source = "using System; public static class Program { [EntryPoint] public static void Main() { unsafe { int value = 9; int* pointer = &value; object boxed = pointer; Console.WriteLine(boxed is int*); int* copy = (int*)boxed; Console.WriteLine(*copy); } } }";
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "True\n9\n", result.StandardOutput);
});

Run("inheritance diagnostics", () =>
{
    const string source = "public sealed class Closed { } public class Invalid : Closed { } public class Base { public virtual int Value() { return 1; } protected int field; public virtual int Property { get; protected set; } private virtual int Hidden() { return 0; } } public class Derived : Base { public int Value() { return 2; } private int field; public override int Property { get; set; } public sealed override string ToString() { return \"Derived\"; } } public class Further : Derived { public override string ToString() { return \"Further\"; } } public static class Program { [EntryPoint] public static void Main() { } }";
    var diagnostics = Compile(source).GetDiagnostics();
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1227"), "A sealed base was accepted.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1230"), "Inherited member hiding was accepted.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1229"), "A sealed virtual slot was overridden.");
    Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1228"), "A private virtual member was accepted.");
    Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT1229") >= 2, "An override changed accessor accessibility.");

    const string invalidAs = "public static class Program { [EntryPoint] public static void Main() { object value = 1 as object; } }";
    Assert(Compile(invalidAs).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2147"), "The as operator boxed a value-type source.");

    const string virtualStruct = "public struct Value { public virtual int Read() { return 1; } public virtual int Property { get; } } public static class Program { [EntryPoint] public static void Main() { } }";
    Assert(Compile(virtualStruct).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1228") >= 2, "A structure declared an ordinary virtual member.");
});

Run("object syntax surface", () =>
{
    const string source = "public class Base { public Base(int value) { } } public class Derived : Base { public Derived() : this(1) { } private Derived(int value) : base(value) { } public bool Check(object value) { return value is Derived && value as Derived != null; } }";
    var tree = SyntaxTree.ParseText(source, "object-syntax.ct");
    Assert(tree.ToFullString() == source, "Draft 0.4 object syntax did not round-trip.");
    var derived = tree.Root.Types.Single(type => type.Name == "Derived");
    Assert(derived.BaseType?.Name == "Base", "The class base clause was not retained.");
    Assert(derived.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Initializer?.Kind == ConstructorInitializerKind.This), "A this constructor initializer was not retained.");
    Assert(derived.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Initializer?.Kind == ConstructorInitializerKind.Base), "A base constructor initializer was not retained.");
});

Run("enum and struct object behavior", () =>
{
    const string source = """
        using System;
        public enum State : int { None = 0, Ready = 2, Alias = 2 }
        public struct Pair
        {
            public int X;
            public Pair(int value) { X = value; }
            public override string ToString() { return X.ToString(); }
            public override bool Equals(object value)
            {
                if (!(value is Pair)) return false;
                Pair other = (Pair)value;
                return X == other.X;
            }
            public override int GetHashCode() { return X; }
        }
        public struct Plain
        {
            public int X;
            public string Text;
            public Plain(int value, string text) { X = value; Text = text; }
        }
        public class Key
        {
            private int value;
            public Key(int value) { this.value = value; }
            public override bool Equals(object other) { return other is Key && ((Key)other).value == value; }
            public override int GetHashCode() { return value; }
        }
        public struct Inner
        {
            public Key Key;
            public Inner(Key key) { Key = key; }
        }
        public struct Outer
        {
            public Inner Inner;
            public Outer(Inner inner) { Inner = inner; }
        }
        public static class Program
        {
            [EntryPoint]
            public static void Main()
            {
                Console.WriteLine(State.Ready.ToString());
                Console.WriteLine(((State)3).ToString());
                object left = new Pair(5);
                object right = new Pair(5);
                Console.WriteLine(left.ToString());
                Console.WriteLine(Object.Equals(left, right));
                Console.WriteLine(left.GetHashCode() == right.GetHashCode());
                object plainLeft = new Plain(4, "same");
                object plainRight = new Plain(4, "same");
                Console.WriteLine(Object.Equals(plainLeft, plainRight));
                Console.WriteLine(plainLeft.GetHashCode() == plainRight.GetHashCode());
                object outerLeft = new Outer(new Inner(new Key(8)));
                object outerRight = new Outer(new Inner(new Key(8)));
                Console.WriteLine(Object.Equals(outerLeft, outerRight));
                Console.WriteLine(outerLeft.GetHashCode() == outerRight.GetHashCode());
            }
        }
        """;
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "Ready\n3\n5\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n", result.StandardOutput);
});

Run("object cast runtime failures", () =>
{
    const string invalidReference = "public class Base { } public class Left : Base { } public class Right : Base { } public static class Program { [EntryPoint] public static void Main() { Base value = new Left(); Right invalid = (Right)value; } }";
    var cast = CompileAndRun(invalidReference);
    Assert(cast.ExitCode != 0 && cast.StandardError.Contains("CTO0001", StringComparison.Ordinal), cast.StandardError);

    const string nullUnbox = "public static class Program { [EntryPoint] public static void Main() { object value = null; int invalid = (int)value; } }";
    var nullResult = CompileAndRun(nullUnbox);
    Assert(nullResult.ExitCode != 0 && nullResult.StandardError.Contains("CTO0002", StringComparison.Ordinal), nullResult.StandardError);

    const string wrongUnbox = "public static class Program { [EntryPoint] public static void Main() { object value = 1u; int invalid = (int)value; } }";
    var wrongResult = CompileAndRun(wrongUnbox);
    Assert(wrongResult.ExitCode != 0 && wrongResult.StandardError.Contains("CTO0003", StringComparison.Ordinal), wrongResult.StandardError);
});

Run("constructor and hierarchy cycles", () =>
{
    const string inheritance = "public class A : B { } public class B : A { } public static class Program { [EntryPoint] public static void Main() { } }";
    Assert(Compile(inheritance).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1226"), "An inheritance cycle was accepted.");
    const string constructors = "public class Loop { public Loop() : this(1) { } private Loop(int value) : this() { } } public static class Program { [EntryPoint] public static void Main() { } }";
    Assert(Compile(constructors).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1232"), "A constructor cycle was accepted.");
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

Run("object model example", () =>
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "ObjectModel.ct"));
    var result = CompileAndRun(source);
    Assert(result.ExitCode == 0, result.StandardError);
    Assert(Normalize(result.StandardOutput) == "Examples.Pair\n", result.StandardOutput);
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

Run("object ABI snapshot", () =>
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "ObjectModel.ct"));
    var projection = ProjectObjectAbi(Emit(source));
    var expected = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "object-model.abi.txt")));
    Assert(projection == expected, "Generated object ABI snapshot changed.");
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
        return RunCompiledProgram(executablePath);
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
        if (configured.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var compiler = configured[4..];
            var linuxSource = WslPath(cPath);
            var linuxOutput = WslPath(executablePath);
            return RunGnuCompiler("wsl", ["--exec", compiler], linuxSource, linuxOutput);
        }
        var compilerName = Path.GetFileNameWithoutExtension(configured);
        var arguments = compilerName.Equals("cl", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/nologo", "/std:clatest", "/W4", "/WX", $"/Fe:{executablePath}", cPath }
            : null;
        return arguments is not null
            ? RunProcess(configured, arguments)
            : RunGnuCompiler(configured, [], cPath, executablePath);
    }

    if (!OperatingSystem.IsWindows())
        return RunGnuCompiler("cc", [], cPath, executablePath);

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

static ProcessResult RunGnuCompiler(string command, IReadOnlyList<string> prefix, string cPath, string executablePath)
{
    var configuredStandard = Environment.GetEnvironmentVariable("CTILDE_C_STANDARD");
    var standard = string.IsNullOrWhiteSpace(configuredStandard) ? "gnu23" : configuredStandard;
    var result = RunProcess(command, [.. prefix, $"-std={standard}", "-Wall", "-Wextra", "-Werror", "-o", executablePath, cPath]);
    if (!string.IsNullOrWhiteSpace(configuredStandard) || standard != "gnu23" || !RejectedCStandard(result))
        return result;
    return RunProcess(command, [.. prefix, "-std=gnu2x", "-Wall", "-Wextra", "-Werror", "-o", executablePath, cPath]);
}

static bool RejectedCStandard(ProcessResult result)
{
    if (result.ExitCode == 0)
        return false;
    var output = result.StandardOutput + result.StandardError;
    return output.Contains("gnu23", StringComparison.OrdinalIgnoreCase) &&
        (output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("invalid value", StringComparison.OrdinalIgnoreCase));
}

static string WslPath(string path)
{
    var windowsPath = Path.GetFullPath(path).Replace('\\', '/');
    var result = RunProcess("wsl", ["--exec", "wslpath", "-a", "-u", windowsPath]);
    Assert(result.ExitCode == 0, result.StandardError);
    return result.StandardOutput.Trim();
}

static ProcessResult RunCompiledProgram(string executablePath)
{
    var configured = Environment.GetEnvironmentVariable("CTILDE_CC");
    return configured?.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase) == true
        ? RunProcess("wsl", ["--exec", WslPath(executablePath)])
        : RunProcess(executablePath, []);
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

static string ProjectObjectAbi(string generated)
{
    var result = new StringBuilder();
    var captureBlock = false;
    foreach (var line in Normalize(generated).Split('\n'))
    {
        if (line.StartsWith("struct ct_vtable", StringComparison.Ordinal) ||
            line.StartsWith("struct ct_t_8_Examples_4_Base", StringComparison.Ordinal) ||
            line.StartsWith("struct ct_t_8_Examples_7_Derived", StringComparison.Ordinal) ||
            line.StartsWith("static const ct_vtable ct_vtable_", StringComparison.Ordinal))
            captureBlock = true;

        var singleLine = line.StartsWith("typedef struct ct_object", StringComparison.Ordinal) ||
            line.StartsWith("typedef struct ct_box_", StringComparison.Ordinal) ||
            line.StartsWith("static ct_type_descriptor ct_desc_", StringComparison.Ordinal) ||
            line.StartsWith("static ct_object* ct_checked_cast", StringComparison.Ordinal) ||
            line.StartsWith("static ct_object* ct_safe_cast", StringComparison.Ordinal) ||
            line.Contains("ct_init_ct_ctor_", StringComparison.Ordinal) ||
            line.Contains("ct_l_", StringComparison.Ordinal) &&
                (line.Contains("ct_checked_cast", StringComparison.Ordinal) || line.Contains("ct_safe_cast", StringComparison.Ordinal)) ||
            line.StartsWith("static ", StringComparison.Ordinal) &&
                (line.Contains(" ct_vthunk_", StringComparison.Ordinal) ||
                 line.Contains(" ct_box_", StringComparison.Ordinal) ||
                 line.Contains(" ct_unbox_", StringComparison.Ordinal));

        if (captureBlock || singleLine)
            result.AppendLine(line);
        if (captureBlock && line == "};")
            captureBlock = false;
    }
    return Normalize(result.ToString()).Replace("E:/Projects/CTilde/examples/ObjectModel.ct", "test.ct", StringComparison.Ordinal);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
