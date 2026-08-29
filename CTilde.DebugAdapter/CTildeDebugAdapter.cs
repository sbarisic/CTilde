using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using DapThread = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread;

namespace CTilde.DebugAdapter;

internal sealed class CTildeDebugAdapter : DebugAdapterBase, IDisposable
{
    private readonly GdbMi _gdb = new();
    private readonly object _eventGate = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly ConcurrentDictionary<int, VariableContainer> _variables = new();
    private readonly ConcurrentDictionary<int, int> _frameLevels = new();
    private readonly ConcurrentDictionary<int, DebugFunction?> _frameFunctions = new();
    private readonly ConcurrentDictionary<int, int> _frameThreads = new();
    private readonly ConcurrentDictionary<int, DebugSite?> _frameSites = new();
    private readonly Dictionary<string, List<LogicalBreakpoint>> _sourceBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LogicalBreakpoint> _functionBreakpoints = [];
    private readonly HashSet<string> _runtimeFunctionBreakpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LogicalBreakpoint> _temporaryBreakpoints = [];
    private readonly Dictionary<int, DataBreakpointTarget> _dataBreakpoints = [];
    private readonly ConcurrentDictionary<int, (string File, int Line)> _gotoTargets = new();
    private readonly HashSet<string> _exceptionFilters = new(StringComparer.Ordinal);
    private DebugTarget? _target;
    private DebugMap? _map;
    private DebugControlImage? _control;
    private ulong _controlAddress;
    private ulong _summaryAddress;
    private int _pointerSize;
    private DebugMemoryLayout? _summaryLayout;
    private int? _bootstrapBreakpoint;
    private DebugSite? _currentSite;
    private DebugFunction? _currentFunction;
    private DebugControlSnapshot? _currentControl;
    private int _currentStoppedThread;
    private PendingLogicalStep? _pendingLogicalStep;
    private RuntimeException? _currentException;
    private WslTerminalBroker? _terminal;
    private OwnedQemuSession? _qemu;
    private FileStream? _launchLease;
    private int _nextReference;
    private int _nextBreakpoint;
    private bool _configured;
    private bool _stopAtEntry;
    private bool _showRuntimeFrames;
    private bool _terminated;
    private bool _trace;
    private bool _controlReady;
    private bool _targetRunning;
    private bool _pendingControlSync;
    private bool _resumeAfterControlSync;
    private int? _selectedThread;
    private int? _selectedFrame;
    private int? _qemuTrapBreakpoint;
    private TaskCompletionSource<bool>? _targetStopWaiter;
    private bool _suppressTargetStop;
    private bool _closingQemuGdb;

    internal CTildeDebugAdapter()
    {
        _gdb.AsyncRecord += OnGdbAsync;
        _gdb.Output += (category, output) => SendOutput(category, output);
        _gdb.Exited += code => _ = Task.Run(async () =>
        {
            if (_closingQemuGdb)
                return;
            if (_qemu is not null)
                await _qemu.StopAsync().ConfigureAwait(false);
            TerminateOnce(code is null or 0 ? null : $"GDB exited with code {code}.");
        });
    }

    internal void Run(Stream input, Stream output)
    {
        InitializeProtocolClient(input, output);
        Protocol.Run();
    }

