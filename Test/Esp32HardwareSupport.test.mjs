import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  DapMessageFramer,
  Utf8Transcript,
  findUniqueSourceLine,
  parseFirmwareTranscript,
  parseIdfSize,
  parseRuntimeFailureTranscript,
  serializeHardwareReport,
  withTimeout,
} from './Esp32HardwareSupport.mjs';

test('source anchors must be unique', () => {
  assert.equal(findUniqueSourceLine('first\nunique anchor\nlast\n', 'unique anchor'), 2);
  assert.throws(() => findUniqueSourceLine('same\nsame\n', 'same'), /matched 2 lines/);
  assert.throws(() => findUniqueSourceLine('none\n', 'missing'), /matched 0 lines/);
});

test('firmware transcript extracts measurements and alternating transitions', () => {
  const markers = [
    'esp error: ESP_OK', 'C~ ESP-IDF hardware test', 'virtual: 42', 'delegate: 42',
    'function pointer: 42', 'timer64: ok', 'native buffer: 42', 'native utf8: ok',
    'opaque defer: ok', 'delegate context: 42', 'export: 42', 'threading: ok',
    'boxed: 7', 'exception: caught on ESP32', 'arc heap recovery: True', 'CTILDE_ESP_OK',
    'free heap: 297000', 'minimum free heap: 286000', 'stack high water: 6500', 'tick: 19',
  ];
  for (let index = 0; index < 25; index++)
    markers.push(`ws2812: ${index % 2 === 0 ? 'on' : 'off'}`);
  assert.deepEqual(parseFirmwareTranscript(markers.join('\n')), {
    freeHeap: 297000, minimumFreeHeap: 286000, stackHighWater: 6500, tick: 19, transitions: 25,
  });
  assert.throws(() => parseFirmwareTranscript(markers.concat('WS2812_UPDATE_FAILED').join('\n')), /failure text/);
  assert.throws(() => parseFirmwareTranscript(markers.concat('Rebooting...').join('\n')), /failure text/);
});

test('runtime failure requires the diagnostic and reset', () => {
  assert.deepEqual(parseRuntimeFailureTranscript('CTILDE_ESP_FAILURE_TEST\nCTN0001\nabort() was called\nrst: SW_CPU_RESET'), {
    runtimeCode: 'CTN0001', reset: 'SW_CPU_RESET',
  });
  assert.deepEqual(parseRuntimeFailureTranscript('CTILDE_ESP_FAILURE_TEST\nCTN0001\nabort() was called\nRebooting...'), {
    runtimeCode: 'CTN0001', reset: 'Rebooting',
  });
  assert.throws(() => parseRuntimeFailureTranscript('CTILDE_ESP_FAILURE_TEST\nCTN0001\nabort() was called'), /software-reset marker/);
  assert.throws(() => parseRuntimeFailureTranscript('CTILDE_ESP_FAILURE_TEST\nCTN0001\nRebooting...'), /abort/);
});

test('ESP-IDF size output is parsed', () => {
  const parsed = parseIdfSize('ctilde_tcan485.bin binary size 0x25c10 bytes.\nTotal image size: 154,525 bytes\n| Flash Code | 65,222 |\n| DRAM | 14,028 |');
  assert.equal(parsed.binaryBytes, 0x25c10);
  assert.equal(parsed.imageBytes, 154525);
  assert.deepEqual(parsed.sections, { flashCode: 65222, dram: 14028 });
});

test('DAP framing handles fragmented UTF-8 messages', () => {
  const message = JSON.stringify({ type: 'event', event: 'output', body: { output: 'C~ ✓' } });
  const frame = Buffer.from(`Content-Length: ${Buffer.byteLength(message)}\r\n\r\n${message}`);
  const framer = new DapMessageFramer();
  assert.deepEqual(framer.push(frame.subarray(0, 11)), []);
  assert.deepEqual(framer.push(frame.subarray(11)), [JSON.parse(message)]);
  const transcript = new Utf8Transcript();
  const utf8 = Buffer.from('žlutý');
  transcript.push(utf8.subarray(0, 2));
  transcript.push(utf8.subarray(2));
  assert.equal(transcript.finish(), 'žlutý');
});

test('timeouts reject with an actionable operation name', async () => {
  await assert.rejects(withTimeout(new Promise(() => {}), 5, 'waiting for hardware'), /waiting for hardware/);
});

test('hardware reports serialize as deterministic newline-terminated JSON', () => {
  const report = { version: 1, automatedPassed: true, nested: { value: 42 } };
  const serialized = serializeHardwareReport(report);
  assert.equal(serialized.endsWith('\n'), true);
  assert.deepEqual(JSON.parse(serialized), report);
  assert.equal(serializeHardwareReport(report), serialized);
  assert.throws(() => serializeHardwareReport(null), /must be an object/);
});

test('hardware runner writes UTF-8 without requiring PowerShell 7 encoding names', () => {
  const runner = readFileSync(new URL('./Test-Esp32Hardware.ps1', import.meta.url), 'utf8');
  assert.equal(runner.includes('-Encoding utf8NoBOM'), false);
  assert.match(runner, /\[IO\.File\]::WriteAllText/);
  assert.match(runner, /\$previousErrorActionPreference = \$ErrorActionPreference[\s\S]*\$ErrorActionPreference = "Continue"[\s\S]*\$exitCode = \$LASTEXITCODE[\s\S]*\$ErrorActionPreference = \$previousErrorActionPreference/);
});
