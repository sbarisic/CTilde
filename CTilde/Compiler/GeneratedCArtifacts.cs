using System.Collections.Immutable;

namespace CTilde;

public enum GeneratedCLayout
{
    Unity,
    Modules,
}

public enum GeneratedCArtifactKind
{
    RuntimeHeader,
    InternalHeader,
    DependencyHeader,
    RuntimeSource,
    NamespaceSource,
    EntrySource,
    SymbolMap,
    DebugMap,
    CMakeFragment,
}

public sealed record GeneratedCArtifact(string RelativePath, string Content, GeneratedCArtifactKind Kind);

public sealed record CBundleEmitResult(
    bool Success,
    ImmutableArray<GeneratedCArtifact> Artifacts,
    ImmutableArray<Diagnostic> Diagnostics);
