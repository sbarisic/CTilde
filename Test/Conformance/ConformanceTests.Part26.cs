namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart26(ConformanceSuite suite)
    {
        suite.Run("draft 0.35 generic containers and array algorithms runtime", () =>
        {
            const string source = """
                using System;
                using System.Collections;

                public sealed class Item
                {
                    public int Key;
                    public int Order;
                    public Item(int key, int order) { Key = key; Order = order; }
                }

                public static class Program
                {
                    private static int callbacks;
                    private static bool EqualInt(int left, int right) { callbacks++; return left == right; }
                    private static bool Positive(int value) { callbacks++; return value > 0; }
                    private static bool Even(int value) { callbacks++; return value % 2 == 0; }
                    private static int Double(int value) { callbacks++; return value * 2; }
                    private static int Sum(int state, int value) { callbacks++; return state + value; }
                    private static void Visit(int value) { Console.Write(value); }
                    private static int CompareItem(Item left, Item right) { return left.Key - right.Key; }
                    private static string ShowInt(int value) { return "v" + value.ToString(); }
                    private static int TextLength(string value) { return value.Length; }

                    [EntryPoint]
                    public static void Main()
                    {
                        Pair<int, string> pair = new Pair<int, string>(7, "seven");
                        Console.WriteLine(pair.First);
                        Console.WriteLine(pair.Second);
                        Option<Pair<int, string> > nested = Option<Pair<int, string> >.Some(pair);
                        Pair<int, string> nestedValue;
                        nested.TryGet(out nestedValue);
                        Console.WriteLine(nestedValue.First);

                        Option<int> some = Option<int>.Some(4);
                        Option<int> none = Option<int>.None;
                        int optionValue;
                        Console.WriteLine(some.TryGet(out optionValue));
                        Console.WriteLine(optionValue);
                        Console.WriteLine(none.Or(9));
                        Console.WriteLine(some.Map<string>(ShowInt).Or("missing"));

                        Result<int, string> ok = Result<int, string>.Ok(5);
                        Result<int, string> error = Result<int, string>.Err("bad");
                        string errorValue;
                        Console.WriteLine(ok.IsOk);
                        Console.WriteLine(error.TryGetErr(out errorValue));
                        Console.WriteLine(errorValue);
                        Console.WriteLine(error.OkOr(8));
                        Console.WriteLine(ok.MapOk<string>(ShowInt).OkOr("missing"));
                        Console.WriteLine(error.MapErr<int>(TextLength).ErrOr(0));

                        int[] values = new int[4];
                        values[0] = 3; values[1] = 2; values[2] = 2; values[3] = -1;
                        callbacks = 0;
                        Console.WriteLine(ArrayAlgorithms.Contains<int>(values, 2, EqualInt));
                        Console.WriteLine(callbacks);
                        callbacks = 0;
                        Console.WriteLine(ArrayAlgorithms.Any<int>(values, Positive));
                        Console.WriteLine(callbacks);
                        callbacks = 0;
                        Console.WriteLine(ArrayAlgorithms.All<int>(values, Positive));
                        Console.WriteLine(callbacks);
                        callbacks = 0;
                        int[] filtered = ArrayAlgorithms.Filter<int>(values, Even);
                        Console.WriteLine(callbacks);
                        Console.WriteLine(filtered.Length);
                        callbacks = 0;
                        int[] mapped = ArrayAlgorithms.Map<int, int>(values, Double);
                        Console.WriteLine(callbacks);
                        Console.WriteLine(mapped[0]);
                        callbacks = 0;
                        Console.WriteLine(ArrayAlgorithms.Fold<int, int>(values, 0, Sum));
                        Console.WriteLine(callbacks);
                        int[] reversed = ArrayAlgorithms.Reversed<int>(values);
                        Console.WriteLine(reversed[0]);
                        ArrayAlgorithms.ForEach<int>(filtered, Visit);
                        Console.WriteLine("");

                        Item[] items = new Item[4];
                        items[0] = new Item(2, 0); items[1] = new Item(1, 1);
                        items[2] = new Item(2, 2); items[3] = new Item(1, 3);
                        Item[] sorted = ArrayAlgorithms.Sorted<Item>(items, CompareItem);
                        Console.WriteLine(sorted[0].Order);
                        Console.WriteLine(sorted[1].Order);
                        Console.WriteLine(sorted[2].Order);
                        Console.WriteLine(sorted[3].Order);
                        Console.WriteLine(items[0].Order);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "7\nseven\n7\nTrue\n4\n9\nv4\nTrue\nTrue\nbad\n8\nv5\n3\nTrue\n2\nTrue\n1\nFalse\n4\n4\n2\n4\n6\n6\n4\n-1\n22\n1\n3\n0\n2\n0\n", result.StandardOutput);
        });

        suite.Run("draft 0.35 UTF-8 helpers validate and preserve failure outputs", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Text;

                public static class Program
                {
                    private static void Decode(string text, int offset)
                    {
                        rune value;
                        int read;
                        bool ok = Utf8.TryDecode(text, offset, out value, out read);
                        Console.WriteLine(ok);
                        Console.WriteLine((uint)value);
                        Console.WriteLine(read);
                    }

                    private static unsafe void DecodeNative(NativeBuffer<byte> bytes, nuint length)
                    {
                        ReadOnlyNativeBuffer<byte> source = new ReadOnlyNativeBuffer<byte>(bytes.Pointer, length);
                        rune value;
                        nuint read;
                        Console.WriteLine(Utf8.TryDecode(source, out value, out read));
                        Console.WriteLine((uint)value);
                        Console.WriteLine(read);
                    }

                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        Decode("A", 0);
                        Decode("¢", 0);
                        Decode("€", 0);
                        Decode("😀", 0);
                        Decode("¢", 1);
                        Decode("A", -1);
                        Decode("A", 1);
                        NativeBuffer<byte> bytes = stackalloc byte[4];
                        bytes[0] = (byte)0xF0; bytes[1] = (byte)0x9F; bytes[2] = (byte)0x98; bytes[3] = (byte)0x80;
                        ReadOnlyNativeBuffer<byte> readable = bytes;
                        rune decoded;
                        nuint consumed;
                        Console.WriteLine(Utf8.TryDecode(readable, out decoded, out consumed));
                        Console.WriteLine((uint)decoded);
                        Console.WriteLine(consumed);

                        bytes[0] = (byte)0x80;
                        DecodeNative(bytes, 1);
                        bytes[0] = (byte)0xE2; bytes[1] = (byte)0x82;
                        DecodeNative(bytes, 2);
                        bytes[0] = (byte)0xE2; bytes[1] = (byte)0x28; bytes[2] = (byte)0xA1;
                        DecodeNative(bytes, 3);
                        bytes[0] = (byte)0xC0; bytes[1] = (byte)0x80;
                        DecodeNative(bytes, 2);
                        bytes[0] = (byte)0xED; bytes[1] = (byte)0xA0; bytes[2] = (byte)0x80;
                        DecodeNative(bytes, 3);
                        bytes[0] = (byte)0xF4; bytes[1] = (byte)0x90; bytes[2] = (byte)0x80; bytes[3] = (byte)0x80;
                        DecodeNative(bytes, 4);

                        NativeBuffer<byte> shortDestination = stackalloc byte[3];
                        shortDestination[0] = (byte)17; shortDestination[1] = (byte)18; shortDestination[2] = (byte)19;
                        nuint written;
                        Console.WriteLine(Utf8.TryEncode(r'😀', shortDestination, out written));
                        Console.WriteLine(written);
                        Console.WriteLine(shortDestination[0]);
                        Console.WriteLine(Utf8.TryEncode(r'€', shortDestination, out written));
                        Console.WriteLine(written);
                        Console.WriteLine(shortDestination[0]);
                        Console.WriteLine(shortDestination[1]);
                        Console.WriteLine(shortDestination[2]);

                        NativeBuffer<byte> output = stackalloc byte[4];
                        for (nuint length = 0; length <= (nuint)4; length++)
                        {
                            NativeBuffer<byte> view = new NativeBuffer<byte>(output.Pointer, length);
                            Console.WriteLine(Utf8.TryEncode(r'😀', view, out written));
                            Console.WriteLine(written);
                        }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n65\n1\nTrue\n162\n2\nTrue\n8364\n3\nTrue\n128512\n4\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n0\nTrue\n128512\n4\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n0\nFalse\n0\n17\nTrue\n3\n226\n130\n172\nFalse\n0\nFalse\n0\nFalse\n0\nFalse\n0\nTrue\n4\n", result.StandardOutput);
        });

        suite.Run("draft 0.35 array edge cases captures and exceptional cleanup", () =>
        {
            const string source = """
                using System;
                using System.Collections;
                public sealed class Box { public int Value; public Box(int value) { Value = value; } }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_objects")] [NoAlloc] public static uint LiveObjects();
                }
                public static class Program
                {
                    private static int calls;
                    private static bool Positive(int value) { calls++; return value > 0; }
                    private static Box ThrowSecond(Box value) { calls++; if (calls == 2) throw new Exception("stop"); return new Box(value.Value); }
                    [EntryPoint] public static void Main()
                    {
                        int[] empty = new int[0];
                        calls = 0; Console.WriteLine(ArrayAlgorithms.Any<int>(empty, Positive)); Console.WriteLine(calls);
                        calls = 0; Console.WriteLine(ArrayAlgorithms.All<int>(empty, Positive)); Console.WriteLine(calls);
                        int[] singleton = new int[1]; singleton[0] = 3;
                        int threshold = 2;
                        Predicate<int> captured = [threshold] value => value > threshold;
                        Console.WriteLine(ArrayAlgorithms.Count<int>(singleton, captured));
                        captured = null;

                        uint baseline = Diagnostics.LiveObjects();
                        try
                        {
                            Box[] source = new Box[3];
                            source[0] = new Box(1); source[1] = new Box(2); source[2] = new Box(3);
                            calls = 0;
                            Box[] mapped = ArrayAlgorithms.Map<Box, Box>(source, ThrowSecond);
                        }
                        catch (Exception error) { Console.WriteLine(error.Message); }
                        Console.WriteLine(Diagnostics.LiveObjects() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "False\n0\nTrue\n0\n1\nstop\nTrue\n", result.StandardOutput);
        });

        suite.Run("draft 0.35 library specializations emit deterministically and on demand", () =>
        {
            const string source = """
                using System;
                using System.Collections;
                public static class Program
                {
                    private static int Double(int value) { return value * 2; }
                    [EntryPoint] public static void Main()
                    {
                        int[] values = new int[1]; values[0] = 3;
                        int[] mapped = ArrayAlgorithms.Map<int, int>(values, Double);
                    }
                }
                """;
            var first = Emit(source);
            var second = Emit(source);
            Assert(first == second, "Closed standard-library specializations were not deterministic.");
            Assert(first.Contains("ArrayAlgorithms", StringComparison.Ordinal) && first.Contains("Mapper", StringComparison.Ordinal), "Used generic library members were not emitted.");
            Assert(!first.Contains("Utf8", StringComparison.Ordinal) && !first.Contains("Result", StringComparison.Ordinal), "Unused Draft 0.35 library members entered emitted C.");
        });

        suite.Run("draft 0.35 generic library editor services", () =>
        {
            const string path = "draft035-editor.ct";
            const string source = """
                using System;
                using System.Collections;
                using System.Text;
                public static class Program
                {
                    private static bool Positive(int value) { return value > 0; }
                    public static void Check()
                    {
                        Option<int> option = Option<int>.Some(1);
                        bool present = option.HasValue;
                        rune scalar; int read;
                        bool decoded = Utf8.TryDecode("A", 0, out scalar, out read);
                        int[] values = new int[1];
                        bool any = ArrayAlgorithms.Any<int>(values, Positive);
                    }
                }
                """;
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, path)]);
            const string optionCompletionSource = "using System; public static class P { public static void M() { Option<int> option = Option<int>.None; option. } }";
            var optionCompletionService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(optionCompletionSource, "option-completion.ct")]);
            var optionMember = optionCompletionSource.IndexOf("option.", StringComparison.Ordinal) + "option.".Length;
            var optionCompletions = optionCompletionService.GetCompletions("option-completion.ct", optionMember);
            var map = optionCompletions.Single(item => item.Label == "Map");
            Assert(optionCompletions.Any(item => item.Label == "HasValue"), "Closed Option completion omitted HasValue.");
            Assert(map.DocumentationId is not null && optionCompletionService.GetDocumentation(map.DocumentationId)?.Summary.Contains("Maps", StringComparison.Ordinal) == true,
                $"Generic library completion documentation was unavailable for '{map.DocumentationId}'.");

            const string utf8CompletionSource = "using System.Text; public static class P { public static void M() { Utf8. } }";
            var utf8CompletionService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(utf8CompletionSource, "utf8-completion.ct")]);
            var utf8Member = utf8CompletionSource.IndexOf("Utf8.", StringComparison.Ordinal) + "Utf8.".Length;
            Assert(utf8CompletionService.GetCompletions("utf8-completion.ct", utf8Member).Any(item => item.Label == "TryDecode"), "Utf8 completion omitted TryDecode.");
            Assert(utf8CompletionService.GetDocumentation("M:System.Text.Utf8.TryDecode(string,int,out rune,out int)")?.Summary.Contains("Decodes", StringComparison.Ordinal) == true,
                "Utf8 overload documentation was unavailable.");
            Assert(service.GetDocumentation("M:System.Collections.ArrayAlgorithms.Sorted``1(T[],System.Ordering<T>)")?.Summary.Contains("stable", StringComparison.Ordinal) == true,
                "ArrayAlgorithms documentation was unavailable.");
            var optionType = source.IndexOf("Option<int> option", StringComparison.Ordinal) + 1;
            Assert(service.GetHover(path, optionType)?.Contents.Contains("Option", StringComparison.Ordinal) == true, "Closed generic hover was unavailable.");
            Assert(service.GetDefinition(path, optionType)?.FilePath == "stdlib/System/Generics.ct", "Option definition did not navigate to its embedded source.");
            var utf8Type = source.IndexOf("Utf8.", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition(path, utf8Type)?.FilePath == "stdlib/System/Utf8.ct", "Utf8 definition did not navigate to its embedded source.");
            Assert(service.GetSemanticTokens(path).Any(token => token.Span.Start == source.IndexOf("Option<int> option", StringComparison.Ordinal) &&
                token.Kind == LanguageSemanticTokenKind.Struct && token.Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary)),
                "Option was not semantically classified as a default-library structure.");
        });
    }
}
