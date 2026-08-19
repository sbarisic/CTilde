namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart5(ConformanceSuite suite)
    {
        suite.Run("feature C symbol snapshot", () =>
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

        suite.Run("object ABI snapshot", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "ObjectModel.ct"));
            var projection = ProjectObjectAbi(Emit(source));
            var expected = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "object-model.abi.txt")));
            Assert(projection == expected, "Generated object ABI snapshot changed.");
        });

        suite.Run("bounds failure", () =>
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

        suite.Run("null failure", () =>
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

        suite.Run("negative array length failure", () =>
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

        suite.Run("integer division failure", () =>
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

        suite.Run("allocation overflow guard emitted", () =>
        {
            var generated = Emit("public static class Program { [EntryPoint] public static void Main() { int[] values = new int[0]; } }");
            Assert(generated.Contains("CTA0002", StringComparison.Ordinal), "Allocation overflow guard was not emitted.");
        });
    }
}
