using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CTilde.Cli;

internal sealed record NativeProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed record NativeProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool ForwardOutput = true);

internal static class NativeProcessRunner
{
    public static async Task<NativeProcessResult> RunAsync(NativeProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        if (request.Environment is not null)
        {
            foreach (var entry in request.Environment)
                startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new NativeBuildException($"Could not start native tool '{request.FileName}'.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new NativeBuildException($"Could not start native tool '{request.FileName}': {exception.Message}", exception);
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, standardOutput, request.ForwardOutput ? Console.Out : null, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, standardError, request.ForwardOutput ? Console.Error : null, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
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
        return new NativeProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder capture, TextWriter? forward, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            capture.AppendLine(line);
            if (forward is not null)
                await forward.WriteLineAsync(line);
        }
    }
}

internal sealed class NativeBuildException : Exception
{
    public NativeBuildException(string message) : base(message) { }
    public NativeBuildException(string message, Exception innerException) : base(message, innerException) { }
}
