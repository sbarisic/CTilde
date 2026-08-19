using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    static Compilation Compile(string source, CompilationOptions? options = null, string path = "test.ct") => Compilation.Create([SyntaxTree.ParseText(source, path)], options);

    static string Emit(string source, CompilationOptions? options = null, string path = "test.ct")
    {
        var compilation = Compile(source, options, path);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var result = compilation.EmitC(writer);
        Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return writer.ToString();
    }

    static ProcessResult CompileAndRun(string source, bool memoryDiagnostics = false, string nativeSuffix = "", bool threads = false, string? standardInput = null, byte[]? standardInputBytes = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ctilde-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            var cPath = Path.Combine(directory, "program.c");
            var executablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "program.exe" : "program");
            File.WriteAllText(cPath, Emit(source) + nativeSuffix, new UTF8Encoding(false));
            var compilerResult = RunCompiler(cPath, executablePath, memoryDiagnostics, threads);
            Assert(compilerResult.ExitCode == 0, $"C compiler failed:{Environment.NewLine}{compilerResult.StandardOutput}{compilerResult.StandardError}");
            return RunCompiledProgram(executablePath, standardInput, standardInputBytes, standardInput is null && standardInputBytes is null ? null : directory);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    static ProcessResult RunCompiler(string cPath, string executablePath, bool memoryDiagnostics = false, bool threads = false)
    {
        var diagnosticDefine = memoryDiagnostics ? "CT_MEMORY_DIAGNOSTICS" : null;
        var configured = Environment.GetEnvironmentVariable("CTILDE_CC");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
            {
                var compiler = configured[4..];
                var linuxSource = WslPath(cPath);
                var linuxOutput = WslPath(executablePath);
                return RunGnuCompiler("wsl", ["--exec", compiler], linuxSource, linuxOutput, memoryDiagnostics, threads);
            }
            var compilerName = Path.GetFileNameWithoutExtension(configured);
            var arguments = compilerName.Equals("cl", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { "/nologo", "/std:clatest", "/O2", "/W4", "/WX", "/wd4702" }
                : null;
            if (arguments is not null)
            {
                if (diagnosticDefine is not null)
                    arguments.Add($"/D{diagnosticDefine}");
                arguments.Add($"/Fe:{executablePath}");
                arguments.Add(cPath);
            }
            return arguments is not null
                ? RunProcess(configured, arguments)
                : RunGnuCompiler(configured, [], cPath, executablePath, memoryDiagnostics, threads);
        }

        if (!OperatingSystem.IsWindows())
            return RunGnuCompiler("cc", [], cPath, executablePath, memoryDiagnostics, threads);

        var vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        Assert(File.Exists(vsWhere), "No C compiler was configured and vswhere.exe was not found.");
        var discovery = RunProcess(vsWhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]);
        Assert(discovery.ExitCode == 0 && !string.IsNullOrWhiteSpace(discovery.StandardOutput), "Visual Studio C tools were not found.");
        var installation = discovery.StandardOutput.Trim();
        var vcVars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        var commandFile = Path.Combine(Path.GetDirectoryName(cPath)!, "compile.cmd");
        var defineArgument = diagnosticDefine is null ? "" : $" /D{diagnosticDefine}";
        File.WriteAllText(commandFile, $"@echo off{Environment.NewLine}call \"{vcVars}\" >nul{Environment.NewLine}cl /nologo /std:clatest /O2 /W4 /WX /wd4702{defineArgument} /Fe:\"{executablePath}\" \"{cPath}\"{Environment.NewLine}", Encoding.ASCII);
        return RunProcess("cmd.exe", ["/d", "/c", commandFile]);
    }

    static ProcessResult RunGnuCompiler(string command, IReadOnlyList<string> prefix, string cPath, string executablePath, bool memoryDiagnostics = false, bool threads = false)
    {
        var configuredStandard = Environment.GetEnvironmentVariable("CTILDE_C_STANDARD");
        var standard = string.IsNullOrWhiteSpace(configuredStandard) ? "gnu23" : configuredStandard;
        var diagnosticArguments = memoryDiagnostics ? new[] { "-DCT_MEMORY_DIAGNOSTICS" } : [];
        var threadArguments = threads ? new[] { "-pthread" } : [];
        var sanitizerArguments = threads && Environment.GetEnvironmentVariable("CTILDE_THREAD_SANITIZER") == "1"
            ? new[] { "-fsanitize=thread", "-fno-omit-frame-pointer", "-g" }
            : [];
        var mathArguments = command.Equals("wsl", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows()
            ? new[] { "-lm" }
            : [];
        var result = RunProcess(command, [.. prefix, $"-std={standard}", "-O2", "-Wall", "-Wextra", "-Werror", .. diagnosticArguments, .. threadArguments, .. sanitizerArguments, "-o", executablePath, cPath, .. mathArguments]);
        if (!string.IsNullOrWhiteSpace(configuredStandard) || standard != "gnu23" || !RejectedCStandard(result))
            return result;
        return RunProcess(command, [.. prefix, "-std=gnu2x", "-O2", "-Wall", "-Wextra", "-Werror", .. diagnosticArguments, .. threadArguments, .. sanitizerArguments, "-o", executablePath, cPath, .. mathArguments]);
    }

    static bool RejectedCStandard(ProcessResult result)
    {
        if (result.ExitCode == 0)
            return false;
        var output = result.StandardOutput + result.StandardError;
        return output.Contains("gnu23", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("invalid value", StringComparison.OrdinalIgnoreCase));
    }

    static string WslPath(string path)
    {
        var windowsPath = Path.GetFullPath(path).Replace('\\', '/');
        var result = RunProcess("wsl", ["--exec", "wslpath", "-a", "-u", windowsPath]);
        Assert(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    static ProcessResult RunCompiledProgram(string executablePath, string? standardInput = null, byte[]? standardInputBytes = null, string? workingDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable("CTILDE_CC");
        if (configured?.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var arguments = new List<string>();
            if (workingDirectory is not null)
            {
                arguments.Add("--cd");
                arguments.Add(WslPath(workingDirectory));
            }
            arguments.Add("--exec");
            arguments.Add(WslPath(executablePath));
            return RunProcess("wsl", arguments, standardInput, standardInputBytes);
        }
        return RunProcess(executablePath, [], standardInput, standardInputBytes, workingDirectory);
    }

    static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string? standardInput = null, byte[]? standardInputBytes = null, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null || standardInputBytes is not null,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        else if (standardInputBytes is not null)
        {
            process.StandardInput.BaseStream.Write(standardInputBytes);
            process.StandardInput.Close();
        }
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    static string ProjectObjectAbi(string generated)
    {
        var result = new StringBuilder();
        var captureBlock = false;
        foreach (var line in Normalize(generated).Split('\n'))
        {
            if (line.StartsWith("struct ct_vtable", StringComparison.Ordinal) ||
                line.StartsWith("struct ct_t_8_Examples_4_Base", StringComparison.Ordinal) ||
                line.StartsWith("struct ct_t_8_Examples_7_Derived", StringComparison.Ordinal) ||
                line.StartsWith("static const ct_vtable ct_vtable_", StringComparison.Ordinal))
                captureBlock = true;

            var singleLine = line.StartsWith("typedef struct ct_object", StringComparison.Ordinal) ||
                line.StartsWith("typedef struct ct_box_", StringComparison.Ordinal) ||
                line.StartsWith("static ct_type_descriptor ct_desc_", StringComparison.Ordinal) ||
                line.StartsWith("static ct_object* ct_checked_cast", StringComparison.Ordinal) ||
                line.StartsWith("static ct_object* ct_safe_cast", StringComparison.Ordinal) ||
                line.Contains("ct_init_ct_ctor_", StringComparison.Ordinal) ||
                line.Contains("ct_l_", StringComparison.Ordinal) &&
                    (line.Contains("ct_checked_cast", StringComparison.Ordinal) || line.Contains("ct_safe_cast", StringComparison.Ordinal)) ||
                line.StartsWith("static ", StringComparison.Ordinal) &&
                    (line.Contains(" ct_vthunk_", StringComparison.Ordinal) ||
                     line.Contains(" ct_box_", StringComparison.Ordinal) ||
                     line.Contains(" ct_unbox_", StringComparison.Ordinal));

            if (captureBlock || singleLine)
                result.AppendLine(line);
            if (captureBlock && line == "};")
                captureBlock = false;
        }
        return Normalize(result.ToString()).Replace("E:/Projects/CTilde/examples/ObjectModel.ct", "test.ct", StringComparison.Ordinal);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
