namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart27(ConformanceSuite suite)
    {
        suite.Run("draft 0.36 defaults collections enumeration and iterators", () =>
        {
            const string source = """
                using System;
                using System.Collections;

                public static class Tracker { public static int Disposed; }
                public struct CounterEnumerator
                {
                    private int current;
                    public CounterEnumerator(int ignored) { current = 0; }
                    public int Current { get { return current; } }
                    public bool MoveNext() { current++; return current <= 2; }
                    public void Dispose() { Tracker.Disposed++; }
                }
                public sealed class CounterSequence
                {
                    public CounterEnumerator GetEnumerator() { return new CounterEnumerator(0); }
                }

                public static class Program
                {
                    private static bool Equal(int left, int right) { return left == right; }
                    private static int Hash(int value) { return value; }
                    private static IEnumerable<int> Values()
                    {
                        yield return 2;
                        yield return 3;
                        yield break;
                    }
                    private static int First()
                    {
                        foreach (int value in new CounterSequence()) return value;
                        return 0;
                    }
                    private static void ThrowFromLoop()
                    {
                        foreach (int value in new CounterSequence())
                            if (value > 0) throw new Exception();
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        int zero = default(int);
                        List<int> list = new List<int>();
                        list.Add(4);
                        list.Insert(0, 1);
                        list[1] = 5;
                        Stack<int> stack = new Stack<int>(); stack.Push(6);
                        Queue<int> queue = new Queue<int>(); queue.Enqueue(7);
                        Map<int, int> map = new Map<int, int>(Hash, Equal);
                        map.Add(8, 9); map[8] = 10;
                        Set<int> setValue = new Set<int>(Hash, Equal); setValue.Add(11);
                        int sum = zero + stack.Pop() + queue.Dequeue() + map[8];
                        foreach (int value in list) sum = sum + value;
                        IEnumerable<int> sequence = list;
                        foreach (int value in sequence) sum = sum + value;
                        foreach (int value in Values()) sum = sum + value;
                        foreach (int value in setValue) sum = sum + value;
                        sum = sum + First();
                        try { ThrowFromLoop(); } catch { sum = sum + Tracker.Disposed; }
                        Console.WriteLine(sum);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "54\n", result.StandardOutput);
        });

        suite.Run("draft 0.36 collection symbols are available to editor services", () =>
        {
            const string path = "draft036-editor.ct";
            const string source = "using System.Collections; public static class P { public static void M() { List<int> values = new List<int>(); values. } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, path)]);
            var position = source.LastIndexOf("values.", StringComparison.Ordinal) + "values.".Length;
            var labels = service.GetCompletions(path, position).Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
            Assert(labels.Contains("Add") && labels.Contains("Count") && labels.Contains("GetEnumerator"), "List<T> completion is incomplete.");
            var listPosition = source.IndexOf("List<int>", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition(path, listPosition)?.FilePath == "stdlib/System/LinearCollections.ct", "List<T> definition did not navigate to its embedded source.");
        });
    }
}
