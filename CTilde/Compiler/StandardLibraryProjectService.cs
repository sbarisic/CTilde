using System.Collections.Immutable;

namespace CTilde;

public sealed record StandardLibraryValidationResult(
    string Variant,
    CompilationTarget Target,
    ImmutableArray<Diagnostic> Diagnostics);

public static class StandardLibraryProjectService
{
    public static ImmutableArray<StandardLibraryValidationResult> Validate(CTildeProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Configuration.Kind != CTildeProjectKind.StandardLibrary)
            throw new ArgumentException("The project is not a standard-library project.", nameof(project));

        ValidatePhysicalInventory(project);
        var variants = new[]
        {
            new Variant("hosted-baseline", CompilationTarget.Hosted, CompilationArchitecture.Auto, false, false, false, StandardVectorTypes.None, ImmutableArray<CpuFeature>.Empty),
            new Variant("hosted-full", CompilationTarget.Hosted, CompilationArchitecture.X64, true, true, true, StandardVectorTypes.All, [CpuFeature.Simd128]),
            new Variant("cosmopolitan-full", CompilationTarget.Cosmopolitan, CompilationArchitecture.X64, true, true, true, StandardVectorTypes.All, [CpuFeature.Simd128]),
            new Variant("esp-idf-full", CompilationTarget.EspIdf, CompilationArchitecture.Xtensa, true, true, false, StandardVectorTypes.All, ImmutableArray<CpuFeature>.Empty),
            new Variant("freestanding-baseline", CompilationTarget.Freestanding, CompilationArchitecture.X64, false, false, false, StandardVectorTypes.None, ImmutableArray<CpuFeature>.Empty),
            new Variant("freestanding-full", CompilationTarget.Freestanding, CompilationArchitecture.X64, true, true, false, StandardVectorTypes.Simd, [CpuFeature.Simd128]),
        };

        return [.. variants.Select(variant =>
        {
            var trees = StandardLibrary.GetPhysicalSyntaxTrees(project.RootDirectory, variant.Target,
                variant.NativeIntegers, variant.NativeUtf8, variant.HostedIo, variant.Vectors);
            var options = new CompilationOptions(variant.Target, Architecture: variant.Architecture, CpuFeatures: variant.CpuFeatures);
            return new StandardLibraryValidationResult(variant.Name, variant.Target,
                Compilation.CreateStandardLibrary(trees, options).GetDiagnostics());
        })];
    }

    internal static ImmutableArray<SyntaxTree> LoadEditorTrees(
        string sourceRoot,
        string documentPath,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var normalized = Path.GetFullPath(documentPath).Replace('\\', '/');
        var target = normalized.Contains("/Esp/Idf/", StringComparison.OrdinalIgnoreCase)
            ? CompilationTarget.EspIdf
            : normalized.EndsWith("/MemoryFreestanding.ct", StringComparison.OrdinalIgnoreCase)
                ? CompilationTarget.Freestanding
                : CompilationTarget.Hosted;
        return StandardLibrary.GetPhysicalSyntaxTrees(sourceRoot, target, false, false,
            target == CompilationTarget.Hosted, StandardVectorTypes.All, overrides, applyTransforms: false);
    }

    private static void ValidatePhysicalInventory(CTildeProject project)
    {
        var expected = Directory.EnumerateFiles(project.RootDirectory, "*.ct", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .ToHashSet(PathComparer);
        if (!expected.SetEquals(project.SourceFiles.Select(Path.GetFullPath)))
            throw new CTildeProjectException($"Standard-library manifest '{project.ManifestPath}' must include every physical .ct source exactly once.");
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Variant(
        string Name,
        CompilationTarget Target,
        CompilationArchitecture Architecture,
        bool NativeIntegers,
        bool NativeUtf8,
        bool HostedIo,
        StandardVectorTypes Vectors,
        ImmutableArray<CpuFeature> CpuFeatures);
}
