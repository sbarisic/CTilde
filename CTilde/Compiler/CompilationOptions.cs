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
    Instrumented,
}

public enum DebugMemoryMode
{
    Off,
    Objects,
    Guarded,
}

public sealed record CompilationOptions(
    CompilationTarget Target = CompilationTarget.Hosted,
    string? SourceRoot = null,
    DebugInformationMode DebugInformation = DebugInformationMode.None,
    DebugMemoryMode DebugMemory = DebugMemoryMode.Off);
