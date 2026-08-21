import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'fs';
import { tmpdir } from 'os';
import * as path from 'path';
import { spawnSync } from 'child_process';
import {
    Breakpoint,
    BreakpointEvent,
    ContinuedEvent,
    Handles,
    InitializedEvent,
    LoggingDebugSession,
    OutputEvent,
    Scope,
    Source,
    StackFrame,
    StoppedEvent,
    TerminatedEvent,
    Thread,
} from '@vscode/debugadapter';
import { DebugProtocol } from '@vscode/debugprotocol';
import { GdbMi, MiRecord, MiTuple, miArray, miString, miTuple } from './gdbMi';

const espSerialBridgeSource = [
    'import os,serial,sys,threading,time',
    'last=None',
    'for attempt in range(25):',
    '  try:',
    '    connection=serial.Serial(sys.argv[1],int(sys.argv[2]),timeout=.05,dsrdtr=False,rtscts=False)',
    '    break',
    '  except Exception as error:',
    '    last=error;time.sleep(.2)',
    'else:',
    '  raise last',
    'deadline=time.time()+1.5',
    'while time.time()<deadline:',
    '  connection.read(4096)',
    'connection.write(b"\\x03");connection.flush()',
    'def target_to_gdb():',
    '  protocol_started=False',
    '  while True:',
    '    data=connection.read(4096)',
    '    if not data:',
    '      continue',
    '    if not protocol_started:',
    '      marker=data.find(b"$")',
    '      if marker<0:',
    '        continue',
    '      data=data[marker:];protocol_started=True',
    '    os.write(sys.stdout.fileno(),data)',
    'threading.Thread(target=target_to_gdb,daemon=True).start()',
    'try:',
    '  while True:',
    '    data=os.read(sys.stdin.fileno(),4096)',
    '    if not data:',
    '      break',
    '    connection.write(data);connection.flush()',
    'finally:',
    '  connection.close()',
    '',
].join('\n');

interface DebugSource { readonly file: string; readonly line: number; readonly column: number; }
interface DebugVariable { readonly name: string; readonly storage: string; readonly type: string; readonly durable?: boolean; }
interface DebugFunction {
    readonly name: string;
    readonly displayName: string;
    readonly source?: DebugSource;
    readonly receiver?: string;
    readonly receiverType?: string;
    readonly parameters: readonly DebugVariable[];
    readonly locals: readonly DebugVariable[];
    readonly executable: readonly DebugSource[];
}
interface DebugTypeField { readonly name: string; readonly storage: string; readonly type: string; readonly static: boolean; }
interface DebugType { readonly name: string; readonly storage: string; readonly kind: string; readonly base?: string; readonly fields: readonly DebugTypeField[]; readonly values?: readonly { name: string; value: string }[]; }
interface DebugMap {
    readonly version: number;
    readonly files: readonly string[];
    readonly entryPoint?: string;
    readonly functions: readonly DebugFunction[];
    readonly types: readonly DebugType[];
    readonly boxes?: readonly { type: string; storage: string; valueType: string }[];
    readonly runtimeHooks: { readonly throw: string; readonly fatal: string };
}
interface DebugTarget {
    readonly version: number;
    readonly target: 'hosted' | 'esp-idf';
    readonly backend: 'gdb' | 'msvc';
    readonly program: string;
    readonly debugMap: string;
    readonly sourceRoot: string;
    readonly workingDirectory: string;
    readonly gdbCommand?: string;
    readonly gdbPrefixArguments?: readonly string[];
    readonly serialPython?: string;
    readonly espTarget?: string;
    readonly serialPort?: string;
    readonly baudRate?: number;
}
interface CTildeDebugArguments {
    readonly debugTarget: string;
    readonly request: 'launch' | 'attach';
    readonly args?: readonly string[];
    readonly cwd?: string;
    readonly stopAtEntry?: boolean;
    readonly showRuntimeFrames?: boolean;
    readonly processId?: string | number;
    readonly gdbPath?: string;
    readonly serialPort?: string;
    readonly baudRate?: number;
}
interface VariableContainer {
    readonly expression: string;
    readonly type: string;
    readonly frameId: number;
}

export class CTildeDebugSession extends LoggingDebugSession {
    private readonly gdb = new GdbMi();
    private readonly variableHandles = new Handles<VariableContainer>();
    private readonly frameLevels = new Map<number, number>();
    private readonly frameFunctions = new Map<number, DebugFunction | undefined>();
    private readonly sourceBreakpoints = new Map<string, number[]>();
    private functionBreakpoints: number[] = [];
    private readonly logpoints = new Map<number, { message: string; fn?: DebugFunction }>();
    private exceptionBreakpoints: number[] = [];
    private exceptionFilters = new Set<string>();
    private target: DebugTarget | undefined;
    private debugMap: DebugMap | undefined;
    private configurationDone: (() => void) | undefined;
    private configurationPromise = new Promise<void>(resolve => this.configurationDone = resolve);
    private request: 'launch' | 'attach' = 'launch';
    private launchArguments: CTildeDebugArguments | undefined;
    private stoppedException: { id: string; description: string; breakMode: 'always' | 'unhandled' } | undefined;
    private espBridgePath: string | undefined;
    private pendingRuntimeHook: 'throw' | 'fatal' | undefined;
    private suppressNextRunningEvent = false;
    private suppressNextTargetStop = false;

