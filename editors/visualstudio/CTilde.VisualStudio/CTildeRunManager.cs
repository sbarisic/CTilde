using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CTilde.VisualStudio.Core;
using Newtonsoft.Json.Linq;

namespace CTilde.VisualStudio;

internal static class CTildeRunManager
{
    internal const string DebuggingUnavailableMessage =
        "C~ debugging is not available yet. Use Start Without Debugging (Ctrl+F5) or Run C~ Project.";
    internal const string RunUnsupportedMessage = "This C~ project manifest does not support Run.";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Process> Processes = new(StringComparer.OrdinalIgnoreCase);

    internal static bool SupportsRun(string projectPath)
    {
        try { return SupportsRun(CTildeProjectContract.Load(projectPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    internal static bool SupportsRun(CTildeProjectContract contract)
    {
        try { return ReadRunSupport(contract); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    internal static void Start(CTildeProjectContract contract)
    {
        bool supportsRun;
        try { supportsRun = ReadRunSupport(contract); }
        catch (Newtonsoft.Json.JsonException exception)
        {
            throw new InvalidDataException($"The C~ manifest is invalid: {exception.Message}", exception);
        }
        if (!supportsRun)
            throw new InvalidOperationException(RunUnsupportedMessage);
        var projectPath = Path.GetFullPath(contract.ProjectPath);
        var options = CTildeToolPaths.Current;
        var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var compiler = string.IsNullOrWhiteSpace(options.CompilerPath)
            ? Path.Combine(extensionDirectory, "Tools", "Compiler", "ctilde.dll")
            : Path.GetFullPath(options.CompilerPath);
        if (!File.Exists(compiler))
            throw new FileNotFoundException("The C~ compiler was not found.", compiler);
        var dotnet = string.IsNullOrWhiteSpace(options.DotNetPath) ? "dotnet" : options.DotNetPath;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = CommandContracts.JoinWindowsArguments(CommandContracts.Arguments(CTildeCommandKind.Run, compiler, contract.ManifestPath)),
                WorkingDirectory = Path.GetDirectoryName(contract.ManifestPath)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            },
            EnableRaisingEvents = true,
        };
        process.Exited += (_, _) => Remove(projectPath, process);
        try
        {
            lock (Gate)
            {
                if (Processes.ContainsKey(projectPath))
                    throw new InvalidOperationException("This C~ project already has a running process.");
                Processes.Add(projectPath, process);
                if (!process.Start())
                    throw new InvalidOperationException("The C~ project did not start.");
            }
            CTildeOutput.WriteLine($"Started C~ project in an external console: {contract.ManifestPath}");
        }
        catch (Win32Exception exception)
        {
            Remove(projectPath, process);
            throw new InvalidOperationException(CommandOutcomes.MissingDotNetMessage(exception.Message), exception);
        }
        catch
        {
            Remove(projectPath, process);
            throw;
        }
    }

    private static void Remove(string projectPath, Process process)
    {
        lock (Gate)
        {
            if (Processes.TryGetValue(projectPath, out var current) && ReferenceEquals(current, process))
                Processes.Remove(projectPath);
        }
        process.Dispose();
    }

    private static bool ReadRunSupport(CTildeProjectContract contract)
    {
        var manifest = JObject.Parse(File.ReadAllText(contract.ManifestPath));
        return RunSupport.IsSupported(manifest["kind"]?.Value<string>(), manifest["target"]?.Value<string>(), manifest["run"] is JObject);
    }
}
