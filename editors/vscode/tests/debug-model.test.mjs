import assert from 'node:assert/strict';
import test from 'node:test';
import { buildEnabledSiteWords, debugMemoryChanges, decodeDebugMemoryField, encodeDebugControlValue, espTrapInstructionSize, espTrapResumeExpression, findExecutableSite, gdbResumeCommand, isTruthyGdbValue, parseHitCondition, patchDebugBitmap, patchDebugMemory, resolveLogicalFrameSite, runEspDetachSequence, stripInternalProbeConsole, TargetConsoleBuffer } from '../out/debugModel.js';

test('logical breakpoint bitmaps support more than ESP instruction breakpoint limits', () => {
  assert.deepEqual(buildEnabledSiteWords(70, [0, 1, 31, 32, 69]), [0x80000003, 1, 32]);
  assert.throws(() => buildEnabledSiteWords(2, [2]), /Invalid C~ debug site/);
});

test('source breakpoints relocate only within their containing method', () => {
  const functions = [
    { source: { file: 'src/Program.ct', line: 2, column: 1 }, sites: [
      { id: 0, kind: 'entry', source: { file: 'src/Program.ct', line: 2, column: 1 } },
      { id: 1, kind: 'statement', source: { file: 'src/Program.ct', line: 5, column: 9 } },
    ] },
    { source: { file: 'src/Program.ct', line: 10, column: 1 }, sites: [
      { id: 2, kind: 'entry', source: { file: 'src/Program.ct', line: 10, column: 1 } },
      { id: 3, kind: 'statement', source: { file: 'src/Program.ct', line: 12, column: 9 } },
    ] },
  ];
  assert.equal(findExecutableSite(functions, 'src/Program.ct', 4)?.site.id, 1);
  assert.equal(findExecutableSite(functions, 'src/Program.ct', 11)?.site.id, 3);
  assert.equal(findExecutableSite(functions, 'src/Missing.ct', 1), undefined);
});

test('the active logical probe overrides the native return-address line', () => {
  const sites = [
    { id: 1, kind: 'statement', source: { file: 'Program.ct', line: 266, column: 9 } },
    { id: 2, kind: 'statement', source: { file: 'Program.ct', line: 268, column: 9 } },
  ];
  assert.equal(resolveLogicalFrameSite(sites, 268)?.id, 2);
  assert.equal(resolveLogicalFrameSite(sites, 268, sites[0])?.id, 1);
});

test('adapter-owned condition and hit-count parsing is deterministic', () => {
  assert.equal(parseHitCondition('4'), 4);
  assert.equal(parseHitCondition('0'), undefined);
  assert.equal(parseHitCondition('>= 4'), undefined);
  assert.equal(isTruthyGdbValue('1'), true);
  assert.equal(isTruthyGdbValue('0x0'), false);
  assert.equal(isTruthyGdbValue('(nil)'), false);
});

test('ESP logical traps advance by the target instruction width', () => {
    assert.equal(espTrapInstructionSize('esp32'), 3);
    assert.equal(espTrapInstructionSize('esp32s2'), 3);
    assert.equal(espTrapInstructionSize('esp32s3'), 3);
    assert.equal(espTrapInstructionSize('esp32c3'), 4);
    assert.equal(espTrapInstructionSize('esp32c6'), 4);
    assert.equal(espTrapResumeExpression('esp32'),
      '((esp_gdbstub_frame_t*)running_task_frame)->pc += 3');
    assert.equal(espTrapResumeExpression('esp32c3'),
      '((esp_gdbstub_frame_t*)running_task_frame)->mepc += 4');
    assert.equal(gdbResumeCommand(true, 8), '-exec-continue');
    assert.equal(gdbResumeCommand(false, 8), '-exec-continue --thread 8');
    assert.equal(encodeDebugControlValue(1, 4), '01000000');
    assert.equal(encodeDebugControlValue('(uintptr_t)0x3ffb1234', 4), '3412fb3f');
    assert.equal(encodeDebugControlValue('0x123456789abcdef0', 8), 'f0debc9a78563412');
});

