import { StringDecoder } from 'node:string_decoder';

export const requiredFirmwareMarkers = [
  'esp error: ESP_OK',
  'C~ ESP-IDF hardware test',
  'virtual: 42',
  'delegate: 42',
  'function pointer: 42',
  'timer64: ok',
  'generated bindings: ok',
  'native buffer: 42',
  'native utf8: ok',
  'opaque defer: ok',
  'delegate context: 42',
  'export: 42',
  'threading: ok',
  'boxed: 7',
  'exception: caught on ESP32',
  'arc heap recovery: True',
  'draft16 concurrency: ok',
  'CTILDE_ESP_OK',
];

export function findUniqueSourceLine(source, anchor) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const matches = [];
  for (let index = 0; index < lines.length; index++) {
    if (lines[index].includes(anchor))
      matches.push(index + 1);
  }
  if (matches.length !== 1)
    throw new Error(`Source anchor '${anchor}' matched ${matches.length} lines; exactly one is required.`);
  return matches[0];
}

export const esp32DebugSourceAnchors = Object.freeze({
  firstStatement: 'EspError configureError = Ws2812.Configure',
  exerciseCall: 'ExerciseArc();',
  arcObject: 'node.Text = index.ToString();',
  arcIterationEnd: 'index++;',
  afterSelfTests: 'Console.Write("minimum free heap: ");',
  loopDelay: 'FreeRtos.DelayMilliseconds(500u);',
});

export function resolveEsp32DebugSourceLines(source) {
  return Object.fromEntries(Object.entries(esp32DebugSourceAnchors)
    .map(([name, anchor]) => [name, findUniqueSourceLine(source, anchor)]));
}

function requiredNumber(transcript, label) {
  const expression = new RegExp(`${escapeRegex(label)}\\s*(\\d+)`, 'i');
  const match = expression.exec(transcript);
  if (match === null)
    throw new Error(`Hardware transcript omitted '${label}'.`);
  const value = Number.parseInt(match[1], 10);
  if (!Number.isSafeInteger(value) || value <= 0)
    throw new Error(`Hardware transcript reported an invalid ${label} value: ${match[1]}.`);
  return value;
}

