using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CTilde.Cli;

internal enum DebugStubKind
{
    HostedNative,
    EspUartGdbStub,
    EspQemuNativeGdb,
}

internal static class DebugPreparation
{
    public static void WriteDescriptor(BuildRequest request, NativeBuildOutcome native)
    {
        var program = request.Target == CTilde.CompilationTarget.Hosted
            ? request.ExecutablePath!
            : ReadEspProjectDescription(request).Elf;
        if (!File.Exists(program))
            throw new NativeBuildException($"Debug program was not produced: {program}");
        if (request.DebugMapPath is null || !File.Exists(request.DebugMapPath))
            throw new NativeBuildException("The compiler did not produce the required C~ debug map.");

        var backend = native.Backend;
        string? gdbCommand = null;
        string? serialPython = null;
        string[] gdbPrefix = [];
        string? espTarget = null;
        if (request.Target == CTilde.CompilationTarget.EspIdf)
        {
            var description = ReadEspProjectDescription(request);
            espTarget = description.Target;
            gdbCommand = FindEspTool(description.ToolPrefix + "gdb" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty), request.EspIdfPath);
            serialPython = FindEspPython(request.EspIdfPath);
        }
        else if (backend == "gdb")
        {
            if (native.WslCompiler is not null)
            {
                gdbCommand = native.CompilerCommand;
                gdbPrefix = ["--exec", "gdb"];
            }
            else
                gdbCommand = FindHostedGdb(native.CompilerCommand);
        }

