using System.Collections.Immutable;

namespace CTilde;

public enum CompilationTarget
{
    Hosted,
    EspIdf,
    Freestanding,
    Cosmopolitan,
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

public enum TargetEnvironment
{
    Native,
    Qemu,
}

public enum EspIdfChip
{
    Esp32,
    Esp32C3,
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

public enum EspIdfPanicPolicy
{
    Abort,
    Restart,
    Halt,
}

public enum CpuFeature
{
    Simd128,
}

public sealed record CompilationOptions(
    CompilationTarget Target = CompilationTarget.Hosted,
    string? SourceRoot = null,
    DebugInformationMode DebugInformation = DebugInformationMode.None,
    DebugMemoryMode DebugMemory = DebugMemoryMode.Off,
    CompilationArchitecture Architecture = CompilationArchitecture.Auto,
    bool NoRecursion = false,
    string? SourceIdentityRoot = null,
    EspIdfPanicPolicy PanicPolicy = EspIdfPanicPolicy.Abort,
    ImmutableArray<CpuFeature> CpuFeatures = default,
    TargetEnvironment Environment = TargetEnvironment.Native);
