using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CTilde.Cli;

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
        var descriptor = new Dictionary<string, object?>
        {
            ["generator"] = "C~ draft 0.14",
            ["version"] = 1,
            ["runtimeAbi"] = 14,
            ["target"] = request.Target == CTilde.CompilationTarget.Hosted ? "hosted" : "esp-idf",
            ["backend"] = backend,
            ["program"] = Path.GetFullPath(program),
            ["debugMap"] = Path.GetFullPath(request.DebugMapPath),
            ["sourceRoot"] = Path.GetFullPath(request.RootDirectory),
            ["workingDirectory"] = Path.GetFullPath(request.RootDirectory),
            ["compilerCommand"] = native.CompilerCommand,
            ["gdbCommand"] = gdbCommand,
            ["gdbPrefixArguments"] = gdbPrefix,
            ["serialPython"] = serialPython,
            ["espTarget"] = espTarget,
            ["espProject"] = request.EspIdfProjectDirectory,
            ["serialPort"] = request.SerialPort,
            ["baudRate"] = request.BaudRate,
            ["sources"] = sources,
        };
        WriteAtomically(request.DebugTargetPath!, JsonSerializer.Serialize(descriptor,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (request.Trace)
            Console.Error.WriteLine($"trace: wrote debug target {request.DebugTargetPath}");
    }

    public static int ValidateAttach(BuildRequest request)
    {
        var path = request.DebugTargetPath!;
        if (!File.Exists(path))
            throw new NativeBuildException($"Debug target does not exist: {path}. Run a debug Launch first.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("runtimeAbi").GetInt32() != 14)
                throw new NativeBuildException("Existing debug metadata was produced by an incompatible compiler/runtime. Run a debug Launch first.");
            var expectedTarget = request.Target == CTilde.CompilationTarget.Hosted ? "hosted" : "esp-idf";
            if (!root.GetProperty("target").GetString()!.Equals(expectedTarget, StringComparison.Ordinal))
                throw new NativeBuildException($"Existing debug artifacts target {root.GetProperty("target").GetString()}, not {expectedTarget}.");
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
        var buildDirectory = Path.Combine(request.EspIdfProjectDirectory!, "build");
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

    private static void WriteAtomically(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
