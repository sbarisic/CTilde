using System.Security.Cryptography;
using System.Text.Json;

namespace CTilde.DebugAdapter;

internal sealed class DebugTarget
{
    public int Version { get; init; }
    public int RuntimeAbi { get; init; }
    public string Target { get; init; } = string.Empty;
    public string TargetEnvironment { get; init; } = string.Empty;
    public string DebugStub { get; init; } = string.Empty;
    public string DebugTransport { get; init; } = string.Empty;
    public string Backend { get; init; } = string.Empty;
    public string Program { get; init; } = string.Empty;
    public string DebugMap { get; init; } = string.Empty;
    public string SourceRoot { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public string GdbCommand { get; set; } = string.Empty;
    public string[] GdbPrefixArguments { get; init; } = [];
    public string EspTarget { get; init; } = string.Empty;
    public DebugLaunchCommand? Launch { get; init; }
    public string GdbHost { get; init; } = string.Empty;
    public int GdbPort { get; init; }
    public bool Instrumented { get; init; }
    public string MemoryDiagnostics { get; init; } = string.Empty;
    public DebugSourceHash[] Sources { get; init; } = [];
}

internal sealed class DebugLaunchCommand
{
    public string FileName { get; init; } = string.Empty;
    public string[] Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public bool OwnsProcess { get; init; }
}

internal sealed class DebugSourceHash
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class DebugMap
{
    public int Version { get; init; }
    public bool Instrumented { get; init; }
    public string MemoryDiagnostics { get; init; } = string.Empty;
    public string EntryPoint { get; init; } = string.Empty;
    public DebugFunction[] Functions { get; init; } = [];
    public DebugType[] Types { get; init; } = [];
    public DebugBox[] Boxes { get; init; } = [];
    public DebugRuntimeHooks RuntimeHooks { get; init; } = new();
    public DebugMemoryBlock? RuntimeControl { get; init; }
    public DebugMemoryBlock? RuntimeSummary { get; init; }
}

internal sealed class DebugBox
{
    public string Type { get; init; } = string.Empty;
    public string Storage { get; init; } = string.Empty;
    public string ValueType { get; init; } = string.Empty;
}

internal sealed class DebugMemoryBlock
{
    public string Symbol { get; init; } = string.Empty;
    public DebugMemoryLayout[] Layouts { get; init; } = [];
}

internal sealed class DebugMemoryLayout
{
    public int PointerSize { get; init; }
    public int Size { get; init; }
    public int? EnabledOffset { get; init; }
    public Dictionary<string, DebugMemoryField> Fields { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class DebugMemoryField
{
    public int Offset { get; init; }
    public int Width { get; init; }
}

internal sealed class DebugType
{
    public string Name { get; init; } = string.Empty;
    public string Storage { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Base { get; init; }
    public DebugTypeField[] Fields { get; init; } = [];
}

internal sealed class DebugTypeField
{
    public string Name { get; init; } = string.Empty;
    public string Storage { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Static { get; init; }
}

internal sealed class DebugFunction
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Receiver { get; init; }
    public string? ReceiverType { get; init; }
    public DebugSource? Source { get; init; }
    public DebugVariable[] Parameters { get; init; } = [];
    public DebugVariable[] Locals { get; init; } = [];
    public DebugSite[] Sites { get; init; } = [];
    public DebugScope[] Scopes { get; init; } = [];
}

internal sealed class DebugVariable
{
    public string Name { get; init; } = string.Empty;
    public string Storage { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int? ScopeId { get; init; }
    public int? LiveStart { get; init; }
    public int? LiveEnd { get; init; }
}

internal sealed class DebugSite
{
    public int Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public DebugSource Source { get; init; } = new();
}

internal sealed class DebugSource
{
    public string File { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; } = 1;
    public int? SpanStart { get; init; }
    public int? SpanLength { get; init; }
}

internal sealed class DebugScope
{
    public int Id { get; init; }
    public int? Parent { get; init; }
    public DebugSource Source { get; init; } = new();
}

internal sealed class DebugRuntimeHooks
{
    public string Throw { get; init; } = string.Empty;
    public string Fatal { get; init; } = string.Empty;
    public string Control { get; init; } = string.Empty;
    public string Trap { get; init; } = string.Empty;
    public string Ready { get; init; } = string.Empty;
}

internal static class DebugTargetValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static (DebugTarget Target, DebugMap Map) Load(string descriptorPath, string? gdbOverride)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
            throw new InvalidDataException("The C~ debug launch did not specify a prepared descriptor.");
        descriptorPath = Path.GetFullPath(descriptorPath);
        if (!File.Exists(descriptorPath))
            throw new FileNotFoundException("The prepared C~ debug descriptor was not found.", descriptorPath);

        var target = Deserialize<DebugTarget>(descriptorPath, "debug descriptor");
        if (target.Version != 3 || target.RuntimeAbi <= 0 || !target.Instrumented)
            throw new InvalidDataException("The C~ debug descriptor is not version 3 instrumented metadata. Prepare the project again.");
        var hosted = target.Target.Equals("hosted", StringComparison.Ordinal);
        var qemu = target.Target.Equals("esp-idf", StringComparison.Ordinal) &&
            target.TargetEnvironment.Equals("qemu", StringComparison.Ordinal);
        if (!hosted && !qemu)
            throw new InvalidDataException("This Visual Studio debug adapter supports hosted and ESP-IDF QEMU Debug Launch targets only.");
        if (!target.Backend.Equals("gdb", StringComparison.Ordinal))
            throw new InvalidDataException("C~ debugging requires a GDB backend.");
        if (qemu)
            ValidateQemu(target, gdbOverride);
        RequireFile(target.Program, "prepared executable");
        RequireFile(target.DebugMap, "C~ debug map");

        var map = Deserialize<DebugMap>(target.DebugMap, "debug map");
        if (map.Version != 3 || !map.Instrumented)
            throw new InvalidDataException("The C~ debug map is not version 3 instrumented metadata. Prepare the project again.");
        if (map.RuntimeControl is null || string.IsNullOrWhiteSpace(map.RuntimeControl.Symbol) || map.RuntimeControl.Layouts.Length == 0)
            throw new InvalidDataException("The C~ debug map does not contain a runtime-control layout. Prepare the project again.");
        if (string.IsNullOrWhiteSpace(map.RuntimeHooks.Control) || string.IsNullOrWhiteSpace(map.RuntimeHooks.Trap))
            throw new InvalidDataException("The C~ debug map does not contain the logical control and trap hooks. Prepare the project again.");
        if (qemu && string.IsNullOrWhiteSpace(map.RuntimeHooks.Ready))
            throw new InvalidDataException("The C~ QEMU debug map does not contain the ready hook. Prepare the project again.");
        foreach (var source in target.Sources)
        {
            RequireFile(source.Path, "recorded source");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source.Path)));
            if (!actual.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"C~ debug metadata is stale for '{source.Path}'. Build the debug target again.");
        }

