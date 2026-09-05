using System.Text;
using System.Text.Json;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart48(ConformanceSuite suite)
    {
        suite.Run("draft 0.50 compact map experiment", () =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "CTilde.sln"))) root = root.Parent;
            Assert(root is not null, "Repository root was not found.");
            var prototype = Path.Combine(root!.FullName, "Test", "Fixtures", "CompactMap", "CompactMap.ct");
            var source = new StringBuilder("""
                using System;
                using System.Diagnostics;
                public sealed class Cell { public int Number; public Cell(int value) { Number = value; } }
                public static class Diagnostics {
                    [Extern("ct_memory_diagnostic_live_allocations")] [NoAlloc] public static uint Live();
                    [Extern("ct_memory_diagnostic_total_allocations")] [NoAlloc] public static uint Total();
                }
                public static class Program {
                    private static int Hash(int key) { return key; }
                    private static int Collision(int key) { return key % 4; }
                    private static bool Equal(int a, int b) { return a == b; }
                    private static int HashCell(Cell key) { return key.Number; }
                    private static int CollisionCell(Cell key) { return key.Number % 4; }
                    private static bool EqualCell(Cell a, Cell b) { return a.Number == b.Number; }
                """);
            foreach (var compact in new[] { false, true })
            foreach (var references in new[] { false, true })
            {
                var name = (compact ? "Compact" : "Baseline") + (references ? "References" : "Integers");
                var type = references ? "Cell" : "int";
                var layout = compact ? "Review.Collections.CompactMap" : "System.Collections.Map";
                var key = references ? "new Cell(index)" : "index";
                var value = references ? "new Cell(index + 1)" : "index + 1";
                var number = references ? "pair.Second.Number" : "pair.Second";
                var lookup = references ? "found.Number" : "found";
                var suffix = references ? "Cell" : "";
                // 2048 inserted entries produce 4096 slots at the common 75% load limit.
                var scalarSize = references ? "sizeof(nuint)" : "sizeof(int)";
                var entrySize = references ? "((17u + sizeof(nuint) - 1u) / sizeof(nuint) + 2u) * sizeof(nuint)" : "sizeof(Review.Collections.CompactEntry<int, int>)";
                var payload = compact ? $"(nuint)map.StorageSlots * (sizeof(int) + {entrySize})" : $"(nuint)4096 * (5u * sizeof(int) + sizeof(bool) + 2u * {scalarSize})";
                source.Append($$"""
                    private static void {{name}}(bool collisions, bool report) {
                        uint live = Diagnostics.Live(); long elapsed; uint allocations; nuint bytes;
                        {{name}}Work(collisions, out elapsed, out allocations, out bytes);
                        if (Diagnostics.Live() != live) throw new InvalidOperationException();
                        if (report) {
                            Console.Write("{{name}},"); Console.Write(collisions); Console.Write(",");
                            Console.Write(elapsed); Console.Write(","); Console.Write(allocations);
                            Console.Write(","); Console.WriteLine(bytes);
                        }
                    }
                    private static void {{name}}Work(bool collisions, out long elapsed, out uint allocations, out nuint bytes) {
                        uint before = Diagnostics.Total();
                        long start = Stopwatch.GetTimestampNanoseconds();
                        bytes = 0u; int sum = 0;
                        {
                            Hasher<{{type}}> hash = Hash{{suffix}};
                            if (collisions) hash = Collision{{suffix}};
                            {{layout}}<{{type}}, {{type}}> map = new {{layout}}<{{type}}, {{type}}>(hash, Equal{{suffix}});
                            int index = 0;
                            while (index < 2048) { map.Add({{key}}, {{value}}); index++; }
                            bytes = {{payload}};
                            index = 0;
                            while (index < 2048) {
                                {{type}} found;
                                if (!map.TryGetValue({{key}}, out found) || {{lookup}} != index + 1) throw new InvalidOperationException();
                                index++;
                            }
                            index = 0;
                            while (index < 2048) { if (!map.Remove({{key}})) throw new InvalidOperationException(); index += 2; }
                            foreach (Pair<{{type}}, {{type}}> pair in map) { sum += {{number}}; }
                            if (sum != 1049600 || map.Count != 1024) throw new InvalidOperationException();
                            map.Clear();
                            if (map.Count != 0) throw new InvalidOperationException();
                        }
                        elapsed = Stopwatch.GetTimestampNanoseconds() - start;
                        allocations = Diagnostics.Total() - before;
                    }
                    """);
            }
            source.Append("[EntryPoint] public static void Main() { int run = 0; while (run < 9) { bool report = run >= 2;");
            foreach (var references in new[] { false, true })
            foreach (var collisions in new[] { "false", "true" })
            {
                var suffix = references ? "References" : "Integers";
                source.Append($"if (run % 2 == 0) {{ Baseline{suffix}({collisions}, report); Compact{suffix}({collisions}, report); }} else {{ Compact{suffix}({collisions}, report); Baseline{suffix}({collisions}, report); }}");
            }
            source.Append("run++; } } }");
            Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts", "correctness-review"));
            File.WriteAllText(Path.Combine(root.FullName, "artifacts", "correctness-review", "MapBenchmark.ct"), source.ToString());
            File.WriteAllText(Path.Combine(root.FullName, "artifacts", "correctness-review", "MapBenchmark.c"), Emit([SyntaxTree.ParseText(File.ReadAllText(prototype), prototype), SyntaxTree.ParseText(source.ToString(), "MapBenchmark.ct")]));
            var result = CompileAndRun([SyntaxTree.ParseText(File.ReadAllText(prototype), prototype), SyntaxTree.ParseText(source.ToString(), "MapBenchmark.ct")], memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            var rows = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',')).ToArray();
            Assert(rows.Length == 56, "Missing benchmark samples: " + result.StandardOutput);
            var reportDirectory = Path.Combine(root.FullName, "artifacts", "correctness-review");
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(Path.Combine(reportDirectory, "compact-map.csv"), "layout,collisions,nanoseconds,allocations,arrayPayloadBytes\n" + result.StandardOutput);
            var groups = rows.GroupBy(row => (Layout: row[0], Collisions: row[1])).Select(group => new {
                layout = group.Key.Layout, collisions = bool.Parse(group.Key.Collisions), samples = group.Count(),
                medianNanoseconds = group.Select(row => long.Parse(row[2])).Order().ElementAt(3),
                allocations = uint.Parse(group.First()[3]), arrayPayloadBytes = ulong.Parse(group.First()[4])
            }).ToArray();
            File.WriteAllText(Path.Combine(reportDirectory, "compact-map.json"), JsonSerializer.Serialize(new {
                compiler = Environment.GetEnvironmentVariable("CTILDE_CC") ?? "default", entries = 2048, warmups = 2,
                measurement = "Combined growth, insertion, lookup, removal, ordered iteration, clear and ARC cleanup. Payload excludes object and array headers; diagnostic allocations include reference-key probes. Every sample checks live allocation balance.", results = groups
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
