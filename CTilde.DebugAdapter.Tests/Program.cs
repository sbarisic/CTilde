using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using CTilde.DebugAdapter;

var tests = new List<(string Name, Action Run)>
{
    ("MI result parsing", MiResultParsing),
    ("DAP framing and initialize", DapFraming),
    ("MI stream parsing", MiStreamParsing),
    ("fake GDB command lifecycle", FakeGdbLifecycle),
    ("debug control 32-bit layout", () => DebugControlLayout(4)),
    ("debug control 64-bit layout", () => DebugControlLayout(8)),
    ("debug control bitmap across words", DebugControlBitmap),
    ("logical breakpoint relocation stays in method", LogicalBreakpointRelocation),
    ("logical local lifetimes and shadowing", LogicalLocalLifetimes),
    ("descriptor validation", DescriptorValidation),
    ("stale source rejection", StaleSourceRejection),
    ("QEMU descriptor validation", QemuDescriptorValidation),
    ("malformed QEMU descriptor rejection", MalformedQemuDescriptorRejection),
    ("unsupported target rejection", UnsupportedTargetRejection),
    ("owned QEMU port conflict", OwnedQemuPortConflict),
    ("owned QEMU lifecycle", OwnedQemuLifecycle),
    ("owned QEMU early exit", OwnedQemuEarlyExit),
};
if (args is ["--real", var descriptor])
    tests.Add(("real hosted DAP session", () => RealDapSession(descriptor)));
if (args is ["--real-qemu", var qemuDescriptor])
    tests.Add(("real ESP-IDF QEMU DAP session", () => RealQemuDapSession(qemuDescriptor)));
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
return failures;

static void MiResultParsing()
{
    var record = MiParser.Parse("17^done,bkpt={number=\"4\",fullname=\"C:\\\\work\\\\Program.ct\",line=\"9\"}")!;
    Equal(17, record.Token);
    Equal('^', record.Kind);
    Equal("done", record.Name);
    var breakpoint = MiParser.Tuple(record.Results["bkpt"]);
    Equal("4", MiParser.String(breakpoint, "number"));
    Equal("9", MiParser.String(breakpoint, "line"));
}

static void DapFraming()
{
    var adapter = typeof(DebugTarget).Assembly.Location;
    var start = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    start.ArgumentList.Add(adapter);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
    var json = "{\"seq\":1,\"type\":\"request\",\"command\":\"initialize\",\"arguments\":{\"adapterID\":\"ctilde\",\"linesStartAt1\":true,\"columnsStartAt1\":true}}";
    var payload = Encoding.UTF8.GetBytes(json);
    var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
    process.StandardInput.BaseStream.Write(header);
    process.StandardInput.BaseStream.Write(payload);
    process.StandardInput.BaseStream.Flush();
    using var responseDocument = ReadDapDocumentWithTimeout(process);
    var response = responseDocument.RootElement.GetRawText();
    if (!response.Contains("\"success\":true", StringComparison.Ordinal) ||
        !response.Contains("\"supportsConfigurationDoneRequest\":true", StringComparison.Ordinal))
        throw new InvalidOperationException("The initialize response did not advertise the required C~ capabilities: " + response);
    process.StandardInput.Close();
    if (!process.WaitForExit(3000)) process.Kill(true);
}

