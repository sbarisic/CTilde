import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  DapMessageFramer,
  Utf8Transcript,
  findUniqueSourceLine,
  createMemoryBaseline,
  expectedConsoleFixture,
  expectedConsoleWireFixture,
  extractConsoleFixture,
  parseFirmwareTranscript,
  parseIdfSize,
  parseMemoryLayoutTranscript,
  parseMemoryValidationTranscript,
  parseObjectSymbols,
  parseRuntimeFailureTranscript,
  serializeHardwareReport,
  withTimeout,
  validateMemoryBaseline,
  updateMemoryBaseline,
  validateUsbSerialDevice,
} from './Esp32HardwareSupport.mjs';

test('source anchors must be unique', () => {
  assert.equal(findUniqueSourceLine('first\nunique anchor\nlast\n', 'unique anchor'), 2);
  assert.throws(() => findUniqueSourceLine('same\nsame\n', 'same'), /matched 2 lines/);
  assert.throws(() => findUniqueSourceLine('none\n', 'missing'), /matched 0 lines/);
});

test('firmware transcript extracts measurements and alternating transitions', () => {
  const markers = [
    'esp error: ESP_OK', 'C~ ESP-IDF hardware test', 'virtual: 42', 'delegate: 42',
    'function pointer: 42', 'timer64: ok', 'generated bindings: ok', 'wifi: not configured', 'native buffer: 42', 'native utf8: ok',
    'opaque defer: ok', 'delegate context: 42', 'export: 42', 'threading: ok', 'draft15 concurrency: ok',
    'boxed: 7', 'exception: caught on ESP32', 'arc heap recovery: True', 'CTILDE_ESP_OK',
    'free heap: 297000', 'minimum free heap: 286000', 'stack high water: 6500', 'tick: 19',
  ];
  for (let index = 0; index < 25; index++)
    markers.push(`ws2812: ${index % 2 === 0 ? 'on' : 'off'}`);
  assert.deepEqual(parseFirmwareTranscript(markers.join('\n')), {
    freeHeap: 297000, minimumFreeHeap: 286000, stackHighWater: 6500, tick: 19, transitions: 25,
  });
  const configuredMarkers = markers.map(marker => marker === 'wifi: not configured' ? 'generated wifi/http bindings: ok' : marker);
  assert.equal(parseFirmwareTranscript(configuredMarkers.join('\n')).transitions, 25);
  assert.throws(() => parseFirmwareTranscript(markers.filter(marker => marker !== 'wifi: not configured').join('\n')), /offline fallback marker/);
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

test('raw UART console fixture requires exact UTF-8 bytes', () => {
  const bytes = Buffer.concat([Buffer.from([0xff, 0x00]), Buffer.from(expectedConsoleWireFixture), Buffer.from('later')]);
  const parsed = extractConsoleFixture(bytes);
  assert.equal(parsed.text, expectedConsoleWireFixture);
  assert.equal(parsed.byteLength, Buffer.byteLength(expectedConsoleWireFixture));
  assert.throws(() => extractConsoleFixture(Buffer.from(expectedConsoleWireFixture.replace('čćž', 'ccz'))), /differ/);
  assert.throws(() => extractConsoleFixture(Buffer.from('missing')), /omitted/);
});

test('COM port validation accepts only the configured USB-to-UART bridge', () => {
  const device = { name: 'USB Serial Device (COM4)', deviceId: 'COM4', pnpDeviceId: 'USB\\VID_1A86&PID_55D4\\576E013427' };
  assert.deepEqual(validateUsbSerialDevice(device, 'COM4', 'VID_1A86&PID_55D4'), {
    name: device.name,
    pnpDeviceId: device.pnpDeviceId,
  });
  assert.throws(() => validateUsbSerialDevice({ ...device, pnpDeviceId: 'BTHENUM\\DEVICE' }, 'COM4', 'VID_1A86&PID_55D4'), /not the expected/);
  assert.throws(() => validateUsbSerialDevice({ ...device, deviceId: 'COM7' }, 'COM4', 'VID_1A86&PID_55D4'), /does not match/);
  assert.throws(() => validateUsbSerialDevice(null, 'COM4', 'VID_1A86&PID_55D4'), /not present/);
});

test('memory validation parses exact layouts and recovered ownership', () => {
  const transcript = [
    'CT_LAYOUT object 16 4', 'CT_LAYOUT string 24 4 20', 'CT_LAYOUT descriptor 28 4',
    'CT_LAYOUT vtable 8 4', 'CT_LAYOUT type:Example 20 4', 'CT_LAYOUT array:int 20 4 16 4',
    'CT_LAYOUT box:int 20 4', 'CT_LAYOUT totals 280 80 52',
    'OOM class ok', 'OOM array ok', 'OOM box ok', 'OOM string ok', 'OOM recovery ok',
    'MEMORY baseline 0 0 10', 'MEMORY final 0 0 14', 'CTILDE_MEMORY_OK',
  ].join('\n');
  const result = parseMemoryValidationTranscript(transcript);
  assert.deepEqual(result.layout.object, [16, 4]);
  assert.equal(result.final.totalAllocations, 14);
  assert.throws(() => parseMemoryValidationTranscript(transcript.replace('MEMORY final 0 0 14', 'MEMORY final 1 0 14')), /return to baseline/);
  assert.throws(() => parseMemoryValidationTranscript(`${transcript}\nleak detected`), /failure text/);
});

test('ELF immutable symbols must reside in read-only sections', () => {
  const parsed = parseObjectSymbols('3f400020 g     O .rodata.ct_d_x 0000001c ct_d_x\n3f400040 l     O .rodata.ct_v_x 00000008 ct_v_x\n3f400048 l     O .flash.rodata.ct_sl_x 00000018 ct_sl_x');
  assert.deepEqual(parsed, { count: 3, bytes: 60, descriptors: 1, vtables: 1, literals: 1 });
  assert.throws(() => parseObjectSymbols('3ffb0020 g     O .data.ct_d_x 0000001c ct_d_x'), /read-only/);
});

test('balanced memory budgets enforce growth, runtime floors, layouts, and toolchains', () => {
  const tools = { idf: '6.0.2', gcc: '15.2.0' };
  const target = { binaryBytes: 100000, imageBytes: 99000, flashCode: 50000, flashData: 20000, iram: 10000, dram: 12000 };
  const hardware = { freeHeap: 290000, minimumFreeHeap: 280000, stackHighWater: 6000 };
  const layout = { object: [16, 4] };
  const baseline = createMemoryBaseline(tools, { esp32: target }, hardware, layout);
  assert.equal(baseline.targets.esp32.maximum.binaryBytes, 102000);
  assert.equal(baseline.targets.esp32.maximum.iram, 10512);
  assert.equal(validateMemoryBaseline(baseline, { tools, targets: { esp32: target }, hardware, layout }), true);
  assert.throws(() => validateMemoryBaseline(baseline, { tools: { ...tools, gcc: 'new' }, targets: { esp32: target } }), /AcceptMemoryBaseline/);
  assert.throws(() => validateMemoryBaseline(baseline, { tools, targets: { esp32: { ...target, dram: 13000 } } }), /budget/);
  assert.throws(() => validateMemoryBaseline(baseline, { tools, targets: { esp32: target }, hardware: { ...hardware, freeHeap: 1 } }), /minimum budget/);
  assert.throws(() => validateMemoryBaseline(baseline, { tools, targets: { esp32: target }, layout: { object: [20, 4] } }), /ABI baseline/);
  const updated = updateMemoryBaseline(baseline, { tools, targets: { esp32c3: { ...target, dram: 11000 } } });
  assert.deepEqual(Object.keys(updated.targets), ['esp32', 'esp32c3']);
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

test('Wi-Fi hardware runner splats named build parameters', () => {
  const runner = readFileSync(new URL('./Test-Esp32Wifi.ps1', import.meta.url), 'utf8');
  assert.match(runner, /\$buildParameters = @\{/);
  assert.match(runner, /IdfPath = \$IdfPath/);
  assert.match(runner, /Target = "esp32"/);
  assert.match(runner, /& \$buildScript @buildParameters/);
  assert.doesNotMatch(runner, /& \$buildScript @arguments/);
  assert.match(runner, /sys\.stdout\.buffer\.write\(chunk\)/);
  assert.match(runner, /sys\.stdout\.buffer\.flush\(\)/);
  assert.doesNotMatch(runner, /sys\.stdout\.buffer\.write\(data\)/);
  assert.match(runner, /Find-ByteSequence \$bytes \$applicationMarker/);
  assert.match(runner, /\$utf8\.GetString\(\$applicationBytes\)/);
  assert.match(runner, /\$freeAfter \+ 8192 -lt \$freeBefore/);
  assert.match(runner, /Restored the original TCan485 firmware/);
  assert.doesNotMatch(runner, /HTTPS success marker was not received\.`n\$transcript/);
});

test('visual confirmation is requested while the release workload is still running', () => {
  const runner = readFileSync(new URL('./Test-Esp32Hardware.ps1', import.meta.url), 'utf8');
  const prompt = runner.indexOf('Did the onboard T-CAN485 WS2812 visibly alternate');
  const memoryFixture = runner.indexOf('=== Managed layout and allocation failure ===');
  assert.ok(prompt >= 0 && prompt < memoryFixture);
  assert.equal(runner.includes('throw "Visible WS2812 confirmation was not provided."'), false);
});
