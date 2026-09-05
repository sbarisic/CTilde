using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CTilde.DebugAdapter;

internal sealed record MiRecord(int? Token, char Kind, string Name, IReadOnlyDictionary<string, object> Results, string? Text = null);

internal sealed class GdbMi : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MiRecord>> _pending = new();
    private readonly StringBuilder _buffer = new();
    private readonly object _writeGate = new();
    private Process? _process;
    private int _nextToken;

    internal event Action<MiRecord>? AsyncRecord;
    internal event Action<string, string>? Output;
    internal event Action<int?>? Exited;

    internal void Start(string command, IEnumerable<string> prefixArguments, string workingDirectory)
    {
        if (_process is not null)
            throw new InvalidOperationException("GDB is already running.");
        var start = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in prefixArguments.Concat(["--quiet", "--interpreter=mi2"]))
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) Consume(args.Data + "\n"); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Output?.Invoke("stderr", args.Data + "\n"); };
        process.Exited += (_, _) => OnExit(process);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("GDB did not start.");
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal async Task<MiRecord> CommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("GDB is not running.");
        var token = Interlocked.Increment(ref _nextToken);
        var completion = new TaskCompletionSource<MiRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(token, completion))
            throw new InvalidOperationException("Could not allocate a GDB command token.");
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            lock (_writeGate)
            {
                process.StandardInput.WriteLine(token.ToString(CultureInfo.InvariantCulture) + command);
                process.StandardInput.Flush();
            }
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(token, out _);
        }
    }

    internal async Task CloseAsync()
    {
        var process = _process;
        if (process is null)
            return;
        try { await CommandAsync("-gdb-exit").WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        }
    }

    private void Consume(string chunk)
    {
        lock (_buffer)
        {
            _buffer.Append(chunk);
            while (true)
            {
                var text = _buffer.ToString();
                var newline = text.IndexOf('\n');
                if (newline < 0)
                    return;
                _buffer.Remove(0, newline + 1);
                var record = MiParser.Parse(text[..newline].TrimEnd('\r'));
                if (record is null)
                    continue;
                if (record.Kind is '~' or '@' or '&')
                    Output?.Invoke(record.Kind == '~' ? "console" : record.Kind == '@' ? "stdout" : "stderr", record.Text ?? string.Empty);
                else if (record.Kind == '^' && record.Token is int token && _pending.TryGetValue(token, out var pending))
                {
                    if (record.Name == "error") pending.TrySetException(new InvalidOperationException(MiParser.String(record.Results, "msg", "GDB command failed.")));
                    else pending.TrySetResult(record);
                }
                else
                    AsyncRecord?.Invoke(record);
            }
        }
    }

    private void OnExit(Process process)
    {
        var code = process.ExitCode;
        Interlocked.CompareExchange(ref _process, null, process);
        var error = new InvalidOperationException($"GDB exited with code {code}.");
        foreach (var pending in _pending.Values)
            pending.TrySetException(error);
        _pending.Clear();
        Exited?.Invoke(code);
        process.Dispose();
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }
}

internal static class MiParser
{
    internal static MiRecord? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "(gdb)") return null;
        var position = 0;
        while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
        int? token = position == 0 ? null : int.Parse(text[..position], CultureInfo.InvariantCulture);
        if (position >= text.Length || "^*+=~@&".IndexOf(text[position]) < 0) return null;
        var kind = text[position++];
        if (kind is '~' or '@' or '&')
            return new MiRecord(token, kind, string.Empty, new Dictionary<string, object>(), ReadCString(text, ref position));
        var comma = text.IndexOf(',', position);
        var name = comma < 0 ? text[position..] : text[position..comma];
        position = comma < 0 ? text.Length : comma + 1;
        return new MiRecord(token, kind, name, ReadResults(text, ref position, '\0'));
    }

    internal static string String(IReadOnlyDictionary<string, object> values, string name, string fallback = "") =>
        values.TryGetValue(name, out var value) && value is string text ? text : fallback;

    internal static IReadOnlyDictionary<string, object> Tuple(object? value) =>
        value as IReadOnlyDictionary<string, object> ?? new Dictionary<string, object>();

    internal static IReadOnlyList<object> Array(object? value) => value switch
    {
        IReadOnlyList<object> items => items,
        null => [],
        _ => [value],
    };

    private static Dictionary<string, object> ReadResults(string text, ref int position, char terminator)
    {
        var results = new Dictionary<string, object>(StringComparer.Ordinal);
        while (position < text.Length && text[position] != terminator)
        {
            var start = position;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '-')) position++;
            if (position == start || position >= text.Length || text[position] != '=') break;
            var key = text[start..position++];
            var value = ReadValue(text, ref position);
            if (results.TryGetValue(key, out var old))
                results[key] = old is List<object> list ? list.Append(value).ToList() : new List<object> { old, value };
            else results[key] = value;
            if (position < text.Length && text[position] == ',') position++; else break;
        }
        return results;
    }

    private static object ReadValue(string text, ref int position)
    {
        if (position >= text.Length) return string.Empty;
        if (text[position] == '"') return ReadCString(text, ref position);
        if (text[position] == '{') { position++; var value = ReadResults(text, ref position, '}'); if (position < text.Length) position++; return value; }
        if (text[position] == '[')
        {
            position++;
            var values = new List<object>();
            while (position < text.Length && text[position] != ']')
            {
                var saved = position;
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '-')) position++;
                if (position > saved && position < text.Length && text[position] == '=')
                {
                    var key = text[saved..position++];
                    values.Add(new Dictionary<string, object> { [key] = ReadValue(text, ref position) });
                }
                else { position = saved; values.Add(ReadValue(text, ref position)); }
                if (position < text.Length && text[position] == ',') position++; else break;
            }
            if (position < text.Length && text[position] == ']') position++;
            return values;
        }
        var start = position;
        while (position < text.Length && text[position] is not (',' or '}' or ']')) position++;
        return text[start..position];
    }

    private static string ReadCString(string text, ref int position)
    {
        if (position >= text.Length || text[position] != '"') return text[position..];
        position++;
        var value = new StringBuilder();
        while (position < text.Length)
        {
            var character = text[position++];
            if (character == '"') break;
            if (character != '\\') { value.Append(character); continue; }
            if (position >= text.Length) break;
            var escaped = text[position++];
            if (escaped is >= '0' and <= '7')
            {
                var octal = escaped - '0';
                for (var digits = 1; digits < 3 && position < text.Length && text[position] is >= '0' and <= '7'; digits++)
                    octal = octal * 8 + text[position++] - '0';
                value.Append((char)octal);
                continue;
            }
            value.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', '"' => '"', '\\' => '\\', _ => escaped });
        }
        return value.ToString();
    }
}