static void RealDapSession(string descriptor)
{
    var adapter = typeof(DebugTarget).Assembly.Location;
    var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(adapter);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
    var sequence = 0;
    SendDap(process, ++sequence, "initialize", new { adapterID = "ctilde", linesStartAt1 = true, columnsStartAt1 = true });
    ReadUntil(process, message => message.RootElement.TryGetProperty("command", out var command) && command.GetString() == "initialize");
    SendDap(process, ++sequence, "launch", new { request = "launch", debugTarget = Path.GetFullPath(descriptor), stopAtEntry = true, showRuntimeFrames = false, externalConsole = true, trace = false, memoryDiagnostics = "objects" });
    var initialized = false;
    var launched = false;
    while (!initialized || !launched)
    {
        using var message = ReadDapDocumentWithTimeout(process);
        var root = message.RootElement;
        initialized |= root.TryGetProperty("event", out var eventName) && eventName.GetString() == "initialized";
        launched |= root.TryGetProperty("command", out var command) && command.GetString() == "launch" && root.GetProperty("success").GetBoolean();
    }
    using var target = JsonDocument.Parse(File.ReadAllText(descriptor));
    var source = target.RootElement.GetProperty("sources")[0].GetProperty("path").GetString()!;
    SendDap(process, ++sequence, "setBreakpoints", new { source = new { name = Path.GetFileName(source), path = source }, breakpoints = new[] { new { line = 13, condition = "left == 2" } } });
    using var breakpoints = ReadUntil(process, message => Response(message, "setBreakpoints"));
    if (!breakpoints.RootElement.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean())
        throw new InvalidOperationException("The real GDB session did not verify the C~ source breakpoint: " + breakpoints.RootElement.GetRawText());
    SendDap(process, ++sequence, "configurationDone", new { });
    using var configured = ReadUntil(process, message => Response(message, "configurationDone"));
    using var entryStopped = ReadUntil(process, message => message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "stopped");
    SendDap(process, ++sequence, "continue", new { threadId = 1 });
    using var continued = ReadUntil(process, message => Response(message, "continue"));
    using var stopped = ReadUntil(process, message => message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "stopped");
    if (stopped.RootElement.GetProperty("body").GetProperty("reason").GetString() != "breakpoint")
        throw new InvalidOperationException("The real C~ line-13 stop was not reported as a logical breakpoint: " + stopped.RootElement.GetRawText());
    SendDap(process, ++sequence, "threads", new { });
    using var threads = ReadUntil(process, message => Response(message, "threads"));
    if (threads.RootElement.GetProperty("body").GetProperty("threads").GetArrayLength() == 0)
        throw new InvalidOperationException("The real GDB session returned no threads.");
    SendDap(process, ++sequence, "stackTrace", new { threadId = 1 });
    using var stack = ReadUntil(process, message => Response(message, "stackTrace"));
    if (stack.RootElement.GetProperty("body").GetProperty("stackFrames").GetArrayLength() == 0)
        throw new InvalidOperationException("The real GDB session returned no C~ stack frames.");
    var firstSource = stack.RootElement.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("source").GetProperty("path").GetString();
    var firstLine = stack.RootElement.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("line").GetInt32();
    if (!string.Equals(Path.GetFullPath(source), firstSource, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"The real GDB frame mapped to '{firstSource}', not '{source}'. Stop={stopped.RootElement.GetRawText()} Stack={stack.RootElement.GetRawText()}");
    Equal(13, firstLine);
    var frameId = stack.RootElement.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
    SendDap(process, ++sequence, "scopes", new { frameId });
    using var scopes = ReadUntil(process, message => Response(message, "scopes"));
    var localsReference = scopes.RootElement.GetProperty("body").GetProperty("scopes")[0].GetProperty("variablesReference").GetInt32();
    SendDap(process, ++sequence, "variables", new { variablesReference = localsReference });
    using var variables = ReadUntil(process, message => Response(message, "variables"));
    var locals = variables.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
        .ToDictionary(variable => variable.GetProperty("name").GetString()!, variable => variable.GetProperty("value").GetString()!, StringComparer.Ordinal);
    Equal("2", locals["left"]);
    Equal("3", locals["right"]);
    Equal("5", locals["result"]);
    foreach (var expected in new[] { (Name: "left", Value: "2"), (Name: "right", Value: "3"), (Name: "result", Value: "5") })
    {
        SendDap(process, ++sequence, "evaluate", new { expression = expected.Name, frameId, context = "watch" });
        using var evaluated = ReadUntil(process, message => Response(message, "evaluate"));
        Equal(expected.Value, evaluated.RootElement.GetProperty("body").GetProperty("result").GetString());
    }
    var runtimeReference = scopes.RootElement.GetProperty("body").GetProperty("scopes")[3].GetProperty("variablesReference").GetInt32();
    SendDap(process, ++sequence, "variables", new { variablesReference = runtimeReference });
    using var runtime = ReadUntil(process, message => Response(message, "variables"));
    if (!runtime.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
        .Any(variable => variable.GetProperty("name").GetString() == "Live object count"))
        throw new InvalidOperationException("The real C~ breakpoint returned no ARC/runtime diagnostics.");
    var probe = runtime.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
        .Single(variable => variable.GetProperty("name").GetString() == "Current probe site").GetProperty("value").GetString();
    Equal("4 — Program.ct:13 (statement)", probe);
    SendDap(process, ++sequence, "next", new { threadId = 1 });
    using var nextResponse = ReadUntil(process, message => Response(message, "next"));
    using var nextStop = ReadUntil(process, message => message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "stopped");
    SendDap(process, ++sequence, "stackTrace", new { threadId = 1 });
    using var nextStack = ReadUntil(process, message => Response(message, "stackTrace"));
    Equal(14, nextStack.RootElement.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("line").GetInt32());
    SendDap(process, ++sequence, "restart", new { });
    using var restarted = ReadUntil(process, message => Response(message, "restart"));
    using var restartedStop = ReadUntil(process, message => message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "stopped");
    SendDap(process, ++sequence, "disconnect", new { terminateDebuggee = true });
    using var disconnected = ReadUntil(process, message => Response(message, "disconnect"));
    process.StandardInput.Close();
    if (!process.WaitForExit(5000)) process.Kill(true);
}

static void RealQemuDapSession(string descriptor)
{
    using var targetDocument = JsonDocument.Parse(File.ReadAllText(descriptor));
    var targetRoot = targetDocument.RootElement;
    var mapPath = targetRoot.GetProperty("debugMap").GetString()!;
    using var mapDocument = JsonDocument.Parse(File.ReadAllText(mapPath));
    var mapRoot = mapDocument.RootElement;
    var entryName = mapRoot.GetProperty("entryPoint").GetString();
    var entry = mapRoot.GetProperty("functions").EnumerateArray().Single(function => function.GetProperty("name").GetString() == entryName);
    var site = entry.GetProperty("sites").EnumerateArray().First(candidate => candidate.GetProperty("kind").GetString() == "statement");
    var sourceInfo = site.GetProperty("source");
    var source = Path.GetFullPath(Path.Combine(targetRoot.GetProperty("sourceRoot").GetString()!, sourceInfo.GetProperty("file").GetString()!));
    var line = sourceInfo.GetProperty("line").GetInt32();

    var adapter = typeof(DebugTarget).Assembly.Location;
    var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(adapter);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Adapter process did not start.");
    var sequence = 0;
    SendDap(process, ++sequence, "initialize", new { adapterID = "ctilde", linesStartAt1 = true, columnsStartAt1 = true });
    using var initializedResponse = ReadUntilQemu(process, message => Response(message, "initialize"), "initialize response");
    SendDap(process, ++sequence, "launch", new { request = "launch", debugTarget = Path.GetFullPath(descriptor), stopAtEntry = true, showRuntimeFrames = false, trace = true, memoryDiagnostics = "objects" });
    var initialized = false;
    var launched = false;
    while (!initialized || !launched)
    {
        using var message = ReadDapDocumentWithTimeout(process, TimeSpan.FromSeconds(40));
        var root = message.RootElement;
        initialized |= root.TryGetProperty("event", out var eventName) && eventName.GetString() == "initialized";
        launched |= root.TryGetProperty("command", out var command) && command.GetString() == "launch" && root.GetProperty("success").GetBoolean();
    }
    SendDap(process, ++sequence, "setBreakpoints", new { source = new { name = Path.GetFileName(source), path = source }, breakpoints = new[] { new { line } } });
    using var breakpoints = ReadUntilQemu(process, message => Response(message, "setBreakpoints"), "breakpoint response");
    if (!breakpoints.RootElement.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean())
        throw new InvalidOperationException("The QEMU session did not verify the C~ source breakpoint.");
    SendDap(process, ++sequence, "configurationDone", new { });
    using var configured = ReadUntilQemu(process, message => Response(message, "configurationDone"), "configuration response");
    RequireSuccess(configured, "configurationDone");
    using var entryStopped = ReadUntilQemu(process, message => StoppedFor(message, "entry"), "startup entry stop");
    SendDap(process, ++sequence, "threads", new { });
    using var entryThreads = ReadUntilQemu(process, message => Response(message, "threads"), "entry threads response");
    SendDap(process, ++sequence, "continue", new { threadId = 1 });
    using var continued = ReadUntilQemu(process, message => Response(message, "continue"), "continue response");
    using var breakpointStopped = ReadUntilQemu(process, message => StoppedFor(message, "breakpoint"), "logical breakpoint stop");
    SendDap(process, ++sequence, "stackTrace", new { threadId = 1 });
    using var stack = ReadUntilQemu(process, message => Response(message, "stackTrace"), "stack response");
    var firstFrame = stack.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
    Equal(line, firstFrame.GetProperty("line").GetInt32());
    Equal(Path.GetFullPath(source), firstFrame.GetProperty("source").GetProperty("path").GetString());
    SendDap(process, ++sequence, "next", new { threadId = 1 });
    using var nextResponse = ReadUntilQemu(process, message => Response(message, "next"), "next response");
    using var nextStop = ReadUntilQemu(process, message => StoppedFor(message, "step"), "next stop");
    SendDap(process, ++sequence, "continue", new { threadId = 1 });
    using var finalContinue = ReadUntilQemu(process, message => Response(message, "continue"), "final continue response");
    using var marker = ReadUntilQemu(process, message => message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "output" &&
        message.RootElement.GetProperty("body").GetProperty("output").GetString()!.Contains("CTILDE_ESP_QEMU_OK", StringComparison.Ordinal), "completion marker");
    SendDap(process, ++sequence, "restart", new { });
    using var restarted = ReadUntilQemu(process, message => Response(message, "restart"), "restart response");
    RequireSuccess(restarted, "restart");
    using var restartedEntry = ReadUntilQemu(process, message => StoppedFor(message, "entry"), "restarted entry stop");
    SendDap(process, ++sequence, "disconnect", new { terminateDebuggee = true });
    using var disconnected = ReadUntilQemu(process, message => Response(message, "disconnect"), "disconnect response");
    process.StandardInput.Close();
    if (!process.WaitForExit(10000)) process.Kill(true);
    if (OwnedQemuSession.PortIsOpenAsync("127.0.0.1", 3333, TimeSpan.FromMilliseconds(200), CancellationToken.None).GetAwaiter().GetResult())
        throw new InvalidOperationException("The QEMU DAP session left port 3333 open after disconnect.");
}

static bool StoppedFor(JsonDocument message, string reason) =>
    message.RootElement.TryGetProperty("event", out var eventName) && eventName.GetString() == "stopped" &&
    message.RootElement.GetProperty("body").GetProperty("reason").GetString() == reason;

static void RequireSuccess(JsonDocument response, string operation)
{
    if (!response.RootElement.GetProperty("success").GetBoolean())
        throw new InvalidOperationException($"QEMU DAP {operation} failed: {response.RootElement.GetRawText()}");
}

static bool Response(JsonDocument message, string command) =>
    message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "response" &&
    message.RootElement.TryGetProperty("command", out var responseCommand) && responseCommand.GetString() == command;

static void SendDap(Process process, int sequence, string command, object arguments)
{
    var json = JsonSerializer.Serialize(new { seq = sequence, type = "request", command, arguments });
    var length = Encoding.UTF8.GetByteCount(json);
    process.StandardInput.Write($"Content-Length: {length}\r\n\r\n");
    process.StandardInput.Write(json);
    process.StandardInput.Flush();
}

static JsonDocument ReadUntil(Process process, Func<JsonDocument, bool> predicate)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        JsonDocument message;
        try { message = ReadDapDocumentWithTimeout(process, remaining); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException("Timed out waiting for the expected DAP message. " + process.StandardError.ReadToEnd());
        }
        if (predicate(message)) return message;
        message.Dispose();
    }
    throw new TimeoutException("Timed out waiting for the expected DAP message. " + process.StandardError.ReadToEnd());
}