        if (hosted && !string.IsNullOrWhiteSpace(gdbOverride))
            target.GdbCommand = gdbOverride;
        if (string.IsNullOrWhiteSpace(target.GdbCommand))
            throw new InvalidDataException("No GDB executable is configured for this C~ debug target.");
        if (Path.IsPathFullyQualified(target.GdbCommand))
            RequireFile(target.GdbCommand, "GDB executable");
        return (target, map);
    }

    private static void ValidateQemu(DebugTarget target, string? gdbOverride)
    {
        if (!string.IsNullOrWhiteSpace(gdbOverride))
            throw new InvalidDataException("ESP-IDF QEMU debugging must use the target cross-GDB from the prepared descriptor; remove the Visual Studio GDB override.");
        if (!target.DebugStub.Equals("esp-qemu-native-gdb", StringComparison.Ordinal) ||
            !target.DebugTransport.Equals("tcp-remote-gdb", StringComparison.Ordinal))
            throw new InvalidDataException("The prepared ESP-IDF QEMU target does not use the native-GDB TCP debug stub and transport.");
        if (target.EspTarget is not ("esp32" or "esp32c3"))
            throw new InvalidDataException("Visual Studio QEMU debugging supports ESP32 and ESP32-C3 firmware only.");
        if (!target.GdbHost.Equals("127.0.0.1", StringComparison.Ordinal) || target.GdbPort != 3333)
            throw new InvalidDataException("The prepared ESP-IDF QEMU target must use 127.0.0.1:3333.");
        var launch = target.Launch;
        if (launch is null || !launch.OwnsProcess || string.IsNullOrWhiteSpace(launch.FileName) ||
            string.IsNullOrWhiteSpace(launch.WorkingDirectory) || !Directory.Exists(launch.WorkingDirectory))
            throw new InvalidDataException("The prepared ESP-IDF QEMU target does not contain a valid owned launch command.");
    }

    private static T Deserialize<T>(string path, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"The C~ {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The C~ {description} '{path}' is invalid: {exception.Message}", exception);
        }
    }

    private static void RequireFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"The {description} was not found.", path);
    }
}
