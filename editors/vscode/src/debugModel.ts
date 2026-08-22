export interface LogicalSource {
    readonly file: string;
    readonly line: number;
    readonly column: number;
}

export interface LogicalSite {
    readonly id: number;
    readonly kind: string;
    readonly source: LogicalSource;
}

export interface LogicalFunction<TSite extends LogicalSite = LogicalSite> {
    readonly source?: LogicalSource | null;
    readonly sites: readonly TSite[];
}

export function findExecutableSite<TFunction extends LogicalFunction<TSite>, TSite extends LogicalSite>(
    functions: readonly TFunction[], file: string, line: number, column = 1,
): { fn: TFunction; site: TSite } | undefined {
    const containing = functions.filter(fn => fn.source != null && normalize(fn.source.file) === file &&
        fn.source.line <= line && fn.sites.some(site => normalize(site.source.file) === file && site.source.line >= line))
        .sort((left, right) => (right.source?.line ?? 0) - (left.source?.line ?? 0));
    const fn = containing[0];
    if (fn === undefined)
        return undefined;
    const site = fn.sites.filter(candidate => normalize(candidate.source.file) === file &&
        (candidate.source.line > line || candidate.source.line === line && candidate.source.column >= column))
        .sort((left, right) => left.source.line - right.source.line || left.source.column - right.source.column)[0];
    return site === undefined ? undefined : { fn, site };
}

export function buildEnabledSiteWords(siteCount: number, siteIds: readonly number[]): number[] {
    const words = new Array<number>(Math.max(1, Math.ceil(siteCount / 32))).fill(0);
    for (const siteId of siteIds) {
        if (!Number.isInteger(siteId) || siteId < 0 || siteId >= siteCount)
            throw new Error(`Invalid C~ debug site ${siteId}.`);
        words[Math.floor(siteId / 32)] = (words[Math.floor(siteId / 32)] | (1 << (siteId % 32))) >>> 0;
    }
    return words;
}

export function resolveLogicalFrameSite<TSite extends LogicalSite>(
    sites: readonly TSite[], nativeLine: number, currentSite?: TSite,
): TSite | undefined {
    return currentSite ?? sites.filter(site => site.source.line <= nativeLine)
        .sort((left, right) => right.source.line - left.source.line || right.source.column - left.source.column)[0];
}

export function parseHitCondition(value: string | undefined): number | undefined {
    if (value === undefined || !/^\d+$/.test(value))
        return undefined;
    const parsed = Number.parseInt(value, 10);
    return parsed > 0 ? parsed : undefined;
}

export function espTrapInstructionSize(target: string | undefined): 3 | 4 {
    return target === 'esp32' || target === 'esp32s2' || target === 'esp32s3' ? 3 : 4;
}

export function espTrapResumeExpression(target: string | undefined): string {
    const field = target === 'esp32' || target === 'esp32s2' || target === 'esp32s3' ? 'pc' : 'mepc';
    return `((esp_gdbstub_frame_t*)running_task_frame)->${field} += ${espTrapInstructionSize(target)}`;
}

export function gdbResumeCommand(espIdf: boolean, threadId: number): string {
    return espIdf ? '-exec-continue' : `-exec-continue --thread ${threadId}`;
}

export interface EspDetachOperations {
    readonly running: boolean;
    readonly trapAlreadyAdvanced: boolean;
    interrupt(): Promise<void>;
    readLogicalReason(): Promise<number>;
    advanceLogicalTrap(): Promise<void>;
    removeNativeBreakpoints(): Promise<void>;
    clearLogicalControl(): Promise<void>;
    continueWithoutDebugger(): Promise<void>;
}

export async function runEspDetachSequence(operations: EspDetachOperations): Promise<void> {
    if (operations.running)
        await operations.interrupt();
    const logicalReason = await operations.readLogicalReason();
    if (logicalReason !== 0 && !operations.trapAlreadyAdvanced)
        await operations.advanceLogicalTrap();
    await operations.removeNativeBreakpoints();
    await operations.clearLogicalControl();
    await operations.continueWithoutDebugger();
}

export function encodeDebugControlValue(value: string | number, width: number): string {
    let integer: bigint;
    if (typeof value === 'number')
        integer = BigInt(value >>> 0);
    else {
        const hexadecimal = /0x[0-9a-f]+/i.exec(value);
        const decimal = /-?\d+/.exec(value);
        if (hexadecimal === null && decimal === null)
            throw new Error(`C~ debug control value is not an integer: ${value}`);
        integer = BigInt(hexadecimal?.[0] ?? decimal![0]);
    }
    const bytes: string[] = [];
    for (let index = 0; index < width; index++)
        bytes.push(Number(integer >> BigInt(index * 8) & 0xffn).toString(16).padStart(2, '0'));
    return bytes.join('');
}

export interface DebugMemoryFieldLayout { readonly offset: number; readonly width: number; }
export interface DebugMemoryLayout {
    readonly pointerSize: number;
    readonly size: number;
    readonly enabledOffset?: number;
    readonly fields: Readonly<Record<string, DebugMemoryFieldLayout>>;
}

export function decodeDebugMemoryField(contents: string, layout: DebugMemoryLayout, field: string): bigint {
    const definition = layout.fields[field];
    if (definition === undefined)
        throw new Error(`C~ debug memory layout does not define '${field}'.`);
    const bytes = Buffer.from(contents, 'hex');
    if (definition.offset < 0 || definition.width <= 0 || definition.offset + definition.width > bytes.length)
        throw new Error(`C~ debug memory field '${field}' is outside the returned target memory.`);
    let result = 0n;
    for (let index = definition.width - 1; index >= 0; index--)
        result = result << 8n | BigInt(bytes[definition.offset + index]);
    return result;
}

