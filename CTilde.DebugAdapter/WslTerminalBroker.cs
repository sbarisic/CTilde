using System.Diagnostics;
using System.Text;

namespace CTilde.DebugAdapter;

internal sealed class WslTerminalBroker : IAsyncDisposable
{
    private readonly Process _process;
    internal string TtyPath { get; }

    private WslTerminalBroker(Process process, string ttyPath)
    {
        _process = process;
        TtyPath = ttyPath;
    }

    internal static async Task<WslTerminalBroker> StartAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString("N");
        var ttyFile = Path.Combine(Path.GetTempPath(), $"ctilde-wsl-tty-{token}.txt");
        var scriptFile = Path.Combine(Path.GetTempPath(), $"ctilde-wsl-terminal-{token}.py");
        await File.WriteAllTextAsync(scriptFile, """
        import os,pty,sys,threading
        master,slave=pty.openpty()
        with open(sys.argv[1],"w",encoding="utf-8") as output:
            output.write(os.ttyname(slave));output.flush()
        def console_to_target():
            try:
                while True:
                    data=os.read(sys.stdin.fileno(),4096)
                    if not data: break
                    os.write(master,data)
            except OSError: pass
        threading.Thread(target=console_to_target,daemon=True).start()
        try:
            while True:
                data=os.read(master,4096)
                if not data: break
                os.write(sys.stdout.fileno(),data)
        except OSError: pass
        """, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add("--exec");
        start.ArgumentList.Add("python3");
        start.ArgumentList.Add(ConvertPath(scriptFile));
        start.ArgumentList.Add(ConvertPath(ttyFile));
        var process = Process.Start(start) ?? throw new InvalidOperationException("The WSL debug console did not start.");
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(ttyFile))
                {
                    var tty = (await File.ReadAllTextAsync(ttyFile, cancellationToken).ConfigureAwait(false)).Trim();
                    if (tty.StartsWith("/dev/pts/", StringComparison.Ordinal))
                        return new WslTerminalBroker(process, tty);
                }
                if (process.HasExited) throw new InvalidOperationException("The WSL terminal broker exited before reporting its TTY.");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("Timed out waiting for the WSL debug console TTY.");
        }
        catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
        finally { TryDelete(scriptFile); TryDelete(ttyFile); }
    }

    internal static string ConvertPath(string path)
    {
        var start = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--exec"); start.ArgumentList.Add("wslpath"); start.ArgumentList.Add("-a"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("wslpath did not start.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException($"Could not convert '{path}' to a WSL path.");
        return output;
    }

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(true);
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