export function parseFirmwareTranscript(transcript, minimumTransitions = 25) {
  const normalized = transcript.replaceAll('\r\n', '\n');
  const missing = requiredFirmwareMarkers.filter(marker => !normalized.includes(marker));
  if (missing.length !== 0)
    throw new Error(`Firmware transcript omitted required marker(s): ${missing.join(', ')}.`);
  if (!normalized.includes('wifi: not configured') && !normalized.includes('generated wifi/http bindings: ok'))
    throw new Error("Firmware transcript omitted both the configured Wi-Fi success marker and the offline fallback marker.");
  const applicationStart = normalized.indexOf('C~ ESP-IDF hardware test');
  const applicationTranscript = applicationStart < 0 ? normalized : normalized.slice(applicationStart);
  const failure = /(?:\b[A-Z][A-Z0-9_]*FAILED\b|Guru Meditation|Task watchdog|panic(?:'ed)?|CTILDE runtime error|Rebooting\.\.\.|\brst:)/i.exec(applicationTranscript);
  if (failure !== null)
    throw new Error(`Firmware transcript contains failure text: ${failure[0]}.`);

  const transitions = [...normalized.matchAll(/^ws2812:\s*(on|off)\s*$/gim)].map(match => match[1].toLowerCase());
  if (transitions.length < minimumTransitions)
    throw new Error(`Firmware produced ${transitions.length} WS2812 transitions; ${minimumTransitions} are required.`);
  for (let index = 1; index < transitions.length; index++) {
    if (transitions[index] === transitions[index - 1])
      throw new Error(`WS2812 transition ${index + 1} repeated '${transitions[index]}' instead of alternating.`);
  }

  const freeHeap = requiredNumber(normalized, 'free heap:');
  const minimumFreeHeap = requiredNumber(normalized, 'minimum free heap:');
  const stackHighWater = requiredNumber(normalized, 'stack high water:');
  const tick = requiredNumber(normalized, 'tick:');
  if (stackHighWater < 1024)
    throw new Error(`Main-task stack high-water headroom is ${stackHighWater} bytes; at least 1024 are required.`);

  return { freeHeap, minimumFreeHeap, stackHighWater, tick, transitions: transitions.length };
}

export function parseRuntimeFailureTranscript(transcript) {
  const normalized = transcript.replaceAll('\r\n', '\n');
  for (const marker of ['CTILDE_ESP_FAILURE_TEST', 'CTN0001']) {
    if (!normalized.includes(marker))
      throw new Error(`Runtime-failure transcript omitted '${marker}'.`);
  }
  const diagnosticIndex = normalized.indexOf('CTN0001');
  const abortIndex = normalized.indexOf('abort()', diagnosticIndex);
  if (abortIndex < 0)
    throw new Error("Runtime-failure transcript omitted 'abort()' after CTN0001.");
  const softwareResetIndex = normalized.indexOf('SW_CPU_RESET', abortIndex);
  const rebootIndex = normalized.indexOf('Rebooting...', abortIndex);
  const reset = softwareResetIndex >= 0
    ? 'SW_CPU_RESET'
    : rebootIndex >= 0
      ? 'Rebooting'
      : null;
  if (!reset)
    throw new Error("Runtime-failure transcript omitted a software-reset marker ('SW_CPU_RESET' or 'Rebooting...').");
  return { runtimeCode: 'CTN0001', reset };
}

export function parseIdfSize(output) {
  const binaryHex = /binary size\s+0x([0-9a-f]+)/i.exec(output);
  const total = /Total image size:\s*([0-9,]+)\s*bytes/i.exec(output);
  const sections = {};
  const sectionNames = new Map([['Flash Code', 'flashCode'], ['Flash Data', 'flashData'], ['IRAM', 'iram'], ['DRAM', 'dram']]);
  for (const [name, key] of sectionNames) {
    const match = new RegExp(`^(?:\\|\\s*)?${escapeRegex(name)}\\s*(?:\\|\\s*|\\s+)([0-9,]+)`, 'im').exec(output);
    if (match !== null)
      sections[key] = Number.parseInt(match[1].replaceAll(',', ''), 10);
  }
  const binaryBytes = binaryHex === null ? undefined : Number.parseInt(binaryHex[1], 16);
  const imageBytes = total === null ? undefined : Number.parseInt(total[1].replaceAll(',', ''), 10);
  if (binaryBytes === undefined && imageBytes === undefined)
    throw new Error('ESP-IDF size output did not contain a binary or total image size.');
  return { binaryBytes, imageBytes, sections };
}

export const expectedConsoleFixture = [
  'CTILDE_CONSOLE_BEGIN',
  'ASCII: C~ direct USB-UART',
  'UTF8: čćž €',
  'SIGNED: -42',
  'UNSIGNED: 4294967295',
  'FLOAT: 1.5',
  'BOOLEAN: True False',
  'CTILDE_CONSOLE_OK',
  '',
].join('\n');

// ESP-IDF's UART VFS expands C newlines to the console's CRLF wire format.
export const expectedConsoleWireFixture = expectedConsoleFixture.replaceAll('\n', '\r\n');

export function extractConsoleFixture(rawBytes) {
  const bytes = Buffer.from(rawBytes);
  const startMarker = Buffer.from('CTILDE_CONSOLE_BEGIN\r\n', 'utf8');
  const endMarker = Buffer.from('CTILDE_CONSOLE_OK\r\n', 'utf8');
  const start = bytes.indexOf(startMarker);
  if (start < 0)
    throw new Error('Raw UART bytes omitted CTILDE_CONSOLE_BEGIN.');
  const endStart = bytes.indexOf(endMarker, start);
  if (endStart < 0)
    throw new Error('Raw UART bytes omitted CTILDE_CONSOLE_OK.');
  const frame = bytes.subarray(start, endStart + endMarker.length);
  const text = new TextDecoder('utf-8', { fatal: true }).decode(frame);
  if (text !== expectedConsoleWireFixture)
    throw new Error(`Console fixture bytes differ from the expected UTF-8 sequence.\nExpected: ${JSON.stringify(expectedConsoleWireFixture)}\nActual: ${JSON.stringify(text)}`);
  return { text, byteLength: frame.length, bytesBase64: frame.toString('base64') };
}

export function validateUsbSerialDevice(device, expectedPort, expectedId) {
  if (device === null || typeof device !== 'object')
    throw new Error(`ESP32 serial port ${expectedPort} is not present.`);
  const deviceId = String(device.deviceId ?? '');
  const pnpDeviceId = String(device.pnpDeviceId ?? '');
  if (deviceId.toUpperCase() !== expectedPort.toUpperCase())
    throw new Error(`Serial device '${deviceId}' does not match expected port '${expectedPort}'.`);
  if (/BTH|BLUETOOTH/i.test(pnpDeviceId) || !pnpDeviceId.toUpperCase().includes(expectedId.toUpperCase()))
    throw new Error(`${expectedPort} is not the expected T-CAN485 USB-to-UART bridge (${expectedId}): ${pnpDeviceId}`);
  return { name: String(device.name ?? deviceId), pnpDeviceId };
}

export function parseMemoryLayoutTranscript(transcript) {
  const entries = {};
  for (const match of transcript.matchAll(/^CT_LAYOUT\s+(\S+)\s+([0-9 ]+)\s*$/gm)) {
    const values = match[2].trim().split(/\s+/).map(value => Number.parseInt(value, 10));
    if (values.some(value => !Number.isSafeInteger(value) || value < 0))
      throw new Error(`Invalid layout values for '${match[1]}'.`);
    entries[match[1]] = values;
  }
  for (const key of ['object', 'string', 'descriptor', 'vtable', 'totals']) {
    if (!(key in entries))
      throw new Error(`Memory-layout transcript omitted '${key}'.`);
  }
  if (!Object.keys(entries).some(key => key.startsWith('type:')) ||
      !Object.keys(entries).some(key => key.startsWith('array:')) ||
      !Object.keys(entries).some(key => key.startsWith('box:')))
    throw new Error('Memory-layout transcript must include representative type, array, and box layouts.');
  return Object.fromEntries(Object.entries(entries).sort(([left], [right]) => left.localeCompare(right)));
}

export function parseMemoryValidationTranscript(transcript) {
  const failure = /(?:FAILED|Guru Meditation|Task watchdog|panic(?:'ed)?|abort\(\)|CTILDE runtime error|Rebooting\.\.\.|\brst:|\bleak\b)/i.exec(transcript);
  if (failure !== null)
    throw new Error(`Memory-validation transcript contains failure text: ${failure[0]}.`);
  for (const marker of ['OOM class ok', 'OOM array ok', 'OOM box ok', 'OOM string ok', 'OOM recovery ok', 'CTILDE_MEMORY_OK']) {
    if (!transcript.includes(marker))
      throw new Error(`Memory-validation transcript omitted '${marker}'.`);
  }
  const baseline = /^MEMORY baseline\s+(\d+)\s+(\d+)\s+(\d+)\s*$/m.exec(transcript);
  const final = /^MEMORY final\s+(\d+)\s+(\d+)\s+(\d+)\s*$/m.exec(transcript);
  if (baseline === null || final === null)
    throw new Error('Memory-validation transcript omitted allocation counters.');
  const baselineValues = baseline.slice(1).map(value => Number.parseInt(value, 10));
  const finalValues = final.slice(1).map(value => Number.parseInt(value, 10));
  if (finalValues[0] !== baselineValues[0] || finalValues[1] !== baselineValues[1])
    throw new Error(`Live memory did not return to baseline (${baselineValues.slice(0, 2)} -> ${finalValues.slice(0, 2)}).`);
  if (finalValues[2] <= baselineValues[2])
    throw new Error('Allocation-failure recovery did not record subsequent successful allocations.');
  return {
    baseline: { liveAllocations: baselineValues[0], liveObjects: baselineValues[1], totalAllocations: baselineValues[2] },
    final: { liveAllocations: finalValues[0], liveObjects: finalValues[1], totalAllocations: finalValues[2] },
    layout: parseMemoryLayoutTranscript(transcript),
  };
}

export function parseObjectSymbols(output) {
  const symbols = [];
  for (const line of output.split(/\r?\n/)) {
    const match = /^\s*([0-9a-f]+)\s+\w+\s+O\s+(\S+)\s+([0-9a-f]+)\s+(ct_(?:d|v|sl)_[A-Za-z0-9_]+)\s*$/i.exec(line);
    if (match !== null)
      symbols.push({ name: match[4], section: match[2], size: Number.parseInt(match[3], 16) });
  }
  if (symbols.length === 0)
    throw new Error('Object-symbol output did not contain retained descriptor, vtable, or string-literal symbols.');
  const writable = symbols.filter(symbol => !/^\.(?:rodata|flash\.rodata)(?:\.|$)/.test(symbol.section));
  if (writable.length !== 0)
    throw new Error(`Immutable runtime symbol(s) were not placed in read-only storage: ${writable.map(symbol => `${symbol.name}:${symbol.section}`).join(', ')}.`);
  return {
    count: symbols.length,
    bytes: symbols.reduce((sum, symbol) => sum + symbol.size, 0),
    descriptors: symbols.filter(symbol => symbol.name.startsWith('ct_d_')).length,
    vtables: symbols.filter(symbol => symbol.name.startsWith('ct_v_')).length,
    literals: symbols.filter(symbol => symbol.name.startsWith('ct_sl_')).length,
  };
}

function growthLimit(value, minimum) {
  return value + Math.max(minimum, Math.ceil(value * 0.02));
}

export function createMemoryBaseline(tools, targets, hardware, layout) {
  const normalizedTargets = {};
  for (const [target, measurements] of Object.entries(targets)) {
    normalizedTargets[target] = {
      observed: measurements,
      maximum: {
        binaryBytes: growthLimit(measurements.binaryBytes, 1024),
        imageBytes: growthLimit(measurements.imageBytes, 1024),
        flashCode: growthLimit(measurements.flashCode, 1024),
        flashData: growthLimit(measurements.flashData, 1024),
        iram: growthLimit(measurements.iram, 512),
        dram: growthLimit(measurements.dram, 512),
      },
    };
  }
  return {
    version: 1,
    tools,
    targets: normalizedTargets,
    hardware: {
      observed: hardware,
      minimum: {
        freeHeap: hardware.freeHeap - 4096,
        minimumFreeHeap: hardware.minimumFreeHeap - 4096,
        stackHighWater: Math.max(1024, hardware.stackHighWater - 512),
      },
    },
    layout,
  };
}

export function updateMemoryBaseline(existing, actual) {
  const targets = {};
  for (const [name, entry] of Object.entries(existing?.targets ?? {}))
    targets[name] = entry.observed;
  Object.assign(targets, actual.targets ?? {});
  const hardware = actual.hardware ?? existing?.hardware?.observed;
  const layout = actual.layout ?? existing?.layout;
  if (hardware === undefined || layout === undefined)
    throw new Error('A baseline update requires physical hardware and layout measurements at least once.');
  return createMemoryBaseline(actual.tools ?? existing?.tools, targets, hardware, layout);
}

export function validateMemoryBaseline(baseline, actual) {
  if (baseline?.version !== 1)
    throw new Error('Unsupported ESP memory baseline version; use -AcceptMemoryBaseline to rebaseline.');
  for (const [name, expected] of Object.entries(baseline.tools)) {
    if (actual.tools?.[name] !== expected)
      throw new Error(`ESP memory baseline tool '${name}' differs ('${expected}' vs '${actual.tools?.[name]}'); use -AcceptMemoryBaseline with the accepted toolchain.`);
  }
  for (const [target, measured] of Object.entries(actual.targets ?? {})) {
    const expected = baseline.targets?.[target];
    if (expected === undefined)
      throw new Error(`ESP memory baseline omitted target '${target}'; use -AcceptMemoryBaseline to rebaseline.`);
    for (const [name, maximum] of Object.entries(expected.maximum)) {
      if (measured[name] > maximum)
        throw new Error(`${target} ${name} is ${measured[name]} bytes; budget is ${maximum}. Use -AcceptMemoryBaseline only after reviewing the increase.`);
    }
  }
  if (actual.hardware !== undefined) {
    for (const [name, minimum] of Object.entries(baseline.hardware.minimum)) {
      if (actual.hardware[name] < minimum)
        throw new Error(`Physical ESP32 ${name} is ${actual.hardware[name]} bytes; minimum budget is ${minimum}.`);
    }
  }
  if (actual.layout !== undefined && JSON.stringify(actual.layout) !== JSON.stringify(baseline.layout))
    throw new Error('Managed layout differs from the exact ABI baseline; use -AcceptMemoryBaseline only after an ABI review.');
  return true;
}

export class DapMessageFramer {
  #buffer = Buffer.alloc(0);

  push(chunk) {
    this.#buffer = Buffer.concat([this.#buffer, Buffer.from(chunk)]);
    const messages = [];
    while (true) {
      const headerEnd = this.#buffer.indexOf('\r\n\r\n');
      if (headerEnd < 0)
        break;
      const header = this.#buffer.subarray(0, headerEnd).toString('ascii');
      const match = /(?:^|\r\n)Content-Length:\s*(\d+)(?:\r\n|$)/i.exec(header);
      if (match === null)
        throw new Error(`DAP frame omitted Content-Length: ${header}`);
      const length = Number.parseInt(match[1], 10);
      const bodyStart = headerEnd + 4;
      if (this.#buffer.length < bodyStart + length)
        break;
      const body = this.#buffer.subarray(bodyStart, bodyStart + length).toString('utf8');
      messages.push(JSON.parse(body));
      this.#buffer = this.#buffer.subarray(bodyStart + length);
    }
    return messages;
  }
}

export class Utf8Transcript {
  #decoder = new StringDecoder('utf8');
  #text = '';

  push(chunk) {
    const value = this.#decoder.write(Buffer.from(chunk));
    this.#text += value;
    return value;
  }

  finish() {
    this.#text += this.#decoder.end();
    return this.#text;
  }

  get text() { return this.#text; }
}

export async function withTimeout(promise, milliseconds, description) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`Timed out after ${milliseconds} ms while ${description}.`)), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

export function serializeHardwareReport(report) {
  if (report === null || typeof report !== 'object' || Array.isArray(report))
    throw new Error('The hardware report must be an object.');
  return `${JSON.stringify(report, null, 2)}\n`;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