    public constructor() {
        super('ctilde-debug.txt');
        this.setDebuggerLinesStartAt1(true);
        this.setDebuggerColumnsStartAt1(true);
        this.gdb.onOutput = (category, text) => this.sendEvent(new OutputEvent(text, category === 'stdout' ? 'stdout' : category === 'stderr' ? 'stderr' : 'console'));
        this.gdb.onAsync = record => void this.handleAsync(record);
        this.gdb.onExit = () => {
            this.removeEspBridge();
            this.sendEvent(new TerminatedEvent());
        };
    }

    protected initializeRequest(response: DebugProtocol.InitializeResponse): void {
        response.body = {
            supportsConfigurationDoneRequest: true,
            supportsConditionalBreakpoints: true,
            supportsHitConditionalBreakpoints: true,
            supportsFunctionBreakpoints: true,
            supportsExceptionInfoRequest: true,
            supportsEvaluateForHovers: true,
            supportsTerminateRequest: true,
            supportsRestartRequest: true,
            supportsCancelRequest: true,
            supportTerminateDebuggee: true,
            exceptionBreakpointFilters: [
                { filter: 'ctilde-thrown', label: 'All thrown C~ exceptions', default: false },
                { filter: 'ctilde-unhandled', label: 'Unhandled C~ exceptions', default: false },
                { filter: 'ctilde-fatal', label: 'Fatal C~ runtime failures', default: false },
            ],
        };
        this.sendResponse(response);
    }

    protected launchRequest(response: DebugProtocol.LaunchResponse, args: DebugProtocol.LaunchRequestArguments): void {
        void this.startSession(response, args as CTildeDebugArguments, 'launch');
    }

    protected attachRequest(response: DebugProtocol.AttachResponse, args: DebugProtocol.AttachRequestArguments): void {
        void this.startSession(response, args as CTildeDebugArguments, 'attach');
    }

    private async startSession(response: DebugProtocol.Response, args: CTildeDebugArguments, request: 'launch' | 'attach'): Promise<void> {
        let operation = 'reading the debug descriptor';
        try {
            this.request = request;
            this.launchArguments = args;
            const targetPath = path.resolve(args.debugTarget);
            if (!existsSync(targetPath))
                throw new Error(`C~ debug target does not exist: ${targetPath}`);
            this.target = JSON.parse(readFileSync(targetPath, 'utf8')) as DebugTarget;
            this.debugMap = JSON.parse(readFileSync(this.target.debugMap, 'utf8')) as DebugMap;
            if (this.target.version !== 1 || this.debugMap.version !== 1)
                throw new Error('Unsupported C~ debug metadata version. Rebuild the project with the current compiler.');
            if (this.target.backend !== 'gdb')
                throw new Error('This configuration was built with MSVC. Use C~: Debug Project for the cppvsdbg fallback.');
            if (!existsSync(this.target.program))
                throw new Error(`Debug program does not exist: ${this.target.program}`);
            const gdbCommand = args.gdbPath?.trim() || this.target.gdbCommand || 'gdb';
            operation = `starting ${gdbCommand}`;
            this.gdb.start(gdbCommand, this.target.gdbPrefixArguments ?? [], args.cwd || this.target.workingDirectory);
            operation = 'loading native debug symbols';
            await this.gdb.command(`-file-exec-and-symbols ${miQuote(this.debuggerProgram(gdbCommand))}`);
            operation = 'configuring GDB';
            await this.gdb.command('-gdb-set pagination off');
            await this.gdb.command('-gdb-set print elements 200');
            if (this.target.target === 'esp-idf') {
                await this.gdb.command(`-interpreter-exec console ${miQuote('set remote trace-status-packet off')}`);
                await this.gdb.command(`-interpreter-exec console ${miQuote('set remote software-breakpoint-packet on')}`);
            }
            if (request === 'launch' && args.args !== undefined && args.args.length !== 0)
                await this.gdb.command(`-exec-arguments ${args.args.map(miQuote).join(' ')}`);
            this.sendEvent(new InitializedEvent());
            this.sendResponse(response);
        } catch (error) {
            const detail = error instanceof Error ? error.message : String(error);
            this.sendErrorResponse(response, 1001, `Debug startup failed while ${operation}: ${detail}`);
        }
    }

