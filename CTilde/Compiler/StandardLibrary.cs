using System.Collections.Immutable;
using System.Text;

namespace CTilde;

internal static class StandardLibrary
{
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyCommonSyntaxTrees = new(() => LoadSyntaxTrees(["Object.ct", "Exception.ct", "Console.ct", "Environment.ct", "Memory.ct"]));
    private static readonly Lazy<ImmutableArray<SyntaxTree>> LazyEspIdfSyntaxTrees = new(() => LoadSyntaxTrees(["EspIdf.ct"]));

    public static ImmutableArray<SyntaxTree> GetSyntaxTrees(CompilationTarget target) => target == CompilationTarget.EspIdf
        ? LazyCommonSyntaxTrees.Value.AddRange(LazyEspIdfSyntaxTrees.Value)
        : LazyCommonSyntaxTrees.Value;

    private static ImmutableArray<SyntaxTree> LoadSyntaxTrees(IReadOnlyList<string> files)
    {
        var assembly = typeof(StandardLibrary).Assembly;
        var trees = ImmutableArray.CreateBuilder<SyntaxTree>(files.Count);

        foreach (var file in files)
        {
            var resourceName = $"CTilde.StandardLibrary.{file}";
            using var stream = assembly.GetManifestResourceStream(resourceName) ??
                throw new InvalidOperationException($"The embedded standard-library resource '{resourceName}' is missing.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            trees.Add(SyntaxTree.ParseText(reader.ReadToEnd(), $"stdlib/System/{file}"));
        }

        return trees.ToImmutable();
    }
}
