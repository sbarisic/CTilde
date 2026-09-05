import assert from 'node:assert/strict';
import test from 'node:test';
import {
  parseManagedShellMemory,
  parseManagedShellTaskManager,
  validateManagedShellMemory,
  validateManagedShellTaskManager,
} from './ManagedShellDiagnostics.mjs';

const memoryTranscript = `
RAM summary
  total=300000 used=120000 available=180000 used-percent=40%
  allocated-payload=110000 allocator-overhead=10000
  minimum-free=170000 peak-used=130000 largest-free-block=90000 fragmentation=50%
  allocated-blocks=30 free-blocks=10 total-blocks=40
Capability pools (overlap; do not sum)
  default: total=300000 used=120000 available=180000 largest=90000 minimum-free=170000
  8-bit: total=300000 used=120000 available=180000 largest=90000 minimum-free=170000
  32-bit: total=250000 used=100000 available=150000 largest=80000 minimum-free=140000
  internal: total=300000 used=120000 available=180000 largest=90000 minimum-free=170000
  DMA: total=200000 used=70000 available=130000 largest=60000 minimum-free=120000
  executable: total=100000 used=40000 available=60000 largest=30000 minimum-free=55000
  SPIRAM: not configured
Managed processes
  count=1 attributed-payload=4096
  pid=7 state=running module=examples.hello heap=4096 limit=65536 tasks=1
Managed modules
  count=1
  module=examples.hello version=1.0.0 load-refs=1 active-calls=1 live-allocations=8 stopping=no
FreeRTOS tasks
  count=2
  task=1 name=IDLE0 state=ready priority=0 affinity=0 stack-min=512
  task=4 name=examples.hello state=blocked priority=1 affinity=any stack-min=2048
LittleFS
  total=2031616 used=40960 available=1990656 used-percent=2%
Heap integrity
  ok
ct> `;

const taskManagerTranscript = `
Task manager
  sample-ms=250 cpu-scale=per-core cores=2 maximum=200.0%
  system-cpu=37.4% freertos-tasks=12 active-processes=2
  memory-basis=managed-payload/total-8bit-ram total-ram=300000 (excludes shared code and stacks)
  PID STATE MODULE THREADS HEAP LIMIT MEM% CPU STACK-MIN
  pid=7 state=running module=examples.hello threads=1 heap=4096 limit=65536 mem=1.3% cpu=31.2% stack-min=2048
  pid=8 state=starting module=examples.hello threads=1 heap=0 limit=65536 mem=0.0% cpu=n/a stack-min=n/a
ct> `;

test('ManagedShell memory transcript contains consistent diagnostics', () => {
  const report = validateManagedShellMemory(parseManagedShellMemory(memoryTranscript));
  assert.equal(report.processes.attributedPayload, 4096);
  assert.equal(report.processes.rows[0].module, 'examples.hello');
  assert.equal(report.modules.count, 1);
  assert.equal(report.modules.rows[0].liveAllocations, 8);
  assert.equal(report.capabilities.get('SPIRAM'), null);
});

test('ManagedShell memory transcript rejects missing and inconsistent data', () => {
  assert.throws(() => parseManagedShellMemory(memoryTranscript.replace('LittleFS\n', 'Filesystem\n')), /LittleFS/);
  assert.throws(() => validateManagedShellMemory(parseManagedShellMemory(
    memoryTranscript.replace('total-blocks=40', 'total-blocks=41'))), /block counts/);
  assert.throws(() => validateManagedShellMemory(parseManagedShellMemory(
    memoryTranscript.replace('attributed-payload=4096', 'attributed-payload=4095'))), /attributed payload/);
  assert.throws(() => validateManagedShellMemory(parseManagedShellMemory(
    memoryTranscript.replace('available=180000 used-percent', 'available=180001 used-percent'))), /Used RAM/);
  assert.throws(() => validateManagedShellMemory(parseManagedShellMemory(
    memoryTranscript.replace('task=4', 'task=1'))), /ordered|unique/);
  assert.throws(() => validateManagedShellMemory(parseManagedShellMemory(
    memoryTranscript.replace('SPIRAM: not configured', 'SPIRAM: total=1 used=0 available=1 largest=1 minimum-free=1'))), /SPIRAM/);
});

test('ManagedShell task manager transcript validates per-core CPU and partial rows', () => {
  const report = validateManagedShellTaskManager(parseManagedShellTaskManager(taskManagerTranscript));
  assert.equal(report.maximumCpu, 200);
  assert.equal(report.rows[0].cpu, 31.2);
  assert.equal(report.rows[0].memoryPercent, 1.3);
  assert.equal(report.rows[0].state, 'running');
  assert.equal(report.rows[1].cpu, null);
});

test('ManagedShell task manager rejects invalid CPU and row counts', () => {
  assert.throws(() => validateManagedShellTaskManager(parseManagedShellTaskManager(
    taskManagerTranscript.replace('mem=1.3%', 'mem=40.0%'))), /memory percentage/);
  assert.throws(() => validateManagedShellTaskManager(parseManagedShellTaskManager(
    taskManagerTranscript.replace('system-cpu=37.4%', 'system-cpu=201.0%'))), /System CPU/);
  assert.throws(() => validateManagedShellTaskManager(parseManagedShellTaskManager(
    taskManagerTranscript.replace('active-processes=2', 'active-processes=1'))), /process count/);
  assert.throws(() => validateManagedShellTaskManager(parseManagedShellTaskManager(
    taskManagerTranscript.replace('cpu=31.2%', 'cpu=101.0%'))), /Process CPU/);
  assert.throws(() => validateManagedShellTaskManager(parseManagedShellTaskManager(
    taskManagerTranscript.replace('state=running', 'state=exited'))), /inactive process/);
});
