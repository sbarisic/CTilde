#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { DapMessageFramer, resolveEsp32DebugSourceLines, withTimeout } from './Esp32HardwareSupport.mjs';

class DapClient {
  #child;
  #framer = new DapMessageFramer();
  #sequence = 1;
  #pending = new Map();
  #events = [];
  #waiters = [];
  targetOutput = '';
  targetStdout = '';

  constructor(adapter) {
    this.#child = spawn(process.execPath, [adapter], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.#child.stdout.on('data', chunk => {
      for (const message of this.#framer.push(chunk))
        this.#receive(message);
    });
    this.#child.stderr.setEncoding('utf8');
    this.#child.stderr.on('data', text => { this.targetOutput += `[adapter stderr] ${text}`; });
    this.#child.on('exit', (code, signal) => {
      const error = new Error(`C~ debug adapter exited with code ${code ?? 'null'} signal ${signal ?? 'none'}.`);
      for (const pending of this.#pending.values()) pending.reject(error);
      for (const waiter of this.#waiters) waiter.reject(error);
      this.#pending.clear();
      this.#waiters.length = 0;
    });
  }

  async request(command, args = {}, timeout = 10000) {
    const seq = this.#sequence++;
    const body = JSON.stringify({ seq, type: 'request', command, arguments: args });
    const promise = new Promise((resolve, reject) => this.#pending.set(seq, { resolve, reject }));
    this.#child.stdin.write(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`);
    const response = await withTimeout(promise, timeout, `waiting for DAP ${command}`);
    if (!response.success)
      throw new Error(`${command} failed: ${response.message ?? JSON.stringify(response.body)}`);
    return response;
  }

  waitEvent(event, predicate = () => true, timeout = 10000) {
    const existing = this.#events.findIndex(candidate => candidate.event === event && predicate(candidate));
    if (existing >= 0)
      return Promise.resolve(this.#events.splice(existing, 1)[0]);
    const promise = new Promise((resolve, reject) => this.#waiters.push({ event, predicate, resolve, reject }));
    return withTimeout(promise, timeout, `waiting for DAP ${event} event`);
  }

  waitStopped(timeout = 10000) { return this.waitEvent('stopped', () => true, timeout); }

  close() {
    this.#child.stdin.end();
    setTimeout(() => { if (!this.#child.killed) this.#child.kill(); }, 1000).unref();
  }

  #receive(message) {
    if (message.type === 'response') {
      const pending = this.#pending.get(message.request_seq);
      if (pending !== undefined) {
        this.#pending.delete(message.request_seq);
        pending.resolve(message);
      }
      return;
    }
    if (message.type !== 'event')
      return;
    if (message.event === 'output') {
      const category = message.body?.category ?? 'console';
      const output = message.body?.output ?? '';
      this.targetOutput += `[${category}] ${output}`;
      if (category === 'stdout')
        this.targetStdout += output;
    }
    const waiterIndex = this.#waiters.findIndex(waiter => waiter.event === message.event && waiter.predicate(message));
    if (waiterIndex >= 0) {
      const [waiter] = this.#waiters.splice(waiterIndex, 1);
      waiter.resolve(message);
    } else {
      this.#events.push(message);
    }
  }
}

const options = parseArguments(process.argv.slice(2));
const sourcePath = path.resolve(requiredOption('source'));
const adapterPath = path.resolve(requiredOption('adapter'));
const descriptorPath = path.resolve(requiredOption('descriptor'));
const reportPath = path.resolve(requiredOption('report'));
const serialPort = options.get('port') ?? 'COM4';
const baudRate = Number.parseInt(options.get('baud') ?? '460800', 10);
const source = readFileSync(sourcePath, 'utf8');
const lines = resolveEsp32DebugSourceLines(source);

const result = {
  passed: false,
  serialPort,
  baudRate,
  lines,
  checks: {},
  timingsMs: {},
  output: '',
};

const client = new DapClient(adapterPath);
try {
  await timed('initialize', () => client.request('initialize', {
    clientID: 'ctilde-hardware-acceptance', adapterID: 'ctilde', linesStartAt1: true, columnsStartAt1: true,
    pathFormat: 'path', supportsVariableType: true, supportsVariablePaging: true, supportsRunInTerminalRequest: false,
  }));
  await timed('launch', () => client.request('launch', {
    request: 'launch', debugTarget: descriptorPath, cwd: path.dirname(sourcePath), serialPort, baudRate,
    stopAtEntry: true, showRuntimeFrames: false, memoryDiagnostics: 'guarded',
  }, 30000));
  await client.waitEvent('initialized', () => true, 10000);

  await setSourceBreakpoints(Object.values(lines));
  await client.request('setExceptionBreakpoints', { filters: ['ctilde-thrown', 'ctilde-fatal'] });
  await client.request('configurationDone', {}, 30000);

  let stop = await timed('startupStop', () => client.waitStopped(30000));
  assert(stop.body.reason === 'entry', `Expected the pre-initialization entry stop, got '${stop.body.reason}'.`);
  result.checks.startupStop = true;

  await resume();
  stop = await waitForLine(lines.firstStatement, 30000);
  result.checks.firstStatement = true;
  const firstFrame = await topFrame(stop.body.threadId);
  await timed('stepOver', () => client.request('next', { threadId: stop.body.threadId }));
  const stepped = await timed('stepOverStop', () => client.waitStopped(15000));
  const steppedFrame = await topFrame(stepped.body.threadId);
  assert(steppedFrame.line !== firstFrame.line, 'Step Over did not advance to a different C~ source line.');
  assertSourceFrame(steppedFrame);
  result.checks.stepOver = { from: firstFrame.line, to: steppedFrame.line };

  await resume(stepped.body.threadId);
  stop = await waitForLine(lines.exerciseCall, 45000);
  await timed('stepInto', () => client.request('stepIn', { threadId: stop.body.threadId }));
  stop = await timed('stepIntoStop', () => client.waitStopped(15000));
  let frame = await topFrame(stop.body.threadId);
  assert(frame.name.includes('ExerciseArc'), `Step Into stopped in '${frame.name}', not ExerciseArc.`);
  assertSourceFrame(frame);
  result.checks.stepInto = frame.name;

  await resume(stop.body.threadId);
  stop = await waitForLine(lines.arcObject, 20000);
  frame = await topFrame(stop.body.threadId);
  const threads = (await client.request('threads')).body.threads ?? [];
  assert(threads.length >= 3, `Expected at least three FreeRTOS tasks, got ${threads.length}.`);
  result.checks.threadCount = threads.length;

  const scopes = (await client.request('scopes', { frameId: frame.id })).body.scopes;
  const localsScope = requiredNamed(scopes, 'Locals');
  const runtimeScope = requiredNamed(scopes, 'C~ Runtime');
  const locals = await variables(localsScope.variablesReference);
  const node = requiredNamed(locals, 'node');
  assert(node.value !== 'null' && node.variablesReference > 0, 'The initialized ArcNode local is not inspectable.');
  const nodeChildren = await variables(node.variablesReference);
  const objectRuntime = requiredNamed(nodeChildren, '$runtime');
  const objectMetadata = await variables(objectRuntime.variablesReference);
  assert(requiredNamed(objectMetadata, 'Canary').value === 'intact', 'Guarded ARC canary is not intact.');
  const refCount = requiredNamed(objectMetadata, 'RefCount');
  assert(Number.parseInt(refCount.value, 10) > 0, `ArcNode RefCount is invalid: ${refCount.value}.`);

  const runtime = await variables(runtimeScope.variablesReference);
  const liveCount = Number.parseInt(requiredNamed(runtime, 'Live object count').value, 10);
  assert(liveCount > 0, `Expected active ARC objects inside ExerciseArc, got ${liveCount}.`);
  const liveObjects = requiredNamed(runtime, 'Live objects');
  const livePage = await variables(liveObjects.variablesReference, 0, 1);
  assert(livePage.length > 0, 'The ARC live-object registry could not be expanded.');
  result.checks.arcActive = { liveCount, refCount: Number.parseInt(refCount.value, 10), canary: 'intact' };

  const watchInfo = (await client.request('dataBreakpointInfo', {
    variablesReference: objectRuntime.variablesReference, name: 'RefCount', frameId: frame.id,
  })).body;
  assert(typeof watchInfo.dataId === 'string', `RefCount did not provide a data-breakpoint ID: ${watchInfo.description}.`);
  let watchResult = (await client.request('setDataBreakpoints', {
    breakpoints: [{ dataId: watchInfo.dataId, accessType: 'write' }],
  })).body.breakpoints[0];
  assert(watchResult.verified, `RefCount hardware watchpoint was rejected: ${watchResult.message ?? 'unknown error'}.`);
  await setSourceBreakpoints([]);
  await resume(stop.body.threadId);
  stop = await timed('refCountWatchStop', () => waitForReason('data breakpoint', 45000));
  result.checks.refCountWatchpoint = true;
  await client.request('setDataBreakpoints', { breakpoints: [] });

  await setSourceBreakpoints([lines.arcIterationEnd]);
  await resume(stop.body.threadId);
  stop = await waitForLine(lines.arcIterationEnd, 15000);
  await setSourceBreakpoints([]);
  await timed('stepOut', () => client.request('stepOut', { threadId: stop.body.threadId }));
  stop = await timed('stepOutStop', () => client.waitStopped(15000));
  frame = await topFrame(stop.body.threadId);
  assert(frame.name.includes('RunManagedSelfTests'), `Step Out stopped in '${frame.name}', not RunManagedSelfTests.`);
  result.checks.stepOut = frame.name;

  await setSourceBreakpoints([lines.afterSelfTests, lines.loopDelay]);
  await resume(stop.body.threadId);
  stop = await waitForLine(lines.afterSelfTests, 45000);
  frame = await topFrame(stop.body.threadId);
  const afterScopes = (await client.request('scopes', { frameId: frame.id })).body.scopes;
  const afterRuntime = await variables(requiredNamed(afterScopes, 'C~ Runtime').variablesReference);
  const afterLive = Number.parseInt(requiredNamed(afterRuntime, 'Live object count').value, 10);
  const allocations = Number.parseInt(requiredNamed(afterRuntime, 'Total allocations').value, 10);
  const releases = Number.parseInt(requiredNamed(afterRuntime, 'Total final releases').value, 10);
  assert(afterLive === 0, `Managed self-tests left ${afterLive} live objects.`);
  assert(allocations === releases, `ARC allocation/final-release totals differ: ${allocations} vs ${releases}.`);
  result.checks.arcRecovered = { liveCount: afterLive, allocations, finalReleases: releases };

  await resume(stop.body.threadId);
  stop = await waitForLine(lines.loopDelay, 20000);
  frame = await topFrame(stop.body.threadId);
  const loopScopes = (await client.request('scopes', { frameId: frame.id })).body.scopes;
  const loopLocalsScope = requiredNamed(loopScopes, 'Locals');
  const loopLocals = await variables(loopLocalsScope.variablesReference);
  const count = requiredNamed(loopLocals, 'count');
  assert(Number.isInteger(Number.parseInt(count.value, 10)), `Loop local count is invalid: ${count.value}.`);
  const countInfo = (await client.request('dataBreakpointInfo', {
    variablesReference: loopLocalsScope.variablesReference, name: 'count', frameId: frame.id,
  })).body;
  assert(typeof countInfo.dataId === 'string', 'Loop count did not provide a data-breakpoint ID.');
  watchResult = (await client.request('setDataBreakpoints', {
    breakpoints: [{ dataId: countInfo.dataId, accessType: 'write' }],
  })).body.breakpoints[0];
  assert(watchResult.verified, `Loop hardware watchpoint was rejected: ${watchResult.message ?? 'unknown error'}.`);
  result.checks.lexicalLocal = { count: Number.parseInt(count.value, 10), watchpointInstalled: true };

  assert(result.checks.exceptionStops >= 1, 'No caught C~ exception filter stop was observed.');
  assert(client.targetStdout.includes('C~ ESP-IDF hardware test'), 'C~ Console output was not forwarded through DAP target output.');
  result.checks.consoleForwarding = true;

  await client.request('setDataBreakpoints', { breakpoints: [] });
  await timed('disconnect', () => client.request('disconnect', { terminateDebuggee: false }, 15000));
  result.checks.cleanDetach = true;
  result.output = client.targetOutput;
  result.passed = true;
} catch (error) {
  result.error = error instanceof Error ? error.stack ?? error.message : String(error);
  result.output = client.targetOutput;
  process.exitCode = 1;
  try { await client.request('disconnect', { terminateDebuggee: false }, 5000); } catch { }
} finally {
  client.close();
  writeFileSync(reportPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
}

async function setSourceBreakpoints(requestedLines) {
  const response = await client.request('setBreakpoints', {
    source: { name: path.basename(sourcePath), path: sourcePath },
    breakpoints: [...new Set(requestedLines)].map(line => ({ line })),
    sourceModified: false,
  });
  const breakpoints = response.body.breakpoints ?? [];
  assert(breakpoints.length === new Set(requestedLines).size, 'The adapter returned an unexpected logical-breakpoint count.');
  for (const breakpoint of breakpoints)
    assert(breakpoint.verified, `Logical breakpoint at ${breakpoint.line ?? '?'} was not verified: ${breakpoint.message ?? ''}`);
  if (requestedLines.length > 2)
    result.checks.moreThanTwoBreakpoints = breakpoints.length;
}

async function resume(threadId = 1) {
  await client.request('continue', { threadId });
}

async function waitForLine(line, timeout) {
  const deadline = Date.now() + timeout;
  while (true) {
    const remaining = deadline - Date.now();
    assert(remaining > 0, `Timed out waiting for Program.ct:${line}.`);
    const stop = await client.waitStopped(remaining);
    if (stop.body.reason === 'exception') {
      result.checks.exceptionStops = (result.checks.exceptionStops ?? 0) + 1;
      await resume(stop.body.threadId);
      continue;
    }
    const frame = await topFrame(stop.body.threadId);
    if (frame.line === line) {
      assertSourceFrame(frame);
      return stop;
    }
    throw new Error(`Expected Program.ct:${line}, stopped at ${frame.source?.path ?? '<native>'}:${frame.line} (${stop.body.reason}).`);
  }
}

async function waitForReason(reason, timeout) {
  const deadline = Date.now() + timeout;
  while (true) {
    const remaining = deadline - Date.now();
    assert(remaining > 0, `Timed out waiting for a '${reason}' stop.`);
    const stop = await client.waitStopped(remaining);
    if (stop.body.reason === reason)
      return stop;
    if (stop.body.reason === 'exception')
      result.checks.exceptionStops = (result.checks.exceptionStops ?? 0) + 1;
    else if (stop.body.reason !== 'breakpoint' && stop.body.reason !== 'step')
      throw new Error(`Expected a '${reason}' stop, got '${stop.body.reason}'.`);
    await resume(stop.body.threadId);
  }
}

async function topFrame(threadId) {
  const response = await client.request('stackTrace', { threadId, startFrame: 0, levels: 20 });
  const frame = response.body.stackFrames?.[0];
  assert(frame !== undefined, `Thread ${threadId} has no stack frame.`);
  return frame;
}

async function variables(reference, start, count) {
  return (await client.request('variables', { variablesReference: reference, start, count }, 30000)).body.variables ?? [];
}

function requiredNamed(values, name) {
  const value = values.find(candidate => candidate.name === name);
  assert(value !== undefined, `Debugger presentation omitted '${name}'.`);
  return value;
}

function assertSourceFrame(frame) {
  const actual = path.resolve(frame.source?.path ?? '');
  const equal = process.platform === 'win32' ? actual.toLowerCase() === sourcePath.toLowerCase() : actual === sourcePath;
  assert(equal, `Debugger exposed generated frame '${frame.source?.path ?? '<none>'}'.`);
}

async function timed(name, action) {
  const started = performance.now();
  try { return await action(); }
  finally { result.timingsMs[name] = Math.round((performance.now() - started) * 10) / 10; }
}

function assert(condition, message) {
  if (!condition)
    throw new Error(message);
}

function parseArguments(args) {
  const parsed = new Map();
  for (let index = 0; index < args.length; index += 2) {
    if (!args[index].startsWith('--') || index + 1 >= args.length)
      throw new Error(`Expected --name value arguments, got '${args[index]}'.`);
    parsed.set(args[index].slice(2), args[index + 1]);
  }
  return parsed;
}

function requiredOption(name) {
  const value = options.get(name);
  if (value === undefined || value.length === 0)
    throw new Error(`Missing required --${name} option.`);
  return value;
}
