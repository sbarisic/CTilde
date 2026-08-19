using System.Text;
using CTilde;

namespace CTilde.Cli;

internal static class NativeBuildDriver
{
    public static Task<int> BuildAsync(BuildRequest request, bool usesInlineAssembly, CancellationToken cancellationToken) =>
        request.Target == CompilationTarget.Hosted
            ? HostedBuildDriver.BuildAsync(request, usesInlineAssembly, cancellationToken)
            : EspIdfBuildDriver.BuildAsync(request, cancellationToken);
}

internal static class HostedBuildDriver
{
    public static async Task<int> BuildAsync(BuildRequest request, bool usesInlineAssembly, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.ExecutablePath!)!);
        var compiler = await ResolveCompilerAsync(request.Compiler, request.RootDirectory, cancellationToken);
        if (usesInlineAssembly && compiler.Kind == HostedCompilerKind.Msvc)
            throw new NativeBuildException("Inline assembly requires a GNU-compatible GCC or Clang compiler; MSVC is not supported for programs containing asm.");
        if (request.Trace)
            Console.Error.WriteLine($"trace: native compiler {compiler.Command}");
        var result = compiler.Kind == HostedCompilerKind.Msvc
            ? await CompileMsvcAsync(compiler, request, cancellationToken)
            : await CompileGnuAsync(compiler, request, cancellationToken);
        if (result == 0 && request.Trace)
            Console.Error.WriteLine($"trace: wrote native executable {request.ExecutablePath}");
        return result;
    }

    private static async Task<HostedCompiler> ResolveCompilerAsync(string configured, string workingDirectory, CancellationToken cancellationToken)
    {
        var value = configured;
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            value = Environment.GetEnvironmentVariable("CTILDE_CC") ?? "auto";
        if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsWindows() && value.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
            {
                var wsl = NativeToolDiscovery.FindOnPath("wsl") ?? throw new NativeBuildException("wsl.exe was not found.");
                return new HostedCompiler(wsl, HostedCompilerKind.WslGnu, null, value[4..]);
            }
            var known = value.ToLowerInvariant() switch
            {
                "msvc" => "cl",
                "gcc" => "gcc",
                "clang" => "clang",
                _ => value,
            };
            if (Path.GetFileNameWithoutExtension(known).Equals("cl", StringComparison.OrdinalIgnoreCase))
                return await ResolveMsvcAsync(known, workingDirectory, cancellationToken);
            var command = NativeToolDiscovery.FindOnPath(known) ?? throw new NativeBuildException($"Configured C compiler '{value}' was not found.");
            return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
        }

        if (OperatingSystem.IsWindows())
        {
            var pathCl = NativeToolDiscovery.FindOnPath("cl");
            if (pathCl is not null)
                return new HostedCompiler(pathCl, HostedCompilerKind.Msvc, null, null);
            try
            {
                return await ResolveMsvcAsync("cl", workingDirectory, cancellationToken);
            }
            catch (NativeBuildException)
            {
                foreach (var candidate in new[] { "clang", "gcc" })
                {
                    var command = NativeToolDiscovery.FindOnPath(candidate);
                    if (command is not null)
                        return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
                }
                throw;
            }
        }

        foreach (var candidate in new[] { "cc", "clang", "gcc" })
        {
            var command = NativeToolDiscovery.FindOnPath(candidate);
            if (command is not null)
                return new HostedCompiler(command, HostedCompilerKind.Gnu, null, null);
        }
        throw new NativeBuildException("No hosted C compiler was found. Install MSVC, GCC, or Clang, or pass --compiler.");
    }

    private static async Task<HostedCompiler> ResolveMsvcAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var existing = NativeToolDiscovery.FindOnPath(command);
        if (existing is not null)
            return new HostedCompiler(existing, HostedCompilerKind.Msvc, null, null);
        if (!OperatingSystem.IsWindows())
            throw new NativeBuildException("MSVC is available only on Windows.");

        var vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vsWhere))
            throw new NativeBuildException("MSVC was not found and vswhere.exe is unavailable.");
        var discovery = await NativeProcessRunner.RunAsync(new NativeProcessRequest(vsWhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            workingDirectory, ForwardOutput: false), cancellationToken);
        var installation = discovery.StandardOutput.Trim();
        if (discovery.ExitCode != 0 || installation.Length == 0)
            throw new NativeBuildException("Visual Studio C tools were not found.");
        var vcVars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcVars))
            throw new NativeBuildException($"MSVC environment script was not found: {vcVars}");

        NativeProcessResult environmentResult;
        var environmentScript = Path.Combine(Path.GetTempPath(), $"ctilde-vcvars-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(environmentScript, $"@call \"{vcVars}\" >nul{Environment.NewLine}@set{Environment.NewLine}", Encoding.ASCII);
            environmentResult = await NativeProcessRunner.RunAsync(new NativeProcessRequest("cmd.exe",
                ["/d", "/c", environmentScript], workingDirectory, ForwardOutput: false), cancellationToken);
        }
        finally
        {
            if (File.Exists(environmentScript))
                File.Delete(environmentScript);
        }
        if (environmentResult.ExitCode != 0)
            throw new NativeBuildException($"Could not initialize the MSVC build environment: {environmentResult.StandardError.Trim()}");
        var environment = ParseEnvironment(environmentResult.StandardOutput);
        var cl = NativeToolDiscovery.FindOnPath("cl", environment.GetValueOrDefault("PATH"));
        if (cl is null)
            throw new NativeBuildException("The initialized Visual Studio environment did not contain cl.exe.");
        return new HostedCompiler(cl, HostedCompilerKind.Msvc, environment, null);
    }

    private static Dictionary<string, string> ParseEnvironment(string contents)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                environment[line[..separator]] = line[(separator + 1)..];
        }
        return environment;
    }

    private static async Task<int> CompileMsvcAsync(HostedCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.Configuration == CTildeNativeBuildConfiguration.Debug
            ? new[] { "/Od", "/Zi" }
            : ["/O2"];
        var arguments = new List<string> { "/nologo", "/std:clatest", "/W4", "/WX", "/wd4702" };
        arguments.AddRange(configuration);
        arguments.Add($"/Fe:{request.ExecutablePath}");
        arguments.Add(request.GeneratedCPath!);
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        return result.ExitCode;
    }

    private static async Task<int> CompileGnuAsync(HostedCompiler compiler, BuildRequest request, CancellationToken cancellationToken)
    {
        var generatedC = request.GeneratedCPath!;
        var executable = request.ExecutablePath!;
        var prefix = Array.Empty<string>();
        if (compiler.Kind == HostedCompilerKind.WslGnu)
        {
            generatedC = await WslPathAsync(compiler.Command, generatedC, request.RootDirectory, cancellationToken);
            executable = await WslPathAsync(compiler.Command, executable, request.RootDirectory, cancellationToken);
            prefix = ["--exec", compiler.WslCompiler!];
        }
        var configuration = request.Configuration == CTildeNativeBuildConfiguration.Debug
            ? new[] { "-O0", "-g" }
            : ["-O2"];
        var arguments = new List<string>(prefix) { "-std=gnu23" };
        arguments.AddRange(configuration);
        arguments.AddRange(["-Wall", "-Wextra", "-Werror", "-o", executable, generatedC]);
        if (compiler.Kind == HostedCompilerKind.WslGnu || !OperatingSystem.IsWindows())
            arguments.Add("-lm");
        var first = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment, ForwardOutput: false), cancellationToken);
        if (first.ExitCode == 0)
            return first.ExitCode;
        if (!RejectedCStandard(first))
        {
            Console.Out.Write(first.StandardOutput);
            Console.Error.Write(first.StandardError);
            return first.ExitCode;
        }
        arguments[prefix.Length] = "-std=gnu2x";
        if (request.Trace)
            Console.Error.WriteLine("trace: compiler rejected gnu23; retrying with gnu2x");
        var fallback = await NativeProcessRunner.RunAsync(new NativeProcessRequest(compiler.Command, arguments,
            Path.GetDirectoryName(request.ExecutablePath!)!, compiler.Environment), cancellationToken);
        return fallback.ExitCode;
    }

    private static async Task<string> WslPathAsync(string wsl, string path, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(wsl,
            ["--exec", "wslpath", "-a", "-u", path], workingDirectory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new NativeBuildException($"Could not translate '{path}' to a WSL path: {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    private static bool RejectedCStandard(NativeProcessResult result)
    {
        var output = result.StandardOutput + result.StandardError;
        return output.Contains("gnu23", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("invalid value", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record HostedCompiler(
        string Command,
        HostedCompilerKind Kind,
        IReadOnlyDictionary<string, string>? Environment,
        string? WslCompiler);
    private enum HostedCompilerKind { Msvc, Gnu, WslGnu }
}

internal static class EspIdfBuildDriver
{
    public static async Task<int> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var project = request.EspIdfProjectDirectory!;
        if (!Directory.Exists(project) || !File.Exists(Path.Combine(project, "CMakeLists.txt")))
            throw new NativeBuildException($"ESP-IDF project '{project}' must contain CMakeLists.txt.");
        var componentDirectory = Path.Combine(project, "main");
        var componentFile = Path.Combine(componentDirectory, "CMakeLists.txt");
        if (!File.Exists(componentFile))
            throw new NativeBuildException($"ESP-IDF project '{project}' must contain main/CMakeLists.txt.");
        var generatedRelativePath = Path.GetRelativePath(componentDirectory, request.GeneratedCPath!).Replace('\\', '/');
        if (generatedRelativePath.StartsWith("../", StringComparison.Ordinal) ||
            !File.ReadAllText(componentFile).Contains(generatedRelativePath, StringComparison.Ordinal))
            throw new NativeBuildException($"ESP-IDF main/CMakeLists.txt must register generated source '{generatedRelativePath}'.");

        var idfCommand = NativeToolDiscovery.FindOnPath("idf.py");
        NativeProcessRequest process;
        if (idfCommand is not null)
        {
            process = OperatingSystem.IsWindows()
                ? CreateWindowsPythonRequest(idfCommand, project)
                : new NativeProcessRequest(idfCommand, ["build"], project);
        }
        else
        {
            var activeRequest = CreateActiveEnvironmentRequest(project, request.EspIdfPath);
            if (activeRequest is not null)
                process = activeRequest;
            else
            {
                var idfPath = request.EspIdfPath ?? Environment.GetEnvironmentVariable("IDF_PATH");
                if (string.IsNullOrWhiteSpace(idfPath) || !Directory.Exists(idfPath))
                    throw new NativeBuildException("ESP-IDF tools are not active. Open an ESP-IDF terminal or pass --idf-path.");
                process = CreateActivatedRequest(Path.GetFullPath(idfPath), project);
            }
        }
        if (request.Trace)
            Console.Error.WriteLine($"trace: running ESP-IDF build in {project}");
        var result = await NativeProcessRunner.RunAsync(process, cancellationToken);
        return result.ExitCode;
    }

    private static NativeProcessRequest? CreateActiveEnvironmentRequest(string project, string? requestedIdfPath)
    {
        var idfPath = Environment.GetEnvironmentVariable("IDF_PATH");
        var pythonEnvironment = Environment.GetEnvironmentVariable("IDF_PYTHON_ENV_PATH");
        if (string.IsNullOrWhiteSpace(idfPath) || string.IsNullOrWhiteSpace(pythonEnvironment))
            return null;
        if (!string.IsNullOrWhiteSpace(requestedIdfPath) &&
            !Path.GetFullPath(requestedIdfPath).Equals(Path.GetFullPath(idfPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        var idfScript = Path.Combine(idfPath, "tools", "idf.py");
        var python = OperatingSystem.IsWindows()
            ? Path.Combine(pythonEnvironment, "Scripts", "python.exe")
            : Path.Combine(pythonEnvironment, "bin", "python");
        return File.Exists(idfScript) && File.Exists(python)
            ? new NativeProcessRequest(python, [idfScript, "build"], project)
            : null;
    }

    private static NativeProcessRequest CreateWindowsPythonRequest(string idfCommand, string project)
    {
        var python = NativeToolDiscovery.FindOnPath("python") ?? NativeToolDiscovery.FindOnPath("python.exe");
        if (python is null)
            throw new NativeBuildException("idf.py was found, but its Python interpreter was not available.");
        return new NativeProcessRequest(python, [idfCommand, "build"], project);
    }

    private static NativeProcessRequest CreateActivatedRequest(string idfPath, string project)
    {
        if (OperatingSystem.IsWindows())
        {
            var exportScript = Path.Combine(idfPath, "export.ps1");
            if (!File.Exists(exportScript))
                throw new NativeBuildException($"ESP-IDF activation script was not found: {exportScript}");
            var script = $"$ErrorActionPreference='Stop'; . '{PowerShellQuote(exportScript)}' | Out-Host; & idf.py build; exit $LASTEXITCODE";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return new NativeProcessRequest("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], project);
        }

        var export = Path.Combine(idfPath, "export.sh");
        if (!File.Exists(export))
            throw new NativeBuildException($"ESP-IDF activation script was not found: {export}");
        var shell = NativeToolDiscovery.FindOnPath("bash") ?? throw new NativeBuildException("bash is required to activate ESP-IDF.");
        return new NativeProcessRequest(shell, ["-lc", $"source {ShellQuote(export)} >/dev/null && exec idf.py build"], project);
    }

    private static string PowerShellQuote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