static JsonDocument ReadUntilQemu(Process process, Func<JsonDocument, bool> predicate, string stage)
{
    var seen = new List<string>();
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
    while (DateTime.UtcNow < deadline)
    {
        JsonDocument message;
        try { message = ReadDapDocumentWithTimeout(process, deadline - DateTime.UtcNow); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new TimeoutException($"Timed out waiting for QEMU DAP {stage}. Seen: {string.Join(Environment.NewLine, seen)} Adapter stderr: {process.StandardError.ReadToEnd()}");
        }
        if (predicate(message)) return message;
        seen.Add(message.RootElement.GetRawText());
        message.Dispose();
    }
    throw new TimeoutException($"Timed out waiting for QEMU DAP {stage}. Seen: {string.Join(Environment.NewLine, seen)} Adapter stderr: {process.StandardError.ReadToEnd()}");
}

static JsonDocument ReadDapDocument(Process process)
{
    try { return JsonDocument.Parse(ReadDapMessage(process.StandardOutput.BaseStream)); }
    catch (EndOfStreamException exception)
    {
        process.WaitForExit(2000);
        throw new EndOfStreamException(exception.Message + " " + process.StandardError.ReadToEnd(), exception);
    }
}

static JsonDocument ReadDapDocumentWithTimeout(Process process, TimeSpan? timeout = null) =>
    Task.Run(() => ReadDapDocument(process)).WaitAsync(timeout ?? TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

static string ReadDapMessage(Stream stream)
{
    var header = new List<byte>();
    while (header.Count < 8192)
    {
        var value = stream.ReadByte();
        if (value < 0) throw new EndOfStreamException("Adapter closed before a DAP response.");
        header.Add((byte)value);
        if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
    }
    var headerText = Encoding.ASCII.GetString(header.ToArray());
    var lengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var length = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..].Trim());
    var payload = new byte[length];
    var offset = 0;
    while (offset < length)
    {
        var read = stream.Read(payload, offset, length - offset);
        if (read == 0) throw new EndOfStreamException("Adapter truncated a DAP response.");
        offset += read;
    }
    return Encoding.UTF8.GetString(payload);
}

