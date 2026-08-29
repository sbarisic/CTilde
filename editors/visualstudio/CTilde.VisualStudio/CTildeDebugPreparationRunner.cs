using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Net.Sockets;
using CTilde.VisualStudio.Core;

namespace CTilde.VisualStudio;

internal static class CTildeDebugPreparationRunner
{
    internal static async Task<CTildeDebugPreparation> PrepareAsync(CTildeProjectContract contract, CancellationToken cancellationToken)
    {
        using var leaseCheck = CTildeProjectLaunchLease.Acquire(contract.ManifestPath);
        var options = CTildeToolPaths.Current;
        var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var compilerDll = string.IsNullOrWhiteSpace(options.CompilerPath)
            ? Path.Combine(extensionDirectory, "Tools", "Compiler", "ctilde.dll")
            : Path.GetFullPath(options.CompilerPath);
        if (!File.Exists(compilerDll))
            throw new FileNotFoundException("The C~ compiler was not found.", compilerDll);
        var preparation = DebugLaunchContracts.CreatePreparation(compilerDll, contract.ManifestPath,
            options.DebugCompiler, Environment.GetEnvironmentVariable("CTILDE_CC"), options.DebugMemory,
            options.EspIdfPath, options.EspClangPath);
        if (preparation.Target != "hosted" && await PortIsOpenAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("ESP-IDF QEMU cannot prepare because 127.0.0.1:3333 is already in use. Stop the active QEMU debug session and try again.");
        Directory.CreateDirectory(Path.GetDirectoryName(preparation.DescriptorPath)!);
        var dotnet = string.IsNullOrWhiteSpace(options.DotNetPath) ? "dotnet" : options.DotNetPath;
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = CommandContracts.JoinWindowsArguments(preparation.Arguments),
            WorkingDirectory = Path.GetDirectoryName(contract.ManifestPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var output = new ConcurrentQueue<string>();
        void RecordOutput(string line)
        {
            output.Enqueue(line);
            Trace.WriteLine(line, "C~ debug preparation");
        }
        RecordOutput($"Preparing C~ debug target with {preparation.Toolchain}: {contract.ManifestPath}");
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) RecordOutput(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) RecordOutput(args.Data); };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The C~ debug preparation process did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using (cancellationToken.Register(() => Kill(process)))
                await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(CommandOutcomes.MissingDotNetMessage(exception.Message), exception);
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"C~ debug preparation failed with exit code {process.ExitCode}.{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
        if (!File.Exists(preparation.DescriptorPath))
            throw new FileNotFoundException("C~ debug preparation did not produce its descriptor.", preparation.DescriptorPath);
        Trace.WriteLine($"Prepared C~ debug target: {preparation.DescriptorPath}", "C~ debug preparation");
        return preparation;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {process.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
    }

    private static async Task<bool> PortIsOpenAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync("127.0.0.1", 3333);
            var timeout = Task.Delay(250, cancellationToken);
            return await Task.WhenAny(connect, timeout).ConfigureAwait(false) == connect && !connect.IsFaulted && client.Connected;
        }
        catch (SocketException) { return false; }
    }
}
