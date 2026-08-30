using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart30(ConformanceSuite suite)
    {
        suite.Run("semantic reference index and lenses", () =>
        {
            const string declarations = """
                public enum Mode { Off, On }
                public class Box<T>
                {
                    public int Field;
                    public int Property { get; set; }
                    public Box(int value) { Field = value; }
                    public int Map(int value) { int local = value; return local + Field; }
                    public int Map(string value) { return 0; }
                }
                """;
            const string uses = """
                public static class Program
                {
                    public static void Run()
                    {
                        Box<int> box = new Box<int>(1);
                        int mapped = box.Map(2);
                        box.Property = box.Field;
                        Mode mode = Mode.On;
                        { int shadow = mapped; mapped = shadow; }
                        { int shadow = mapped; mapped = shadow; }
                    }
                }
                """;
            var service = LanguageServiceSnapshot.Create([
                SyntaxTree.ParseText(declarations, "declarations.ct"),
                SyntaxTree.ParseText(uses, "uses.ct")]);
            var declarationLenses = service.GetReferenceLenses("declarations.ct");
            var useLenses = service.GetReferenceLenses("uses.ct");

            LanguageReferenceLens Lens(string name, Func<LanguageReferenceLens, bool>? predicate = null)
            {
                var matches = declarationLenses.Where(lens => lens.Name == name && (predicate?.Invoke(lens) ?? true)).ToArray();
                Assert(matches.Length == 1, $"Expected one '{name}' lens; found {matches.Length}. Lenses: {string.Join(", ", declarationLenses.Select(lens => $"{lens.Kind}:{lens.Name}={lens.ReferenceCount}"))}");
                return matches[0];
            }

            Assert(Lens("Box", lens => lens.Kind == LanguageSymbolKind.Class).ReferenceCount == 2, "Generic type references were not normalized to the declaration.");
            Assert(Lens("Box", lens => lens.Kind == LanguageSymbolKind.Constructor).ReferenceCount == 1, "Constructor reference count was incorrect.");
            Assert(Lens("Field").ReferenceCount == 3, "Field references were not indexed exactly once per location.");
            Assert(Lens("Property").ReferenceCount == 1, "Property reference count was incorrect.");
            var mapLenses = declarationLenses.Where(lens => lens.Name == "Map").ToArray();
            Assert(mapLenses.Length == 2 && mapLenses.Count(lens => lens.ReferenceCount == 1) == 1 && mapLenses.Count(lens => lens.ReferenceCount == 0) == 1,
                "Overloaded methods were not distinguished by semantic identity.");
            Assert(Lens("On").ReferenceCount == 1, "Enum-member references were not indexed.");
            Assert(declarationLenses.Where(lens => lens.Name == "value" && lens.Kind == LanguageSymbolKind.Parameter).Any(lens => lens.ReferenceCount == 1),
                "Parameter references were not indexed.");
            Assert(useLenses.Count(lens => lens.Name == "shadow") == 2 && useLenses.Where(lens => lens.Name == "shadow").All(lens => lens.ReferenceCount == 1),
                "Shadowed locals did not retain distinct reference identities.");

            var mapUse = uses.IndexOf("Map(2)", StringComparison.Ordinal);
            var references = service.GetReferences("uses.ct", mapUse, includeDeclaration: false);
            Assert(references.Length == 1 && references[0].FilePath == "uses.ct", "Reference lookup returned the wrong overload or included its declaration.");
            var withDeclaration = service.GetReferences("uses.ct", mapUse, includeDeclaration: true);
            Assert(withDeclaration.Length == 2 && withDeclaration.Count(reference => reference.IsDeclaration) == 1,
                "Reference lookup did not honor declaration inclusion.");
        });

        suite.Run("inline assembly reference index", () =>
        {
            const string source = "public static class Program { [EntryPoint] public static unsafe void Main() { int value = 1; [NoAlloc] asm (in value as source) { addl source, source } } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "asm-reference.ct")]);
            var lens = service.GetReferenceLenses("asm-reference.ct").Single(item => item.Name == "value" && item.Kind == LanguageSymbolKind.Variable);
            Assert(lens.ReferenceCount >= 1, "Resolved inline-assembly operands were not attributed to their named C~ symbol.");
            Assert(service.GetReferences("asm-reference.ct", source.IndexOf("source, source", StringComparison.Ordinal)).Length >= 1,
                "Inline-assembly body references did not resolve through the reference index.");
        });

        suite.Run("operator reference locations", () =>
        {
            const string source = "public struct V { public static V operator +(V left, V right) { return left; } } public static class P { public static void M() { V left = new V(); V right = new V(); V sum = left + right; } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "operator-reference.ct")]);
            var use = source.LastIndexOf('+');
            var references = service.GetReferences("operator-reference.ct", use);
            Assert(references.Length == 1 && references[0].Span == new TextSpan(use, 1), "Operator reference did not use the physical operator-token range.");
            var lens = service.GetReferenceLenses("operator-reference.ct").Single(item => item.Name == "operator +");
            Assert(lens.ReferenceCount == 1, "Operator CodeLens count was incorrect.");
        });

        suite.Run("physical and embedded standard-library reference identity", () =>
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var standardLibraryRoot = Path.Combine(repositoryRoot, "CTilde", "StandardLibrary");
            var mathPath = Path.Combine(standardLibraryRoot, "System", "Math.ct");
            var physical = LanguageServiceSnapshot.CreateStandardLibraryProject(standardLibraryRoot, mathPath);
            const string source = "public static class P { public static float M() { return Math.Sqrt(4.0f); } }";
            var embedded = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "embedded-use.ct")]);
            var use = source.IndexOf("Sqrt", StringComparison.Ordinal) + 1;
            var embeddedKey = embedded.GetReferences("embedded-use.ct", use).First().SymbolKey;
            Assert(physical.GetReferenceLenses(mathPath).Any(lens => lens.Name == "Sqrt" && lens.SymbolKey == embeddedKey),
                "Physical and embedded standard-library declarations did not share a stable semantic reference identity.");
        });

        suite.Run("lambda parameter references exclude synthetic methods", () =>
        {
            const string source = "public delegate int Transform(int value); public static class P { public static int M() { Transform twice = (int item) => item + item; return twice(2); } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "lambda-reference.ct")]);
            var lenses = service.GetReferenceLenses("lambda-reference.ct");
            var parameter = lenses.Single(lens => lens.Name == "item");
            Assert(parameter.Kind == LanguageSymbolKind.Parameter && parameter.ReferenceCount == 2, "Lambda parameter references were not indexed.");
            Assert(lenses.All(lens => !lens.Name.StartsWith("<lambda_", StringComparison.Ordinal)), "A compiler-generated lambda method received a reference lens.");
        });

        suite.Run("override and scoped-variable reference identity", () =>
        {
            const string source = """
                using System;
                public class Base { public virtual int Read() { return 1; } }
                public class Derived : Base { public override int Read() { return 2; } }
                public static class P
                {
                    public static void M()
                    {
                        Derived derived = new Derived();
                        int first = derived.Read();
                        Base baseValue = derived;
                        int second = baseValue.Read();
                        foreach (int item in new int[1]) { second = item; }
                        try { throw new Exception(); } catch (Exception error) { Console.WriteLine(error); }
                        Missing;
                    }
                }
                """;
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "scoped-reference.ct")]);
            var lenses = service.GetReferenceLenses("scoped-reference.ct");
            var reads = lenses.Where(lens => lens.Name == "Read").ToArray();
            Assert(reads.Length == 2 && reads.All(lens => lens.ReferenceCount == 1), "Overrides did not retain distinct semantic reference identities.");
            var itemLens = lenses.Single(lens => lens.Name == "item");
            Assert(itemLens.ReferenceCount == 1, $"Foreach-variable references were not indexed (count {itemLens.ReferenceCount}).");
            var errorLens = lenses.Single(lens => lens.Name == "error");
            Assert(errorLens.ReferenceCount == 1, $"Catch-variable references were not indexed (count {errorLens.ReferenceCount}).");
            Assert(service.GetReferences("scoped-reference.ct", source.IndexOf("Missing", StringComparison.Ordinal)).IsEmpty,
                "An unresolved identifier was included in the reference index.");
        });
    }
}
