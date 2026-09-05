const stripAnsi = text => text.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').replaceAll('\r', '');

function numberField(line, name) {
  const match = line.match(new RegExp(`(?:^|\\s)${name}=([0-9]+)(?:%|\\s|$)`));
  if (!match) throw new Error(`Missing numeric field '${name}' in '${line}'.`);
  return Number.parseInt(match[1], 10);
}

function textField(line, name) {
  const match = line.match(new RegExp(`(?:^|\\s)${name}=([^\\s]+)(?:\\s|$)`));
  if (!match) throw new Error(`Missing text field '${name}' in '${line}'.`);
  return match[1];
}

function limitField(line) {
  const value = textField(line, 'limit');
  if (value === 'unlimited') return null;
  if (!/^[0-9]+$/.test(value)) throw new Error(`Invalid heap limit '${value}'.`);
  return Number.parseInt(value, 10);
}

function section(lines, heading, nextHeading) {
  const start = lines.findIndex(line => line === heading);
  if (start < 0) throw new Error(`Missing '${heading}' section.`);
  const end = nextHeading === undefined ? lines.length : lines.findIndex((line, index) => index > start && line === nextHeading);
  return lines.slice(start + 1, end < 0 ? lines.length : end);
}

function reportLines(text, heading) {
  const normalized = stripAnsi(text);
  const start = normalized.lastIndexOf(heading);
  if (start < 0) throw new Error(`Missing '${heading}' report.`);
  return normalized.slice(start).split('\n').map(line => line.trim()).filter(Boolean);
}

export function parseManagedShellMemory(text) {
  const lines = reportLines(text, 'RAM summary');
  const ram = section(lines, 'RAM summary', 'Capability pools (overlap; do not sum)');
  const pools = section(lines, 'Capability pools (overlap; do not sum)', 'Managed processes');
  const processes = section(lines, 'Managed processes', 'Managed modules');
  const modules = section(lines, 'Managed modules', 'FreeRTOS tasks');
  const tasks = section(lines, 'FreeRTOS tasks', 'LittleFS');
  const littlefs = section(lines, 'LittleFS', 'Heap integrity');
  const integrity = section(lines, 'Heap integrity');
  if (ram.length < 4) throw new Error('RAM summary is incomplete.');
  const capabilityNames = ['default', '8-bit', '32-bit', 'internal', 'DMA', 'executable', 'SPIRAM'];
  const capabilities = new Map();
  for (const name of capabilityNames) {
    const row = pools.find(line => line.startsWith(`${name}:`));
    if (!row) throw new Error(`Missing capability row '${name}'.`);
    capabilities.set(name, row === `${name}: not configured` ? null : {
      total: numberField(row, 'total'),
      used: numberField(row, 'used'),
      available: numberField(row, 'available'),
      largest: numberField(row, 'largest'),
      minimumFree: numberField(row, 'minimum-free'),
    });
  }
  const processSummary = processes.find(line => line.startsWith('count='));
  const moduleSummary = modules.find(line => line.startsWith('count='));
  const taskSummary = tasks.find(line => line.startsWith('count='));
  if (!processSummary || !moduleSummary || !taskSummary) throw new Error('A diagnostics count row is missing.');
  const taskRows = tasks.filter(line => line.startsWith('task=')).map(line => ({
    number: numberField(line, 'task'),
    state: textField(line, 'state'),
    priority: numberField(line, 'priority'),
    affinity: textField(line, 'affinity'),
    stackMinimum: numberField(line, 'stack-min'),
  }));
  const processRows = processes.filter(line => line.startsWith('pid=')).map(line => ({
    pid: numberField(line, 'pid'),
    state: textField(line, 'state'),
    module: textField(line, 'module'),
    heap: numberField(line, 'heap'),
    limit: limitField(line),
    tasks: numberField(line, 'tasks'),
  }));
  const moduleRows = modules.filter(line => line.startsWith('module=')).map(line => ({
    module: textField(line, 'module'),
    version: textField(line, 'version'),
    loadReferences: numberField(line, 'load-refs'),
    activeCalls: numberField(line, 'active-calls'),
    liveAllocations: numberField(line, 'live-allocations'),
    stopping: textField(line, 'stopping'),
  }));
  if (integrity[0] !== 'ok' && integrity[0] !== 'corrupt') throw new Error('Heap integrity result is missing.');
  if (littlefs.length !== 1 || littlefs[0].startsWith('error:')) throw new Error('LittleFS measurements are unavailable.');
  return {
    ram: {
      total: numberField(ram[0], 'total'),
      used: numberField(ram[0], 'used'),
      available: numberField(ram[0], 'available'),
      allocatedPayload: numberField(ram[1], 'allocated-payload'),
      allocatorOverhead: numberField(ram[1], 'allocator-overhead'),
      minimumFree: numberField(ram[2], 'minimum-free'),
      peakUsed: numberField(ram[2], 'peak-used'),
      largest: numberField(ram[2], 'largest-free-block'),
      fragmentation: numberField(ram[2], 'fragmentation'),
      allocatedBlocks: numberField(ram[3], 'allocated-blocks'),
      freeBlocks: numberField(ram[3], 'free-blocks'),
      totalBlocks: numberField(ram[3], 'total-blocks'),
    },
    capabilities,
    processes: {
      count: numberField(processSummary, 'count'),
      attributedPayload: numberField(processSummary, 'attributed-payload'),
      rows: processRows,
    },
    modules: { count: numberField(moduleSummary, 'count'), rows: moduleRows },
    tasks: { count: numberField(taskSummary, 'count'), rows: taskRows },
    littlefs: {
      total: numberField(littlefs[0], 'total'),
      used: numberField(littlefs[0], 'used'),
      available: numberField(littlefs[0], 'available'),
    },
    integrity: integrity[0],
  };
}