test('internal logical-probe console reports are removed without hiding unrelated output', () => {
  const output = [
    'target warning remains visible\n',
    'Thread 1 received signal SIGTRAP, Trace/breakpoint trap.\n',
    'ct_debug_trap () at E:/Project/main/generated/ctilde_runtime.c:299\n',
    '299        esp_cpu_dbgr_break();\n',
    'warning: multi-threaded target stopped without sending a thread-id, using first non-exited thread\n',
    'another diagnostic remains visible\n',
  ].join('');
  assert.equal(stripInternalProbeConsole(output), 'target warning remains visible\nanother diagnostic remains visible\n');
  assert.equal(stripInternalProbeConsole('Program received signal SIGTRAP, Trace/breakpoint trap.\nct_debug_trap () at /tmp/ctilde_runtime.c:410\n410 (void)raise(SIGTRAP);\n'), '');
  assert.equal(stripInternalProbeConsole('Program received signal SIGSEGV, Segmentation fault.\n'),
    'Program received signal SIGSEGV, Segmentation fault.\n');
  assert.equal(stripInternalProbeConsole('user wrote ct_debug_trap() in a message\n'),
    'user wrote ct_debug_trap() in a message\n');
});

test('target console buffering handles fragmented probe output, diagnostic mode, and cleanup', () => {
  const hidden = new TargetConsoleBuffer();
  hidden.append('Thread 1 received signal SIGTRAP, Trace/');
  hidden.append('breakpoint trap.\nct_debug_trap () at C:/generated/ctilde_runtime.c:299\n');
  hidden.append('299 esp_cpu_dbgr_break();\nunrelated warning\n');
  assert.equal(hidden.finish(true, false), 'unrelated warning\n');

  const visible = new TargetConsoleBuffer();
  visible.append('Thread 1 received signal SIGTRAP, Trace/breakpoint trap.\n');
  assert.match(visible.finish(true, true), /SIGTRAP/);

  const native = new TargetConsoleBuffer();
  native.append('Program received signal SIGSEGV, Segmentation fault.\n');
  assert.match(native.finish(false, false), /SIGSEGV/);

  const cleared = new TargetConsoleBuffer();
  cleared.append('stale stop output');
  cleared.clear();
  assert.equal(cleared.finish(false, false), '');
});

test('debug control images decode, patch, and coalesce target memory writes', () => {
  const layout = {
    pointerSize: 4,
    size: 24,
    enabledOffset: 16,
    fields: {
      SessionActive: { offset: 0, width: 4 },
      SelectedThread: { offset: 4, width: 4 },
      CurrentReason: { offset: 8, width: 4 },
    },
  };
  const empty = Buffer.alloc(layout.size).toString('hex');
  let changed = patchDebugMemory(empty, layout, { SessionActive: 1, SelectedThread: '0x3ffb1234', CurrentReason: 7 });
  changed = patchDebugBitmap(changed, layout, [0x80000001, 2]);
  assert.equal(decodeDebugMemoryField(changed, layout, 'SelectedThread'), 0x3ffb1234n);
  assert.equal(decodeDebugMemoryField(changed, layout, 'CurrentReason'), 7n);
  assert.deepEqual(debugMemoryChanges(empty, changed), [{ offset: 0, contents: changed.slice(0, 42) }]);
});

test('console filtering streams ordinary lines while retaining possible trap fragments', () => {
  const output = new TargetConsoleBuffer();
  output.append('application one\napplication two\napplication three\n');
  assert.equal(output.drain(false), 'application one\n');
  output.append('Thread 1 received signal SIGTRAP, Trace/breakpoint trap.\n');
  output.append('ct_debug_trap () at C:/generated/ctilde_runtime.c:299\n299 esp_cpu_dbgr_break();\n');
  assert.equal(output.finish(true, false), 'application two\napplication three\n');
});

test('ESP detach interrupts, advances an unhandled logical trap once, clears state, then continues', async () => {
  const calls = [];
  await runEspDetachSequence({
    running: true,
    trapAlreadyAdvanced: false,
    interrupt: async () => calls.push('interrupt'),
    readLogicalReason: async () => { calls.push('reason'); return 1; },
    advanceLogicalTrap: async () => calls.push('advance'),
    removeNativeBreakpoints: async () => calls.push('remove-native'),
    clearLogicalControl: async () => calls.push('clear-logical'),
    continueWithoutDebugger: async () => calls.push('continue'),
  });
  assert.deepEqual(calls, ['interrupt', 'reason', 'advance', 'remove-native', 'clear-logical', 'continue']);

  calls.length = 0;
  await runEspDetachSequence({
    running: false,
    trapAlreadyAdvanced: true,
    interrupt: async () => calls.push('interrupt'),
    readLogicalReason: async () => { calls.push('reason'); return 1; },
    advanceLogicalTrap: async () => calls.push('advance'),
    removeNativeBreakpoints: async () => calls.push('remove-native'),
    clearLogicalControl: async () => calls.push('clear-logical'),
    continueWithoutDebugger: async () => calls.push('continue'),
  });
  assert.deepEqual(calls, ['reason', 'remove-native', 'clear-logical', 'continue']);
});
