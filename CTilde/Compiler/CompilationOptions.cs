namespace CTilde;

public enum CompilationTarget
{
    Hosted,
    EspIdf,
}

public enum DebugInformationMode
{
    None,
    Source,
}

public sealed record CompilationOptions(
    CompilationTarget Target = CompilationTarget.Hosted,
    string? SourceRoot = null,
    DebugInformationMode DebugInformation = DebugInformationMode.None);