export function validateManagedShellMemory(report) {
  if (report.ram.available > report.ram.total) throw new Error('Available RAM exceeds total RAM.');
  if (report.ram.used !== report.ram.total - report.ram.available) throw new Error('Used RAM is inconsistent.');
  if (report.ram.allocatedPayload + report.ram.allocatorOverhead !== report.ram.used)
    throw new Error('Allocator payload and overhead are inconsistent.');
  if (report.ram.peakUsed !== report.ram.total - report.ram.minimumFree) throw new Error('Peak RAM use is inconsistent.');
  if (report.ram.largest > report.ram.available) throw new Error('Largest free block exceeds available RAM.');
  if (report.ram.fragmentation < 0 || report.ram.fragmentation > 100) throw new Error('RAM fragmentation is invalid.');
  if (report.ram.allocatedBlocks + report.ram.freeBlocks !== report.ram.totalBlocks)
    throw new Error('Heap block counts are inconsistent.');
  if (report.littlefs.used > report.littlefs.total) throw new Error('LittleFS usage exceeds capacity.');
  if (report.littlefs.available !== report.littlefs.total - report.littlefs.used)
    throw new Error('LittleFS available space is inconsistent.');
  for (const [name, pool] of report.capabilities) {
    if (pool === null) continue;
    if (pool.available > pool.total || pool.largest > pool.available)
      throw new Error(`Capability pool '${name}' is inconsistent.`);
  }
  if (report.processes.rows.length !== report.processes.count)
    throw new Error('Managed process count does not match process rows.');
  const attributedPayload = report.processes.rows.reduce((total, process) => total + process.heap, 0);
  if (attributedPayload !== report.processes.attributedPayload)
    throw new Error('Managed process attributed payload is inconsistent.');
  if (report.modules.rows.length !== report.modules.count)
    throw new Error('Managed module count does not match module rows.');
  if (report.modules.rows.some(module => module.stopping !== 'yes' && module.stopping !== 'no'))
    throw new Error('Managed module stopping state is invalid.');
  if (report.tasks.rows.length !== report.tasks.count) throw new Error('FreeRTOS task count does not match task rows.');
  let prior = -1;
  const seen = new Set();
  for (const task of report.tasks.rows) {
    if (task.number <= prior) throw new Error('FreeRTOS tasks are not strictly ordered.');
    if (seen.has(task.number)) throw new Error('FreeRTOS task numbers are not unique.');
    if (task.stackMinimum < 0) throw new Error('FreeRTOS stack headroom is negative.');
    prior = task.number;
    seen.add(task.number);
  }
  if (report.capabilities.get('SPIRAM') !== null) throw new Error('SPIRAM should be reported as not configured.');
  if (report.integrity !== 'ok') throw new Error('Heap integrity is not ok.');
  return report;
}

