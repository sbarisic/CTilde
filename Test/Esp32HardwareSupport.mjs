import { StringDecoder } from 'node:string_decoder';

export const requiredFirmwareMarkers = [
  'esp error: ESP_OK',
  'C~ ESP-IDF hardware test',
  'virtual: 42',
  'delegate: 42',
  'function pointer: 42',
  'timer64: ok',
  'native buffer: 42',
  'native utf8: ok',
  'opaque defer: ok',
  'delegate context: 42',
  'export: 42',
  'threading: ok',
  'boxed: 7',
  'exception: caught on ESP32',
  'arc heap recovery: True',
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
