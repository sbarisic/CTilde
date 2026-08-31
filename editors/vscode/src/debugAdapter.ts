import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'fs';
import { tmpdir } from 'os';
import * as path from 'path';
import { ChildProcessWithoutNullStreams, spawn, spawnSync } from 'child_process';
import { Socket } from 'net';
import {
    Breakpoint,
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
import { buildEnabledSiteWords, DebugMemoryLayout, debugMemoryChanges, decodeDebugMemoryField, encodeDebugControlValue, espTrapResumeExpression, findExecutableSite, gdbResumeCommand, isTruthyGdbValue, parseHitCondition, patchDebugBitmap, patchDebugMemory, resolveLogicalFrameSite, runEspDetachSequence, TargetConsoleBuffer } from './debugModel';

const espSerialBridgeSource = [
    'import os,serial,sys,threading,time',
    'last=None',
    'for attempt in range(25):',
    '  try:',
    '    connection=serial.Serial(port=None,baudrate=int(sys.argv[2]),timeout=.05,dsrdtr=False,rtscts=False)',
    '    connection.dtr=False;connection.rts=False;connection.port=sys.argv[1];connection.open()',
    '    connection.dtr=False;connection.rts=False',
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

function delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function canConnect(host: string, port: number, timeout: number): Promise<boolean> {
    return new Promise(resolve => {
        const socket = new Socket();
        let settled = false;
        const finish = (connected: boolean): void => {
            if (settled)
                return;
            settled = true;
            socket.destroy();
            resolve(connected);
        };
        socket.setTimeout(timeout);
        socket.once('connect', () => finish(true));
        socket.once('timeout', () => finish(false));
        socket.once('error', () => finish(false));
        socket.connect(port, host);
    });
}

async function waitForPort(host: string, port: number, child: ChildProcessWithoutNullStreams, timeout: number): Promise<void> {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        if (child.exitCode !== null)
            throw new Error(`ESP-IDF QEMU exited with code ${child.exitCode} before its GDB server became ready.`);
        if (await canConnect(host, port, 200))
            return;
        await delay(100);
    }
    throw new Error(`Timed out waiting for ESP-IDF QEMU GDB server at ${host}:${port}.`);
}

interface DebugSource { readonly file: string; readonly line: number; readonly column: number; }
interface DebugVariable {
    readonly name: string;
    readonly storage: string;
    readonly type: string;
    readonly durable?: boolean;
    readonly scopeId?: number;
    readonly liveStart?: number;
    readonly liveEnd?: number;
}
interface DebugSite { readonly id: number; readonly kind: string; readonly source: DebugSource & { readonly spanStart?: number; readonly spanLength?: number }; }
interface DebugScope { readonly id: number; readonly parent?: number; readonly source: DebugSource & { readonly spanStart?: number; readonly spanLength?: number }; }
interface DebugFunction {
    readonly name: string;
    readonly displayName: string;
    readonly source?: DebugSource | null;
    readonly receiver?: string;
    readonly receiverType?: string;
    readonly parameters: readonly DebugVariable[];
    readonly locals: readonly DebugVariable[];
    readonly executable: readonly DebugSource[];
    readonly sites: readonly DebugSite[];
    readonly scopes?: readonly DebugScope[];
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
    readonly instrumented: boolean;
    readonly memoryDiagnostics: 'off' | 'objects' | 'guarded';
    readonly runtimeHooks: { readonly throw: string; readonly fatal: string; readonly control?: string; readonly trap?: string; readonly ready?: string };
    readonly runtimeControl?: { readonly symbol: string; readonly layouts?: readonly DebugMemoryLayout[] };
    readonly runtimeSummary?: { readonly symbol: string; readonly layouts?: readonly DebugMemoryLayout[] };
}
interface DebugTarget {
    readonly version: number;
    readonly target: 'hosted' | 'esp-idf';
    readonly targetEnvironment?: 'native' | 'qemu';
    readonly debugStub?: 'hosted-native' | 'esp-uart-gdbstub' | 'esp-qemu-native-gdb';
    readonly backend: 'gdb' | 'msvc';
    readonly program: string;
    readonly debugMap: string;
    readonly sourceRoot: string;
    readonly workingDirectory: string;
    readonly arguments?: readonly string[];
    readonly environment?: Readonly<Record<string, string>>;
    readonly gdbCommand?: string;
    readonly gdbPrefixArguments?: readonly string[];
    readonly serialPython?: string;
    readonly espTarget?: string;
    readonly serialPort?: string;
    readonly baudRate?: number;
    readonly instrumented: boolean;
    readonly memoryDiagnostics: 'off' | 'objects' | 'guarded';
    readonly launch?: {
        readonly fileName: string;
        readonly arguments: readonly string[];
        readonly workingDirectory: string;
        readonly environment?: Readonly<Record<string, string>>;
        readonly ownsProcess: boolean;
    };
    readonly gdbHost?: string;
    readonly gdbPort?: number;
}
interface CTildeDebugArguments {
    readonly debugTarget: string;
    readonly request: 'launch' | 'attach';
    readonly args?: readonly string[];
    readonly environment?: Readonly<Record<string, string>>;
    readonly cwd?: string;
    readonly stopAtEntry?: boolean;
    readonly showRuntimeFrames?: boolean;
    readonly processId?: string | number;
    readonly gdbPath?: string;
    readonly serialPort?: string;
    readonly baudRate?: number;
    readonly memoryDiagnostics?: 'off' | 'objects' | 'guarded';
}
interface VariableContainer {
    readonly expression: string;
    readonly type: string;
    readonly frameId: number;
}
interface LogicalBreakpoint {
    readonly siteId: number;
    readonly fn: DebugFunction;
    readonly condition?: string;
    readonly hitCondition?: number;
    readonly logMessage?: string;
    readonly temporary?: boolean;
    hits: number;
}

const debugEventThrow = 1;
const debugEventUnhandled = 2;
const debugEventFatal = 4;
const debugEventAllocation = 8;
const debugEventRelease = 16;
const debugEventLeak = 32;
const debugEventStartup = 64;

export class CTildeDebugSession extends LoggingDebugSession {
    private readonly gdb = new GdbMi();
    private readonly variableHandles = new Handles<VariableContainer>();
    private readonly frameLevels = new Map<number, number>();
    private readonly frameFunctions = new Map<number, DebugFunction | undefined>();
    private readonly frameSites = new Map<number, DebugSite | undefined>();
    private readonly frameThreads = new Map<number, number>();
    private readonly sourceBreakpoints = new Map<string, LogicalBreakpoint[]>();
    private functionBreakpoints: LogicalBreakpoint[] = [];
    private readonly runtimeFunctionBreakpoints = new Set<string>();
    private readonly temporaryBreakpoints = new Map<number, LogicalBreakpoint>();
    private readonly dataBreakpoints = new Map<string, { id: number; thread: string; activation: string }>();
    private exceptionFilters = new Set<string>();
    private target: DebugTarget | undefined;
    private debugMap: DebugMap | undefined;
    private configurationDone: (() => void) | undefined;
    private configurationPromise = new Promise<void>(resolve => this.configurationDone = resolve);
    private request: 'launch' | 'attach' = 'launch';
    private launchArguments: CTildeDebugArguments | undefined;
    private stoppedException: { id: string; description: string; breakMode: 'always' | 'unhandled' } | undefined;
    private espBridgePath: string | undefined;
    private bootstrapBreakpoint: number | undefined;
    private controlReady = false;
    private targetRunning = false;
    private suppressNextTargetStop = false;
    private pendingControlSync = false;
    private resumeAfterControlSync = false;
    private currentSite: DebugSite | undefined;
    private currentThreadState = '0';
    private currentActivation = '0';
    private currentDepth = 0;
    private currentStoppedThreadId = 0;
    private pendingLogicalStep: { mode: 1 | 2 | 3; origin?: DebugSite; originFunction?: string; threadId: number } | undefined;
    private readonly stopWaiters: (() => void)[] = [];
    private disconnecting = false;
    private translatingStop = false;
    private readonly targetConsoleOutput = new TargetConsoleBuffer();
    private readonly controlAddresses = new Map<string, string>();
    private controlLayout: DebugMemoryLayout | undefined;
    private controlBase: string | undefined;
    private controlImage: string | undefined;
    private pointerSize = 4;
    private runtimeSummaryLayout: DebugMemoryLayout | undefined;
    private runtimeSummaryBase: string | undefined;
    private threadCache: Thread[] | undefined;
    private readonly stackCache = new Map<number, StackFrame[]>();
    private selectedThreadId: number | undefined;
    private selectedFrameLevel: number | undefined;
    private readonly frameVariableCache = new Map<number, Map<string, string>>();
    private endingEspSession: Promise<void> | undefined;
    private stoppedAtLogicalTrap = false;
    private espTrapAdvanced = false;
    private qemuProcess: ChildProcessWithoutNullStreams | undefined;
    private qemuTrapBreakpoint: number | undefined;

    public constructor() {
        super('ctilde-debug.txt');
        this.setDebuggerLinesStartAt1(true);
        this.setDebuggerColumnsStartAt1(true);
        this.gdb.onOutput = (category, output) => {
            if (category === 'stdout' || category === 'stderr') {
                this.sendEvent(new OutputEvent(output, category));
                return;
            }
            if (this.targetRunning || this.translatingStop) {
                this.targetConsoleOutput.append(output);
                const safe = this.targetConsoleOutput.drain(this.launchArguments?.showRuntimeFrames === true);
                if (safe.length !== 0)
                    this.sendEvent(new OutputEvent(safe, 'console'));
                return;
            }
            this.sendEvent(new OutputEvent(output, 'console'));
        };
        this.gdb.onAsync = record => {
            void this.handleAsync(record).catch(error => {
                this.finishTargetConsoleOutput(false);
                this.sendEvent(new OutputEvent(`C~ debugger could not process a native stop: ${String(error)}\n`, 'stderr'));
                this.sendEvent(new StoppedEvent('pause', 1, 'Native target stop could not be translated completely.'));
            });
        };
        this.gdb.onExit = () => {
            this.clearTargetConsoleOutput();
            this.removeEspBridge();
            void this.terminateOwnedQemu();
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
            supportsLogPoints: true,
            supportsGotoTargetsRequest: true,
            supportsDataBreakpoints: true,
            supportsReadMemoryRequest: true,
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
            this.controlAddresses.clear();
            this.controlLayout = undefined;
            this.controlBase = undefined;
            this.controlImage = undefined;
            this.runtimeSummaryLayout = undefined;
            this.runtimeSummaryBase = undefined;
            this.invalidateStopCaches();
            const targetPath = path.resolve(args.debugTarget);
            if (!existsSync(targetPath))
                throw new Error(`C~ debug target does not exist: ${targetPath}`);
            this.target = JSON.parse(readFileSync(targetPath, 'utf8')) as DebugTarget;
            this.debugMap = JSON.parse(readFileSync(this.target.debugMap, 'utf8')) as DebugMap;
            if (this.target.version !== 3 || this.debugMap.version !== 3 || !this.target.instrumented || !this.debugMap.instrumented)
                throw new Error('C~ debug metadata v3 with instrumentation is required. Rebuild with --prepare-debug launch using the current compiler and extension.');
            if (this.target.backend !== 'gdb')
                throw new Error('This configuration was built with MSVC. Use C~: Debug Project for the cppvsdbg fallback.');
            if (this.isQemu() && request === 'attach')
                throw new Error('QEMU targets support Debug Launch only in v1. Start a new Debug Launch instead of attaching.');
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
            const launchArguments = args.args ?? this.target.arguments ?? [];
            if (request === 'launch' && launchArguments.length !== 0)
                await this.gdb.command(`-exec-arguments ${launchArguments.map(miQuote).join(' ')}`);
            const launchEnvironment = args.environment ?? this.target.environment ?? {};
            if (request === 'launch')
            for (const [name, value] of Object.entries(launchEnvironment).sort(([left], [right]) => left.localeCompare(right)))
                await this.gdb.command(`-gdb-set environment ${miQuote(`${name}=${value}`)}`);
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
                if (this.isQemu())
                    await this.startOwnedQemu();
                this.suppressNextTargetStop = true;
                await this.gdb.command(`-interpreter-exec console ${miQuote(`target remote ${this.espRemoteTarget()}`)}`);
                this.targetRunning = false;
                this.installStopAtEntry();
                if (this.isQemu()) {
                    const ready = this.debugMap?.runtimeHooks.ready;
                    if (ready === undefined)
                        throw new Error('QEMU debug metadata does not expose its bootstrap probe.');
                    this.bootstrapBreakpoint = await this.insertBreakpoint(`-t ${ready}`);
                    await this.gdb.command('-exec-continue');
                } else {
                    await this.synchronizeDebugControl();
                    if (this.request === 'launch') {
                        await this.setControl('StartupReleased', 1);
                        await this.gdb.command('-exec-continue');
                    } else
                    this.sendEvent(new StoppedEvent('pause', 1, 'Attached to the ESP runtime GDB stub.'));
                }
            } else if (this.request === 'launch') {
                await this.configurationPromise;
                this.installStopAtEntry();
                this.bootstrapBreakpoint = await this.insertBreakpoint('-t main');
                await this.gdb.command('-exec-run');
            } else if (this.launchArguments?.processId !== undefined) {
                await this.gdb.command(`-target-attach ${String(this.launchArguments.processId)}`);
                this.targetRunning = false;
                await this.synchronizeDebugControl();
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
            const installed: LogicalBreakpoint[] = [];
            const result: DebugProtocol.Breakpoint[] = [];
            for (const requested of args.breakpoints ?? []) {
                if (requested.hitCondition !== undefined && parseHitCondition(requested.hitCondition) === undefined) {
                    const breakpoint = new Breakpoint(false, requested.line, requested.column ?? 1,
                        new Source(path.basename(sourcePath), sourcePath)) as DebugProtocol.Breakpoint;
                    breakpoint.message = 'C~ hit counts must be a positive integer.';
                    result.push(breakpoint);
                    continue;
                }
                const match = this.findSourceSite(relative, requested.line, requested.column);
                if (match === undefined) {
                    const breakpoint = new Breakpoint(false, requested.line, requested.column ?? 1,
                        new Source(path.basename(sourcePath), sourcePath)) as DebugProtocol.Breakpoint;
                    breakpoint.message = 'No executable C~ probe exists in the containing method.';
                    result.push(breakpoint);
                    continue;
                }
                const logical: LogicalBreakpoint = {
                    siteId: match.site.id,
                    fn: match.fn,
                    condition: requested.condition,
                    hitCondition: parseHitCondition(requested.hitCondition),
                    logMessage: requested.logMessage,
                    hits: 0,
                };
                installed.push(logical);
                const breakpoint = new Breakpoint(true, match.site.source.line, match.site.source.column,
                    new Source(path.basename(sourcePath), sourcePath)) as DebugProtocol.Breakpoint;
                breakpoint.id = match.site.id + 1;
                if (match.site.source.line !== requested.line)
                    breakpoint.message = `Relocated to executable C~ line ${match.site.source.line}.`;
                result.push(breakpoint);
            }
            this.sourceBreakpoints.set(relative, installed);
            await this.requestControlSync();
            response.body = { breakpoints: result };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1002, String(error));
        }
    }

    protected async setFunctionBreakPointsRequest(response: DebugProtocol.SetFunctionBreakpointsResponse, args: DebugProtocol.SetFunctionBreakpointsArguments): Promise<void> {
        this.functionBreakpoints = [];
        this.runtimeFunctionBreakpoints.clear();
        const breakpoints: DebugProtocol.Breakpoint[] = [];
        for (const requested of args.breakpoints) {
            if (requested.hitCondition !== undefined && parseHitCondition(requested.hitCondition) === undefined) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = 'C~ hit counts must be a positive integer.';
                breakpoints.push(breakpoint);
                continue;
            }
            if (requested.name === '$allocation' || requested.name === '$final-release' || requested.name === '$leak') {
                const breakpoint = new Breakpoint(this.debugMap?.memoryDiagnostics !== 'off') as DebugProtocol.Breakpoint;
                if (!breakpoint.verified)
                    breakpoint.message = 'ARC runtime breakpoints require memory diagnostics in the instrumented image.';
                else
                    breakpoint.id = -(['$allocation', '$final-release', '$leak'].indexOf(requested.name) + 1);
                if (breakpoint.verified)
                    this.runtimeFunctionBreakpoints.add(requested.name);
                breakpoints.push(breakpoint);
                continue;
            }
            const candidates = this.debugMap?.functions.filter(fn => matchesFunctionBreakpoint(fn, requested.name)) ?? [];
            if (candidates.length !== 1) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = candidates.length === 0 ? 'C~ function was not found.' : 'C~ function name is ambiguous.';
                breakpoints.push(breakpoint);
                continue;
            }
            const site = candidates[0].sites.find(candidate => candidate.kind === 'entry');
            if (site === undefined) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = 'The function has no reachable instrumented entry site.';
                breakpoints.push(breakpoint);
                continue;
            }
            this.functionBreakpoints.push({ siteId: site.id, fn: candidates[0], condition: requested.condition,
                hitCondition: parseHitCondition(requested.hitCondition), hits: 0 });
            const breakpoint = new Breakpoint(true) as DebugProtocol.Breakpoint;
            breakpoint.id = site.id + 1;
            breakpoints.push(breakpoint);
        }
        await this.requestControlSync();
        response.body = { breakpoints };
        this.sendResponse(response);
    }

    protected async setExceptionBreakPointsRequest(response: DebugProtocol.SetExceptionBreakpointsResponse, args: DebugProtocol.SetExceptionBreakpointsArguments): Promise<void> {
        this.exceptionFilters = new Set(args.filters);
        await this.requestControlSync();
        this.sendResponse(response);
    }

    protected async threadsRequest(response: DebugProtocol.ThreadsResponse): Promise<void> {
        if (this.threadCache === undefined) {
            const record = await this.gdb.command('-thread-info');
            this.threadCache = miArray(record.results.threads).map(value => miTuple(value)).map(thread =>
                new Thread(Number.parseInt(miString(thread.id), 10), miString(thread.name) || `Thread ${miString(thread.id)}`));
        }
        response.body = { threads: this.threadCache };
        this.sendResponse(response);
    }

    protected async stackTraceRequest(response: DebugProtocol.StackTraceResponse, args: DebugProtocol.StackTraceArguments): Promise<void> {
        const cached = this.stackCache.get(args.threadId);
        if (cached !== undefined) {
            response.body = { stackFrames: cached, totalFrames: cached.length };
            this.sendResponse(response);
            return;
        }
        const record = await this.gdb.command(`-stack-list-frames --thread ${args.threadId}`);
        const frames: StackFrame[] = [];
        const currentMatch = this.currentSite === undefined ? undefined : this.allSites().find(candidate => candidate.site.id === this.currentSite?.id);
        let mappedCurrentSite = false;
        for (const value of miArray(record.results.stack)) {
            const wrapper = miTuple(value);
            const frame = miTuple(wrapper.frame ?? value);
            const rawName = miString(frame.func);
            const mapped = this.debugMap?.functions.find(fn => fn.name === rawName);
            const file = miString(frame.fullname) || miString(frame.file);
            const generated = mapped === undefined || file === '<ctilde-generated>';
            if (generated && !this.launchArguments?.showRuntimeFrames)
                continue;
            const level = Number.parseInt(miString(frame.level), 10);
            const id = args.threadId * 10000 + level + 1;
            this.frameLevels.set(id, level);
            this.frameFunctions.set(id, mapped);
            this.frameThreads.set(id, args.threadId);
            const nativeLine = Number.parseInt(miString(frame.line) || String(mapped?.source?.line ?? 1), 10);
            const exactCurrentSite = !mappedCurrentSite && args.threadId === this.currentStoppedThreadId &&
                currentMatch?.fn.name === rawName ? currentMatch.site : undefined;
            if (exactCurrentSite !== undefined)
                mappedCurrentSite = true;
            const frameSite = resolveLogicalFrameSite(mapped?.sites ?? [], nativeLine, exactCurrentSite);
            this.frameSites.set(id, frameSite);
            const sourcePath = exactCurrentSite !== undefined ? this.absoluteSource(exactCurrentSite.source.file)
                : mapped?.source == null ? file : this.absoluteSource(mapped.source.file);
            frames.push(new StackFrame(id, mapped?.displayName ?? rawName,
                sourcePath.length === 0 ? undefined : new Source(path.basename(sourcePath), sourcePath),
                exactCurrentSite?.source.line ?? nativeLine, exactCurrentSite?.source.column ?? 1));
        }
        this.stackCache.set(args.threadId, frames);
        response.body = { stackFrames: frames, totalFrames: frames.length };
        this.sendResponse(response);
    }

    protected scopesRequest(response: DebugProtocol.ScopesResponse, args: DebugProtocol.ScopesArguments): void {
        const scopes = [
            new Scope('Locals', this.variableHandles.create({ expression: '$locals', type: '$scope', frameId: args.frameId }), false),
            new Scope('Arguments', this.variableHandles.create({ expression: '$arguments', type: '$scope', frameId: args.frameId }), false),
            new Scope('Statics', this.variableHandles.create({ expression: '$statics', type: '$statics', frameId: args.frameId }), true),
        ];
        if (this.debugMap?.memoryDiagnostics !== 'off')
            scopes.push(new Scope('C~ Runtime', this.variableHandles.create({ expression: '$runtime', type: '$runtime', frameId: args.frameId }), true));
        response.body = {
            scopes,
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
            await this.selectFrame(this.frameThreads.get(container.frameId), level);
            if (container.type === '$scope') {
                const fn = this.frameFunctions.get(container.frameId);
                const definitions = container.expression === '$arguments' ? fn?.parameters ?? [] : this.liveLocals(fn, container.frameId);
                const knownValues = await this.frameVariables(container.frameId);
                const variables: DebugProtocol.Variable[] = [];
                for (const definition of definitions) {
                    try {
                        variables.push(await this.makeVariable(definition.name, definition.storage, definition.type, container.frameId,
                            knownValues.get(definition.storage)));
                    } catch {
                        // GDB rejects locals that are outside their lexical scope or optimized away.
                    }
                }
                if (container.expression === '$arguments' && fn?.receiver !== undefined && fn.receiverType !== undefined) {
                    try { variables.push(await this.makeVariable('this', fn.receiver, fn.receiverType, container.frameId, knownValues.get(fn.receiver))); } catch { }
                }
                response.body = { variables };
            } else if (container.type === '$runtime')
                response.body = { variables: await this.runtimeVariables(container.frameId) };
            else if (container.type === '$statics')
                response.body = { variables: await this.staticVariables(container.frameId) };
            else if (container.type === '$liveobjects')
                response.body = { variables: await this.liveObjectVariables(container.frameId, args.start ?? 0, args.count ?? 100) };
            else if (container.type === '$objectruntime')
                response.body = { variables: await this.objectRuntimeVariables(container.expression, container.frameId) };
            else
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
            const watch = this.translateWatch(args.expression.trim(), fn, frameId);
            if (watch === undefined)
                throw new Error('C~ watches support identifiers, field access, and array indices.');
            const variable = await this.makeVariable(args.expression, watch.expression, watch.type, frameId);
            response.body = { result: variable.value, type: variable.type, variablesReference: variable.variablesReference };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1004, String(error));
        }
    }

    protected async continueRequest(response: DebugProtocol.ContinueResponse, args: DebugProtocol.ContinueArguments): Promise<void> {
        try {
            this.pendingLogicalStep = undefined;
            await this.setControl('StepMode', 0);
            await this.resume(args.threadId);
            response.body = { allThreadsContinued: true };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1008, `Could not continue the C~ target: ${String(error)}`);
        }
    }

    protected async nextRequest(response: DebugProtocol.NextResponse, args: DebugProtocol.NextArguments): Promise<void> {
        await this.logicalStepRequest(response, args.threadId, 2);
    }

    protected async stepInRequest(response: DebugProtocol.StepInResponse, args: DebugProtocol.StepInArguments): Promise<void> {
        await this.logicalStepRequest(response, args.threadId, 1);
    }

    protected async stepOutRequest(response: DebugProtocol.StepOutResponse, args: DebugProtocol.StepOutArguments): Promise<void> {
        await this.logicalStepRequest(response, args.threadId, 3);
    }

    protected pauseRequest(response: DebugProtocol.PauseResponse, args: DebugProtocol.PauseArguments): void {
        void this.gdb.command(`-exec-interrupt --thread ${args.threadId}`);
        this.sendResponse(response);
    }

    protected gotoTargetsRequest(response: DebugProtocol.GotoTargetsResponse, args: DebugProtocol.GotoTargetsArguments): void {
        const relative = args.source.path === undefined ? '' : this.relativeSource(path.resolve(args.source.path));
        const match = this.findSourceSite(relative, args.line, args.column);
        response.body = { targets: match === undefined ? [] : [{ id: match.site.id + 1, label: `${match.fn.displayName}:${match.site.source.line}`, line: match.site.source.line, column: match.site.source.column }] };
        this.sendResponse(response);
    }

    protected async gotoRequest(response: DebugProtocol.GotoResponse, args: DebugProtocol.GotoArguments): Promise<void> {
        const siteId = args.targetId - 1;
        const match = this.allSites().find(candidate => candidate.site.id === siteId);
        if (match === undefined) {
            this.sendErrorResponse(response, 1006, 'The selected C~ run-to-cursor site is no longer available.');
            return;
        }
        this.temporaryBreakpoints.clear();
        this.temporaryBreakpoints.set(siteId, { siteId, fn: match.fn, temporary: true, hits: 0 });
        await this.synchronizeDebugControl();
        await this.resume(args.threadId);
        this.sendResponse(response);
    }

    protected dataBreakpointInfoRequest(response: DebugProtocol.DataBreakpointInfoResponse, args: DebugProtocol.DataBreakpointInfoArguments): void {
        const frameId = args.frameId ?? 1;
        const fn = this.frameFunctions.get(frameId);
        const translated = this.dataBreakpointExpression(args.variablesReference, args.name, frameId, fn);
        if (translated === undefined) {
            response.body = { dataId: null, description: args.name, accessTypes: [], canPersist: false };
        } else {
            response.body = {
                dataId: JSON.stringify({ expression: translated.expression, type: translated.type, frameId }),
                description: args.name,
                accessTypes: ['write', 'readWrite'],
                canPersist: false,
            };
        }
        this.sendResponse(response);
    }

    protected async setDataBreakpointsRequest(response: DebugProtocol.SetDataBreakpointsResponse, args: DebugProtocol.SetDataBreakpointsArguments): Promise<void> {
        for (const watchpoint of this.dataBreakpoints.values()) {
            try { await this.gdb.command(`-break-delete ${watchpoint.id}`); } catch { }
        }
        this.dataBreakpoints.clear();
        const breakpoints: DebugProtocol.Breakpoint[] = [];
        for (const requested of args.breakpoints) {
            try {
                const data = JSON.parse(requested.dataId) as { expression: string; type: string; frameId: number };
                if (!isAddressableWatchType(data.type))
                    throw new Error(`C~ cannot watch values of type '${data.type}' with a native data breakpoint.`);
                const level = this.frameLevels.get(data.frameId);
                if (level === undefined)
                    throw new Error('The local variable stack activation is no longer active.');
                await this.gdb.command(`-stack-select-frame ${level}`);
                let watchExpression = data.expression;
                if (this.target?.target === 'esp-idf') {
                    const size = nativeWatchSize(data.type);
                    if (size === undefined || size > 4)
                        throw new Error(`ESP runtime-stub watchpoints require an addressable 1, 2, or 4-byte value; '${data.type}' is not supported.`);
                    const address = BigInt(await this.addressOf(data.expression));
                    if (address % BigInt(size) !== 0n)
                        throw new Error(`ESP watchpoint address 0x${address.toString(16)} is not aligned to ${size} bytes.`);
                    watchExpression = `*(uint${size * 8}_t*)(uintptr_t)0x${address.toString(16)}`;
                }
                const option = requested.accessType === 'readWrite' ? '-a' : '';
                const record = await this.gdb.command(`-break-watch ${option} ${miQuote(watchExpression)}`);
                const id = Number.parseInt(miString(miTuple(record.results.wpt ?? record.results.hw_awpt ?? record.results.hw_rwpt).number), 10);
                this.dataBreakpoints.set(requested.dataId, { id, thread: this.currentThreadState, activation: this.currentActivation });
                const breakpoint = new Breakpoint(true) as DebugProtocol.Breakpoint;
                breakpoint.id = id;
                breakpoints.push(breakpoint);
            } catch (error) {
                const breakpoint = new Breakpoint(false) as DebugProtocol.Breakpoint;
                breakpoint.message = `Could not install hardware watchpoint: ${String(error instanceof Error ? error.message : error)}`;
                breakpoints.push(breakpoint);
            }
        }
        response.body = { breakpoints };
        this.sendResponse(response);
    }

    protected async readMemoryRequest(response: DebugProtocol.ReadMemoryResponse, args: DebugProtocol.ReadMemoryArguments): Promise<void> {
        try {
            const count = Math.min(args.count, 4096);
            const address = offsetAddress(args.memoryReference, args.offset ?? 0);
            const record = await this.gdb.command(`-data-read-memory-bytes ${address} ${count}`);
            const memory = miArray(record.results.memory).map(miTuple)[0];
            response.body = { address, data: Buffer.from(miString(memory?.contents), 'hex').toString('base64') };
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1007, String(error));
        }
    }

    protected async disconnectRequest(response: DebugProtocol.DisconnectResponse): Promise<void> {
        if (this.isQemu()) {
            await this.endQemuSession();
            this.sendResponse(response);
            return;
        }
        if (this.target?.target === 'esp-idf') {
            await this.endEspSessionAndContinue();
            this.sendResponse(response);
            return;
        }
        try {
            this.disconnecting = true;
            if (this.targetRunning)
                await this.interruptAndWait();
            this.clearTargetConsoleOutput();
            await this.clearDebugControl();
            await this.gdb.command('-target-detach');
        } catch (error) {
            this.sendEvent(new OutputEvent(`C~ debugger disconnect cleanup failed: ${errorMessage(error)}\n`, 'stderr'));
        }
        await this.gdb.close();
        this.removeEspBridge();
        this.sendResponse(response);
    }

    protected async terminateRequest(response: DebugProtocol.TerminateResponse): Promise<void> {
        if (this.isQemu()) {
            await this.endQemuSession();
            this.sendResponse(response);
            return;
        }
        if (this.target?.target === 'esp-idf') {
            await this.endEspSessionAndContinue();
            this.sendResponse(response);
            return;
        }
        try {
            this.clearTargetConsoleOutput();
            await this.gdb.command('-exec-abort');
        } catch (error) {
            this.sendEvent(new OutputEvent(`C~ debugger termination failed: ${errorMessage(error)}\n`, 'stderr'));
        }
        await this.gdb.close();
        this.removeEspBridge();
        this.sendResponse(response);
    }

    private async endEspSessionAndContinue(): Promise<void> {
        if (this.endingEspSession !== undefined)
            return this.endingEspSession;
        this.endingEspSession = this.performEspDetachAndContinue();
        return this.endingEspSession;
    }

    private async performEspDetachAndContinue(): Promise<void> {
        this.disconnecting = true;
        let safeToContinue = true;
        try {
            await runEspDetachSequence({
                running: this.targetRunning,
                trapAlreadyAdvanced: this.espTrapAdvanced,
                interrupt: () => this.interruptAndWait(),
                readLogicalReason: async () => {
                    const fast = await this.refreshFastControl().catch(() => false);
                    const reason = fast
                        ? Number(this.fastControlValue('CurrentReason'))
                        : Number.parseInt(await this.evaluateNative('ct_debug_control.CurrentReason'), 10);
                    this.stoppedAtLogicalTrap = reason !== 0;
                    return reason;
                },
                advanceLogicalTrap: async () => {
                    await this.evaluateNative(espTrapResumeExpression(this.target?.espTarget));
                    this.espTrapAdvanced = true;
                },
                removeNativeBreakpoints: async () => {
                    for (const watchpoint of this.dataBreakpoints.values())
                        await this.gdb.command(`-break-delete ${watchpoint.id}`);
                    this.dataBreakpoints.clear();
                    if (this.bootstrapBreakpoint !== undefined) {
                        try { await this.gdb.command(`-break-delete ${this.bootstrapBreakpoint}`); } catch { }
                        this.bootstrapBreakpoint = undefined;
                    }
                },
                clearLogicalControl: () => this.clearDebugControl(),
                continueWithoutDebugger: async () => {
                    await withTimeout(this.gdb.command(`-interpreter-exec console ${miQuote('set confirm off')}`), 2000,
                        'Timed out while configuring ESP GDB detach.');
                    await withTimeout(this.gdb.command(`-interpreter-exec console ${miQuote('kill')}`), 3000,
                        'Timed out while asking the ESP GDB stub to continue.');
                },
            });
            this.sourceBreakpoints.clear();
            this.functionBreakpoints = [];
            this.runtimeFunctionBreakpoints.clear();
            this.temporaryBreakpoints.clear();
            this.exceptionFilters.clear();
            this.pendingLogicalStep = undefined;
            this.clearTargetConsoleOutput();
        } catch (error) {
            safeToContinue = false;
            this.sendEvent(new OutputEvent(
                `C~ could not safely detach and continue the ESP target: ${errorMessage(error)} Reset the board before using it.\n`, 'stderr'));
        } finally {
            this.invalidateStopCaches();
            this.controlReady = false;
            this.controlImage = undefined;
            try { await this.gdb.close(); } catch { }
            this.removeEspBridge();
        }
        if (safeToContinue)
            this.sendEvent(new OutputEvent('C~ debugger detached; the ESP firmware is continuing with debugger breakpoints disabled.\n', 'console'));
    }

    protected async restartRequest(response: DebugProtocol.RestartResponse): Promise<void> {
        try {
            if (this.isQemu()) {
                await this.restartQemu();
                this.sendResponse(response);
                return;
            }
            try { await this.gdb.command('-exec-abort'); } catch { }
            this.clearTargetConsoleOutput();
            this.controlReady = false;
            this.currentSite = undefined;
            this.pendingLogicalStep = undefined;
            this.installStopAtEntry();
            this.bootstrapBreakpoint = await this.insertBreakpoint('-t main');
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

    private async makeVariable(name: string, expression: string, type: string, frameId: number, knownValue?: string): Promise<DebugProtocol.Variable> {
        if (type === 'string') {
            const isNull = knownValue === undefined
                ? await this.evaluateNative(`${expression} == 0`) !== '0'
                : !isTruthyGdbValue(knownValue);
            if (isNull)
                return { name, value: 'null', type, variablesReference: 0, evaluateName: expression, memoryReference: '0x0' };
            const length = Number.parseInt(await this.evaluateNative(`${expression}->Length`), 10);
            const address = await this.evaluateNative(`(void*)${expression}->Data`);
            const contents = length <= 0 ? '' : await this.readUtf8(address, Math.min(length, 4096));
            const suffix = length > 4096 ? '…' : '';
            return { name, value: JSON.stringify(contents + suffix), type, variablesReference: this.variableHandles.create({ expression, type, frameId }), evaluateName: expression, memoryReference: address };
        }
        const mappedType = this.debugMap?.types.find(candidate => candidate.name === type);
        const reference = type.endsWith('[]') || mappedType?.kind === 'class' || mappedType?.kind === 'delegate';
        const referenceIsNull = reference && (knownValue === undefined
            ? await this.evaluateNative(`${expression} == 0`) !== '0'
            : !isTruthyGdbValue(knownValue));
        if (referenceIsNull)
            return { name, value: 'null', type, variablesReference: 0, evaluateName: expression, memoryReference: '0x0' };
        const value = knownValue ?? await this.evaluateNative(expression);
        if (mappedType?.kind === 'enum') {
            const enumValue = mappedType.values?.find(candidate => candidate.value === value);
            return { name, value: enumValue === undefined ? value : `${enumValue.name} (${value})`, type, variablesReference: 0, evaluateName: expression };
        }
        let displayType = type;
        let expansionType = type;
        if (mappedType?.kind === 'class') {
            const actual = await this.evaluateCString(`((ct_object*)(void*)(${expression}))->Type->Name`);
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
        const memoryReference = reference ? value : await this.addressOf(expression).catch(() => undefined);
        return { name, value, type: displayType || undefined, variablesReference: expandable ? this.variableHandles.create({ expression, type: expansionType, frameId }) : 0, evaluateName: expression, memoryReference };
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
                result.push({ name: '$runtime', value: 'ARC metadata', variablesReference: this.variableHandles.create({ expression: container.expression, type: '$objectruntime', frameId: container.frameId }) });
            return result;
        }
        return [{ name: '$native', value: await this.evaluateNative(container.expression), variablesReference: 0 }];
    }

    private async evaluateNative(expression: string): Promise<string> {
        const record = await this.gdb.command(`-data-evaluate-expression ${miQuote(expression)}`);
        return miString(record.results.value);
    }

    private async selectFrame(threadId: number | undefined, level: number): Promise<void> {
        if (threadId !== undefined && this.selectedThreadId !== threadId) {
            await this.gdb.command(`-thread-select ${threadId}`);
            this.selectedThreadId = threadId;
            this.selectedFrameLevel = undefined;
        }
        if (this.selectedFrameLevel === level)
            return;
        await this.gdb.command(`-stack-select-frame ${level}`);
        this.selectedFrameLevel = level;
    }

    private async frameVariables(frameId: number): Promise<Map<string, string>> {
        const cached = this.frameVariableCache.get(frameId);
        if (cached !== undefined)
            return cached;
        const record = await this.gdb.command('-stack-list-variables --all-values');
        const result = new Map<string, string>();
        for (const value of miArray(record.results.variables)) {
            const variable = miTuple(value);
            const name = miString(variable.name);
            if (name.length !== 0)
                result.set(name, miString(variable.value));
        }
        this.frameVariableCache.set(frameId, result);
        return result;
    }

    private invalidateStopCaches(): void {
        this.threadCache = undefined;
        this.stackCache.clear();
        this.frameLevels.clear();
        this.frameFunctions.clear();
        this.frameSites.clear();
        this.frameThreads.clear();
        this.frameVariableCache.clear();
        this.variableHandles.reset();
        this.selectedThreadId = undefined;
        this.selectedFrameLevel = undefined;
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
            this.targetRunning = true;
            this.stoppedAtLogicalTrap = false;
            this.espTrapAdvanced = false;
            this.invalidateStopCaches();
            this.sendEvent(new ContinuedEvent(0, true));
            return;
        }
        if (record.name !== 'stopped')
            return;
        this.targetRunning = false;
        this.translatingStop = true;
        for (const resolve of this.stopWaiters.splice(0))
            resolve();
        if (this.disconnecting) {
            this.clearTargetConsoleOutput();
            return;
        }
        if (this.suppressNextTargetStop) {
            this.suppressNextTargetStop = false;
            this.finishTargetConsoleOutput(false);
            return;
        }
        this.invalidateStopCaches();
        this.currentStoppedThreadId = 0;
        this.stoppedAtLogicalTrap = false;
        this.espTrapAdvanced = false;
        const reason = miString(record.results.reason);
        const threadId = Number.parseInt(miString(record.results['thread-id']) || '1', 10);
        const breakpointNumber = Number.parseInt(miString(record.results.bkptno), 10);
        if (this.bootstrapBreakpoint !== undefined && breakpointNumber === this.bootstrapBreakpoint) {
            this.bootstrapBreakpoint = undefined;
            if (this.isQemu()) {
                const trap = this.debugMap?.runtimeHooks.trap;
                if (trap === undefined)
                    throw new Error('QEMU debug metadata does not expose its logical trap probe.');
                this.qemuTrapBreakpoint = await this.insertBreakpoint(trap);
            }
            await this.synchronizeDebugControl();
            this.finishTargetConsoleOutput(false);
            await this.resume(threadId);
            return;
        }
        if (this.pendingControlSync) {
            const resume = this.resumeAfterControlSync;
            this.pendingControlSync = false;
            this.resumeAfterControlSync = false;
            if (this.isPhysicalEsp()) {
                const fast = await this.refreshFastControl().catch(() => false);
                const pendingReason = fast
                    ? Number(this.fastControlValue('CurrentReason'))
                    : Number.parseInt(await this.evaluateNative('ct_debug_control.CurrentReason'), 10);
                if (pendingReason !== 0) {
                    this.stoppedAtLogicalTrap = true;
                    await this.evaluateNative(espTrapResumeExpression(this.target?.espTarget));
                    this.espTrapAdvanced = true;
                }
            }
            await this.synchronizeDebugControl();
            this.finishTargetConsoleOutput(false);
            if (resume)
                await this.resume(threadId);
            return;
        }
        if (reason === 'watchpoint-trigger' || reason === 'read-watchpoint-trigger' || reason === 'access-watchpoint-trigger') {
            this.currentSite = undefined;
            this.finishTargetConsoleOutput(false);
            this.sendEvent(new StoppedEvent('data breakpoint', threadId, 'C~ data breakpoint'));
            return;
        }
        let debugReason = 0;
        let usedFastControl = false;
        try {
            usedFastControl = await this.refreshFastControl();
            debugReason = usedFastControl
                ? Number(this.fastControlValue('CurrentReason'))
                : Number.parseInt(await this.evaluateNative('ct_debug_control.CurrentReason'), 10);
        } catch { }
        this.stoppedAtLogicalTrap = debugReason !== 0;
        if (this.stoppedAtLogicalTrap && this.isPhysicalEsp()) {
            await this.evaluateNative(espTrapResumeExpression(this.target?.espTarget));
            this.espTrapAdvanced = true;
        }
        if (debugReason === 1) {
            const siteId = usedFastControl ? Number(this.fastControlValue('CurrentSite')) : Number.parseInt(await this.evaluateNative('ct_debug_control.CurrentSite'), 10);
            this.currentThreadState = usedFastControl ? formatAddress(this.fastControlValue('CurrentThread')) : normalizeAddress(await this.evaluateNative('(void*)ct_debug_control.CurrentThread'));
            this.currentActivation = usedFastControl ? this.fastControlValue('CurrentActivation').toString() : await this.evaluateNative('ct_debug_control.CurrentActivation');
            this.currentDepth = usedFastControl ? Number(this.fastControlValue('CurrentValue')) : Number.parseInt(await this.evaluateNative('ct_debug_control.CurrentValue'), 10);
            await this.removeExpiredLocalWatchpoints();
            const match = this.allSites().find(candidate => candidate.site.id === siteId);
            this.currentSite = match?.site;
            this.currentStoppedThreadId = threadId;
            if (match !== undefined)
                await this.selectFunctionFrame(match.fn, threadId);
            const candidates = this.logicalBreakpoints().filter(candidate => candidate.siteId === siteId);
            let shouldStop = candidates.length === 0;
            for (const candidate of candidates) {
                candidate.hits++;
                if (candidate.hitCondition !== undefined && candidate.hits < candidate.hitCondition)
                    continue;
                if (candidate.condition !== undefined) {
                    try {
                        if (!isTruthyGdbValue(await this.evaluateNative(this.translateExpression(candidate.condition, candidate.fn))))
                            continue;
                    } catch (error) {
                        this.sendEvent(new OutputEvent(`C~ breakpoint condition failed: ${String(error)}\n`, 'stderr'));
                        continue;
                    }
                }
                if (candidate.logMessage !== undefined) {
                    this.sendEvent(new OutputEvent(await this.renderLogMessage(candidate.logMessage, candidate.fn) + '\n', 'console'));
                    continue;
                }
                shouldStop = true;
                if (candidate.temporary)
                    this.temporaryBreakpoints.delete(candidate.siteId);
            }
            if (candidates.length === 0 && this.pendingLogicalStep !== undefined && match !== undefined &&
                this.pendingLogicalStep.origin !== undefined && this.pendingLogicalStep.originFunction === match.fn.name &&
                sameSourceLine(this.pendingLogicalStep.origin.source, match.site.source)) {
                await this.setControl('SelectedThread', `(uintptr_t)${this.currentThreadState}`);
                await this.setControl('StepDepth', this.currentDepth);
                await this.setControl('StepMode', this.pendingLogicalStep.mode);
                this.finishTargetConsoleOutput(true);
                await this.resume(threadId);
                return;
            }
            if (shouldStop) {
                this.pendingLogicalStep = undefined;
                const source = match?.site.source;
                const location = source === undefined ? 'an instrumented site' : `${source.file}:${source.line}`;
                const stopKind = candidates.length === 0 ? 'step' : 'breakpoint';
                this.finishTargetConsoleOutput(true);
                this.sendEvent(new StoppedEvent(stopKind, threadId, `C~ ${stopKind} at ${location}`));
                return;
            }
            await this.synchronizeDebugControl();
            this.finishTargetConsoleOutput(true);
            await this.resume(threadId);
            return;
        }
        if (debugReason >= 2 && debugReason <= 6) {
            const object = usedFastControl ? formatAddress(this.fastControlValue('CurrentObject')) : await this.evaluateNative('(void*)ct_debug_control.CurrentObject');
            const value = usedFastControl ? this.fastControlValue('CurrentValue').toString() : await this.evaluateNative('ct_debug_control.CurrentValue');
            if (debugReason === 2 || debugReason === 3) {
                const code = usedFastControl ? await this.evaluateCStringAddress(this.fastControlValue('CurrentCode')) : await this.evaluateCStringPointer('ct_debug_control.CurrentCode');
                const file = usedFastControl ? await this.evaluateCStringAddress(this.fastControlValue('CurrentFile')) : await this.evaluateCStringPointer('ct_debug_control.CurrentFile');
                const line = usedFastControl ? this.fastControlValue('CurrentLine').toString() : await this.evaluateNative('ct_debug_control.CurrentLine');
                const unhandled = value !== '0';
                this.stoppedException = debugReason === 2
                    ? { id: code || 'C~ exception', description: `${code || 'C~ exception'} at ${file}:${line}`, breakMode: unhandled ? 'unhandled' : 'always' }
                    : { id: code || 'C~ fatal runtime failure', description: `Fatal C~ runtime failure ${code}`, breakMode: 'unhandled' };
                this.finishTargetConsoleOutput(true);
                this.sendEvent(new StoppedEvent('exception', threadId, this.stoppedException.description));
            } else {
                const labels = ['', '', '', '', '$allocation', '$final-release', '$leak'];
                this.finishTargetConsoleOutput(true);
                this.sendEvent(new StoppedEvent('function breakpoint', threadId, `C~ ${labels[debugReason]} object=${object} value=${value}`));
            }
            return;
        }
        if (debugReason === 7) {
            this.currentSite = undefined;
            this.finishTargetConsoleOutput(true);
            this.sendEvent(new StoppedEvent('entry', threadId, 'Stopped before C~ runtime and module initialization.'));
            return;
        }
        this.currentSite = undefined;
        this.finishTargetConsoleOutput(false);
        this.sendEvent(new StoppedEvent(reason === 'breakpoint-hit' ? 'breakpoint' : reason === 'signal-received' ? 'pause' : 'step', threadId));
    }

    private finishTargetConsoleOutput(internalProbe: boolean): void {
        const output = this.targetConsoleOutput.finish(internalProbe, this.launchArguments?.showRuntimeFrames === true);
        this.translatingStop = false;
        if (output.length !== 0)
            this.sendEvent(new OutputEvent(output, 'console'));
    }

    private clearTargetConsoleOutput(): void {
        this.targetConsoleOutput.clear();
        this.translatingStop = false;
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

    private isQemu(): boolean {
        return this.target?.target === 'esp-idf' && this.target.targetEnvironment === 'qemu';
    }

    private isPhysicalEsp(): boolean {
        return this.target?.target === 'esp-idf' && !this.isQemu();
    }

    private async startOwnedQemu(): Promise<void> {
        const launch = this.target?.launch;
        if (launch === undefined || !launch.ownsProcess)
            throw new Error('QEMU debug metadata does not contain an owned emulator launch command.');
        const host = this.target?.gdbHost ?? '127.0.0.1';
        const port = this.target?.gdbPort ?? 3333;
        if (await canConnect(host, port, 250))
            throw new Error(`QEMU GDB port ${host}:${port} is already in use. Stop the existing emulator or debugger and retry.`);
        const child = spawn(launch.fileName, [...launch.arguments], {
            cwd: launch.workingDirectory,
            env: { ...process.env, ...(launch.environment ?? {}) },
            windowsHide: true,
            detached: process.platform !== 'win32',
            stdio: 'pipe',
        });
        this.qemuProcess = child;
        child.stdout.on('data', data => this.sendEvent(new OutputEvent(data.toString(), 'console')));
        child.stderr.on('data', data => this.sendEvent(new OutputEvent(data.toString(), 'stderr')));
        child.once('error', error => this.sendEvent(new OutputEvent(`Could not start ESP-IDF QEMU: ${error.message}\n`, 'stderr')));
        child.once('exit', (code, signal) => {
            if (this.qemuProcess !== child)
                return;
            this.qemuProcess = undefined;
            if (!this.disconnecting) {
                this.sendEvent(new OutputEvent(`ESP-IDF QEMU exited (${signal ?? code ?? 'unknown'}).\n`, code === 0 ? 'console' : 'stderr'));
                this.sendEvent(new TerminatedEvent());
            }
        });
        await waitForPort(host, port, child, 20000);
    }

    private async terminateOwnedQemu(): Promise<void> {
        const child = this.qemuProcess;
        this.qemuProcess = undefined;
        if (child === undefined || child.pid === undefined || child.exitCode !== null)
            return;
        if (process.platform === 'win32') {
            spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, timeout: 5000 });
            return;
        }
        try { process.kill(-child.pid, 'SIGTERM'); } catch { try { child.kill('SIGTERM'); } catch { } }
        await delay(250);
        if (child.exitCode === null)
            try { process.kill(-child.pid, 'SIGKILL'); } catch { try { child.kill('SIGKILL'); } catch { } }
    }

    private async endQemuSession(): Promise<void> {
        this.disconnecting = true;
        try {
            if (this.targetRunning)
                await this.interruptAndWait();
            await this.removeQemuBreakpoints();
            await this.clearDebugControl();
            try { await this.gdb.command('-target-disconnect'); } catch { }
        } catch (error) {
            this.sendEvent(new OutputEvent(`C~ QEMU debugger cleanup failed: ${errorMessage(error)}\n`, 'stderr'));
        } finally {
            try { await this.gdb.close(); } catch { }
            await this.terminateOwnedQemu();
            this.controlReady = false;
            this.clearTargetConsoleOutput();
        }
    }

    private async restartQemu(): Promise<void> {
        if (this.targetRunning)
            await this.interruptAndWait();
        await this.removeQemuBreakpoints();
        await this.clearDebugControl();
        try { await this.gdb.command('-target-disconnect'); } catch { }
        await this.terminateOwnedQemu();
        this.controlReady = false;
        this.controlImage = undefined;
        this.currentSite = undefined;
        this.pendingLogicalStep = undefined;
        this.disconnecting = false;
        await this.startOwnedQemu();
        this.suppressNextTargetStop = true;
        await this.gdb.command(`-interpreter-exec console ${miQuote(`target remote ${this.espRemoteTarget()}`)}`);
        const ready = this.debugMap?.runtimeHooks.ready;
        if (ready === undefined)
            throw new Error('QEMU debug metadata does not expose its bootstrap probe.');
        this.bootstrapBreakpoint = await this.insertBreakpoint(`-t ${ready}`);
        await this.gdb.command('-exec-continue');
    }

    private async removeQemuBreakpoints(): Promise<void> {
        for (const breakpoint of [this.bootstrapBreakpoint, this.qemuTrapBreakpoint]) {
            if (breakpoint !== undefined)
                try { await this.gdb.command(`-break-delete ${breakpoint}`); } catch { }
        }
        this.bootstrapBreakpoint = undefined;
        this.qemuTrapBreakpoint = undefined;
    }

    private espRemoteTarget(): string {
        if (this.isQemu())
            return `${this.target?.gdbHost ?? '127.0.0.1'}:${this.target?.gdbPort ?? 3333}`;
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

    private allSites(): { fn: DebugFunction; site: DebugSite }[] {
        return this.debugMap?.functions.flatMap(fn => fn.sites.map(site => ({ fn, site }))) ?? [];
    }

    private logicalBreakpoints(): LogicalBreakpoint[] {
        return [...this.sourceBreakpoints.values()].flat().concat(this.functionBreakpoints, [...this.temporaryBreakpoints.values()]);
    }

    private findSourceSite(file: string, line: number, column = 1): { fn: DebugFunction; site: DebugSite } | undefined {
        return findExecutableSite(this.debugMap?.functions ?? [], file, line, column);
    }

    private installStopAtEntry(): void {
        if (!this.launchArguments?.stopAtEntry || this.target?.target === 'esp-idf')
            return;
        const fn = this.debugMap?.functions.find(candidate => candidate.name === this.debugMap?.entryPoint);
        const site = fn?.sites.find(candidate => candidate.kind === 'entry');
        if (fn !== undefined && site !== undefined)
            this.temporaryBreakpoints.set(site.id, { siteId: site.id, fn, temporary: true, hits: 0 });
    }

    private eventMask(): number {
        let mask = 0;
        if (this.exceptionFilters.has('ctilde-thrown'))
            mask |= debugEventThrow | debugEventUnhandled;
        else if (this.exceptionFilters.has('ctilde-unhandled'))
            mask |= debugEventUnhandled;
        if (this.exceptionFilters.has('ctilde-fatal'))
            mask |= debugEventFatal;
        if (this.runtimeFunctionBreakpoints.has('$allocation'))
            mask |= debugEventAllocation;
        if (this.runtimeFunctionBreakpoints.has('$final-release'))
            mask |= debugEventRelease;
        if (this.runtimeFunctionBreakpoints.has('$leak'))
            mask |= debugEventLeak;
        if (this.target?.target === 'esp-idf' && this.request === 'launch' && this.launchArguments?.stopAtEntry)
            mask |= debugEventStartup;
        return mask;
    }

    private async synchronizeDebugControl(): Promise<void> {
        if (this.debugMap?.runtimeHooks.control === undefined)
            throw new Error('Instrumented C~ metadata does not name its debug control block.');
        const values = buildEnabledSiteWords(this.allSites().length, this.logicalBreakpoints().map(breakpoint => breakpoint.siteId));
        if (await this.ensureFastDebugMemory()) {
            await this.writeFastControl({
                CurrentReason: 0,
                StepMode: 0,
                StepDepth: 0,
                SelectedThread: 0,
                EventMask: this.eventMask(),
                SessionActive: 1,
            }, values);
            this.controlReady = true;
            return;
        }
        for (const field of ['CurrentReason', 'StepMode', 'StepDepth', 'SelectedThread', 'StartupReleased'])
            await this.controlAddress(field);
        for (let index = 0; index < values.length; index++)
            await this.setControl(`Enabled[${index}]`, values[index] >>> 0);
        await this.setControl('EventMask', this.eventMask());
        await this.setControl('SessionActive', 1);
        this.controlReady = true;
    }

    private async requestControlSync(): Promise<void> {
        if (!this.controlReady)
            return;
        if (!this.targetRunning) {
            await this.synchronizeDebugControl();
            return;
        }
        this.pendingControlSync = true;
        this.resumeAfterControlSync = true;
        await this.gdb.command('-exec-interrupt');
    }

    private async setControl(field: string, value: string | number): Promise<void> {
        if (this.controlLayout !== undefined && this.controlImage !== undefined) {
            await this.writeFastControl({ [field]: value });
            return;
        }
        const address = await this.controlAddress(field);
        const width = field === 'SelectedThread' && this.target?.target !== 'esp-idf' ? 8 : 4;
        await this.gdb.command(`-data-write-memory-bytes ${address} ${encodeDebugControlValue(value, width)}`);
    }

    private async controlAddress(field: string): Promise<string> {
        const existing = this.controlAddresses.get(field);
        if (existing !== undefined)
            return existing;
        const address = normalizeAddress(await this.evaluateNative(`(void*)&(ct_debug_control.${field})`));
        this.controlAddresses.set(field, address);
        return address;
    }

    private async clearDebugControl(): Promise<void> {
        if (!this.controlReady || this.targetRunning)
            return;
        const words = Math.max(1, Math.ceil(this.allSites().length / 32));
        if (this.controlLayout !== undefined && this.controlImage !== undefined) {
            await this.writeFastControl({
                SessionActive: 0,
                StartupReleased: 1,
                EventMask: 0,
                StepMode: 0,
                StepDepth: 0,
                SelectedThread: 0,
                CurrentReason: 0,
            }, new Array<number>(words).fill(0));
            this.controlReady = false;
            return;
        }
        for (let index = 0; index < words; index++)
            await this.setControl(`Enabled[${index}]`, 0);
        await this.setControl('EventMask', 0);
        await this.setControl('StepMode', 0);
        await this.setControl('SelectedThread', 0);
        await this.setControl('StartupReleased', 1);
        await this.setControl('CurrentReason', 0);
        await this.setControl('SessionActive', 0);
        this.controlReady = false;
    }

    private async ensureFastDebugMemory(): Promise<boolean> {
        if (this.controlLayout !== undefined && this.controlBase !== undefined && this.controlImage !== undefined)
            return true;
        const layouts = this.debugMap?.runtimeControl?.layouts;
        if (layouts === undefined || layouts.length === 0)
            return false;
        this.pointerSize = this.target?.target === 'esp-idf'
            ? 4
            : Number.parseInt(await this.evaluateNative('sizeof(void*)'), 10);
        this.controlLayout = layouts.find(layout => layout.pointerSize === this.pointerSize);
        if (this.controlLayout === undefined)
            return false;
        this.controlBase = normalizeAddress(await this.evaluateNative(`(void*)&${this.debugMap!.runtimeControl!.symbol}`));
        this.controlImage = await this.readMemoryHex(this.controlBase, this.controlLayout.size);
        const magic = decodeDebugMemoryField(this.controlImage, this.controlLayout, 'Magic');
        if (magic !== 0x43544432n)
            throw new Error(`Instrumented C~ debug control has invalid magic 0x${magic.toString(16)}.`);
        this.runtimeSummaryLayout = this.debugMap?.runtimeSummary?.layouts?.find(layout => layout.pointerSize === this.pointerSize);
        if (this.runtimeSummaryLayout !== undefined && this.debugMap?.runtimeSummary !== undefined)
            this.runtimeSummaryBase = normalizeAddress(await this.evaluateNative(`(void*)&${this.debugMap.runtimeSummary.symbol}`));
        return true;
    }

    private async refreshFastControl(): Promise<boolean> {
        if (!await this.ensureFastDebugMemory() || this.controlBase === undefined || this.controlLayout === undefined)
            return false;
        this.controlImage = await this.readMemoryHex(this.controlBase, this.controlLayout.size);
        return true;
    }

    private fastControlValue(field: string): bigint {
        if (this.controlImage === undefined || this.controlLayout === undefined)
            throw new Error('C~ fast debug control is not available.');
        return decodeDebugMemoryField(this.controlImage, this.controlLayout, field);
    }

    private async writeFastControl(updates: Readonly<Record<string, string | number | bigint>>, bitmap?: readonly number[]): Promise<void> {
        if (this.controlLayout === undefined || this.controlBase === undefined || this.controlImage === undefined)
            throw new Error('C~ fast debug control is not available.');
        let next = patchDebugMemory(this.controlImage, this.controlLayout, updates);
        if (bitmap !== undefined)
            next = patchDebugBitmap(next, this.controlLayout, bitmap);
        for (const change of debugMemoryChanges(this.controlImage, next))
            await this.gdb.command(`-data-write-memory-bytes ${offsetAddress(this.controlBase, change.offset)} ${change.contents}`);
        this.controlImage = next;
    }

    private async readMemoryHex(address: string, length: number): Promise<string> {
        const record = await this.gdb.command(`-data-read-memory-bytes ${address} ${length}`);
        const memory = miArray(record.results.memory).map(miTuple)[0];
        const contents = miString(memory?.contents);
        if (contents.length < length * 2)
            throw new Error(`GDB returned ${contents.length / 2} of ${length} requested debug-memory bytes.`);
        return contents.slice(0, length * 2);
    }

    private async interruptAndWait(): Promise<void> {
        const stopped = new Promise<void>((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Timed out while stopping the target for debugger cleanup.')), 3000);
            this.stopWaiters.push(() => { clearTimeout(timeout); resolve(); });
        });
        await this.gdb.command('-exec-interrupt');
        await stopped;
    }

    private async startLogicalStep(threadId: number, mode: 1 | 2 | 3): Promise<void> {
        if (!this.controlReady || this.currentThreadState === '0' || this.currentThreadState === '0x0' || this.currentSite === undefined)
            throw new Error('C~ logical stepping is available only after stopping at an instrumented C~ source location. Continue to a C~ breakpoint first.');
        await this.setControl('SelectedThread', `(uintptr_t)${this.currentThreadState}`);
        await this.setControl('StepDepth', this.currentDepth);
        await this.setControl('StepMode', mode);
        const match = this.currentSite === undefined ? undefined : this.allSites().find(candidate => candidate.site.id === this.currentSite?.id);
        this.pendingLogicalStep = { mode, origin: this.currentSite, originFunction: match?.fn.name, threadId };
        await this.resume(threadId);
    }

    private async logicalStepRequest(response: DebugProtocol.Response, threadId: number, mode: 1 | 2 | 3): Promise<void> {
        try {
            await this.startLogicalStep(threadId, mode);
            this.sendResponse(response);
        } catch (error) {
            this.sendErrorResponse(response, 1009, `Could not step the C~ target: ${String(error)}`);
        }
    }

    private async resume(threadId: number): Promise<void> {
        if (this.controlReady) {
            try {
                await this.setControl('CurrentReason', 0);
            } catch (error) {
                throw new Error(`could not clear the logical stop reason: ${String(error)}`);
            }
        }
        const command = gdbResumeCommand(this.target?.target === 'esp-idf', threadId);
        try {
            await this.gdb.command(command);
        } catch (error) {
            throw new Error(`native resume command ${command} failed: ${String(error)}`);
        }
    }

    private variableIsLive(variable: DebugVariable, frameId?: number): boolean {
        const position = (frameId === undefined ? this.currentSite : this.frameSites.get(frameId))?.source.spanStart;
        return position === undefined || (variable.liveStart === undefined || position >= variable.liveStart) &&
            (variable.liveEnd === undefined || position < variable.liveEnd);
    }

    private liveLocals(fn: DebugFunction | undefined, frameId?: number): DebugVariable[] {
        if (fn === undefined)
            return [];
        const result = new Map<string, DebugVariable>();
        const scopeLength = (variable: DebugVariable): number => fn.scopes?.find(scope => scope.id === variable.scopeId)?.source.spanLength ?? Number.MAX_SAFE_INTEGER;
        for (const variable of fn.locals.filter(candidate => this.variableIsLive(candidate, frameId)).sort((left, right) => scopeLength(right) - scopeLength(left)))
            result.set(variable.name, variable);
        return [...result.values()].sort((left, right) => (left.liveStart ?? 0) - (right.liveStart ?? 0));
    }

    private async addressOf(expression: string): Promise<string> {
        return normalizeAddress(await this.evaluateNative(`(void*)&(${expression})`));
    }

    private async runtimeVariables(frameId: number): Promise<DebugProtocol.Variable[]> {
        let count: string;
        let allocations: string;
        let releases: string;
        let currentSite: string;
        let quarantineBlocks: string | undefined;
        let quarantineBytes: string | undefined;
        if (this.runtimeSummaryLayout !== undefined && this.runtimeSummaryBase !== undefined) {
            const image = await this.readMemoryHex(this.runtimeSummaryBase, this.runtimeSummaryLayout.size);
            const value = (field: string): string => decodeDebugMemoryField(image, this.runtimeSummaryLayout!, field).toString();
            count = value('LiveObjectCount');
            allocations = value('TotalAllocations');
            releases = value('TotalFinalReleases');
            currentSite = value('CurrentSite');
            quarantineBlocks = value('QuarantineBlocks');
            quarantineBytes = value('QuarantineBytes');
        } else {
            count = await this.evaluateNative('(uint32_t)ct_debug_live_count');
            allocations = await this.evaluateNative('(uint32_t)ct_debug_allocation_count');
            releases = await this.evaluateNative('(uint32_t)ct_debug_final_release_count');
            currentSite = await this.evaluateNative('ct_debug_control.CurrentSite');
        }
        const mode = this.debugMap?.memoryDiagnostics ?? 'off';
        const result: DebugProtocol.Variable[] = [
            { name: 'Memory diagnostics', value: mode, type: 'string', variablesReference: 0 },
            { name: 'Live object count', value: count, type: 'uint', variablesReference: 0 },
            { name: 'Total allocations', value: allocations, type: 'uint', variablesReference: 0 },
            { name: 'Total final releases', value: releases, type: 'uint', variablesReference: 0 },
            { name: 'Live objects', value: `${count} object(s)`, indexedVariables: Number.parseInt(count, 10), variablesReference: this.variableHandles.create({ expression: '$liveobjects', type: '$liveobjects', frameId }) },
            { name: 'Current probe site', value: currentSite, type: 'uint', variablesReference: 0 },
        ];
        if (mode === 'guarded') {
            result.push({ name: 'Quarantine blocks', value: quarantineBlocks ?? await this.evaluateNative('ct_debug_quarantine_count'), type: 'uint', variablesReference: 0 });
            result.push({ name: 'Quarantine bytes', value: quarantineBytes ?? await this.evaluateNative('ct_debug_quarantine_bytes'), type: 'nuint', variablesReference: 0 });
        }
        return result;
    }

    private async staticVariables(frameId: number): Promise<DebugProtocol.Variable[]> {
        const result: DebugProtocol.Variable[] = [];
        for (const type of this.debugMap?.types ?? [])
        for (const field of type.fields.filter(candidate => candidate.static))
            result.push(await this.makeVariable(`${type.name}.${field.name}`, field.storage, field.type, frameId));
        return result;
    }

    private async liveObjectVariables(frameId: number, start: number, count: number): Promise<DebugProtocol.Variable[]> {
        let allocation = 'ct_debug_live_head';
        for (let index = 0; index < start; index++)
            allocation = `(${allocation})->Next`;
        const result: DebugProtocol.Variable[] = [];
        for (let index = start; index < start + Math.max(0, count); index++) {
            if (!isTruthyGdbValue(await this.evaluateNative(`${allocation} != 0`)))
                break;
            const object = `((ct_object*)(void*)((${allocation}) + 1))`;
            const identity = await this.evaluateNative(`(uint32_t)${object}->IdentityHash`);
            const type = await this.evaluateCString(`${object}->Type->Name`);
            const presentationType = this.debugMap?.boxes?.some(box => box.valueType === type) ? 'System.Object' : type || 'System.Object';
            result.push(await this.makeVariable(`#${identity}`, object, presentationType, frameId));
            allocation = `(${allocation})->Next`;
        }
        return result;
    }

    private async objectRuntimeVariables(expression: string, frameId: number): Promise<DebugProtocol.Variable[]> {
        const object = `((ct_object*)(void*)(${expression}))`;
        const allocation = `(((ct_debug_allocation*)(void*)(${expression})) - 1)`;
        const result: DebugProtocol.Variable[] = [
            { name: 'IdentityHash', value: await this.evaluateNative(`(uint32_t)${object}->IdentityHash`), type: 'uint', variablesReference: 0 },
            { name: 'RefCount', value: await this.evaluateNative(`${object}->RefCount`), type: 'uint', variablesReference: 0, evaluateName: `${object}->RefCount`, memoryReference: await this.addressOf(`${object}->RefCount`) },
        ];
        if (this.debugMap?.memoryDiagnostics !== 'off') {
            result.push({ name: 'AllocationSize', value: await this.evaluateNative(`${allocation}->Size`), type: 'nuint', variablesReference: 0 });
            result.push({ name: 'AllocationSite', value: await this.evaluateNative(`${allocation}->LastSite`), type: 'uint', variablesReference: 0 });
            result.push({ name: 'AllocatedAt', value: `${await this.evaluateCString(`${allocation}->File`)}:${await this.evaluateNative(`${allocation}->Line`)}`, type: 'string', variablesReference: 0 });
            if (this.debugMap?.memoryDiagnostics === 'guarded') {
                const canary = await this.evaluateNative(`*(uint32_t*)((uint8_t*)(void*)${expression} + ${allocation}->Size)`);
                result.push({ name: 'Canary', value: canary.toLocaleLowerCase().includes('c71de14d') || canary === '3340624205' ? 'intact' : `corrupt (${canary})`, type: 'string', variablesReference: 0 });
            }
        }
        return result;
    }

    private async evaluateCStringPointer(expression: string): Promise<string> {
        if (!isTruthyGdbValue(await this.evaluateNative(`${expression} != 0`)))
            return '';
        return this.evaluateCString(`(const char*)(uintptr_t)${expression}`);
    }

    private async evaluateCStringAddress(address: bigint): Promise<string> {
        if (address === 0n)
            return '';
        return this.evaluateCString(`(const char*)(uintptr_t)0x${address.toString(16)}`);
    }

    private dataBreakpointExpression(variablesReference: number | undefined, name: string, frameId: number,
        fn: DebugFunction | undefined): { expression: string; type: string } | undefined {
        const container = variablesReference === undefined ? undefined : this.variableHandles.get(variablesReference);
        if (container === undefined || container.type === '$scope')
            return this.translateWatch(name, fn, frameId);
        if (container.type === '$statics') {
            for (const type of this.debugMap?.types ?? []) {
                const field = type.fields.find(candidate => candidate.static && (candidate.name === name || `${type.name}.${candidate.name}` === name));
                if (field !== undefined)
                    return { expression: field.storage, type: field.type };
            }
            return undefined;
        }
        if (container.type === '$objectruntime' && name === 'RefCount')
            return { expression: `((ct_object*)(void*)(${container.expression}))->RefCount`, type: 'uint' };
        if (container.type.endsWith('[]')) {
            const index = /^\[(\d+)\]$/.exec(name);
            return index === null ? undefined : { expression: `${container.expression}->Data[${index[1]}]`, type: container.type.slice(0, -2) };
        }
        const type = this.debugMap?.types.find(candidate => candidate.name === container.type);
        const field = type === undefined ? undefined : this.instanceFields(type).find(candidate => candidate.name === name);
        if (type === undefined || field === undefined)
            return undefined;
        const pointer = type.kind === 'class' || type.kind === 'delegate';
        return { expression: `${pointer ? `((${type.storage}*)(void*)${container.expression})->` : `${container.expression}.`}${field.storage}`, type: field.type };
    }

    private async removeExpiredLocalWatchpoints(): Promise<void> {
        if (this.dataBreakpoints.size === 0 || this.currentThreadState === '0' || this.currentThreadState === '0x0')
            return;
        const active = new Set<string>();
        let frame = `((ct_thread_state*)(void*)${this.currentThreadState})->DebugFrameTop`;
        for (let count = 0; count < 256 && isTruthyGdbValue(await this.evaluateNative(`${frame} != 0`)); count++) {
            active.add(await this.evaluateNative(`${frame}->Activation`));
            frame = `(${frame})->Previous`;
        }
        for (const [dataId, watchpoint] of this.dataBreakpoints) {
            if (watchpoint.thread !== this.currentThreadState || active.has(watchpoint.activation))
                continue;
            try { await this.gdb.command(`-break-delete ${watchpoint.id}`); } catch { }
            this.dataBreakpoints.delete(dataId);
            this.sendEvent(new OutputEvent('A C~ local data breakpoint was removed because its owning method activation exited.\n', 'console'));
        }
    }

    private async selectFunctionFrame(fn: DebugFunction, threadId: number): Promise<void> {
        const record = await this.gdb.command(`-stack-list-frames --thread ${threadId}`);
        for (const value of miArray(record.results.stack)) {
            const wrapper = miTuple(value);
            const frame = miTuple(wrapper.frame ?? value);
            if (miString(frame.func) !== fn.name)
                continue;
            await this.selectFrame(threadId, Number.parseInt(miString(frame.level), 10));
            return;
        }
    }

    private translateExpression(expression: string, fn: DebugFunction | undefined): string {
        const storage = new Map<string, string>();
        for (const variable of [...fn?.parameters ?? [], ...this.liveLocals(fn)])
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

    private translateWatch(expression: string, fn: DebugFunction | undefined, frameId?: number): { expression: string; type: string } | undefined {
        const root = /^([A-Za-z_][A-Za-z0-9_]*|this)/.exec(expression);
        if (root === null)
            return undefined;
        const definition = root[1] === 'this' && fn?.receiver !== undefined && fn.receiverType !== undefined
            ? { name: 'this', storage: fn.receiver, type: fn.receiverType }
            : [...this.liveLocals(fn, frameId), ...fn?.parameters ?? []].find(variable => variable.name === root[1]);
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

function isAddressableWatchType(type: string): boolean {
    return type.length !== 0 && !type.startsWith('$');
}

function nativeWatchSize(type: string): number | undefined {
    if (['bool', 'byte', 'sbyte', 'char'].includes(type))
        return 1;
    if (['short', 'ushort'].includes(type))
        return 2;
    if (['int', 'uint', 'float'].includes(type) || type.endsWith('[]') || type === 'string' || type.includes('.'))
        return 4;
    return undefined;
}

function offsetAddress(reference: string, offset: number): string {
    const parsed = BigInt(reference);
    return `0x${(parsed + BigInt(offset)).toString(16)}`;
}

function normalizeAddress(value: string): string {
    const hexadecimal = /0x[0-9a-f]+/i.exec(value);
    if (hexadecimal !== null)
        return hexadecimal[0];
    const decimal = /\b\d+\b/.exec(value);
    return decimal?.[0] ?? value.trim();
}

function formatAddress(value: bigint): string {
    return `0x${value.toString(16)}`;
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

async function withTimeout<T>(operation: Promise<T>, timeoutMilliseconds: number, message: string): Promise<T> {
    let timer: NodeJS.Timeout | undefined;
    try {
        return await Promise.race([
            operation,
            new Promise<T>((_, reject) => timer = setTimeout(() => reject(new Error(message)), timeoutMilliseconds)),
        ]);
    } finally {
        if (timer !== undefined)
            clearTimeout(timer);
    }
}

function matchesFunctionBreakpoint(fn: DebugFunction, requested: string): boolean {
    const normalized = requested.replaceAll(' ', '');
    const signature = `${fn.displayName}(${fn.parameters.map(parameter => parameter.type).join(',')})`.replaceAll(' ', '');
    if (signature === normalized || signature.endsWith(`.${normalized}`))
        return true;
    return !normalized.includes('(') && (fn.displayName === normalized || fn.displayName.endsWith(`.${normalized}`));
}

function sameSourceLine(left: DebugSource, right: DebugSource): boolean {
    return normalizePath(left.file) === normalizePath(right.file) && left.line === right.line;
}

LoggingDebugSession.run(CTildeDebugSession);
