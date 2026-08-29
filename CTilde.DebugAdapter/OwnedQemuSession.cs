using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CTilde.DebugAdapter;

internal sealed class OwnedQemuSession : IAsyncDisposable
{
    private readonly DebugLaunchCommand _launch;
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _startupTimeout;
    private Process? _process;
    private SafeFileHandle? _job;

    internal OwnedQemuSession(DebugLaunchCommand launch, string host, int port, TimeSpan? startupTimeout = null)
    {
        _launch = launch;
        _host = host;
        _port = port;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);
    }

    internal event Action<string, string>? Output;
    internal event Action<int?>? Exited;
    internal int? ProcessId => _process?.Id;

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
            throw new InvalidOperationException("The owned ESP-IDF QEMU process is already running.");
        if (await PortIsOpenAsync(_host, _port, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"ESP-IDF QEMU cannot start because {_host}:{_port} is already in use. Stop the process using that GDB port and try again.");

        var start = new ProcessStartInfo
        {
            FileName = _launch.FileName,
            WorkingDirectory = _launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in _launch.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var pair in _launch.Environment)
            start.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) Output?.Invoke("stdout", args.Data + Environment.NewLine); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Output?.Invoke("stderr", args.Data + Environment.NewLine); };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(Volatile.Read(ref _process), process))
                Exited?.Invoke(process.ExitCode);
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The owned ESP-IDF QEMU process did not start.");
            _process = process;
            AttachProcessTree(process);
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await WaitForServerAsync(process, _startupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    internal async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        var job = Interlocked.Exchange(ref _job, null);
        if (job is not null)
            job.Dispose();
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        process.Dispose();
        await WaitForPortClosedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private async Task WaitForServerAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"ESP-IDF QEMU exited with code {process.ExitCode} before its GDB server became ready.");
            if (await PortIsOpenAsync(_host, _port, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
                return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"ESP-IDF QEMU did not open its GDB server at {_host}:{_port} within {timeout.TotalSeconds:0} seconds.");
    }

    private async Task WaitForPortClosedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await PortIsOpenAsync(_host, _port, TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false))
                return;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> PortIsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void AttachProcessTree(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the ESP-IDF QEMU process Job Object.");
        var information = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };
        if (!NativeMethods.SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure the ESP-IDF QEMU process Job Object.");
        }
        if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not assign ESP-IDF QEMU to its process Job Object.");
        }
        _job = job;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static class NativeMethods
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
            ref JobObjectExtendedLimitInformation information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }
    }
}