export function patchDebugMemory(contents: string, layout: DebugMemoryLayout,
    updates: Readonly<Record<string, string | number | bigint>>): string {
    const bytes = Buffer.from(contents, 'hex');
    if (bytes.length < layout.size)
        throw new Error('C~ debug memory image is shorter than its advertised layout.');
    for (const [field, value] of Object.entries(updates)) {
        const definition = layout.fields[field];
        if (definition === undefined)
            throw new Error(`C~ debug memory layout does not define '${field}'.`);
        let integer = typeof value === 'bigint' ? value : typeof value === 'number' ? BigInt(value >>> 0) : parseDebugInteger(value);
        for (let index = 0; index < definition.width; index++) {
            bytes[definition.offset + index] = Number(integer & 0xffn);
            integer >>= 8n;
        }
        if (integer !== 0n)
            throw new Error(`C~ debug control value for '${field}' does not fit in ${definition.width} bytes.`);
    }
    return bytes.toString('hex');
}

export function patchDebugBitmap(contents: string, layout: DebugMemoryLayout, values: readonly number[]): string {
    if (layout.enabledOffset === undefined)
        throw new Error('C~ debug memory layout does not define an enabled-site bitmap.');
    const bytes = Buffer.from(contents, 'hex');
    if (layout.enabledOffset + values.length * 4 > bytes.length)
        throw new Error('C~ enabled-site bitmap is outside the returned target memory.');
    for (let index = 0; index < values.length; index++)
        bytes.writeUInt32LE(values[index] >>> 0, layout.enabledOffset + index * 4);
    return bytes.toString('hex');
}

export interface DebugMemoryChange { readonly offset: number; readonly contents: string; }

export function debugMemoryChanges(before: string, after: string, coalesceGap = 8): DebugMemoryChange[] {
    const oldBytes = Buffer.from(before, 'hex');
    const newBytes = Buffer.from(after, 'hex');
    if (oldBytes.length !== newBytes.length)
        throw new Error('C~ debug memory images must have equal lengths.');
    const changes: DebugMemoryChange[] = [];
    let start = -1;
    let last = -1;
    for (let index = 0; index < newBytes.length; index++) {
        if (oldBytes[index] === newBytes[index])
            continue;
        if (start < 0) {
            start = index;
            last = index;
        } else if (index - last <= coalesceGap + 1)
            last = index;
        else {
            changes.push({ offset: start, contents: newBytes.subarray(start, last + 1).toString('hex') });
            start = last = index;
        }
    }
    if (start >= 0)
        changes.push({ offset: start, contents: newBytes.subarray(start, last + 1).toString('hex') });
    return changes;
}

function parseDebugInteger(value: string): bigint {
    const hexadecimal = /0x[0-9a-f]+/i.exec(value);
    const decimal = /-?\d+/.exec(value);
    if (hexadecimal === null && decimal === null)
        throw new Error(`C~ debug value is not an integer: ${value}`);
    return BigInt(hexadecimal?.[0] ?? decimal![0]);
}

export function stripInternalProbeConsole(text: string): string {
    const lines = text.match(/[^\n]*\n|[^\n]+$/g) ?? [];
    const kept: string[] = [];
    let expectTrapSource = false;
    for (const line of lines) {
        const value = line.trim();
        if (/^(?:Thread\s+\d+|Program)\s+received signal SIGTRAP\b/.test(value))
            continue;
        if (/^warning: multi-threaded target stopped without sending a thread-id, using first non-exited thread$/.test(value))
            continue;
        if (/\bct_debug_trap\s*\(\)\s+at\s+.*\bctilde_runtime\.c:\d+\b/.test(value)) {
            expectTrapSource = true;
            continue;
        }
        if (expectTrapSource && /^\d+\s+/.test(value) &&
            (/\besp_cpu_dbgr_break\b/.test(value) || /\braise\s*\(\s*SIGTRAP\s*\)/.test(value) || /\b__debugbreak\b/.test(value))) {
            expectTrapSource = false;
            continue;
        }
        expectTrapSource = false;
        kept.push(line);
    }
    return kept.join('');
}

export class TargetConsoleBuffer {
    private readonly parts: string[] = [];

    public append(text: string): void {
        this.parts.push(text);
    }

    public drain(showRuntimeFrames: boolean): string {
        if (this.parts.length === 0)
            return '';
        const buffered = this.parts.join('');
        if (showRuntimeFrames) {
            this.parts.length = 0;
            return buffered;
        }
        const lines = buffered.match(/[^\n]*\n|[^\n]+$/g) ?? [];
        const completeCount = lines.length - (lines.at(-1)?.endsWith('\n') ? 0 : 1);
        const emitCount = Math.max(0, completeCount - 2);
        if (emitCount === 0)
            return '';
        const emitted = lines.slice(0, emitCount).join('');
        this.parts.length = 0;
        this.parts.push(lines.slice(emitCount).join(''));
        return stripInternalProbeConsole(emitted);
    }

    public finish(internalProbe: boolean, showRuntimeFrames: boolean): string {
        const buffered = this.parts.join('');
        this.parts.length = 0;
        return internalProbe && !showRuntimeFrames ? stripInternalProbeConsole(buffered) : buffered;
    }

    public clear(): void {
        this.parts.length = 0;
    }
}

export function isTruthyGdbValue(value: string): boolean {
    const normalized = value.trim().toLocaleLowerCase();
    return normalized !== '0' && normalized !== 'false' && normalized !== '0x0' && normalized !== '(nil)' && normalized !== 'null';
}

function normalize(value: string): string {
    return value.replaceAll('\\', '/');
}