    protected async configurationDoneRequest(response: DebugProtocol.ConfigurationDoneResponse): Promise<void> {
        this.configurationDone?.();
        this.sendResponse(response);
        try {
            if (this.target?.target === 'esp-idf') {
                this.suppressNextTargetStop = this.request === 'launch' && !this.launchArguments?.stopAtEntry;
                await this.gdb.command(`-interpreter-exec console ${miQuote(`target remote ${this.espRemoteTarget()}`)}`);
                if (this.request === 'launch' && !this.launchArguments?.stopAtEntry)
                    await this.gdb.command('-exec-continue');
            } else if (this.request === 'launch') {
                await this.configurationPromise;
                if (this.launchArguments?.stopAtEntry) {
                    const entry = this.debugMap?.functions.find(candidate => candidate.name === this.debugMap?.entryPoint);
                    if (entry !== undefined)
                        await this.gdb.command(`-break-insert -t ${entry.name}`);
                }
                await this.gdb.command('-exec-run');
            } else if (this.launchArguments?.processId !== undefined) {
                await this.gdb.command(`-target-attach ${String(this.launchArguments.processId)}`);
            } else {
                this.sendEvent(new OutputEvent('Hosted attach requires processId.\n', 'stderr'));
            }
        } catch (error) {
            this.sendEvent(new OutputEvent(`Debugger start failed: ${String(error instanceof Error ? error.message : error)}\n`, 'stderr'));
            this.sendEvent(new TerminatedEvent());
        }
    }