function cpuField(line, name) {
  const match = line.match(new RegExp(`(?:^|\\s)${name}=(n/a|[0-9]+\\.[0-9]+)%?(?:\\s|$)`));
  if (!match) throw new Error(`Missing CPU field '${name}' in '${line}'.`);
  return match[1] === 'n/a' ? null : Number.parseFloat(match[1]);
}

export function parseManagedShellTaskManager(text) {
  const lines = reportLines(text, 'Task manager');
  if (lines.length < 4) throw new Error('Task manager report is incomplete.');
  const configuration = lines.find(line => line.startsWith('sample-ms='));
  const summary = lines.find(line => line.startsWith('system-cpu='));
  const heading = lines.findIndex(line => line === 'PID STATE MODULE THREADS HEAP LIMIT MEM% CPU STACK-MIN');
  const memory = lines.find(line => line.startsWith('memory-basis=managed-payload/total-8bit-ram '));
  if (!configuration || !summary || !memory || heading < 0) throw new Error('Task manager header is incomplete.');
  const rows = lines.slice(heading + 1).filter(line => line.startsWith('pid=')).map(line => ({
    pid: numberField(line, 'pid'),
    state: textField(line, 'state'),
    module: textField(line, 'module'),
    threads: numberField(line, 'threads'),
    heap: numberField(line, 'heap'),
    limit: limitField(line),
    memoryPercent: cpuField(line, 'mem'),
    cpu: cpuField(line, 'cpu'),
    stackMinimum: line.includes('stack-min=n/a') ? null : numberField(line, 'stack-min'),
    line,
  }));
  return {
    sampleMilliseconds: numberField(configuration, 'sample-ms'),
    totalRam: numberField(memory, 'total-ram'),
    cores: numberField(configuration, 'cores'),
    maximumCpu: cpuField(configuration, 'maximum'),
    systemCpu: cpuField(summary, 'system-cpu'),
    freeRtosTasks: numberField(summary, 'freertos-tasks'),
    activeProcesses: numberField(summary, 'active-processes'),
    rows,
  };
}

export function validateManagedShellTaskManager(report) {
  if (report.sampleMilliseconds !== 250) throw new Error('Unexpected task manager sampling interval.');
  if (report.cores !== 2 || report.maximumCpu !== 200) throw new Error('Unexpected per-core CPU scale.');
  if (report.systemCpu !== null && (report.systemCpu < 0 || report.systemCpu > report.maximumCpu))
    throw new Error('System CPU is outside the configured range.');
  if (report.rows.length !== report.activeProcesses) throw new Error('Active process count does not match process rows.');
  const seen = new Set();
  for (const row of report.rows) {
    const expectedMemory = report.totalRam === 0 ? null : Math.floor(row.heap * 1000 / report.totalRam) / 10;
    if (row.memoryPercent !== expectedMemory) throw new Error('Process memory percentage does not match managed payload.');
    if (seen.has(row.pid)) throw new Error('Task manager process IDs are not unique.');
    if (!['starting', 'running', 'cancelling'].includes(row.state))
      throw new Error('Task manager contains an inactive process.');
    if (row.threads < 1) throw new Error('An active process has no threads.');
    if (row.cpu !== null && (row.cpu < 0 || row.cpu > row.threads * 100))
      throw new Error('Process CPU exceeds its per-thread range.');
    if (row.stackMinimum !== null && row.stackMinimum < 0) throw new Error('Process stack headroom is negative.');
    if ((row.cpu === null) !== (row.stackMinimum === null)) throw new Error('Partial process measurements were reported as complete.');
    seen.add(row.pid);
  }
  return report;
}
