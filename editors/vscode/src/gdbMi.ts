import { ChildProcessWithoutNullStreams, spawn } from 'child_process';

export type MiValue = string | MiTuple | MiValue[];
export interface MiTuple { readonly [name: string]: MiValue; }

export interface MiRecord {
    readonly token?: number;
    readonly kind: '^' | '*' | '+' | '=' | '~' | '@' | '&';
    readonly name?: string;
    readonly results: MiTuple;
    readonly text?: string;
}

export function isWriteError(error: Error | null | undefined): error is Error {
    return error != null;
}

export function parseMiRecord(line: string): MiRecord | undefined {
    const text = line.trimEnd();
    if (text.length === 0 || text === '(gdb)')
        return undefined;
    let position = 0;
    while (position < text.length && text.charCodeAt(position) >= 48 && text.charCodeAt(position) <= 57)
        position++;
    const token = position === 0 ? undefined : Number.parseInt(text.slice(0, position), 10);
    const kind = text[position] as MiRecord['kind'];
    if (!['^', '*', '+', '=', '~', '@', '&'].includes(kind))
        return undefined;
    position++;
    if (kind === '~' || kind === '@' || kind === '&') {
        const parser = new MiParser(text, position);
        return { token, kind, results: {}, text: parser.readCString() };
    }
    const nameStart = position;
    while (position < text.length && text[position] !== ',')
        position++;
    const name = text.slice(nameStart, position);
    const parser = new MiParser(text, position < text.length ? position + 1 : position);
    return { token, kind, name, results: parser.readResults() };
}

export class MiRecordStream {
    private buffer = '';

    public push(chunk: string): MiRecord[] {
        this.buffer += chunk;
        const records: MiRecord[] = [];
        while (true) {
            const newline = this.buffer.indexOf('\n');
            if (newline < 0)
                return records;
            const line = this.buffer.slice(0, newline).replace(/\r$/, '');
            this.buffer = this.buffer.slice(newline + 1);
            const record = parseMiRecord(line);
            if (record !== undefined)
                records.push(record);
        }
    }
}

class MiParser {
    public constructor(private readonly text: string, private position: number) { }

    public readResults(terminator?: string): MiTuple {
        const result: Record<string, MiValue> = {};
        while (this.position < this.text.length && this.text[this.position] !== terminator) {
            const name = this.readName();
            if (name.length === 0 || this.text[this.position] !== '=')
                break;
            this.position++;
            const value = this.readValue();
            const existing = result[name];
            result[name] = existing === undefined ? value : Array.isArray(existing) ? [...existing, value] : [existing, value];
            if (this.text[this.position] === ',')
                this.position++;
            else
                break;
        }
        return result;
    }

    public readCString(): string {
        if (this.text[this.position] !== '"')
            return this.text.slice(this.position);
        this.position++;
        let result = '';
        while (this.position < this.text.length) {
            const character = this.text[this.position++];
            if (character === '"')
                break;
            if (character !== '\\') {
                result += character;
                continue;
            }
            const escaped = this.text[this.position++];
            const simple: Record<string, string> = { n: '\n', r: '\r', t: '\t', b: '\b', f: '\f', v: '\v', '"': '"', '\\': '\\' };
            if (escaped in simple) {
                result += simple[escaped];
                continue;
            }
            if (escaped !== undefined && /[0-7]/.test(escaped)) {
                let octal = escaped;
                while (octal.length < 3 && this.position < this.text.length && /[0-7]/.test(this.text[this.position]))
                    octal += this.text[this.position++];
                result += String.fromCharCode(Number.parseInt(octal, 8));
                continue;
            }
            result += escaped ?? '';
        }
        return result;
    }

    private readName(): string {
        const start = this.position;
        while (this.position < this.text.length && /[A-Za-z0-9_\-]/.test(this.text[this.position]))
            this.position++;
        return this.text.slice(start, this.position);
    }