    protected override void HandleProtocolError(Exception exception)
    {
        Console.Error.WriteLine("C~ debug protocol stopped: " + exception);
        TerminateOnce("C~ debug protocol stopped: " + exception.Message);
    }

    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments) => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsFunctionBreakpoints = true,
        SupportsConditionalBreakpoints = true,
        SupportsHitConditionalBreakpoints = true,
        SupportsEvaluateForHovers = true,
        SupportsRestartRequest = true,
        SupportsExceptionInfoRequest = true,
        SupportsLogPoints = true,
        SupportsTerminateRequest = true,
        SupportsDataBreakpoints = true,
        SupportsReadMemoryRequest = true,
        SupportsCancelRequest = true,
        SupportsGotoTargetsRequest = true,
        SupportTerminateDebuggee = true,
        ExceptionBreakpointFilters =
        [
            new ExceptionBreakpointsFilter { Filter = "thrown", Label = "C~ thrown exceptions", Default = false },
            new ExceptionBreakpointsFilter { Filter = "unhandled", Label = "C~ unhandled exceptions", Default = true },
            new ExceptionBreakpointsFilter { Filter = "fatal", Label = "C~ fatal runtime events", Default = true },
        ],
    };

    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        var properties = arguments.ConfigurationProperties ?? new Dictionary<string, JToken>();
        var descriptor = RequiredString(properties, "debugTarget");
        var gdbPath = OptionalString(properties, "gdbPath");
        _stopAtEntry = OptionalBoolean(properties, "stopAtEntry");
        _showRuntimeFrames = OptionalBoolean(properties, "showRuntimeFrames");
        _trace = OptionalBoolean(properties, "trace");
        (_target, _map) = DebugTargetValidator.Load(descriptor, gdbPath);
        _launchLease = AcquireLaunchLease(descriptor);
        ValidateMemoryMode(OptionalString(properties, "memoryDiagnostics"), _target.MemoryDiagnostics);
        StartGdbAsync().GetAwaiter().GetResult();
        SendEvent(new InitializedEvent());
        return new LaunchResponse();
    }

    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
    {
        if (_configured)
            return new ConfigurationDoneResponse();
        _configured = true;
        RunAsync().GetAwaiter().GetResult();
        return new ConfigurationDoneResponse();
    }

    protected override SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
    {
        EnsureStarted();
        var source = arguments.Source.Path ?? throw new InvalidOperationException("A source path is required for C~ breakpoints.");
        _sourceBreakpoints.Remove(source);
        var logical = new List<LogicalBreakpoint>();
        var result = new List<Breakpoint>();
        foreach (var requested in arguments.Breakpoints ?? [])
        {
            var id = Interlocked.Increment(ref _nextBreakpoint);
            try
            {
                var match = FindExecutableSite(source, requested.Line, requested.Column ?? 1)
                    ?? throw new InvalidOperationException("No reachable instrumented C~ statement exists at or after this line in the containing method.");
                var hit = ParseHitCondition(requested.HitCondition);
                if (!string.IsNullOrWhiteSpace(requested.HitCondition) && hit is null)
                    throw new InvalidOperationException("A C~ hit condition must be a positive integer.");
                logical.Add(new LogicalBreakpoint
                {
                    Id = id,
                    SiteId = match.Site.Id,
                    Function = match.Function,
                    Condition = requested.Condition,
                    HitCondition = hit,
                    LogMessage = requested.LogMessage,
                });
                result.Add(new Breakpoint(true)
                {
                    Id = id,
                    Source = arguments.Source,
                    Line = match.Site.Source.Line,
                    Column = match.Site.Source.Column,
                });
            }
            catch (Exception exception)
            {
                result.Add(new Breakpoint(false) { Id = id, Source = arguments.Source, Line = requested.Line, Message = exception.Message });
            }
        }
        _sourceBreakpoints[source] = logical;
        RequestControlSyncAsync().GetAwaiter().GetResult();
        return new SetBreakpointsResponse(result);
    }

    protected override SetFunctionBreakpointsResponse HandleSetFunctionBreakpointsRequest(SetFunctionBreakpointsArguments arguments)
    {
        _functionBreakpoints.Clear();
        _runtimeFunctionBreakpoints.Clear();
        var result = new List<Breakpoint>();
        foreach (var requested in arguments.Breakpoints ?? [])
        {
            var id = Interlocked.Increment(ref _nextBreakpoint);
            try
            {
                if (requested.Name is "$allocation" or "$final-release" or "$leak")
                {
                    _runtimeFunctionBreakpoints.Add(requested.Name);
                    result.Add(new Breakpoint(true) { Id = id });
                    continue;
                }
                var candidates = _map?.Functions.Where(candidate =>
                    candidate.DisplayName.Equals(requested.Name, StringComparison.Ordinal) ||
                    candidate.Name.Equals(requested.Name, StringComparison.Ordinal)).ToArray() ?? [];
                if (candidates.Length == 0) throw new InvalidOperationException("The C~ function was not found.");
                if (candidates.Length > 1) throw new InvalidOperationException("The C~ function breakpoint is ambiguous; use its fully qualified name.");
                var entry = candidates[0].Sites.FirstOrDefault(candidate => candidate.Kind.Equals("entry", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("The C~ function has no reachable instrumented entry site.");
                var hit = ParseHitCondition(requested.HitCondition);
                if (!string.IsNullOrWhiteSpace(requested.HitCondition) && hit is null)
                    throw new InvalidOperationException("A C~ hit condition must be a positive integer.");
                _functionBreakpoints.Add(new LogicalBreakpoint
                {
                    Id = id,
                    SiteId = entry.Id,
                    Function = candidates[0],
                    Condition = requested.Condition,
                    HitCondition = hit,
                });
                result.Add(new Breakpoint(true) { Id = id, Line = entry.Source.Line, Column = entry.Source.Column });
            }
            catch (Exception exception) { result.Add(new Breakpoint(false) { Id = id, Message = exception.Message }); }
        }
        RequestControlSyncAsync().GetAwaiter().GetResult();
        return new SetFunctionBreakpointsResponse(result);
    }

    protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
    {
        _exceptionFilters.Clear();
        foreach (var filter in arguments.Filters ?? []) _exceptionFilters.Add(filter);
        RequestControlSyncAsync().GetAwaiter().GetResult();
        return new SetExceptionBreakpointsResponse { Breakpoints = [] };
    }

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
    {
        var record = Command("-thread-info");
        var threads = new List<DapThread>();
        foreach (var item in MiParser.Array(record.Results.TryGetValue("threads", out var value) ? value : null))
        {
            var tuple = MiParser.Tuple(item);
            var id = ParseInt(MiParser.String(tuple, "id"), 1);
            var name = MiParser.String(tuple, "name", MiParser.String(tuple, "target-id", $"Thread {id}"));
            threads.Add(new DapThread(id, name));
        }
        if (threads.Count == 0) threads.Add(new DapThread(1, "Main Thread"));
        return new ThreadsResponse(threads);
    }

    protected override StackTraceResponse HandleStackTraceRequest(StackTraceArguments arguments)
    {
        var record = Command($"-stack-list-frames --thread {arguments.ThreadId}");
        var frames = new List<StackFrame>();
        var mappedCurrent = false;
        foreach (var item in MiParser.Array(record.Results.TryGetValue("stack", out var value) ? value : null))
        {
            var wrapper = MiParser.Tuple(item);
            var tuple = wrapper.TryGetValue("frame", out var frameValue) ? MiParser.Tuple(frameValue) : wrapper;
            var function = MiParser.String(tuple, "func", "<unknown>");
            var mappedFunction = _map?.Functions.FirstOrDefault(candidate => candidate.Name.Equals(function, StringComparison.Ordinal));
            var file = MiParser.String(tuple, "fullname", MiParser.String(tuple, "file"));
            if (!_showRuntimeFrames && (mappedFunction is null || file.Equals("<ctilde-generated>", StringComparison.Ordinal))) continue;
            var level = ParseInt(MiParser.String(tuple, "level"), frames.Count);
            var id = checked(arguments.ThreadId * 10000 + level + 1);
            _frameLevels[id] = level;
            _frameFunctions[id] = mappedFunction;
            _frameThreads[id] = arguments.ThreadId;
            var line = ParseInt(MiParser.String(tuple, "line"), 1);
            var exactSite = !mappedCurrent && arguments.ThreadId == _currentStoppedThread && mappedFunction == _currentFunction
                ? _currentSite : null;
            if (exactSite is not null) mappedCurrent = true;
            var frameSite = exactSite ?? ResolveFrameSite(mappedFunction?.Sites ?? [], line);
            _frameSites[id] = frameSite;
            var sourcePath = frameSite is not null ? NormalizeSource(frameSite.Source)?.File : mappedFunction?.Source is null ? file : NormalizeSource(mappedFunction.Source)?.File;
            var sourceLine = frameSite?.Source.Line ?? line;
            frames.Add(new StackFrame(id, mappedFunction?.DisplayName ?? function, sourceLine, frameSite?.Source.Column ?? 1)
            {
                Source = string.IsNullOrWhiteSpace(sourcePath) ? null : new Source { Name = Path.GetFileName(sourcePath), Path = sourcePath },
            });
        }
        return new StackTraceResponse(frames) { TotalFrames = frames.Count };
    }

    protected override ScopesResponse HandleScopesRequest(ScopesArguments arguments)
    {
        var locals = AddVariables(new VariableContainer(arguments.FrameId, "locals"));
        var argumentsReference = AddVariables(new VariableContainer(arguments.FrameId, "arguments"));
        var statics = AddVariables(new VariableContainer(arguments.FrameId, "statics"));
        var runtime = AddVariables(new VariableContainer(arguments.FrameId, "runtime"));
        return new ScopesResponse
        {
            Scopes =
            [
                new Scope("Locals", locals, false),
                new Scope("Arguments", argumentsReference, false),
                new Scope("Statics", statics, true),
                new Scope("C~ Runtime", runtime, true),
            ],
        };
    }

    protected override VariablesResponse HandleVariablesRequest(VariablesArguments arguments)
    {
        if (!_variables.TryGetValue(arguments.VariablesReference, out var container))
            return new VariablesResponse([]);
        var level = _frameLevels.TryGetValue(container.FrameId, out var stored) ? stored : 0;
        if (container.Kind == "expression")
            return new VariablesResponse(ExpandExpression(container, level));
        if (container.Kind == "object-runtime")
            return new VariablesResponse(ObjectRuntimeVariables(container, level));
        if (container.Kind == "statics")
            return new VariablesResponse(StaticVariables(container.FrameId, level));
        if (container.Kind == "runtime")
            return new VariablesResponse(RuntimeVariables(container.FrameId, level));
        var thread = _frameThreads.TryGetValue(container.FrameId, out var storedThread) ? storedThread : _currentStoppedThread;
        var record = Command($"-stack-list-variables --thread {thread} --frame {level} --simple-values");
        var result = new List<Variable>();
        var native = MiParser.Array(record.Results.TryGetValue("variables", out var value) ? value : null)
            .Select(MiParser.Tuple).ToDictionary(tuple => MiParser.String(tuple, "name"), tuple => tuple, StringComparer.Ordinal);
        var function = _frameFunctions.TryGetValue(container.FrameId, out var mapped) ? mapped : null;
        var logicalVariables = container.Kind switch
        {
            "locals" when function is not null => LiveLocals(function, container.FrameId),
            "arguments" when function is not null => function.Parameters,
            _ => native.Values.Select(tuple => new DebugVariable
            {
                Name = MiParser.String(tuple, "name"),
                Storage = MiParser.String(tuple, "name"),
                Type = MiParser.String(tuple, "type"),
            }).ToArray(),
        };
        foreach (var logical in logicalVariables)
        {
            if (!native.TryGetValue(logical.Storage, out var tuple)) continue;
            var name = logical.Name;
            var display = MiParser.String(tuple, "value", "<unavailable>");
            var type = string.IsNullOrWhiteSpace(logical.Type) ? MiParser.String(tuple, "type") : logical.Type;
            var reference = CanExpand(type, display) ? AddVariables(new VariableContainer(container.FrameId, "expression", logical.Storage, type)) : 0;
            result.Add(new Variable(name, display, reference) { Type = type, EvaluateName = name, MemoryReference = ExtractAddress(display) });
        }
        if (container.Kind == "arguments" && function is not null && !string.IsNullOrWhiteSpace(function.Receiver) && !string.IsNullOrWhiteSpace(function.ReceiverType) &&
            native.TryGetValue(function.Receiver, out var receiver))
        {
            var display = MiParser.String(receiver, "value", "<unavailable>");
            var reference = AddVariables(new VariableContainer(container.FrameId, "expression", function.Receiver, function.ReceiverType));
            result.Add(new Variable("this", display, reference) { Type = function.ReceiverType, EvaluateName = "this", MemoryReference = ExtractAddress(display) });
        }
        return new VariablesResponse(result);
    }

    protected override EvaluateResponse HandleEvaluateRequest(EvaluateArguments arguments)
    {
        if (!IsSafeWatch(arguments.Expression))
            throw new InvalidOperationException("C~ watches currently support identifiers, fields, and array indices only.");
        var frame = arguments.FrameId ?? _frameLevels.Keys.FirstOrDefault();
        var level = _frameLevels.TryGetValue(frame, out var stored) ? stored : 0;
        var thread = _frameThreads.TryGetValue(frame, out var mappedThread) ? mappedThread : _currentStoppedThread;
        var function = _frameFunctions.TryGetValue(frame, out var mapped) ? mapped : ResolveSelectedFunction(thread);
        var translated = TranslateWatch(arguments.Expression, function, frame)
            ?? throw new InvalidOperationException("The C~ watch does not resolve to a live local, argument, receiver, field, or array element.");
        var record = Command($"-data-evaluate-expression --thread {thread} --frame {level} {Quote(translated.Expression)}");
        var value = MiParser.String(record.Results, "value", "<unavailable>");
        var reference = CanExpand(translated.Type, value) ? AddVariables(new VariableContainer(frame, "expression", translated.Expression, translated.Type)) : 0;
        return new EvaluateResponse(value, reference) { MemoryReference = ExtractAddress(value) };
    }

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments)
    {
        ContinueAsync(arguments.ThreadId).GetAwaiter().GetResult();
        return new ContinueResponse { AllThreadsContinued = true };
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments) { StartLogicalStepAsync(arguments.ThreadId, 2).GetAwaiter().GetResult(); return new NextResponse(); }
    protected override StepInResponse HandleStepInRequest(StepInArguments arguments) { StartLogicalStepAsync(arguments.ThreadId, 1).GetAwaiter().GetResult(); return new StepInResponse(); }
    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments) { StartLogicalStepAsync(arguments.ThreadId, 3).GetAwaiter().GetResult(); return new StepOutResponse(); }
    protected override PauseResponse HandlePauseRequest(PauseArguments arguments) { Command("-exec-interrupt --all"); return new PauseResponse(); }

    protected override GotoTargetsResponse HandleGotoTargetsRequest(GotoTargetsArguments arguments)
    {
        var id = Interlocked.Increment(ref _nextBreakpoint);
        var match = FindExecutableSite(arguments.Source.Path ?? string.Empty, arguments.Line, arguments.Column ?? 1)
            ?? throw new InvalidOperationException("No reachable instrumented C~ statement exists at or after the requested cursor location.");
        _gotoTargets[id] = (arguments.Source.Path ?? string.Empty, match.Site.Source.Line);
        _temporaryBreakpoints[id] = new LogicalBreakpoint { Id = id, SiteId = match.Site.Id, Function = match.Function, Temporary = true };
        return new GotoTargetsResponse([new GotoTarget(id, $"Line {match.Site.Source.Line}", match.Site.Source.Line) { Column = match.Site.Source.Column, EndLine = match.Site.Source.Line }]);
    }

    protected override GotoResponse HandleGotoRequest(GotoArguments arguments)
    {
        if (!_gotoTargets.TryRemove(arguments.TargetId, out _))
            throw new InvalidOperationException("The C~ Run to Cursor target is no longer available.");
        RequestControlSyncAsync().GetAwaiter().GetResult();
        ContinueAsync(arguments.ThreadId).GetAwaiter().GetResult();
        return new GotoResponse();
    }

    protected override DataBreakpointInfoResponse HandleDataBreakpointInfoRequest(DataBreakpointInfoArguments arguments)
    {
        var name = arguments.Name;
        if (string.IsNullOrWhiteSpace(name)) return new DataBreakpointInfoResponse(null, string.Empty);
        var frameId = _selectedFrame is null ? _frameLevels.Keys.FirstOrDefault() : _frameLevels.FirstOrDefault(pair => pair.Value == _selectedFrame).Key;
        var function = _frameFunctions.TryGetValue(frameId, out var mapped) ? mapped : _currentFunction;
        var translated = TranslateWatch(name, function, frameId == 0 ? null : frameId);
        if (translated is null) return new DataBreakpointInfoResponse(null, name) { Description = "The expression is not addressable live C~ storage." };
        var target = new DataBreakpointTarget(translated.Value.Expression, translated.Value.Type, frameId, _currentControl?.Thread ?? 0, _currentControl?.Activation ?? 0);
        var dataId = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target)));
        return new DataBreakpointInfoResponse(dataId, name)
        {
            AccessTypes = [DataBreakpointAccessType.Write, DataBreakpointAccessType.Read, DataBreakpointAccessType.ReadWrite],
            CanPersist = target.Activation == 0,
        };
    }

    protected override SetDataBreakpointsResponse HandleSetDataBreakpointsRequest(SetDataBreakpointsArguments arguments)
    {
        foreach (var number in _dataBreakpoints.Keys) TryCommand($"-break-delete {number}");
        _dataBreakpoints.Clear();
        var result = new List<Breakpoint>();
        foreach (var requested in arguments.Breakpoints ?? [])
        {
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(requested.DataId));
                var target = JsonSerializer.Deserialize<DataBreakpointTarget>(json) ?? throw new InvalidDataException("The C~ data-breakpoint ID is invalid.");
                if (target.FrameId != 0 && _frameLevels.TryGetValue(target.FrameId, out var frame))
                {
                    var thread = _frameThreads.TryGetValue(target.FrameId, out var storedThread) ? storedThread : _currentStoppedThread;
                    Command($"-stack-select-frame --thread {thread} {frame}");
                }
                var access = requested.AccessType switch
                {
                    DataBreakpointAccessType.Read => "-r ",
                    DataBreakpointAccessType.ReadWrite => "-a ",
                    _ => string.Empty,
                };
                var record = Command("-break-watch " + access + Quote(target.Expression));
                var watchpoint = MiParser.Tuple(record.Results.TryGetValue("wpt", out var value) ? value : null);
                var number = ParseInt(MiParser.String(watchpoint, "number"), Interlocked.Increment(ref _nextBreakpoint));
                _dataBreakpoints[number] = target;
                result.Add(new Breakpoint(true) { Id = number });
            }
            catch (Exception exception) { result.Add(new Breakpoint(false) { Message = exception.Message }); }
        }
        return new SetDataBreakpointsResponse(result);
    }

    protected override ReadMemoryResponse HandleReadMemoryRequest(ReadMemoryArguments arguments)
    {
        var count = Math.Max(0, arguments.Count);
        var offset = arguments.Offset ?? 0;
        var address = offset == 0 ? arguments.MemoryReference : $"({arguments.MemoryReference})+{offset}";
        var record = Command($"-data-read-memory-bytes {Quote(address)} {count}");
        var memory = MiParser.Array(record.Results.TryGetValue("memory", out var value) ? value : null).FirstOrDefault();
        var tuple = MiParser.Tuple(memory);
        var contents = MiParser.String(tuple, "contents");
        return new ReadMemoryResponse(address) { Data = Convert.ToBase64String(Convert.FromHexString(contents)) };
    }

    protected override ExceptionInfoResponse HandleExceptionInfoRequest(ExceptionInfoArguments arguments) => _currentException is null
        ? new("ctilde-runtime", ExceptionBreakMode.Always) { Description = "C~ runtime exception or fatal event" }
        : new(_currentException.Id, _currentException.BreakMode) { Description = _currentException.Description };

    protected override RestartResponse HandleRestartRequest(RestartArguments arguments)
    {
        RestartAsync().GetAwaiter().GetResult();
        return new RestartResponse();
    }

    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
    {
        StopAsync().GetAwaiter().GetResult();
        return new DisconnectResponse();
    }

    protected override TerminateResponse HandleTerminateRequest(TerminateArguments arguments)
    {
        StopAsync().GetAwaiter().GetResult();
        return new TerminateResponse();
    }

    private async Task StartGdbAsync()
    {
        var target = _target!;
        _gdb.Start(target.GdbCommand, target.GdbPrefixArguments, target.WorkingDirectory);
        Trace($"Starting GDB for {target.Program}");
        await _gdb.CommandAsync("-gdb-set pagination off").ConfigureAwait(false);
        await _gdb.CommandAsync("-gdb-set confirm off").ConfigureAwait(false);
        var wsl = target.GdbPrefixArguments.Contains("gdb", StringComparer.OrdinalIgnoreCase);
        var program = wsl ? WslTerminalBroker.ConvertPath(target.Program) : target.Program;
        var workingDirectory = wsl ? WslTerminalBroker.ConvertPath(target.WorkingDirectory) : target.WorkingDirectory;
        await _gdb.CommandAsync("-file-exec-and-symbols " + Quote(program)).ConfigureAwait(false);
        await _gdb.CommandAsync("-environment-cd " + Quote(workingDirectory)).ConfigureAwait(false);
        if (IsQemu())
        {
            await _gdb.CommandAsync("-interpreter-exec console \"set remote trace-status-packet off\"").ConfigureAwait(false);
            await _gdb.CommandAsync("-interpreter-exec console \"set remote software-breakpoint-packet on\"").ConfigureAwait(false);
        }
        else if (target.GdbPrefixArguments.Length == 0 && OperatingSystem.IsWindows())
            await _gdb.CommandAsync("-interpreter-exec console \"set new-console on\"").ConfigureAwait(false);
        else if (wsl)
        {
            _terminal = await WslTerminalBroker.StartAsync(target.WorkingDirectory).ConfigureAwait(false);
            await _gdb.CommandAsync("-inferior-tty-set " + Quote(_terminal.TtyPath)).ConfigureAwait(false);
        }
        if (!IsQemu() && target.Arguments.Length != 0)
            await _gdb.CommandAsync("-exec-arguments " + string.Join(" ", target.Arguments.Select(Quote))).ConfigureAwait(false);
        if (!IsQemu())
            foreach (var pair in target.Environment)
                await _gdb.CommandAsync("-gdb-set environment " + Quote(pair.Key + "=" + pair.Value)).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        ResetStopState();
        _controlReady = false;
        _control = null;
        _controlAddress = 0;
        _summaryAddress = 0;
        _summaryLayout = null;
        _pointerSize = 0;
        if (IsQemu())
        {
            await StartAndConnectQemuAsync().ConfigureAwait(false);
            _bootstrapBreakpoint = await InsertNativeBreakpointAsync(_map!.RuntimeHooks.Ready, temporary: true).ConfigureAwait(false);
            _targetRunning = true;
            await _gdb.CommandAsync("-exec-continue").ConfigureAwait(false);
            return;
        }
        _bootstrapBreakpoint = await InsertNativeBreakpointAsync("main", temporary: true).ConfigureAwait(false);
        if (_stopAtEntry)
        {
            var entry = _map?.Functions.FirstOrDefault(candidate => candidate.Name.Equals(_map.EntryPoint, StringComparison.Ordinal))
                ?? _map?.Functions.FirstOrDefault(candidate => candidate.DisplayName.EndsWith(".Main", StringComparison.Ordinal));
            var site = entry?.Sites.FirstOrDefault(candidate => candidate.Kind.Equals("entry", StringComparison.Ordinal));
            if (entry is not null && site is not null)
            {
                var id = Interlocked.Increment(ref _nextBreakpoint);
                _temporaryBreakpoints[id] = new LogicalBreakpoint { Id = id, SiteId = site.Id, Function = entry, Temporary = true };
            }
        }
        _targetRunning = true;
        await _gdb.CommandAsync("-exec-run").ConfigureAwait(false);
    }

    private async Task RestartAsync()
    {
        if (IsQemu())
        {
            await RestartQemuAsync().ConfigureAwait(false);
            return;
        }
        try { await _gdb.CommandAsync("-exec-abort").ConfigureAwait(false); }
        catch
        {
            try { await _gdb.CommandAsync("-exec-interrupt --all").ConfigureAwait(false); } catch { }
            try { await _gdb.CommandAsync("-interpreter-exec console \"kill\"").ConfigureAwait(false); } catch { }
        }
        _terminated = false;
        _temporaryBreakpoints.Clear();
        foreach (var breakpoint in LogicalBreakpoints()) breakpoint.Hits = 0;
        await RunAsync().ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        if (IsQemu())
        {
            _closingQemuGdb = true;
            try { await StopQemuTargetAsync(closeGdb: true).ConfigureAwait(false); }
            finally { _closingQemuGdb = false; }
            TerminateOnce(null);
            return;
        }
        if (_controlReady && !_targetRunning)
        {
            try { await ClearControlAsync().ConfigureAwait(false); } catch (Exception exception) { Trace("Could not clear logical control: " + exception.Message); }
        }
        try { await _gdb.CommandAsync("-gdb-exit").WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { await _gdb.CloseAsync().ConfigureAwait(false); }
        if (_terminal is not null) { await _terminal.DisposeAsync().ConfigureAwait(false); _terminal = null; }
        TerminateOnce(null);
    }

    private async Task StartAndConnectQemuAsync()
    {
        var target = _target!;
        var launch = target.Launch ?? throw new InvalidDataException("The prepared QEMU target has no owned launch command.");
        var qemu = new OwnedQemuSession(launch, target.GdbHost, target.GdbPort);
        qemu.Output += (category, output) => SendOutput(category, output);
        _qemu = qemu;
        try
        {
            Trace("Starting the owned ESP-IDF QEMU process.");
            await qemu.StartAsync().ConfigureAwait(false);
            qemu.Exited += code => _ = Task.Run(() => HandleUnexpectedQemuExitAsync(code));
            Trace($"Connecting target GDB to {target.GdbHost}:{target.GdbPort}.");
            await ExecuteWithSuppressedStopAsync($"-interpreter-exec console {Quote($"target remote {target.GdbHost}:{target.GdbPort}")}").ConfigureAwait(false);
        }
        catch
        {
            await qemu.StopAsync().ConfigureAwait(false);
            if (ReferenceEquals(_qemu, qemu)) _qemu = null;
            throw;
        }
    }

    private async Task RestartQemuAsync()
    {
        Trace("Restarting QEMU: stopping the current target.");
        _closingQemuGdb = true;
        try { await StopQemuTargetAsync(closeGdb: true).ConfigureAwait(false); }
        finally { _closingQemuGdb = false; }
        _terminated = false;
        _temporaryBreakpoints.Clear();
        foreach (var breakpoint in LogicalBreakpoints()) breakpoint.Hits = 0;
        ResetStopState();
        _controlReady = false;
        _control = null;
        _controlAddress = 0;
        _summaryAddress = 0;
        _summaryLayout = null;
        _pointerSize = 0;
        await StartGdbAsync().ConfigureAwait(false);
        Trace("Restarting QEMU: launching a fresh emulator.");
        await StartAndConnectQemuAsync().ConfigureAwait(false);
        Trace("Restarting QEMU: installing the ready breakpoint.");
        _bootstrapBreakpoint = await InsertNativeBreakpointAsync(_map!.RuntimeHooks.Ready, temporary: true).ConfigureAwait(false);
        _targetRunning = true;
        await _gdb.CommandAsync("-exec-continue").ConfigureAwait(false);
        Trace("Restarting QEMU: target resumed toward the ready probe.");
    }

    private async Task StopQemuTargetAsync(bool closeGdb)
    {
        Trace("Stopping QEMU: interrupting the target.");
        var stopped = await InterruptQemuAsync().ConfigureAwait(false);
        Trace("Stopping QEMU: clearing logical control.");
        if (stopped && _controlReady && !_targetRunning)
            try { await ClearControlAsync().ConfigureAwait(false); } catch (Exception exception) { Trace("Could not clear logical control: " + exception.Message); }
        if (stopped)
            foreach (var breakpoint in new[] { _bootstrapBreakpoint, _qemuTrapBreakpoint }.Where(value => value is not null))
                try { await _gdb.CommandAsync($"-break-delete {breakpoint}").ConfigureAwait(false); } catch { }
        _bootstrapBreakpoint = null;
        _qemuTrapBreakpoint = null;
        if (stopped)
        {
            Trace("Stopping QEMU: disconnecting GDB.");
            try { await _gdb.CommandAsync("-interpreter-exec console \"target disconnect\"").ConfigureAwait(false); } catch { }
        }
        else
            _suppressTargetStop = true;
        var qemu = _qemu;
        _qemu = null;
        if (qemu is not null)
        {
            Trace("Stopping QEMU: terminating the owned process tree.");
            await qemu.StopAsync().ConfigureAwait(false);
        }
        if (closeGdb)
        {
            try { await _gdb.CommandAsync("-gdb-exit").WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { await _gdb.CloseAsync().ConfigureAwait(false); }
        }
        _suppressTargetStop = false;
        _targetRunning = false;
    }

    private async Task<bool> InterruptQemuAsync()
    {
        if (!_targetRunning)
            return true;
        try
        {
            await ExecuteWithSuppressedStopAsync("-exec-interrupt").ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            Trace("Could not interrupt QEMU target; process termination will force cleanup: " + exception.Message);
            return false;
        }
    }

    private async Task ExecuteWithSuppressedStopAsync(string command)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _targetStopWaiter = completion;
        _suppressTargetStop = true;
        try
        {
            await _gdb.CommandAsync(command).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            _suppressTargetStop = false;
            if (ReferenceEquals(_targetStopWaiter, completion)) _targetStopWaiter = null;
        }
    }

    private async Task HandleUnexpectedQemuExitAsync(int? code)
    {
        if (_qemu is null)
            return;
        try { await _gdb.CloseAsync().ConfigureAwait(false); } catch { }
        _qemu = null;
        TerminateOnce(code is null ? "ESP-IDF QEMU exited unexpectedly." : $"ESP-IDF QEMU exited unexpectedly with code {code}.");
    }

    private void OnGdbAsync(MiRecord record)
    {
        if (record.Kind != '*' || record.Name != "stopped") return;
        _ = Task.Run(() => HandleStoppedAsync(record));
    }

    private async Task HandleStoppedAsync(MiRecord record)
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _targetRunning = false;
            _targetStopWaiter?.TrySetResult(true);
            if (_suppressTargetStop)
                return;
            InvalidateStopCaches();
            var reason = MiParser.String(record.Results, "reason");
            if (reason.StartsWith("exited", StringComparison.Ordinal)) { TerminateOnce(null); return; }
            var thread = ParseInt(MiParser.String(record.Results, "thread-id"), 1);
            var breakpointNumber = ParseInt(MiParser.String(record.Results, "bkptno"), -1);

            if (_bootstrapBreakpoint == breakpointNumber)
            {
                _bootstrapBreakpoint = null;
                if (IsQemu())
                    _qemuTrapBreakpoint = await InsertNativeBreakpointAsync(_map!.RuntimeHooks.Trap, temporary: false).ConfigureAwait(false);
                await InitializeControlAsync().ConfigureAwait(false);
                await SynchronizeControlAsync().ConfigureAwait(false);
                await ResumeAsync(thread).ConfigureAwait(false);
                return;
            }

            if (_pendingControlSync)
            {
                _pendingControlSync = false;
                await SynchronizeControlAsync().ConfigureAwait(false);
                if (_resumeAfterControlSync)
                {
                    _resumeAfterControlSync = false;
                    await ResumeAsync(thread).ConfigureAwait(false);
                    return;
                }
            }

            if (reason is "watchpoint-trigger" or "read-watchpoint-trigger" or "access-watchpoint-trigger")
            {
                SetRawStop(thread);
                SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.DataBreakpoint) { Description = "C~ data breakpoint", ThreadId = thread, AllThreadsStopped = true });
                return;
            }

            DebugControlSnapshot? control = null;
            if (_controlReady)
            {
                try { control = await ReadControlSnapshotAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    SetRawStop(thread);
                    SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception) { Description = "Could not read C~ logical stop state: " + exception.Message, ThreadId = thread, AllThreadsStopped = true });
                    return;
                }
            }

            if (control?.Reason == 1)
            {
                await HandleLogicalSiteAsync(thread, control).ConfigureAwait(false);
                return;
            }
            if (control is { Reason: >= 2 and <= 6 })
            {
                await HandleRuntimeEventAsync(thread, control).ConfigureAwait(false);
                return;
            }
            if (control is { Reason: 7 })
            {
                SetRawStop(thread);
                SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Entry)
                {
                    Description = "Stopped before C~ runtime and module initialization.",
                    ThreadId = thread,
                    AllThreadsStopped = true,
                });
                return;
            }
            if (control is { Reason: not 0 })
            {
                SetRawStop(thread);
                SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception) { Description = $"Unknown C~ runtime stop reason {control.Reason}.", ThreadId = thread, AllThreadsStopped = true });
                return;
            }

            SetRawStop(thread);
            var dapReason = reason switch
            {
                "breakpoint-hit" => StoppedEvent.ReasonValue.Breakpoint,
                "signal-received" => StoppedEvent.ReasonValue.Pause,
                "end-stepping-range" or "function-finished" or "location-reached" => StoppedEvent.ReasonValue.Step,
                _ => StoppedEvent.ReasonValue.Pause,
            };
            SendEvent(new StoppedEvent(dapReason) { Description = reason, ThreadId = thread, AllThreadsStopped = true });
        }
        catch (Exception exception)
        {
            SendOutput("stderr", "C~ debugger stop processing failed: " + exception.Message + Environment.NewLine);
            SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception) { Description = exception.Message, ThreadId = _currentStoppedThread == 0 ? 1 : _currentStoppedThread, AllThreadsStopped = true });
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task HandleLogicalSiteAsync(int thread, DebugControlSnapshot control)
    {
        var match = AllSites().FirstOrDefault(candidate => candidate.Site.Id == control.Site);
        if (match.Function is null || match.Site is null)
        {
            SetRawStop(thread);
            SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception) { Description = $"C~ runtime reported unknown logical site {control.Site}.", ThreadId = thread, AllThreadsStopped = true });
            return;
        }
        _currentSite = match.Site;
        _currentFunction = match.Function;
        _currentControl = control;
        _currentStoppedThread = thread;
        _currentException = null;
        await SelectFunctionFrameAsync(match.Function, thread).ConfigureAwait(false);
        await RemoveExpiredDataBreakpointsAsync(thread, control).ConfigureAwait(false);

        var candidates = LogicalBreakpoints().Where(candidate => candidate.SiteId == control.Site).ToArray();
        var shouldStop = candidates.Length == 0;
        foreach (var candidate in candidates)
        {
            candidate.Hits++;
            if (candidate.HitCondition is not null && candidate.Hits < candidate.HitCondition) continue;
            if (!string.IsNullOrWhiteSpace(candidate.Condition))
            {
                try
                {
                    var expression = TranslateExpression(candidate.Condition, candidate.Function, null);
                    var evaluated = await EvaluateNativeAsync(expression, thread, _selectedFrame).ConfigureAwait(false);
                    if (!IsTruthy(evaluated)) continue;
                }
                catch (Exception exception)
                {
                    SendOutput("stderr", "C~ breakpoint condition failed: " + exception.Message + Environment.NewLine);
                    continue;
                }
            }
            if (!string.IsNullOrWhiteSpace(candidate.LogMessage))
            {
                SendOutput("console", await ExpandLogMessageAsync(candidate.LogMessage, thread, candidate.Function).ConfigureAwait(false) + Environment.NewLine);
                continue;
            }
            shouldStop = true;
            if (candidate.Temporary) _temporaryBreakpoints.Remove(candidate.Id);
        }

        if (candidates.Length == 0 && _pendingLogicalStep is not null && _pendingLogicalStep.OriginFunction == match.Function.Name && SameSourceLine(_pendingLogicalStep.Origin, match.Site))
        {
            await ApplyLogicalStepAsync(_pendingLogicalStep).ConfigureAwait(false);
            await ResumeAsync(thread).ConfigureAwait(false);
            return;
        }
        if (!shouldStop)
        {
            await SynchronizeControlAsync().ConfigureAwait(false);
            await ResumeAsync(thread).ConfigureAwait(false);
            return;
        }

        var stopReason = candidates.Length == 0 ? StoppedEvent.ReasonValue.Step : StoppedEvent.ReasonValue.Breakpoint;
        _pendingLogicalStep = null;
        SendEvent(new StoppedEvent(stopReason)
        {
            Description = $"C~ {(candidates.Length == 0 ? "step" : "breakpoint")} at {match.Site.Source.File}:{match.Site.Source.Line}",
            ThreadId = thread,
            AllThreadsStopped = true,
        });
    }

    private async Task HandleRuntimeEventAsync(int thread, DebugControlSnapshot control)
    {
        SetRawStop(thread);
        _currentControl = control;
        if (control.Reason is 2 or 3)
        {
            var code = await ReadCStringAsync(control.Code).ConfigureAwait(false);
            var file = await ReadCStringAsync(control.File).ConfigureAwait(false);
            var unhandled = control.Reason == 3 || control.Value != 0;
            var id = string.IsNullOrWhiteSpace(code) ? (control.Reason == 3 ? "C~ fatal runtime failure" : "C~ exception") : code;
            var description = $"{id} at {file}:{control.Line}";
            _currentException = new RuntimeException(id, description, unhandled ? ExceptionBreakMode.Unhandled : ExceptionBreakMode.Always);
            SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception) { Description = description, ThreadId = thread, AllThreadsStopped = true });
            return;
        }
        var label = control.Reason switch { 4 => "allocation", 5 => "final release", 6 => "leak", _ => "runtime event" };
        SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.FunctionBreakpoint) { Description = $"C~ {label} object=0x{control.Object:x} value={control.Value}", ThreadId = thread, AllThreadsStopped = true });
    }

    private async Task<string> ExpandLogMessageAsync(string message, int thread, DebugFunction function)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in Regex.Matches(message, "\\{([^{}]+)\\}"))
        {
            result.Append(message, position, match.Index - position);
            var expression = match.Groups[1].Value.Trim();
            var translated = IsSafeWatch(expression) ? TranslateWatch(expression, function, null) : null;
            if (translated is null) result.Append("<unsupported>");
            else
            {
                result.Append(await EvaluateNativeAsync(translated.Value.Expression, thread, _selectedFrame).ConfigureAwait(false));
            }
            position = match.Index + match.Length;
        }
        result.Append(message, position, message.Length - position);
        return result.ToString();
    }

    private IEnumerable<(DebugFunction Function, DebugSite Site)> AllSites() =>
        _map?.Functions.SelectMany(function => function.Sites.Select(site => (function, site))) ?? [];

    private IEnumerable<LogicalBreakpoint> LogicalBreakpoints() =>
        _sourceBreakpoints.Values.SelectMany(candidate => candidate).Concat(_functionBreakpoints).Concat(_temporaryBreakpoints.Values);

    private (DebugFunction Function, DebugSite Site)? FindExecutableSite(string source, int line, int column)
        => LogicalDebugModel.FindExecutableSite(_map?.Functions ?? [], _target!.SourceRoot, source, line, column);

    private static int? ParseHitCondition(string? value) =>
        LogicalDebugModel.ParseHitCondition(value);

    private static DebugSite? ResolveFrameSite(IEnumerable<DebugSite> sites, int nativeLine) =>
        sites.OrderBy(candidate => Math.Abs(candidate.Source.Line - nativeLine)).ThenBy(candidate => candidate.Source.Line).FirstOrDefault();

    private DebugVariable[] LiveLocals(DebugFunction function, int? frameId)
        => LogicalDebugModel.LiveLocals(function, frameId is not null && _frameSites.TryGetValue(frameId.Value, out var site) ? site : _currentSite);

    private async Task<int> InsertNativeBreakpointAsync(string specification, bool temporary)
    {
        var record = await _gdb.CommandAsync($"-break-insert {(temporary ? "-t " : string.Empty)}{Quote(specification)}").ConfigureAwait(false);
        var breakpoint = MiParser.Tuple(record.Results.TryGetValue("bkpt", out var value) ? value : null);
        var number = ParseInt(MiParser.String(breakpoint, "number"), -1);
        if (number < 0) throw new InvalidOperationException($"GDB did not return a breakpoint number for '{specification}'.");
        return number;
    }

    private async Task InitializeControlAsync()
    {
        _pointerSize = ParseInt(await EvaluateNativeAsync("sizeof(void*)", 1, null).ConfigureAwait(false));
        var layout = _map!.RuntimeControl!.Layouts.FirstOrDefault(candidate => candidate.PointerSize == _pointerSize)
            ?? throw new InvalidDataException($"The C~ debug map has no {_pointerSize * 8}-bit control layout.");
        _controlAddress = ParseAddress(await EvaluateNativeAsync($"(void*)&{_map.RuntimeControl.Symbol}", 1, null).ConfigureAwait(false));
        var bytes = await ReadMemoryAsync(_controlAddress, layout.Size).ConfigureAwait(false);
        _control = new DebugControlImage(layout, bytes);
        var expected = AllSites().Select(candidate => candidate.Site.Id).DefaultIfEmpty(-1).Max() + 1;
        _control.ValidateHeader(expected);
        if (_map.RuntimeSummary is not null)
        {
            _summaryLayout = _map.RuntimeSummary.Layouts.FirstOrDefault(candidate => candidate.PointerSize == _pointerSize);
            if (_summaryLayout is not null)
                _summaryAddress = ParseAddress(await EvaluateNativeAsync($"(void*)&{_map.RuntimeSummary.Symbol}", 1, null).ConfigureAwait(false));
        }
        _controlReady = true;
    }

    private async Task<DebugControlSnapshot> ReadControlSnapshotAsync()
    {
        if (_control is null) throw new InvalidOperationException("The C~ debug control has not been initialized.");
        _control = new DebugControlImage(_control.Layout, await ReadMemoryAsync(_controlAddress, _control.Layout.Size).ConfigureAwait(false));
        _control.ValidateHeader(AllSites().Select(candidate => candidate.Site.Id).DefaultIfEmpty(-1).Max() + 1);
        return _control.Snapshot();
    }

    private async Task SynchronizeControlAsync()
    {
        if (!_controlReady || _control is null) return;
        _control = new DebugControlImage(_control.Layout, await ReadMemoryAsync(_controlAddress, _control.Layout.Size).ConfigureAwait(false));
        _control.Write("CurrentReason", 0);
        _control.Write("StepMode", 0);
        _control.Write("StepDepth", 0);
        _control.Write("SelectedThread", 0);
        _control.Write("StartupReleased", 1);
        _control.Write("EventMask", checked((uint)EventMask()));
        _control.Write("SessionActive", 1);
        _control.WriteEnabledSites(checked((int)_control.Read("SiteCount")), LogicalBreakpoints().Select(candidate => candidate.SiteId));
        await WriteControlAsync().ConfigureAwait(false);
    }

    private async Task RequestControlSyncAsync()
    {
        if (!_controlReady) return;
        if (!_targetRunning) { await SynchronizeControlAsync().ConfigureAwait(false); return; }
        _pendingControlSync = true;
        _resumeAfterControlSync = true;
        await _gdb.CommandAsync("-exec-interrupt --all").ConfigureAwait(false);
    }

    private int EventMask()
    {
        var mask = 0;
        if (_exceptionFilters.Contains("thrown")) mask |= 1 | 2;
        else if (_exceptionFilters.Contains("unhandled")) mask |= 2;
        if (_exceptionFilters.Contains("fatal")) mask |= 4;
        if (_runtimeFunctionBreakpoints.Contains("$allocation")) mask |= 8;
        if (_runtimeFunctionBreakpoints.Contains("$final-release")) mask |= 16;
        if (_runtimeFunctionBreakpoints.Contains("$leak")) mask |= 32;
        if (IsQemu() && _stopAtEntry) mask |= 64;
        return mask;
    }

    private async Task ClearControlAsync()
    {
        if (_control is null) return;
        _control = new DebugControlImage(_control.Layout, await ReadMemoryAsync(_controlAddress, _control.Layout.Size).ConfigureAwait(false));
        _control.Write("SessionActive", 0);
        _control.Write("StartupReleased", 1);
        _control.Write("EventMask", 0);
        _control.Write("StepMode", 0);
        _control.Write("StepDepth", 0);
        _control.Write("SelectedThread", 0);
        _control.Write("CurrentReason", 0);
        _control.WriteEnabledSites(checked((int)_control.Read("SiteCount")), []);
        await WriteControlAsync().ConfigureAwait(false);
        _controlReady = false;
    }

    private async Task WriteControlAsync()
    {
        if (_control is null) throw new InvalidOperationException("The C~ debug control has not been initialized.");
        await _gdb.CommandAsync($"-data-write-memory-bytes 0x{_controlAddress:x} {_control.ToHex()}").ConfigureAwait(false);
    }

    private async Task<byte[]> ReadMemoryAsync(ulong address, int length)
    {
        var record = await _gdb.CommandAsync($"-data-read-memory-bytes 0x{address:x} {length}").ConfigureAwait(false);
        var memory = MiParser.Array(record.Results.TryGetValue("memory", out var value) ? value : null).FirstOrDefault();
        var contents = MiParser.String(MiParser.Tuple(memory), "contents");
        var bytes = DebugControlImage.FromHex(contents);
        if (bytes.Length < length) throw new InvalidDataException($"GDB returned {bytes.Length} of {length} requested debug-memory bytes.");
        return bytes[..length];
    }

    private async Task<string> ReadCStringAsync(ulong address)
    {
        if (address == 0) return string.Empty;
        var bytes = await ReadMemoryAsync(address, 512).ConfigureAwait(false);
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
    }

    private async Task<string> EvaluateNativeAsync(string expression, int thread, int? frame)
    {
        var location = frame is null ? $"--thread {thread}" : $"--thread {thread} --frame {frame.Value}";
        var record = await _gdb.CommandAsync($"-data-evaluate-expression {location} {Quote(expression)}").ConfigureAwait(false);
        return MiParser.String(record.Results, "value", "<unavailable>");
    }

    private async Task SelectFunctionFrameAsync(DebugFunction function, int thread)
    {
        var record = await _gdb.CommandAsync($"-stack-list-frames --thread {thread}").ConfigureAwait(false);
        foreach (var item in MiParser.Array(record.Results.TryGetValue("stack", out var value) ? value : null))
        {
            var wrapper = MiParser.Tuple(item);
            var frame = wrapper.TryGetValue("frame", out var inner) ? MiParser.Tuple(inner) : wrapper;
            if (!MiParser.String(frame, "func").Equals(function.Name, StringComparison.Ordinal)) continue;
            _selectedThread = thread;
            _selectedFrame = ParseInt(MiParser.String(frame, "level"));
            await _gdb.CommandAsync($"-stack-select-frame --thread {thread} {_selectedFrame.Value}").ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException($"The native stack does not contain the owning C~ function '{function.DisplayName}'.");
    }

    private async Task RemoveExpiredDataBreakpointsAsync(int thread, DebugControlSnapshot control)
    {
        if (_dataBreakpoints.Count == 0 || control.Thread == 0) return;
        var active = new HashSet<ulong>();
        var frameValue = await EvaluateNativeAsync($"((ct_thread_state*)(void*)0x{control.Thread:x})->DebugFrameTop", thread, _selectedFrame).ConfigureAwait(false);
        var frameAddress = IsTruthy(frameValue) ? ParseAddress(frameValue) : 0;
        for (var count = 0; count < 256 && frameAddress != 0; count++)
        {
            var activation = await EvaluateNativeAsync($"((ct_debug_method_frame*)(void*)0x{frameAddress:x})->Activation", thread, _selectedFrame).ConfigureAwait(false);
            if (TryParseUnsigned(activation, out var parsed)) active.Add(parsed);
            var previous = await EvaluateNativeAsync($"((ct_debug_method_frame*)(void*)0x{frameAddress:x})->Previous", thread, _selectedFrame).ConfigureAwait(false);
            frameAddress = IsTruthy(previous) ? ParseAddress(previous) : 0;
        }
        foreach (var pair in _dataBreakpoints.Where(pair => pair.Value.ThreadState == control.Thread && pair.Value.Activation != 0 && !active.Contains(pair.Value.Activation)).ToArray())
        {
            try { await _gdb.CommandAsync($"-break-delete {pair.Key}").ConfigureAwait(false); } catch { }
            _dataBreakpoints.Remove(pair.Key);
            SendOutput("console", "A C~ local data breakpoint was removed because its owning method activation exited." + Environment.NewLine);
        }
    }

    private async Task ContinueAsync(int thread)
    {
        _pendingLogicalStep = null;
        if (_controlReady && _control is not null)
        {
            _control = new DebugControlImage(_control.Layout, await ReadMemoryAsync(_controlAddress, _control.Layout.Size).ConfigureAwait(false));
            _control.Write("StepMode", 0);
            _control.Write("StepDepth", 0);
            _control.Write("SelectedThread", 0);
            _control.Write("CurrentReason", 0);
            await WriteControlAsync().ConfigureAwait(false);
        }
        await ResumeAsync(thread).ConfigureAwait(false);
    }

    private async Task StartLogicalStepAsync(int thread, int mode)
    {
        if (!_controlReady || _control is null || _currentSite is null || _currentFunction is null || _currentControl is null)
            throw new InvalidOperationException("C~ logical stepping is available only at an instrumented C~ source location.");
        _pendingLogicalStep = new PendingLogicalStep(mode, _currentSite, _currentFunction.Name, thread, _currentControl.Thread, checked((int)_currentControl.Value));
        await ApplyLogicalStepAsync(_pendingLogicalStep).ConfigureAwait(false);
        await ResumeAsync(thread).ConfigureAwait(false);
    }

    private async Task ApplyLogicalStepAsync(PendingLogicalStep step)
    {
        if (_control is null) return;
        _control = new DebugControlImage(_control.Layout, await ReadMemoryAsync(_controlAddress, _control.Layout.Size).ConfigureAwait(false));
        _control.Write("SelectedThread", step.SelectedThread);
        _control.Write("StepDepth", checked((uint)step.Depth));
        _control.Write("StepMode", checked((uint)step.Mode));
        _control.Write("CurrentReason", 0);
        await WriteControlAsync().ConfigureAwait(false);
    }

    private async Task ResumeAsync(int thread)
    {
        _targetRunning = true;
        await _gdb.CommandAsync($"-exec-continue --thread {thread}").ConfigureAwait(false);
    }

    private void ResetStopState()
    {
        _currentSite = null; _currentFunction = null; _currentControl = null; _currentException = null;
        _currentStoppedThread = 0; _selectedThread = null; _selectedFrame = null; _pendingLogicalStep = null;
        InvalidateStopCaches();
    }

    private void SetRawStop(int thread)
    {
        ResetStopState();
        _currentStoppedThread = thread;
    }

    private void InvalidateStopCaches()
    {
        _variables.Clear(); _frameLevels.Clear(); _frameFunctions.Clear(); _frameThreads.Clear(); _frameSites.Clear();
        _nextReference = 0;
    }

    private static bool SameSourceLine(DebugSite left, DebugSite right) => left.Source.Line == right.Source.Line &&
        NormalizePath(left.Source.File).Equals(NormalizePath(right.Source.File), StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string value) => value.Trim() is not "0" and not "0x0" and not "false" and not "<unavailable>";

    private static ulong ParseAddress(string value)
    {
        var match = Regex.Match(value, "0x[0-9a-fA-F]+");
        if (!match.Success || !ulong.TryParse(match.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new InvalidDataException($"GDB returned an invalid address '{value}'.");
        return address;
    }

    private static bool TryParseUnsigned(string value, out ulong result)
    {
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)
            : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private List<Variable> ExpandExpression(VariableContainer container, int level)
    {
        var expression = container.Expression!;
        var logicalType = container.Type ?? string.Empty;
        if (logicalType.EndsWith("[]", StringComparison.Ordinal))
        {
            var lengthVariable = EvaluateVariable("Length", $"({expression})->Length", "nuint", container.FrameId, level);
            var result = new List<Variable> { lengthVariable };
            if (int.TryParse(lengthVariable.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                var elementType = logicalType[..^2];
                for (var index = 0; index < Math.Min(length, 10000); index++)
                    result.Add(EvaluateVariable($"[{index}]", $"({expression})->Data[{index}]", elementType, container.FrameId, level));
            }
            return result;
        }
        if (logicalType.Equals("string", StringComparison.Ordinal))
        {
            return
            [
                EvaluateVariable("Length", $"({expression})->Length", "nuint", container.FrameId, level),
                EvaluateVariable("UTF-8", $"(const char*)({expression})->Data", "char*", container.FrameId, level),
            ];
        }
        var mappedType = _map?.Types.FirstOrDefault(candidate => candidate.Name.Equals(logicalType, StringComparison.Ordinal));
        if (mappedType is not null)
        {
            var result = new List<Variable>();
            foreach (var field in InstanceFields(mappedType))
            {
                var member = mappedType.Kind is "class" or "delegate" ? $"({expression})->{field.Storage}" : $"({expression}).{field.Storage}";
                result.Add(EvaluateVariable(field.Name, member, field.Type, container.FrameId, level));
            }
            if (mappedType.Kind is "class" or "delegate")
            {
                var runtime = AddVariables(new VariableContainer(container.FrameId, "object-runtime", expression, logicalType));
                result.Add(new Variable("C~ Runtime", "ARC object state", runtime) { Type = "runtime" });
            }
            return result;
        }
        try
        {
            var record = Command($"-var-create - * {Quote(expression)}");
            var name = MiParser.String(record.Results, "name");
            var children = Command($"-var-list-children --all-values {Quote(name)}");
            var result = new List<Variable>();
            foreach (var item in MiParser.Array(children.Results.TryGetValue("children", out var value) ? value : null))
            {
                var wrapper = MiParser.Tuple(item);
                var child = wrapper.TryGetValue("child", out var childValue) ? MiParser.Tuple(childValue) : wrapper;
                var childName = MiParser.String(child, "exp", MiParser.String(child, "name"));
                var display = MiParser.String(child, "value", "<unavailable>");
                var type = MiParser.String(child, "type");
                var evaluateName = expression + "." + childName;
                var reference = ParseInt(MiParser.String(child, "numchild")) > 0 ? AddVariables(new VariableContainer(container.FrameId, "expression", evaluateName, type)) : 0;
                result.Add(new Variable(childName, display, reference) { Type = type, EvaluateName = evaluateName, MemoryReference = ExtractAddress(display) });
            }
            TryCommand("-var-delete " + Quote(name));
            return result;
        }
        catch { return []; }
    }

    private List<Variable> ObjectRuntimeVariables(VariableContainer container, int level)
    {
        var expression = container.Expression!;
        return
        [
            EvaluateVariable("IdentityHash", $"((ct_object*)(void*)({expression}))->IdentityHash", "uint", container.FrameId, level),
            EvaluateVariable("RefCount", $"((ct_object*)(void*)({expression}))->RefCount", "uint", container.FrameId, level),
        ];
    }

    private (string DisplayName, DebugSource? Source) ResolveLogical(string nativeName, string file, int line)
    {
        var function = _map?.Functions.FirstOrDefault(candidate => candidate.Name.Equals(nativeName, StringComparison.Ordinal));
        if (function is null) return (nativeName, string.IsNullOrWhiteSpace(file) ? null : NormalizeSource(new DebugSource { File = file, Line = line, Column = 1 }));
        var site = function.Sites.Where(candidate => candidate.Source.Line <= line).OrderByDescending(candidate => candidate.Source.Line).FirstOrDefault();
        return (string.IsNullOrWhiteSpace(function.DisplayName) ? function.Name : function.DisplayName, NormalizeSource(site?.Source ?? function.Source));
    }

    private DebugSource? NormalizeSource(DebugSource? source)
    {
        if (source is null || Path.IsPathFullyQualified(source.File)) return source;
        return new DebugSource { File = Path.GetFullPath(Path.Combine(_target!.SourceRoot, source.File)), Line = source.Line, Column = source.Column };
    }

    private List<Variable> StaticVariables(int frameId, int level)
    {
        var result = new List<Variable>();
        foreach (var type in _map?.Types ?? [])
        {
            foreach (var field in type.Fields.Where(candidate => candidate.Static))
                result.Add(EvaluateVariable($"{type.Name}.{field.Name}", field.Storage, field.Type, frameId, level));
        }
        return result;
    }

    private List<Variable> RuntimeVariables(int frameId, int level)
    {
        if (_map?.Instrumented != true || _map.MemoryDiagnostics.Equals("off", StringComparison.OrdinalIgnoreCase))
            return [new Variable("Memory diagnostics", "off", 0) { Type = "string" }];
        DebugControlImage? summary = null;
        if (_summaryLayout is not null && _summaryAddress != 0)
        {
            try { summary = new DebugControlImage(_summaryLayout, ReadMemoryAsync(_summaryAddress, _summaryLayout.Size).GetAwaiter().GetResult()); }
            catch (Exception exception) { Trace("Could not read the C~ runtime summary: " + exception.Message); }
        }
        Variable SummaryOrEvaluate(string name, string field, string expression, string type) => summary is null
            ? EvaluateVariable(name, expression, type, frameId, level)
            : new Variable(name, summary.Read(field).ToString(CultureInfo.InvariantCulture), 0) { Type = type };
        var result = new List<Variable>
        {
            new("Memory diagnostics", _map.MemoryDiagnostics, 0) { Type = "string" },
            SummaryOrEvaluate("Live object count", "LiveObjectCount", "ct_debug_live_count", "uint"),
            SummaryOrEvaluate("Total allocations", "TotalAllocations", "ct_debug_allocation_count", "uint"),
            SummaryOrEvaluate("Total final releases", "TotalFinalReleases", "ct_debug_final_release_count", "uint"),
            new("Current probe site", FormatCurrentProbe(), 0) { Type = "string" },
        };
        if (_map.MemoryDiagnostics.Equals("guarded", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(SummaryOrEvaluate("Quarantine blocks", "QuarantineBlocks", "ct_debug_quarantine_count", "uint"));
            result.Add(SummaryOrEvaluate("Quarantine bytes", "QuarantineBytes", "ct_debug_quarantine_bytes", "nuint"));
        }
        return result;
    }

    private string FormatCurrentProbe()
    {
        if (_currentSite is null || _currentControl is null || _currentControl.Site == DebugControlImage.InactiveSite)
            return "Not at a C~ probe";
        return $"{_currentSite.Id} — {Path.GetFileName(_currentSite.Source.File)}:{_currentSite.Source.Line} ({_currentSite.Kind})";
    }

    private Variable EvaluateVariable(string name, string expression, string type, int frameId, int level)
    {
        try
        {
            var thread = _frameThreads.TryGetValue(frameId, out var storedThread) ? storedThread : _currentStoppedThread;
            var record = Command($"-data-evaluate-expression --thread {thread} --frame {level} {Quote(expression)}");
            var display = MiParser.String(record.Results, "value", "<unavailable>");
            var reference = CanExpand(type, display) ? AddVariables(new VariableContainer(frameId, "expression", expression)) : 0;
            return new Variable(name, display, reference) { Type = type, EvaluateName = expression, MemoryReference = ExtractAddress(display) };
        }
        catch (Exception exception)
        {
            Trace($"Could not evaluate {expression}: {exception.Message}");
            return new Variable(name, "<unavailable>", 0) { Type = type };
        }
    }

    private (string File, int Line, DebugFunction? Function) MapBreakpoint(string source, int requestedLine)
    {
        var root = _target!.SourceRoot;
        var relative = Path.IsPathFullyQualified(source) ? Path.GetRelativePath(root, source) : source;
        relative = relative.Replace('\\', '/');
        var sites = _map!.Functions.SelectMany(function => function.Sites.Select(site => (Function: function, Site: site)))
            .Where(candidate => NormalizePath(candidate.Site.Source.File).Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(candidate.Site.Source.File).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Site.Source.Line).ThenBy(candidate => candidate.Site.Source.Column).ToArray();
        var selected = sites.FirstOrDefault(candidate => candidate.Site.Source.Line >= requestedLine);
        if (selected == default && sites.Length != 0) selected = sites[^1];
        return selected == default
            ? (relative, requestedLine, null)
            : (NormalizePath(selected.Site.Source.File), selected.Site.Source.Line, selected.Function);
    }

    private DebugFunction? ResolveSelectedFunction(int thread)
    {
        try
        {
            var record = Command($"-stack-info-frame --thread {thread}");
            var frame = MiParser.Tuple(record.Results.TryGetValue("frame", out var value) ? value : null);
            var name = MiParser.String(frame, "func");
            return _map?.Functions.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.Ordinal));
        }
        catch { return null; }
    }

    private string TranslateExpression(string expression, DebugFunction? function, int? frameId)
    {
        var storage = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in (function?.Parameters ?? []).Concat(function is null ? [] : LiveLocals(function, frameId)))
            storage[variable.Name] = variable.Storage;
        if (!string.IsNullOrWhiteSpace(function?.Receiver)) storage["this"] = function.Receiver!;
        return Regex.Replace(expression, "\\b[A-Za-z_][A-Za-z0-9_]*\\b", match =>
            storage.TryGetValue(match.Value, out var native) ? native : match.Value);
    }

    private (string Expression, string Type)? TranslateWatch(string expression, DebugFunction? function, int? frameId)
    {
        var root = Regex.Match(expression, "^\\s*([A-Za-z_][A-Za-z0-9_]*|this)");
        if (!root.Success) return null;
        DebugVariable? definition;
        if (root.Groups[1].Value == "this" && !string.IsNullOrWhiteSpace(function?.Receiver) && !string.IsNullOrWhiteSpace(function.ReceiverType))
            definition = new DebugVariable { Name = "this", Storage = function.Receiver!, Type = function.ReceiverType! };
        else
            definition = (function is null ? [] : LiveLocals(function, frameId)).Concat(function?.Parameters ?? [])
                .FirstOrDefault(variable => variable.Name.Equals(root.Groups[1].Value, StringComparison.Ordinal));
        if (definition is null) return null;
        var native = definition.Storage;
        var typeName = definition.Type;
        var position = root.Length;
        while (position < expression.Length)
        {
            while (position < expression.Length && char.IsWhiteSpace(expression[position])) position++;
            var remainder = expression[position..];
            var fieldMatch = Regex.Match(remainder, "^\\.\\s*([A-Za-z_][A-Za-z0-9_]*)");
            if (fieldMatch.Success)
            {
                var type = _map?.Types.FirstOrDefault(candidate => candidate.Name.Equals(typeName, StringComparison.Ordinal));
                var field = type is null ? null : InstanceFields(type).FirstOrDefault(candidate => candidate.Name.Equals(fieldMatch.Groups[1].Value, StringComparison.Ordinal));
                if (type is null || field is null) return null;
                native += type.Kind is "class" or "delegate" ? $"->{field.Storage}" : $".{field.Storage}";
                typeName = field.Type;
                position += fieldMatch.Length;
                continue;
            }
            var indexMatch = Regex.Match(remainder, "^\\[\\s*(\\d+)\\s*\\]");
            if (indexMatch.Success && typeName.EndsWith("[]", StringComparison.Ordinal))
            {
                native += $"->Data[{indexMatch.Groups[1].Value}]";
                typeName = typeName[..^2];
                position += indexMatch.Length;
                continue;
            }
            return null;
        }
        return (native, typeName);
    }

    private IEnumerable<DebugTypeField> InstanceFields(DebugType type)
    {
        if (!string.IsNullOrWhiteSpace(type.Base))
        {
            var parent = _map?.Types.FirstOrDefault(candidate => candidate.Name.Equals(type.Base, StringComparison.Ordinal));
            if (parent is not null)
            {
                foreach (var field in InstanceFields(parent))
                    yield return new DebugTypeField { Name = field.Name, Storage = "ct_base." + field.Storage, Type = field.Type };
            }
        }
        foreach (var field in type.Fields.Where(candidate => !candidate.Static)) yield return field;
    }

    private MiRecord Command(string command)
    {
        Trace(command);
        return _gdb.CommandAsync(command).GetAwaiter().GetResult();
    }

    private void TryCommand(string command) { try { Command(command); } catch { } }
    private void EnsureStarted() { if (_target is null) throw new InvalidOperationException("The C~ debug target has not started."); }
    private int AddVariables(VariableContainer container) { var id = Interlocked.Increment(ref _nextReference); _variables[id] = container; return id; }
    private void SendOutput(string category, string output) => SendEvent(new OutputEvent(output)
    {
        Category = category switch
        {
            "stderr" => OutputEvent.CategoryValue.Stderr,
            "stdout" => OutputEvent.CategoryValue.Stdout,
            _ => OutputEvent.CategoryValue.Console,
        },
    });
    private void Trace(string text) { if (_trace) SendOutput("console", "C~ debugger: " + text + Environment.NewLine); }
    private void TerminateOnce(string? message)
    {
        if (_terminated) return;
        _terminated = true;
        _launchLease?.Dispose();
        _launchLease = null;
        if (!string.IsNullOrWhiteSpace(message)) SendOutput("stderr", message + Environment.NewLine);
        SendEvent(new TerminatedEvent());
    }

    private void SendEvent(DebugEvent debugEvent)
    {
        lock (_eventGate)
            Protocol.SendEvent(debugEvent);
    }

    private static bool IsRuntimeFrame(string function) => function.StartsWith("ct_", StringComparison.Ordinal) || function.StartsWith("__ct", StringComparison.Ordinal);
    private bool IsQemu() => _target?.Target.Equals("esp-idf", StringComparison.Ordinal) == true &&
        _target.TargetEnvironment.Equals("qemu", StringComparison.Ordinal);
    private static string NormalizePath(string path) => path.Replace('\\', '/');
    private bool CanExpand(string type, string value) => type.Equals("string", StringComparison.Ordinal) || type.EndsWith("[]", StringComparison.Ordinal) ||
        _map?.Types.Any(candidate => candidate.Name.Equals(type, StringComparison.Ordinal) && (candidate.Fields.Length != 0 || candidate.Kind is "class" or "delegate")) == true ||
        type.Contains('*') || value.StartsWith('{') || value.Contains(" = {");
    private static string? ExtractAddress(string value) { var start = value.IndexOf("0x", StringComparison.OrdinalIgnoreCase); if (start < 0) return null; var end = start + 2; while (end < value.Length && Uri.IsHexDigit(value[end])) end++; return value[start..end]; }
    private static bool IsSafeWatch(string expression) => !string.IsNullOrWhiteSpace(expression) && expression.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '[' or ']' || char.IsWhiteSpace(character));
    private static string Quote(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    private static int ParseInt(string value, int fallback = 0) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static string RequiredString(IReadOnlyDictionary<string, JToken> properties, string name) => OptionalString(properties, name) ?? throw new InvalidOperationException($"Missing C~ debug launch option '{name}'.");
    private static string? OptionalString(IReadOnlyDictionary<string, JToken> properties, string name) => properties.TryGetValue(name, out var value) && value.Type != JTokenType.Null ? value.Value<string>() : null;
    private static bool OptionalBoolean(IReadOnlyDictionary<string, JToken> properties, string name) => properties.TryGetValue(name, out var value) && value.Type == JTokenType.Boolean && value.Value<bool>();
    private static void ValidateMemoryMode(string? requested, string prepared) { if (!string.IsNullOrWhiteSpace(requested) && !requested.Equals(prepared, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The prepared target uses '{prepared}' memory diagnostics, not '{requested}'. Prepare it again."); }
    private static FileStream AcquireLaunchLease(string descriptorPath)
    {
        var descriptor = Path.GetFullPath(descriptorPath);
        var directory = Path.GetDirectoryName(descriptor)!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(descriptor) + ".lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("This C~ project is already running or being debugged.", exception); }
    }

    public void Dispose()
    {
        _gdb.Dispose();
        if (_qemu is not null) _qemu.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _launchLease?.Dispose();
        if (_terminal is not null) _terminal.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record VariableContainer(int FrameId, string Kind, string? Expression = null, string? Type = null);
    private sealed record PendingLogicalStep(int Mode, DebugSite Origin, string OriginFunction, int ThreadId, ulong SelectedThread, int Depth);
    private sealed record RuntimeException(string Id, string Description, ExceptionBreakMode BreakMode);
    private sealed record DataBreakpointTarget(string Expression, string Type, int FrameId, ulong ThreadState, ulong Activation);
}
