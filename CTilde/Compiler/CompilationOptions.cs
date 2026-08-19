namespace CTilde;

public enum CompilationTarget
{
    Hosted,
    EspIdf,
}

public sealed record CompilationOptions(
    CompilationTarget Target = CompilationTarget.Hosted,
    string? SourceRoot = null);