    protected async setBreakPointsRequest(response: DebugProtocol.SetBreakpointsResponse, args: DebugProtocol.SetBreakpointsArguments): Promise<void> {
        try {
            const sourcePath = args.source.path === undefined ? '' : path.resolve(args.source.path);
            const relative = this.relativeSource(sourcePath);
            for (const id of this.sourceBreakpoints.get(relative) ?? []) {
                try { await this.gdb.command(`-break-delete ${id}`); } catch { }
                this.logpoints.delete(id);
            }
            const installed: number[] = [];
            const executable = this.debugMap?.functions.flatMap(fn => fn.executable)
                .filter(location => normalizePath(location.file) === relative) ?? [];
            const result: DebugProtocol.Breakpoint[] = [];
            for (const requested of args.breakpoints ?? []) {
                const actualLine = executable.filter(location => location.line >= requested.line)
                    .sort((left, right) => left.line - right.line)[0]?.line ?? requested.line;
                const fn = this.debugMap?.functions.find(candidate => candidate.executable.some(location =>
                    normalizePath(location.file) === relative && location.line === actualLine));
                const options: string[] = [];
                if (requested.condition)
                    options.push('-c', miQuote(this.translateExpression(requested.condition, fn)));
                if (requested.hitCondition && /^\d+$/.test(requested.hitCondition))
                    options.push('-i', requested.hitCondition);
                if (!this.hasEspBreakpointSlot()) {
                    const breakpoint = new Breakpoint(false, actualLine, 1,
                        new Source(path.basename(sourcePath), sourcePath)) as DebugProtocol.Breakpoint;
                    breakpoint.message = this.espBreakpointLimitMessage();
                    result.push(breakpoint);
                    continue;
                }
                const record = await this.gdb.command(`-break-insert -f ${options.join(' ')} ${miQuote(`${relative}:${actualLine}`)}`);
                const bkpt = miTuple(record.results.bkpt);
                const breakpoint = new Breakpoint(true, Number.parseInt(miString(bkpt.line) || String(actualLine), 10), 1,
                    new Source(path.basename(sourcePath), sourcePath)) as DebugProtocol.Breakpoint;
                breakpoint.id = Number.parseInt(miString(bkpt.number), 10);
                installed.push(breakpoint.id);
                if (requested.logMessage !== undefined)
                    this.logpoints.set(breakpoint.id, { message: requested.logMessage, fn });
                result.push(breakpoint);
            }
            this.sourceBreakpoints.set(relative, installed);
            response.body = { breakpoints: result };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1002, String(error));
        }
    }

    protected async setFunctionBreakPointsRequest(response: DebugProtocol.SetFunctionBreakpointsResponse, args: DebugProtocol.SetFunctionBreakpointsArguments): Promise<void> {
        for (const id of this.functionBreakpoints) {
            try { await this.gdb.command(`-break-delete ${id}`); } catch { }
        }
        this.functionBreakpoints = [];
        const breakpoints: DebugProtocol.Breakpoint[] = [];
        for (const requested of args.breakpoints) {
            const candidates = this.debugMap?.functions.filter(fn => fn.displayName === requested.name || fn.displayName.endsWith(`.${requested.name}`)) ?? [];
            if (candidates.length !== 1) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = candidates.length === 0 ? 'C~ function was not found.' : 'C~ function name is ambiguous.';
                breakpoints.push(breakpoint);
                continue;
            }
            try {
                if (!this.hasEspBreakpointSlot()) {
                    const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                    breakpoint.message = this.espBreakpointLimitMessage();
                    breakpoints.push(breakpoint);
                    continue;
                }
                const options: string[] = [];
                if (requested.condition)
                    options.push('-c', miQuote(this.translateExpression(requested.condition, candidates[0])));
                if (requested.hitCondition && /^\d+$/.test(requested.hitCondition))
                    options.push('-i', requested.hitCondition);
                const record = await this.gdb.command(`-break-insert ${options.join(' ')} ${miQuote(candidates[0].name)}`);
                const breakpoint = new Breakpoint(true) as DebugProtocol.Breakpoint;
                breakpoint.id = Number.parseInt(miString(miTuple(record.results.bkpt).number), 10);
                this.functionBreakpoints.push(breakpoint.id);
                breakpoints.push(breakpoint);
            } catch (error) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = String(error);
                breakpoints.push(breakpoint);
            }
        }
        response.body = { breakpoints };
        this.sendResponse(response);
    }

    protected async setExceptionBreakPointsRequest(response: DebugProtocol.SetExceptionBreakpointsResponse, args: DebugProtocol.SetExceptionBreakpointsArguments): Promise<void> {
        for (const id of this.exceptionBreakpoints) {
            try { await this.gdb.command(`-break-delete ${id}`); } catch { }
        }
        this.exceptionBreakpoints = [];
        const filters = new Set(args.filters);
        this.exceptionFilters = filters;
        const hooks = this.debugMap?.runtimeHooks;
        if (hooks !== undefined) {
            const requestedHooks = [
                filters.has('ctilde-thrown') || filters.has('ctilde-unhandled') ? hooks.throw : undefined,
                filters.has('ctilde-fatal') ? hooks.fatal : undefined,
            ].filter((hook): hook is string => hook !== undefined);
            for (const hook of requestedHooks) {
                if (!this.hasEspBreakpointSlot()) {
                    this.sendEvent(new OutputEvent(`${this.espBreakpointLimitMessage()} Exception breakpoint '${hook}' was not installed.\n`, 'stderr'));
                    continue;
                }
                this.exceptionBreakpoints.push(await this.insertBreakpoint(hook));
            }
        }
        this.sendResponse(response);
    }

    protected async threadsRequest(response: DebugProtocol.ThreadsResponse): Promise<void> {
        const record = await this.gdb.command('-thread-info');
        response.body = {
            threads: miArray(record.results.threads).map(value => miTuple(value)).map(thread =>
                new Thread(Number.parseInt(miString(thread.id), 10), miString(thread.name) || `Thread ${miString(thread.id)}`)),
        };
        this.sendResponse(response);
    }

    protected async stackTraceRequest(response: DebugProtocol.StackTraceResponse, args: DebugProtocol.StackTraceArguments): Promise<void> {
        const record = await this.gdb.command(`-stack-list-frames --thread ${args.threadId}`);
        const frames: StackFrame[] = [];
        this.frameLevels.clear();
        this.frameFunctions.clear();
        for (const value of miArray(record.results.stack)) {
            const wrapper = miTuple(value);
            const frame = miTuple(wrapper.frame ?? value);
            const rawName = miString(frame.func);
            const mapped = this.debugMap?.functions.find(fn => fn.name === rawName);
            const file = miString(frame.fullname) || miString(frame.file);
            const generated = file === '<ctilde-generated>' || (mapped === undefined && rawName.startsWith('ct_'));
            if (generated && !this.launchArguments?.showRuntimeFrames)
                continue;
            const level = Number.parseInt(miString(frame.level), 10);
            const id = frames.length + 1;
            this.frameLevels.set(id, level);
            this.frameFunctions.set(id, mapped);
            const sourcePath = mapped?.source === undefined ? file : this.absoluteSource(mapped.source.file);
            frames.push(new StackFrame(id, mapped?.displayName ?? rawName,
                sourcePath.length === 0 ? undefined : new Source(path.basename(sourcePath), sourcePath),
                Number.parseInt(miString(frame.line) || String(mapped?.source?.line ?? 1), 10), 1));
        }
        response.body = { stackFrames: frames, totalFrames: frames.length };
        this.sendResponse(response);
    }

    protected scopesRequest(response: DebugProtocol.ScopesResponse, args: DebugProtocol.ScopesArguments): void {
        response.body = {
            scopes: [
                new Scope('Locals', this.variableHandles.create({ expression: '$locals', type: '$scope', frameId: args.frameId }), false),
                new Scope('Arguments', this.variableHandles.create({ expression: '$arguments', type: '$scope', frameId: args.frameId }), false),
            ],
        };
        this.sendResponse(response);
    }

    protected async variablesRequest(response: DebugProtocol.VariablesResponse, args: DebugProtocol.VariablesArguments): Promise<void> {
        const container = this.variableHandles.get(args.variablesReference);
        if (container === undefined) {
            response.body = { variables: [] };
            this.sendResponse(response);
            return;
        }
        try {
            const level = this.frameLevels.get(container.frameId) ?? 0;
            await this.gdb.command(`-stack-select-frame ${level}`);
            if (container.type === '$scope') {
                const fn = this.frameFunctions.get(container.frameId);
                const definitions = container.expression === '$arguments' ? fn?.parameters ?? [] : fn?.locals ?? [];
                const variables: DebugProtocol.Variable[] = [];
                for (const definition of definitions) {
                    try {
                        variables.push(await this.makeVariable(definition.name, definition.storage, definition.type, container.frameId));
                    } catch {
                        // GDB rejects locals that are outside their lexical scope or optimized away.
                    }
                }
                if (container.expression === '$arguments' && fn?.receiver !== undefined && fn.receiverType !== undefined) {
                    try { variables.push(await this.makeVariable('this', fn.receiver, fn.receiverType, container.frameId)); } catch { }
                }
                response.body = { variables };
            } else
                response.body = { variables: await this.expandVariable(container, args.start, args.count) };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1003, String(error));
        }
    }

    protected async evaluateRequest(response: DebugProtocol.EvaluateResponse, args: DebugProtocol.EvaluateArguments): Promise<void> {
        try {
            if (args.context === 'repl' && args.expression.trimStart().startsWith('$gdb ')) {
                const expression = args.expression.trimStart().slice(5).trim();
                if (expression.length === 0)
                    throw new Error('Use $gdb <native-expression> for raw GDB evaluation.');
                response.body = { result: await this.evaluateNative(expression), variablesReference: 0 };
                this.sendResponse(response);
                return;
            }
            const frameId = args.frameId ?? 1;
            const fn = this.frameFunctions.get(frameId);
            const watch = this.translateWatch(args.expression.trim(), fn);
            if (watch === undefined)
                throw new Error('C~ watches support identifiers, field access, and array indices.');
            const variable = await this.makeVariable(args.expression, watch.expression, watch.type, frameId);
            response.body = { result: variable.value, type: variable.type, variablesReference: variable.variablesReference };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1004, String(error));
        }
    }

    protected continueRequest(response: DebugProtocol.ContinueResponse, args: DebugProtocol.ContinueArguments): void {
        void this.gdb.command(`-exec-continue --thread ${args.threadId}`);
        response.body = { allThreadsContinued: true };
        this.sendResponse(response);
        this.sendEvent(new ContinuedEvent(args.threadId, true));
    }

    protected nextRequest(response: DebugProtocol.NextResponse, args: DebugProtocol.NextArguments): void {
        void this.gdb.command(`-exec-next --thread ${args.threadId}`);
        this.sendResponse(response);
    }

    protected stepInRequest(response: DebugProtocol.StepInResponse, args: DebugProtocol.StepInArguments): void {
        void this.gdb.command(`-exec-step --thread ${args.threadId}`);
        this.sendResponse(response);
    }

    protected stepOutRequest(response: DebugProtocol.StepOutResponse, args: DebugProtocol.StepOutArguments): void {
        void this.gdb.command(`-exec-finish --thread ${args.threadId}`);
        this.sendResponse(response);
    }

    protected pauseRequest(response: DebugProtocol.PauseResponse, args: DebugProtocol.PauseArguments): void {
        void this.gdb.command(`-exec-interrupt --thread ${args.threadId}`);
        this.sendResponse(response);
    }

    protected async disconnectRequest(response: DebugProtocol.DisconnectResponse): Promise<void> {
        try {
            if (this.target?.target === 'esp-idf')
                await this.gdb.command(`-interpreter-exec console ${miQuote('set confirm off')}`).then(() =>
                    this.gdb.command(`-interpreter-exec console ${miQuote('kill')}`));
            else
                await this.gdb.command('-target-detach');
        } catch { }
        await this.gdb.close();
        this.removeEspBridge();
        this.sendResponse(response);
    }

    protected async terminateRequest(response: DebugProtocol.TerminateResponse): Promise<void> {
        try {
            if (this.target?.target === 'esp-idf')
                await this.gdb.command(`-interpreter-exec console ${miQuote('set confirm off')}`).then(() =>
                    this.gdb.command(`-interpreter-exec console ${miQuote('kill')}`));
            else
                await this.gdb.command('-exec-abort');
        } catch { }
        await this.gdb.close();
        this.removeEspBridge();
        this.sendResponse(response);
    }

    protected async restartRequest(response: DebugProtocol.RestartResponse): Promise<void> {
        try {
            try { await this.gdb.command('-exec-abort'); } catch { }
            await this.gdb.command('-exec-run');
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1005, String(error));
        }
    }

    protected cancelRequest(response: DebugProtocol.CancelResponse): void {
        void this.gdb.command('-exec-interrupt').catch(() => undefined);
        this.sendResponse(response);
    }

    protected exceptionInfoRequest(response: DebugProtocol.ExceptionInfoResponse): void {
        const current = this.stoppedException ?? { id: 'C~ exception', description: 'C~ runtime stopped.', breakMode: 'always' as const };
        response.body = { exceptionId: current.id, description: current.description, breakMode: current.breakMode };
        this.sendResponse(response);
    }

    private async makeVariable(name: string, expression: string, type: string, frameId: number): Promise<DebugProtocol.Variable> {
        if (type === 'string') {
            const nullResult = await this.evaluateNative(`${expression} == 0`);
            if (nullResult !== '0')
                return { name, value: 'null', type, variablesReference: 0 };
            const length = Number.parseInt(await this.evaluateNative(`${expression}->Length`), 10);
            const address = await this.evaluateNative(`(void*)${expression}->Data`);
            const contents = length <= 0 ? '' : await this.readUtf8(address, Math.min(length, 4096));
            const suffix = length > 4096 ? '…' : '';
            return { name, value: JSON.stringify(contents + suffix), type, variablesReference: this.variableHandles.create({ expression, type, frameId }) };
        }
        const mappedType = this.debugMap?.types.find(candidate => candidate.name === type);
        const reference = type.endsWith('[]') || mappedType?.kind === 'class' || mappedType?.kind === 'delegate';
        if (reference && await this.evaluateNative(`${expression} == 0`) !== '0')
            return { name, value: 'null', type, variablesReference: 0 };
        const value = await this.evaluateNative(expression);
        if (mappedType?.kind === 'enum') {
            const enumValue = mappedType.values?.find(candidate => candidate.value === value);
            return { name, value: enumValue === undefined ? value : `${enumValue.name} (${value})`, type, variablesReference: 0 };
        }
        let displayType = type;
        let expansionType = type;
        if (mappedType?.kind === 'class') {
            const actual = await this.evaluateCString(`((ct_object*)(void*)${expression})->Type->Name`);
            const box = this.debugMap?.boxes?.find(candidate => candidate.valueType === actual);
            if (box !== undefined) {
                displayType = `boxed ${actual}`;
                expansionType = `$box:${actual}`;
            } else if (actual.length !== 0) {
                displayType = actual;
                expansionType = actual;
            }
        }
        const expandable = reference || (!isPrimitive(type) && value !== 'null' && value !== '0x0');
        return { name, value, type: displayType || undefined, variablesReference: expandable ? this.variableHandles.create({ expression, type: expansionType, frameId }) : 0 };
    }

    private async expandVariable(container: VariableContainer, start = 0, count = 100): Promise<DebugProtocol.Variable[]> {
        if (container.type === 'string') {
            return [
                { name: 'Length', value: await this.evaluateNative(`${container.expression}->Length`), type: 'int', variablesReference: 0 },
                { name: '$runtime', value: await this.evaluateNative(`*${container.expression}`), variablesReference: 0 },
            ];
        }
        if (container.type.endsWith('[]')) {
            const length = Number.parseInt(await this.evaluateNative(`${container.expression}->Length`), 10);
            const elementType = container.type.slice(0, -2);
            const end = Math.min(length, start + (count <= 0 ? 100 : count));
            const result: DebugProtocol.Variable[] = [];
            for (let index = start; index < end; index++)
                result.push(await this.makeVariable(`[${index}]`, `${container.expression}->Data[${index}]`, elementType, container.frameId));
            return result;
        }
        if (container.type.startsWith('$box:')) {
            const valueType = container.type.slice(5);
            const box = this.debugMap?.boxes?.find(candidate => candidate.valueType === valueType);
            if (box !== undefined)
                return [await this.makeVariable('Value', `((${box.storage}*)(void*)${container.expression})->Value`, valueType, container.frameId)];
        }
        const type = this.debugMap?.types.find(candidate => candidate.name === container.type);
        if (type !== undefined) {
            const pointer = type.kind === 'class' || type.kind === 'delegate';
            const member = pointer ? '->' : '.';
            const expression = pointer ? `((${type.storage}*)(void*)${container.expression})` : container.expression;
            const result: DebugProtocol.Variable[] = [];
            if (type.kind === 'delegate') {
                result.push({ name: 'Target', value: await this.evaluateNative(`${expression}->ct_target`), type: 'object', variablesReference: 0 });
                result.push({ name: 'Method', value: await this.evaluateNative(`${expression}->ct_invoke`), variablesReference: 0 });
            }
            for (const field of this.instanceFields(type))
                result.push(await this.makeVariable(field.name, `${expression}${member}${field.storage}`, field.type, container.frameId));
            if (pointer)
                result.push({ name: '$runtime', value: await this.evaluateNative(`*(ct_object*)(void*)${container.expression}`), variablesReference: 0 });
            return result;
        }
        return [{ name: '$native', value: await this.evaluateNative(container.expression), variablesReference: 0 }];
    }

    private async evaluateNative(expression: string): Promise<string> {
        const record = await this.gdb.command(`-data-evaluate-expression ${miQuote(expression)}`);
        return miString(record.results.value);
    }

    private async readUtf8(address: string, length: number): Promise<string> {
        const record = await this.gdb.command(`-data-read-memory-bytes ${address} ${length}`);
        const memory = miArray(record.results.memory).map(miTuple)[0];
        const hex = miString(memory?.contents);
        return Buffer.from(hex, 'hex').toString('utf8');
    }

    private async handleAsync(record: MiRecord): Promise<void> {
        if (record.kind !== '*')
            return;
        if (record.name === 'running') {
            if (this.suppressNextRunningEvent) {
                this.suppressNextRunningEvent = false;
                return;
            }
            this.sendEvent(new ContinuedEvent(0, true));
            return;
        }
        if (record.name !== 'stopped')
            return;
        if (this.suppressNextTargetStop) {
            this.suppressNextTargetStop = false;
            return;
        }
        this.variableHandles.reset();
        this.frameLevels.clear();
        this.frameFunctions.clear();
        const reason = miString(record.results.reason);
        const threadId = Number.parseInt(miString(record.results['thread-id']) || '1', 10);
        const breakpointNumber = Number.parseInt(miString(record.results.bkptno), 10);
        const logpoint = this.logpoints.get(breakpointNumber);
        if (logpoint !== undefined) {
            this.sendEvent(new OutputEvent(await this.renderLogMessage(logpoint.message, logpoint.fn) + '\n', 'console'));
            void this.gdb.command(`-exec-continue --thread ${threadId}`);
            return;
        }
        const frame = miTuple(record.results.frame);
        const functionName = miString(frame.func);
        const runtimeHook = functionName === this.debugMap?.runtimeHooks.throw
            ? 'throw'
            : functionName === this.debugMap?.runtimeHooks.fatal ? 'fatal' : undefined;
        if (runtimeHook !== undefined && this.pendingRuntimeHook === undefined) {
            this.pendingRuntimeHook = runtimeHook;
            this.suppressNextRunningEvent = true;
            void this.gdb.command(`-exec-step-instruction --thread ${threadId}`).catch(error => {
                this.pendingRuntimeHook = undefined;
                this.sendEvent(new OutputEvent(`Could not inspect the C~ runtime hook: ${String(error)}\n`, 'stderr'));
                this.sendEvent(new StoppedEvent('exception', threadId));
            });
            return;
        }
        const inspectedHook = this.pendingRuntimeHook;
        this.pendingRuntimeHook = undefined;
        if (inspectedHook === 'throw') {
            const code = await this.evaluateCString('code');
            const file = await this.evaluateCString('file');
            const line = await this.evaluateNative('line');
            const unhandled = await this.evaluateNative('unhandled');
            if (!this.exceptionFilters.has('ctilde-thrown') && unhandled === '0') {
                this.suppressNextRunningEvent = true;
                void this.gdb.command(`-exec-continue --thread ${threadId}`);
                return;
            }
            this.stoppedException = { id: code, description: `${code} at ${file}:${line}`, breakMode: unhandled === '0' ? 'always' : 'unhandled' };
            this.sendEvent(new StoppedEvent('exception', threadId, this.stoppedException.description));
        } else if (inspectedHook === 'fatal') {
            const code = await this.evaluateCString('code');
            this.stoppedException = { id: code, description: `Fatal C~ runtime failure ${code}`, breakMode: 'unhandled' };
            this.sendEvent(new StoppedEvent('exception', threadId, this.stoppedException.description));
        } else
            this.sendEvent(new StoppedEvent(reason === 'breakpoint-hit' ? 'breakpoint' : 'step', threadId));
    }

    private relativeSource(sourcePath: string): string {
        const absolute = normalizePath(path.resolve(sourcePath));
        return this.debugMap?.files.includes(absolute)
            ? absolute
            : normalizePath(path.relative(this.target!.sourceRoot, sourcePath));
    }

    private absoluteSource(sourcePath: string): string {
        return path.isAbsolute(sourcePath) ? sourcePath : path.resolve(this.target!.sourceRoot, sourcePath);
    }

    private espRemoteTarget(): string {
        const args = this.launchArguments!;
        const port = args.serialPort || this.target?.serialPort;
        const baud = args.baudRate || this.target?.baudRate || 115200;
        if (!port)
            throw new Error('ESP-IDF runtime GDB-stub debugging requires serialPort.');
        const pythonEnvironment = process.env.IDF_PYTHON_ENV_PATH;
        const python = this.target?.serialPython ?? (pythonEnvironment === undefined
            ? (process.platform === 'win32' ? 'python.exe' : 'python3')
            : path.join(pythonEnvironment, process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python'));
        if (!existsSync(python))
            throw new Error(`ESP-IDF Python interpreter does not exist: ${python}`);
        const helper = path.join(tmpdir(), `ctilde-gdb-serial-bridge-${process.pid}.py`);
        writeFileSync(helper, espSerialBridgeSource, 'utf8');
        this.espBridgePath = helper;
        const quote = (value: string): string => `"${value.replaceAll('"', '""')}"`;
        return `| ${quote(python)} -u ${quote(helper)} ${quote(port)} ${baud}`;
    }

    private removeEspBridge(): void {
        if (this.espBridgePath === undefined)
            return;
        try { unlinkSync(this.espBridgePath); } catch { }
        this.espBridgePath = undefined;
    }

    private debuggerProgram(gdbCommand: string): string {
        const prefix = this.target?.gdbPrefixArguments ?? [];
        if (!path.basename(gdbCommand).toLocaleLowerCase().startsWith('wsl') || !prefix.includes('gdb'))
            return this.target!.program;
        const converted = spawnSync(gdbCommand, ['--exec', 'wslpath', '-a', this.target!.program],
            { encoding: 'utf8', windowsHide: true, timeout: 5000 });
        if (converted.status !== 0 || converted.stdout.trim().length === 0)
            throw new Error(`Could not translate the debug executable path for WSL GDB: ${converted.stderr || converted.error || 'unknown error'}`);
        return converted.stdout.trim();
    }

    private async insertBreakpoint(specification: string): Promise<number> {
        const record = await this.gdb.command(`-break-insert ${specification}`);
        return Number.parseInt(miString(miTuple(record.results.bkpt).number), 10);
    }

    private hasEspBreakpointSlot(): boolean {
        const limit = this.espBreakpointLimit();
        return limit === undefined || this.usedBreakpointCount() < limit;
    }

    private espBreakpointLimit(): number | undefined {
        if (this.target?.target !== 'esp-idf')
            return undefined;
        return this.target.espTarget === 'esp32c3' ? 8 : 2;
    }

    private usedBreakpointCount(): number {
        return [...this.sourceBreakpoints.values()].reduce((count, ids) => count + ids.length, 0)
            + this.functionBreakpoints.length + this.exceptionBreakpoints.length;
    }

    private espBreakpointLimitMessage(): string {
        return `The ${this.target?.espTarget ?? 'ESP'} runtime GDB stub provides ${this.espBreakpointLimit()} hardware breakpoint slots shared by source, function, and exception breakpoints.`;
    }

    private translateExpression(expression: string, fn: DebugFunction | undefined): string {
        const storage = new Map<string, string>();
        for (const variable of [...fn?.parameters ?? [], ...fn?.locals ?? []])
            storage.set(variable.name, variable.storage);
        if (fn?.receiver !== undefined)
            storage.set('this', fn.receiver);
        return expression.replace(/\b[A-Za-z_][A-Za-z0-9_]*\b/g, name => storage.get(name) ?? name);
    }

    private async renderLogMessage(message: string, fn: DebugFunction | undefined): Promise<string> {
        let result = '';
        let position = 0;
        for (const match of message.matchAll(/\{([^{}]+)\}/g)) {
            result += message.slice(position, match.index);
            try { result += await this.evaluateNative(this.translateExpression(match[1], fn)); }
            catch { result += `<cannot evaluate ${match[1]}>`; }
            position = (match.index ?? 0) + match[0].length;
        }
        return result + message.slice(position);
    }

    private async evaluateCString(expression: string): Promise<string> {
        const value = await this.evaluateNative(expression);
        const match = /"((?:\\.|[^"\\])*)"$/.exec(value);
        if (match === null)
            return value;
        try { return JSON.parse(`"${match[1]}"`) as string; }
        catch { return match[1]; }
    }

    private translateWatch(expression: string, fn: DebugFunction | undefined): { expression: string; type: string } | undefined {
        const root = /^([A-Za-z_][A-Za-z0-9_]*|this)/.exec(expression);
        if (root === null)
            return undefined;
        const definition = root[1] === 'this' && fn?.receiver !== undefined && fn.receiverType !== undefined
            ? { name: 'this', storage: fn.receiver, type: fn.receiverType }
            : [...fn?.locals ?? [], ...fn?.parameters ?? []].find(variable => variable.name === root[1]);
        if (definition === undefined)
            return undefined;
        let native = definition.storage;
        let typeName = definition.type;
        let position = root[0].length;
        while (position < expression.length) {
            const fieldMatch = /^\.([A-Za-z_][A-Za-z0-9_]*)/.exec(expression.slice(position));
            if (fieldMatch !== null) {
                const type = this.debugMap?.types.find(candidate => candidate.name === typeName);
                const field = type === undefined ? undefined : this.instanceFields(type).find(candidate => candidate.name === fieldMatch[1]);
                if (type === undefined || field === undefined)
                    return undefined;
                native += `${type.kind === 'class' || type.kind === 'delegate' ? '->' : '.'}${field.storage}`;
                typeName = field.type;
                position += fieldMatch[0].length;
                continue;
            }
            const indexMatch = /^\[(\d+)\]/.exec(expression.slice(position));
            if (indexMatch !== null && typeName.endsWith('[]')) {
                native += `->Data[${indexMatch[1]}]`;
                typeName = typeName.slice(0, -2);
                position += indexMatch[0].length;
                continue;
            }
            return undefined;
        }
        return { expression: native, type: typeName };
    }

    private instanceFields(type: DebugType): DebugTypeField[] {
        const inherited = type.base === undefined ? undefined : this.debugMap?.types.find(candidate => candidate.name === type.base);
        const inheritedFields = inherited === undefined ? [] : this.instanceFields(inherited)
            .map(field => ({ ...field, storage: `ct_base.${field.storage}` }));
        return [...inheritedFields, ...type.fields.filter(field => !field.static)];
    }
}

function miQuote(value: string): string {
    return `"${value.replaceAll('\\', '\\\\').replaceAll('"', '\\"').replaceAll('\n', '\\n')}"`;
}

function normalizePath(value: string): string {
    return value.replaceAll('\\', '/');
}

function isPrimitive(type: string): boolean {
    return ['bool', 'byte', 'sbyte', 'short', 'ushort', 'char', 'int', 'uint', 'long', 'ulong', 'nint', 'nuint', 'float'].includes(type) || type.length === 0;
}

LoggingDebugSession.run(CTildeDebugSession);