static void MiStreamParsing()
{
    var output = MiParser.Parse("~\"line one\\nline two\\tend\"")!;
    Equal("line one\nline two\tend", output.Text);
    var stopped = MiParser.Parse("*stopped,reason=\"breakpoint-hit\",thread-id=\"1\"")!;
    Equal("breakpoint-hit", MiParser.String(stopped.Results, "reason"));
}

static void FakeGdbLifecycle()
{
    var root = Path.Combine(Path.GetTempPath(), "ctilde-fake-gdb", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var script = Path.Combine(root, "fake-gdb.ps1");
    File.WriteAllText(script, """
    while ($null -ne ($line = [Console]::ReadLine())) {
      if ($line -notmatch '^(\d+)(.*)$') { continue }
      $token = $Matches[1]
      $command = $Matches[2]
      if ($command -like '*fail*') { [Console]::WriteLine($token + '^error,msg="fake failure"'); continue }
      if ($command -like '*gdb-exit*') { [Console]::WriteLine($token + '^exit'); exit 0 }
      [Console]::WriteLine($token + '^done,value="ok"')
    }
    """);
    using var gdb = new GdbMi();
    try
    {
        gdb.Start("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], root);
        var result = gdb.CommandAsync("-thread-info").GetAwaiter().GetResult();
        Equal("ok", MiParser.String(result.Results, "value"));
        Throws<InvalidOperationException>(() => gdb.CommandAsync("-fail").GetAwaiter().GetResult(), "fake failure");
        gdb.CloseAsync().GetAwaiter().GetResult();
    }
    finally { try { Directory.Delete(root, true); } catch (IOException) { } }
}

static void DebugControlLayout(int pointerSize)
{
    var pointerWidth = pointerSize;
    var fields = new Dictionary<string, DebugMemoryField>(StringComparer.Ordinal)
    {
        ["Magic"] = new() { Offset = 0, Width = 4 },
        ["SiteCount"] = new() { Offset = 4, Width = 4 },
        ["SessionActive"] = new() { Offset = 8, Width = 4 },
        ["CurrentThread"] = new() { Offset = 16, Width = pointerWidth },
        ["CurrentActivation"] = new() { Offset = 16 + pointerWidth, Width = pointerWidth },
        ["CurrentSite"] = new() { Offset = 16 + pointerWidth * 2, Width = 4 },
        ["CurrentReason"] = new() { Offset = 20 + pointerWidth * 2, Width = 4 },
        ["CurrentObject"] = new() { Offset = 24 + pointerWidth * 2, Width = pointerWidth },
        ["CurrentValue"] = new() { Offset = 24 + pointerWidth * 3, Width = 4 },
        ["CurrentCode"] = new() { Offset = 28 + pointerWidth * 3, Width = pointerWidth },
        ["CurrentFile"] = new() { Offset = 28 + pointerWidth * 4, Width = pointerWidth },
        ["CurrentLine"] = new() { Offset = 28 + pointerWidth * 5, Width = 4 },
    };
    var size = 44 + pointerWidth * 5;
    var image = new DebugControlImage(new DebugMemoryLayout { PointerSize = pointerSize, Size = size, EnabledOffset = size - 12, Fields = fields }, new byte[size]);
    image.Write("Magic", DebugControlImage.Magic);
    image.Write("SiteCount", 65);
    image.Write("CurrentThread", pointerSize == 8 ? 0x1020304050607080UL : 0x50607080UL);
    image.Write("CurrentActivation", 7);
    image.Write("CurrentSite", 4);
    image.Write("CurrentReason", 1);
    image.Write("CurrentObject", 9);
    image.Write("CurrentValue", 3);
    image.Write("CurrentCode", 10);
    image.Write("CurrentFile", 11);
    image.Write("CurrentLine", 13);
    var snapshot = image.Snapshot();
    image.ValidateHeader(65);
    Equal((uint)4, snapshot.Site);
    Equal((uint)1, snapshot.Reason);
    Equal((ulong)7, snapshot.Activation);
    Equal(13, snapshot.Line);
    image.ValidateHeader(64);
    Throws<InvalidDataException>(() => image.ValidateHeader(66), "requires at least");
    var malformed = new DebugControlImage(image.Layout, new byte[size]);
    Throws<InvalidDataException>(() => malformed.ValidateHeader(65), "magic");
}

static void DebugControlBitmap()
{
    var words = DebugControlImage.BuildEnabledSiteWords(70, [0, 31, 32, 64, 69]);
    Equal(3, words.Length);
    Equal(0x80000001u, words[0]);
    Equal(1u, words[1]);
    Equal(0x21u, words[2]);
    Equal(3, LogicalDebugModel.ParseHitCondition("3"));
    Equal<int?>(null, LogicalDebugModel.ParseHitCondition("0"));
    Equal<int?>(null, LogicalDebugModel.ParseHitCondition("abc"));
    Throws<InvalidDataException>(() => DebugControlImage.BuildEnabledSiteWords(4, [4]), "Invalid C~ debug site");
}

static void LogicalBreakpointRelocation()
{
    var root = Path.GetTempPath();
    var source = Path.Combine(root, "Program.ct");
    var first = new DebugFunction
    {
        Name = "first",
        DisplayName = "First",
        Source = new DebugSource { File = source, Line = 5 },
        Sites = [new DebugSite { Id = 0, Source = new DebugSource { File = source, Line = 7, Column = 5 } }, new DebugSite { Id = 1, Source = new DebugSource { File = source, Line = 9, Column = 5 } }],
    };
    var second = new DebugFunction
    {
        Name = "second",
        DisplayName = "Second",
        Source = new DebugSource { File = source, Line = 20 },
        Sites = [new DebugSite { Id = 2, Source = new DebugSource { File = source, Line = 22, Column = 5 } }],
    };
    Equal(1, LogicalDebugModel.FindExecutableSite([first, second], root, source, 8, 1)!.Value.Site.Id);
    if (LogicalDebugModel.FindExecutableSite([first, second], root, source, 10, 1) is not null)
        throw new InvalidOperationException("A breakpoint after First incorrectly relocated into Second.");
}

static void LogicalLocalLifetimes()
{
    var function = new DebugFunction
    {
        Name = "main",
        DisplayName = "Main",
        Scopes = [new DebugScope { Id = 1, Source = new DebugSource { SpanLength = 100 } }, new DebugScope { Id = 2, Parent = 1, Source = new DebugSource { SpanLength = 20 } }],
        Locals =
        [
            new DebugVariable { Name = "future", Storage = "f", LiveStart = 60, LiveEnd = 90, ScopeId = 1 },
            new DebugVariable { Name = "value", Storage = "outer", LiveStart = 10, LiveEnd = 90, ScopeId = 1 },
            new DebugVariable { Name = "value", Storage = "inner", LiveStart = 30, LiveEnd = 50, ScopeId = 2 },
            new DebugVariable { Name = "expired", Storage = "e", LiveStart = 1, LiveEnd = 20, ScopeId = 1 },
        ],
    };
    var site = new DebugSite { Id = 4, Source = new DebugSource { SpanStart = 40 } };
    var locals = LogicalDebugModel.LiveLocals(function, site);
    Equal(1, locals.Length);
    Equal("inner", locals[0].Storage);
}

static void DescriptorValidation()
{
    using var fixture = DescriptorFixture.Create("hosted");
    var (target, map) = DebugTargetValidator.Load(fixture.Descriptor, fixture.Gdb);
    Equal("hosted", target.Target);
    Equal(3, map.Version);
    Equal(fixture.Gdb, target.GdbCommand);
}

static void StaleSourceRejection()
{
    using var fixture = DescriptorFixture.Create("hosted");
    File.AppendAllText(fixture.Source, "changed");
    Throws<InvalidDataException>(() => DebugTargetValidator.Load(fixture.Descriptor, fixture.Gdb), "stale");
}

static void QemuDescriptorValidation()
{
    using var fixture = DescriptorFixture.Create("qemu");
    var (target, map) = DebugTargetValidator.Load(fixture.Descriptor, null);
    Equal("esp-idf", target.Target);
    Equal("qemu", target.TargetEnvironment);
    Equal("ct_debug_qemu_ready", map.RuntimeHooks.Ready);
    Throws<InvalidDataException>(() => DebugTargetValidator.Load(fixture.Descriptor, fixture.Gdb), "cross-GDB");
}

static void MalformedQemuDescriptorRejection()
{
    using (var fixture = DescriptorFixture.Create("qemu"))
    {
        var descriptor = File.ReadAllText(fixture.Descriptor).Replace("\"ownsProcess\":true", "\"ownsProcess\":false", StringComparison.Ordinal);
        File.WriteAllText(fixture.Descriptor, descriptor);
        Throws<InvalidDataException>(() => DebugTargetValidator.Load(fixture.Descriptor, null), "owned launch command");
    }

    using (var fixture = DescriptorFixture.Create("qemu"))
    {
        using var descriptor = JsonDocument.Parse(File.ReadAllText(fixture.Descriptor));
        var mapPath = descriptor.RootElement.GetProperty("debugMap").GetString()!;
        var map = File.ReadAllText(mapPath).Replace("\"ready\":\"ct_debug_qemu_ready\"", "\"ready\":\"\"", StringComparison.Ordinal);
        File.WriteAllText(mapPath, map);
        Throws<InvalidDataException>(() => DebugTargetValidator.Load(fixture.Descriptor, null), "ready hook");
    }
}

static void UnsupportedTargetRejection()
{
    using var fixture = DescriptorFixture.Create("physical");
    Throws<InvalidDataException>(() => DebugTargetValidator.Load(fixture.Descriptor, null), "QEMU Debug Launch");
}

static void OwnedQemuPortConflict()
{
    var port = FreePort();
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
    listener.Start();
    var launch = PowerShellLaunch("Start-Sleep -Seconds 5");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var qemu = new OwnedQemuSession(launch, "127.0.0.1", port, TimeSpan.FromMilliseconds(500));
    try { Throws<InvalidOperationException>(() => qemu.StartAsync(cancellation.Token).GetAwaiter().GetResult(), "already in use"); }
    finally { qemu.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void OwnedQemuLifecycle()
{
    var port = FreePort();
    var launch = PowerShellLaunch($"$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,{port});$l.Start();Write-Output 'QEMU_TEST_READY';while($true){{$c=$l.AcceptTcpClient();$c.Close()}}");
    var output = new StringBuilder();
    var qemu = new OwnedQemuSession(launch, "127.0.0.1", port, TimeSpan.FromSeconds(5));
    qemu.Output += (_, text) => output.Append(text);
    try
    {
        qemu.StartAsync().GetAwaiter().GetResult();
        if (!SpinWait.SpinUntil(() => output.ToString().Contains("QEMU_TEST_READY", StringComparison.Ordinal), TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Owned QEMU output was not forwarded.");
        var processId = qemu.ProcessId ?? throw new InvalidOperationException("Owned QEMU returned no process id.");
        qemu.StopAsync().GetAwaiter().GetResult();
        if (OwnedQemuSession.PortIsOpenAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(100), CancellationToken.None).GetAwaiter().GetResult())
            throw new InvalidOperationException("Owned QEMU left its TCP port open.");
        try { Process.GetProcessById(processId); throw new InvalidOperationException("Owned QEMU process survived Stop."); }
        catch (ArgumentException) { }
    }
    finally { qemu.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void OwnedQemuEarlyExit()
{
    var port = FreePort();
    var qemu = new OwnedQemuSession(PowerShellLaunch("exit 17"), "127.0.0.1", port, TimeSpan.FromSeconds(3));
    try { Throws<InvalidOperationException>(() => qemu.StartAsync().GetAwaiter().GetResult(), "exited with code 17"); }
    finally { qemu.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static DebugLaunchCommand PowerShellLaunch(string command) => new()
{
    FileName = "powershell.exe",
    Arguments = ["-NoProfile", "-Command", command],
    WorkingDirectory = Path.GetTempPath(),
    OwnsProcess = true,
};

static int FreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<T>(Action action, string text) where T : Exception
{
    try { action(); }
    catch (T exception) when (exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{text}'.");
}

sealed class DescriptorFixture : IDisposable
{
    private readonly string _root;
    internal string Descriptor { get; }
    internal string Source { get; }
    internal string Gdb { get; }

    private DescriptorFixture(string root, string descriptor, string source, string gdb)
    {
        _root = root; Descriptor = descriptor; Source = source; Gdb = gdb;
    }

    internal static DescriptorFixture Create(string target)
    {
        var root = Path.Combine(Path.GetTempPath(), "ctilde-debug-adapter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Program.ct");
        var program = Path.Combine(root, "program.exe");
        var map = Path.Combine(root, "program.ctdebug.json");
        var descriptor = Path.Combine(root, "target.json");
        var gdb = Path.Combine(root, "gdb.exe");
        File.WriteAllText(source, "public static class Program {}", Encoding.UTF8);
        File.WriteAllBytes(program, [1]);
        File.WriteAllText(gdb, string.Empty);
        var qemu = target == "qemu";
        File.WriteAllText(map, "{\"version\":3,\"instrumented\":true,\"memoryDiagnostics\":\"objects\",\"functions\":[],\"runtimeHooks\":{\"throw\":\"ct_throw\",\"fatal\":\"ct_fatal\",\"control\":\"ct_debug_control\",\"trap\":\"ct_debug_qemu_trap\",\"ready\":\"ct_debug_qemu_ready\"},\"runtimeControl\":{\"symbol\":\"ct_debug_control\",\"layouts\":[{\"pointerSize\":8,\"size\":8,\"enabledOffset\":4,\"fields\":{\"Magic\":{\"offset\":0,\"width\":4}}}]}}");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var targetName = target == "hosted" ? "hosted" : "esp-idf";
        var qemuFields = qemu
            ? ",\"targetEnvironment\":\"qemu\",\"debugStub\":\"esp-qemu-native-gdb\",\"debugTransport\":\"tcp-remote-gdb\",\"espTarget\":\"esp32\",\"launch\":{\"fileName\":\"powershell.exe\",\"arguments\":[],\"workingDirectory\":\"" + Json(root) + "\",\"environment\":{},\"ownsProcess\":true},\"gdbHost\":\"127.0.0.1\",\"gdbPort\":3333"
            : target == "physical" ? ",\"targetEnvironment\":\"native\",\"debugStub\":\"esp-uart-gdbstub\",\"debugTransport\":\"uart-remote-gdb\"" : string.Empty;
        File.WriteAllText(descriptor, $$"""
        {"version":3,"runtimeAbi":16,"target":"{{targetName}}"{{qemuFields}},"backend":"gdb","program":"{{Json(program)}}","debugMap":"{{Json(map)}}","sourceRoot":"{{Json(root)}}","workingDirectory":"{{Json(root)}}","gdbCommand":"{{Json(gdb)}}","instrumented":true,"memoryDiagnostics":"objects","sources":[{"path":"{{Json(source)}}","sha256":"{{hash}}"}]}
        """);
        return new DescriptorFixture(root, descriptor, source, gdb);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    private static string Json(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
