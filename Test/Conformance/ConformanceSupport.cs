using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    static Compilation Compile(string source, CompilationOptions? options = null, string path = "test.ct") => Compilation.Create([SyntaxTree.ParseText(source, path)], options);

    static Compilation Compile(IEnumerable<SyntaxTree> sources, CompilationOptions? options = null) => Compilation.Create(sources, options);

    static string Emit(string source, CompilationOptions? options = null, string path = "test.ct")
    {
        var compilation = Compile(source, options, path);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var result = compilation.EmitC(writer);
        Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return writer.ToString();
    }

    static string Emit(IEnumerable<SyntaxTree> sources, CompilationOptions? options = null)
    {
        var compilation = Compile(sources, options);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var result = compilation.EmitC(writer);
        Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return writer.ToString();
    }

    static ProcessResult CompileAndRun(string source, bool memoryDiagnostics = false, string nativeSuffix = "", bool threads = false, string? standardInput = null, byte[]? standardInputBytes = null, string? captureFile = null, bool conformance = false)
        => CompileAndRun([SyntaxTree.ParseText(source, "test.ct")], memoryDiagnostics, nativeSuffix, threads, standardInput, standardInputBytes, captureFile, conformance);

    static ProcessResult CompileAndRun(IEnumerable<SyntaxTree> sources, bool memoryDiagnostics = false, string nativeSuffix = "", bool threads = false, string? standardInput = null, byte[]? standardInputBytes = null, string? captureFile = null, bool conformance = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ctilde-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            var cPath = Path.Combine(directory, "program.c");
            var executablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "program.exe" : "program");
            File.WriteAllText(cPath, Emit(sources) + nativeSuffix, new UTF8Encoding(false));
            var compilerResult = RunCompiler(cPath, executablePath, memoryDiagnostics, threads, conformance);
            Assert(compilerResult.ExitCode == 0, $"C compiler failed:{Environment.NewLine}{compilerResult.StandardOutput}{compilerResult.StandardError}");
            var workingDirectory = standardInput is null && standardInputBytes is null && captureFile is null ? null : directory;
            var result = RunCompiledProgram(executablePath, standardInput, standardInputBytes, workingDirectory);
            if (captureFile is null)
                return result;

            Assert(!Path.IsPathFullyQualified(captureFile), "A captured native-test file must use a relative path.");
            var capturedPath = Path.GetFullPath(Path.Combine(directory, captureFile));
            var directoryPrefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Assert(capturedPath.StartsWith(directoryPrefix, comparison), "A captured native-test file cannot leave its temporary directory.");
            Assert(File.Exists(capturedPath), $"The native program did not create '{captureFile}'.");
            return result with { CapturedFile = File.ReadAllBytes(capturedPath) };
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    static ProcessResult RunCompiler(string cPath, string executablePath, bool memoryDiagnostics = false, bool threads = false, bool conformance = false)
    {
        var diagnosticDefines = new List<string>();
        if (memoryDiagnostics)
            diagnosticDefines.Add("CT_MEMORY_DIAGNOSTICS");
        if (conformance)
            diagnosticDefines.Add("CTILDE_CONFORMANCE");
        var configured = Environment.GetEnvironmentVariable("CTILDE_CC");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
            {
                var compiler = configured[4..];
                var linuxSource = WslPath(cPath);
                var linuxOutput = WslPath(executablePath);
                return RunGnuCompiler("wsl", ["--exec", compiler], linuxSource, linuxOutput, memoryDiagnostics, threads, conformance);
            }
            var compilerName = Path.GetFileNameWithoutExtension(configured);
            var arguments = compilerName.Equals("cl", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { "/nologo", "/std:clatest", "/O2", "/W4", "/WX", "/wd4702", $"/Fo:{Path.Combine(Path.GetDirectoryName(cPath)!, "program.obj")}" }
                : null;
            if (arguments is not null)
            {
                foreach (var define in diagnosticDefines)
                    arguments.Add($"/D{define}");
                arguments.Add($"/Fe:{executablePath}");
                arguments.Add(cPath);
            }
            return arguments is not null
                ? RunProcess(configured, arguments)
                : RunGnuCompiler(configured, [], cPath, executablePath, memoryDiagnostics, threads, conformance);
        }

        if (!OperatingSystem.IsWindows())
            return RunGnuCompiler("cc", [], cPath, executablePath, memoryDiagnostics, threads, conformance);

        var vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        Assert(File.Exists(vsWhere), "No C compiler was configured and vswhere.exe was not found.");
        var discovery = RunProcess(vsWhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]);
        Assert(discovery.ExitCode == 0 && !string.IsNullOrWhiteSpace(discovery.StandardOutput), "Visual Studio C tools were not found.");
        var installation = discovery.StandardOutput.Trim();
        var vcVars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        var commandFile = Path.Combine(Path.GetDirectoryName(cPath)!, "compile.cmd");
        var defineArgument = string.Concat(diagnosticDefines.Select(define => $" /D{define}"));
        var objectPath = Path.Combine(Path.GetDirectoryName(cPath)!, "program.obj");
        File.WriteAllText(commandFile, $"@echo off{Environment.NewLine}call \"{vcVars}\" >nul{Environment.NewLine}cl /nologo /std:clatest /O2 /W4 /WX /wd4702{defineArgument} /Fo:\"{objectPath}\" /Fe:\"{executablePath}\" \"{cPath}\"{Environment.NewLine}", Encoding.ASCII);
        return RunProcess("cmd.exe", ["/d", "/c", commandFile]);
    }

    static ProcessResult RunGnuCompiler(string command, IReadOnlyList<string> prefix, string cPath, string executablePath, bool memoryDiagnostics = false, bool threads = false, bool conformance = false)
    {
        var configuredStandard = Environment.GetEnvironmentVariable("CTILDE_C_STANDARD");
        var standard = string.IsNullOrWhiteSpace(configuredStandard) ? "gnu23" : configuredStandard;
        var diagnosticArguments = new List<string>();
        if (memoryDiagnostics)
            diagnosticArguments.Add("-DCT_MEMORY_DIAGNOSTICS");
        if (conformance)
            diagnosticArguments.Add("-DCTILDE_CONFORMANCE");
        var threadArguments = threads ? new[] { "-pthread" } : [];
        var addressSanitizers = Environment.GetEnvironmentVariable("CTILDE_SANITIZERS") == "1";
        var sanitizerArguments = threads && Environment.GetEnvironmentVariable("CTILDE_THREAD_SANITIZER") == "1"
            ? new[] { "-fsanitize=thread", "-fno-omit-frame-pointer", "-g" }
            : addressSanitizers
                ? new[] { "-fsanitize=address,undefined", "-fno-omit-frame-pointer", "-g" }
                : [];
        var optimization = addressSanitizers ? "-O1" : "-O2";
        var mathArguments = command.Equals("wsl", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows()
            ? new[] { "-lm" }
            : [];
        var result = RunProcess(command, [.. prefix, $"-std={standard}", optimization, "-Wall", "-Wextra", "-Werror", .. diagnosticArguments, .. threadArguments, .. sanitizerArguments, "-o", executablePath, cPath, .. mathArguments]);
        if (!string.IsNullOrWhiteSpace(configuredStandard) || standard != "gnu23" || !RejectedCStandard(result))
            return result;
        return RunProcess(command, [.. prefix, "-std=gnu2x", optimization, "-Wall", "-Wextra", "-Werror", .. diagnosticArguments, .. threadArguments, .. sanitizerArguments, "-o", executablePath, cPath, .. mathArguments]);
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
            if (Environment.GetEnvironmentVariable("CTILDE_SANITIZERS") == "1")
            {
                arguments.Add("env");
                arguments.Add("ASAN_OPTIONS=detect_leaks=0");
                arguments.Add("UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1");
            }
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
        var typeNames = System.Text.RegularExpressions.Regex.Matches(generated, "\\{ \\\"Examples\\.(?:Base|Derived)\\\".*?sizeof\\((ct_t_[0-9a-f]{24})\\)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var line in Normalize(generated).Split('\n'))
        {
            if (line.StartsWith("struct ct_vtable", StringComparison.Ordinal) ||
                typeNames.Any(name => line.StartsWith("struct " + name, StringComparison.Ordinal)) ||
                line.StartsWith("static const ct_vtable ct_v_", StringComparison.Ordinal))
                captureBlock = true;

            var singleLine = line.StartsWith("typedef struct ct_object", StringComparison.Ordinal) ||
                line.StartsWith("typedef struct ct_string", StringComparison.Ordinal) ||
                line.StartsWith("typedef struct ct_box_", StringComparison.Ordinal) ||
                line.StartsWith("static ct_type_descriptor ct_d_", StringComparison.Ordinal) ||
                line.StartsWith("static ct_object* ct_checked_cast", StringComparison.Ordinal) ||
                line.StartsWith("static ct_object* ct_safe_cast", StringComparison.Ordinal) ||
                line.Contains("ct_i_", StringComparison.Ordinal) ||
                line.Contains("ct_l_", StringComparison.Ordinal) &&
                    (line.Contains("ct_checked_cast", StringComparison.Ordinal) || line.Contains("ct_safe_cast", StringComparison.Ordinal)) ||
                line.StartsWith("static ", StringComparison.Ordinal) &&
                    (line.Contains(" ct_h_", StringComparison.Ordinal) ||
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

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, byte[]? CapturedFile = null);