        var sources = request.Inputs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new Dictionary<string, object?>
            {
                ["path"] = Path.GetFullPath(path),
                ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            }).ToArray();
        var hostedRun = request.Target == CTilde.CompilationTarget.Hosted ? request.RunConfiguration : null;
        var runArguments = hostedRun?.Arguments.Select((value, index) => ProjectRunDriver.Expand(
            value, request.RootDirectory, request.ExecutablePath, $"run.args[{index}]")).ToArray() ?? [];
        var runEnvironment = hostedRun?.Environment.ToDictionary(entry => entry.Key, entry => ProjectRunDriver.Expand(
            entry.Value, request.RootDirectory, request.ExecutablePath, $"run.environment.{entry.Key}"), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var stub = SelectStub(request);
        var descriptor = new Dictionary<string, object?>
        {
            ["generator"] = $"C~ draft {CompilerContract.DraftVersion}",
            ["version"] = CompilerContract.DebugMetadataVersion,
            ["runtimeAbi"] = CompilerContract.RuntimeAbiVersion,
            ["target"] = request.Target == CTilde.CompilationTarget.Hosted ? "hosted" : "esp-idf",
            ["targetEnvironment"] = request.Environment == CTilde.TargetEnvironment.Qemu ? "qemu" : "native",
            ["debugStub"] = StubName(stub),
            ["debugTransport"] = stub switch
            {
                DebugStubKind.HostedNative => "local-mi",
                DebugStubKind.EspUartGdbStub => "uart-remote-gdb",
                _ => "tcp-remote-gdb",
            },
            ["backend"] = backend,
            ["program"] = Path.GetFullPath(program),
            ["debugMap"] = Path.GetFullPath(request.DebugMapPath),
            ["sourceRoot"] = Path.GetFullPath(request.RootDirectory),
            ["workingDirectory"] = Path.GetFullPath(hostedRun?.WorkingDirectoryPath ?? request.RootDirectory),
            ["arguments"] = runArguments,
            ["environment"] = runEnvironment,
            ["compilerCommand"] = native.CompilerCommand,
            ["gdbCommand"] = gdbCommand,
            ["gdbPrefixArguments"] = gdbPrefix,
            ["serialPython"] = serialPython,
            ["espTarget"] = espTarget,
            ["espProject"] = request.EspIdfProjectDirectory,
            ["serialPort"] = request.SerialPort,
            ["baudRate"] = request.BaudRate,
            ["instrumented"] = request.DebugInformation == CTilde.DebugInformationMode.Instrumented,
            ["memoryDiagnostics"] = request.DebugMemory.ToString().ToLowerInvariant(),
            ["sources"] = sources,
        };
        if (request.Environment == CTilde.TargetEnvironment.Qemu)
        {
            var launch = EspIdfBuildDriver.CreateQemuLaunchRequest(request);
            descriptor["launch"] = new Dictionary<string, object?>
            {
                ["fileName"] = launch.FileName,
                ["arguments"] = launch.Arguments,
                ["workingDirectory"] = launch.WorkingDirectory,
                ["environment"] = launch.Environment ?? new Dictionary<string, string>(),
                ["ownsProcess"] = true,
            };
            descriptor["gdbHost"] = "127.0.0.1";
            descriptor["gdbPort"] = 3333;
        }
        WriteAtomically(request.DebugTargetPath!, JsonSerializer.Serialize(descriptor,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (request.Trace)
            Console.Error.WriteLine($"trace: wrote debug target {request.DebugTargetPath}");
    }

    internal static DebugStubKind SelectStub(BuildRequest request) => request.Target switch
    {
        CTilde.CompilationTarget.Hosted => DebugStubKind.HostedNative,
        CTilde.CompilationTarget.EspIdf when request.Environment == CTilde.TargetEnvironment.Qemu => DebugStubKind.EspQemuNativeGdb,
        _ => DebugStubKind.EspUartGdbStub,
    };

    private static string StubName(DebugStubKind stub) => stub switch
    {
        DebugStubKind.HostedNative => "hosted-native",
        DebugStubKind.EspUartGdbStub => "esp-uart-gdbstub",
        _ => "esp-qemu-native-gdb",
    };

    public static int ValidateAttach(BuildRequest request)
    {
        var path = request.DebugTargetPath!;
        if (!File.Exists(path))
            throw new NativeBuildException($"Debug target does not exist: {path}. Run a debug Launch first.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != CompilerContract.DebugMetadataVersion || root.GetProperty("runtimeAbi").GetInt32() != CompilerContract.RuntimeAbiVersion ||
                !root.GetProperty("instrumented").GetBoolean())
                throw new NativeBuildException("Existing debug metadata was produced by an incompatible compiler/runtime. Run a debug Launch first.");
            var expectedTarget = request.Target == CTilde.CompilationTarget.Hosted ? "hosted" : "esp-idf";
            if (!root.GetProperty("target").GetString()!.Equals(expectedTarget, StringComparison.Ordinal))
                throw new NativeBuildException($"Existing debug artifacts target {root.GetProperty("target").GetString()}, not {expectedTarget}.");
            var expectedEnvironment = request.Environment == CTilde.TargetEnvironment.Qemu ? "qemu" : "native";
            if (!root.TryGetProperty("targetEnvironment", out var storedEnvironment) || storedEnvironment.GetString() != expectedEnvironment)
                throw new NativeBuildException($"Existing debug artifacts use a different target environment. Run a debug Launch for {expectedEnvironment}.");
            var program = root.GetProperty("program").GetString()!;
            var debugMap = root.GetProperty("debugMap").GetString()!;
            if (!File.Exists(program) || !File.Exists(debugMap))
                throw new NativeBuildException("Existing debug artifacts are missing. Run a debug Launch first.");
            foreach (var source in root.GetProperty("sources").EnumerateArray())
            {
                var sourcePath = source.GetProperty("path").GetString()!;
                var expected = source.GetProperty("sha256").GetString()!;
                if (!File.Exists(sourcePath) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).Equals(expected, StringComparison.Ordinal))
                    throw new NativeBuildException($"Debug symbols are stale for '{sourcePath}'. Run a debug Launch before attaching.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new NativeBuildException($"Debug target '{path}' is invalid: {exception.Message}");
        }
    }

    private static (string Elf, string Target, string ToolPrefix) ReadEspProjectDescription(BuildRequest request)
    {
        var buildDirectory = request.EspIdfBuildDirectory;
        var path = Path.Combine(buildDirectory, "project_description.json");
        if (!File.Exists(path))
            throw new NativeBuildException($"ESP-IDF project description was not produced: {path}");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var elfName = root.GetProperty("app_elf").GetString()!;
            var target = root.GetProperty("target").GetString()!;
            var toolPrefix = root.TryGetProperty("monitor_toolprefix", out var prefix) ? prefix.GetString() ?? string.Empty : string.Empty;
            return (Path.GetFullPath(Path.Combine(buildDirectory, elfName)), target, toolPrefix);
        }
        catch (JsonException exception)
        {
            throw new NativeBuildException($"ESP-IDF project description '{path}' is invalid: {exception.Message}");
        }
    }

    private static string FindHostedGdb(string? compiler)
    {
        if (!string.IsNullOrWhiteSpace(compiler) && Path.IsPathFullyQualified(compiler))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(compiler)!, "gdb" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (File.Exists(sibling))
                return sibling;
        }
        return NativeToolDiscovery.FindOnPath("gdb") ?? NativeToolDiscovery.FindOnPath("gdb.exe") ?? "gdb";
    }

    private static string FindEspTool(string name, string? idfPath)
    {
        if (Path.IsPathFullyQualified(name) && File.Exists(name))
            return name;
        var fromPath = NativeToolDiscovery.FindOnPath(name);
        if (fromPath is not null)
            return fromPath;
        foreach (var toolsRoot in EspIdfEnvironment.ToolsRoots(idfPath))
        {
            var tool = FindToolUnder(toolsRoot, Path.GetFileName(name));
            if (tool is not null)
                return tool;
        }
        return name;
    }

    private static string? FindEspPython(string? idfPath)
    {
        var environment = Environment.GetEnvironmentVariable("IDF_PYTHON_ENV_PATH");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            var configured = Path.Combine(environment, OperatingSystem.IsWindows() ? "Scripts" : "bin",
                OperatingSystem.IsWindows() ? "python.exe" : "python");
            if (File.Exists(configured))
                return configured;
        }
        var profileEnvironment = EspIdfEnvironment.FindProfileVariable(idfPath, "IDF_PYTHON_ENV_PATH");
        if (!string.IsNullOrWhiteSpace(profileEnvironment))
        {
            var configured = Path.Combine(profileEnvironment, OperatingSystem.IsWindows() ? "Scripts" : "bin",
                OperatingSystem.IsWindows() ? "python.exe" : "python");
            if (File.Exists(configured))
                return configured;
        }
        foreach (var toolsRoot in EspIdfEnvironment.ToolsRoots(idfPath))
        {
            var python = FindToolUnder(toolsRoot, OperatingSystem.IsWindows() ? "python.exe" : "python");
            if (python is not null)
                return python;
        }
        return null;
    }

    private static string? FindToolUnder(string root, string fileName)
    {
        if (!Directory.Exists(root))
            return null;
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomically(string path, string contents) => AtomicFile.WriteTextIfChanged(path, contents);
}
