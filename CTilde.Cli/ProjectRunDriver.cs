using System.ComponentModel;
using System.Diagnostics;
using CTilde;

namespace CTilde.Cli;

internal static class ProjectRunDriver
{
    public static async Task<int> RunAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var configured = request.RunConfiguration;
        if (configured is null && request.Target is not (CompilationTarget.Hosted or CompilationTarget.Cosmopolitan))
            throw new NativeBuildException($"Target '{TargetName(request.Target)}' requires a 'run' object with a command in '{request.ManifestPath}'.");

        var executor = configured?.Executor ??
            (request.Target == CompilationTarget.Hosted && request.Compiler.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase)
                ? CTildeRunExecutor.Wsl
                : CTildeRunExecutor.Host);
        if (executor == CTildeRunExecutor.Wsl && !OperatingSystem.IsWindows())
            throw new NativeBuildException("The WSL run executor is available only when ctilde is running on Windows.");

        var output = request.ExecutablePath;
        if (output is not null && !File.Exists(output))
            throw new NativeBuildException($"Run build output does not exist: {output}");
        var hostWorkingDirectory = configured?.WorkingDirectoryPath ?? request.RootDirectory;
        if (!Directory.Exists(hostWorkingDirectory))
            throw new NativeBuildException($"Run working directory does not exist: {hostWorkingDirectory}");

        var projectRoot = request.RootDirectory;
        var buildOutput = output;
        if (executor == CTildeRunExecutor.Wsl)
        {
            projectRoot = await WslPathAsync(request.RootDirectory, request.RootDirectory, cancellationToken);
            hostWorkingDirectory = await WslPathAsync(hostWorkingDirectory, request.RootDirectory, cancellationToken);
            if (buildOutput is not null)
                buildOutput = await WslPathAsync(buildOutput, request.RootDirectory, cancellationToken);
        }

        var commandTemplate = configured?.Command;
        if (commandTemplate is null)
        {
            if (buildOutput is null)
                throw new NativeBuildException($"Target '{TargetName(request.Target)}' requires 'run.command'.");
            commandTemplate = "${buildOutput}";
        }
        var command = Expand(commandTemplate, projectRoot, buildOutput, "run.command");
        if (commandTemplate.Contains("${", StringComparison.Ordinal) || ContainsDirectorySeparator(commandTemplate))
        {
            var hostCommandValue = Expand(commandTemplate, request.RootDirectory, output, "run.command");
            var hostCommand = ResolveContainedPath(hostCommandValue, request.RootDirectory, "run.command");
            if (!File.Exists(hostCommand))
                throw new NativeBuildException($"Run command does not exist: {hostCommand}");
            command = executor == CTildeRunExecutor.Wsl
                ? await WslPathAsync(hostCommand, request.RootDirectory, cancellationToken)
                : hostCommand;
        }
        else if (Path.IsPathRooted(command) && executor == CTildeRunExecutor.Host && !File.Exists(command))
            throw new NativeBuildException($"Run command does not exist: {command}");

        var arguments = (configured?.Arguments ?? []).Select((value, index) =>
            Expand(value, projectRoot, buildOutput, $"run.args[{index}]")).ToArray();
        var environment = (configured?.Environment ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty)
            .ToDictionary(entry => entry.Key, entry => Expand(entry.Value, projectRoot, buildOutput, $"run.environment.{entry.Key}"), StringComparer.Ordinal);
        var successExitCodes = configured?.SuccessExitCodes ?? [0];

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
            WorkingDirectory = executor == CTildeRunExecutor.Host ? hostWorkingDirectory : request.RootDirectory,
        };
        if (executor == CTildeRunExecutor.Wsl)
        {
            startInfo.FileName = "wsl";
            startInfo.ArgumentList.Add("--cd");
            startInfo.ArgumentList.Add(hostWorkingDirectory);
            startInfo.ArgumentList.Add("--exec");
            if (environment.Count != 0)
            {
                startInfo.ArgumentList.Add("env");
                foreach (var entry in environment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    startInfo.ArgumentList.Add($"{entry.Key}={entry.Value}");
            }
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = command;
            foreach (var entry in environment)
                startInfo.Environment[entry.Key] = entry.Value;
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (request.Trace)
            Console.Error.WriteLine($"trace: running {command} ({executor.ToString().ToLowerInvariant()} executor)");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new NativeBuildException($"Could not start run command '{command}'.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new NativeBuildException($"Could not start run command '{command}': {exception.Message}", exception);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        if (request.Trace)
            Console.Error.WriteLine($"trace: run exit code {process.ExitCode}{(successExitCodes.Contains(process.ExitCode) ? " accepted" : " rejected")}");
        if (successExitCodes.Contains(process.ExitCode))
            return 0;
        Console.Error.WriteLine($"ctilde: Run command exited with code {process.ExitCode}; expected {string.Join(", ", successExitCodes)}.");
        return process.ExitCode == 0 ? 1 : process.ExitCode;
    }

    internal static string Expand(string value, string projectRoot, string? buildOutput, string property)
    {
        var result = value.Replace("${projectRoot}", projectRoot, StringComparison.Ordinal);
        if (result.Contains("${buildOutput}", StringComparison.Ordinal))
        {
            if (buildOutput is null)
                throw new NativeBuildException($"{property} uses ${{buildOutput}}, but this target has no executable or image output.");
            result = result.Replace("${buildOutput}", buildOutput, StringComparison.Ordinal);
        }
        return result;
    }

    private static string ResolveContainedPath(string value, string root, string property)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new NativeBuildException($"{property} must stay within the project directory.");
        return fullPath;
    }

    private static bool ContainsDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    private static async Task<string> WslPathAsync(string value, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest("wsl", ["--exec", "wslpath", "-a", "-u", value],
            workingDirectory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new NativeBuildException($"Could not translate run path '{value}' for WSL: {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    private static string TargetName(CompilationTarget target) => target switch
    {
        CompilationTarget.EspIdf => "esp-idf",
        CompilationTarget.Freestanding => "freestanding",
        CompilationTarget.Cosmopolitan => "cosmopolitan",
        _ => "hosted",
    };
}