    private readValue(): MiValue {
        if (this.text[this.position] === '"')
            return this.readCString();
        if (this.text[this.position] === '{') {
            this.position++;
            const result = this.readResults('}');
            if (this.text[this.position] === '}')
                this.position++;
            return result;
        }
        if (this.text[this.position] === '[') {
            this.position++;
            const values: MiValue[] = [];
            while (this.position < this.text.length && this.text[this.position] !== ']') {
                const saved = this.position;
                const name = this.readName();
                if (name.length !== 0 && this.text[this.position] === '=') {
                    this.position++;
                    values.push({ [name]: this.readValue() });
                } else {
                    this.position = saved;
                    values.push(this.readValue());
                }
                if (this.text[this.position] === ',')
                    this.position++;
                else
                    break;
            }
            if (this.text[this.position] === ']')
                this.position++;
            return values;
        }
        const start = this.position;
        while (this.position < this.text.length && this.text[this.position] !== ',' && this.text[this.position] !== '}' && this.text[this.position] !== ']')
            this.position++;
        return this.text.slice(start, this.position);
    }
}

interface PendingCommand {
    readonly resolve: (record: MiRecord) => void;
    readonly reject: (error: Error) => void;
}

export class GdbMi {
    private process: ChildProcessWithoutNullStreams | undefined;
    private nextToken = 1;
    private readonly stream = new MiRecordStream();
    private readonly pending = new Map<number, PendingCommand>();

    public onAsync: (record: MiRecord) => void = () => undefined;
    public onOutput: (category: 'console' | 'stdout' | 'stderr', text: string) => void = () => undefined;
    public onExit: (code: number | null) => void = () => undefined;

    public start(command: string, prefixArguments: readonly string[] = [], cwd?: string): void {
        if (this.process !== undefined)
            throw new Error('GDB is already running.');
        const child = spawn(command, [...prefixArguments, '--quiet', '--interpreter=mi2'], { cwd, stdio: 'pipe', windowsHide: true });
        this.process = child;
        child.stdout.setEncoding('utf8');
        child.stderr.setEncoding('utf8');
        child.stdout.on('data', chunk => this.consume(String(chunk)));
        child.stderr.on('data', chunk => this.onOutput('stderr', String(chunk)));
        child.on('error', error => this.rejectAll(error));
        child.on('exit', code => {
            this.process = undefined;
            this.rejectAll(new Error(`GDB exited${code === null ? '' : ` with code ${code}`}.`));
            this.onExit(code);
        });
    }

    public command(command: string): Promise<MiRecord> {
        const child = this.process;
        if (child === undefined)
            return Promise.reject(new Error('GDB is not running.'));
        const token = this.nextToken++;
        return new Promise<MiRecord>((resolve, reject) => {
            this.pending.set(token, { resolve, reject });
            child.stdin.write(`${token}${command}\n`, error => {
                if (isWriteError(error)) {
                    this.pending.delete(token);
                    reject(error);
                }
            });
        });
    }

    public async close(): Promise<void> {
        const child = this.process;
        if (child === undefined)
            return;
        try {
            await this.command('-gdb-exit');
        } catch {
            child.kill();
        }
    }

    private consume(chunk: string): void {
        for (const record of this.stream.push(chunk)) {
            if (record.kind === '~')
                this.onOutput('console', record.text ?? '');
            else if (record.kind === '@')
                this.onOutput('stdout', record.text ?? '');
            else if (record.kind === '&')
                this.onOutput('stderr', record.text ?? '');
            else if (record.kind === '^' && record.token !== undefined) {
                const pending = this.pending.get(record.token);
                if (pending === undefined)
                    continue;
                this.pending.delete(record.token);
                if (record.name === 'error')
                    pending.reject(new Error(String(record.results.msg ?? 'GDB command failed.')));
                else
                    pending.resolve(record);
            } else
                this.onAsync(record);
        }
    }

    private rejectAll(error: Error): void {
        for (const pending of this.pending.values())
            pending.reject(error);
        this.pending.clear();
    }
}

export function miArray(value: MiValue | undefined): MiValue[] {
    if (value === undefined)
        return [];
    return Array.isArray(value) ? value : [value];
}

export function miTuple(value: MiValue | undefined): MiTuple {
    return value !== undefined && !Array.isArray(value) && typeof value === 'object' ? value : {};
}

export function miString(value: MiValue | undefined): string {
    return typeof value === 'string' ? value : '';
}
