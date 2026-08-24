namespace CTilde;

public enum CompilationTarget
{
    Hosted,
    EspIdf,
}

public enum CompilationArchitecture
{
    Auto,
    X86,
    X64,
    Arm32,
    Arm64,
    Xtensa,
    RiscV32,
    RiscV64,
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
    DebugMemoryMode DebugMemory = DebugMemoryMode.Off,
    CompilationArchitecture Architecture = CompilationArchitecture.Auto);
