import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const {
  DevelopmentServerWatchManager,
  resolveServerLaunch,
  RestartCoordinator,
  serverPathError,
  stageExternalServer,
} = require("../out/serverDevelopment.js");

test("server launch resolution preserves the bundled fallback", () => {
  const extensionPath = path.resolve("extension");
  const launch = resolveServerLaunch("  ", extensionPath, path.resolve("workspace"));
  assert.deepEqual(launch, {
    serverDll: path.join(extensionPath, "server", "CTilde.LanguageServer.dll"),
    workingDirectory: extensionPath,
    isExternal: false,
  });
});

test("server launch resolution accepts absolute, relative, and workspace-variable paths", () => {
  const extensionPath = path.resolve("extension");
  const workspacePath = path.resolve("workspace");
  const absolutePath = path.resolve("external", "CTilde.LanguageServer.dll");

  assert.equal(resolveServerLaunch(absolutePath, extensionPath, undefined).serverDll, absolutePath);
  assert.equal(
    resolveServerLaunch(path.join("build", "server.dll"), extensionPath, workspacePath).serverDll,
    path.join(workspacePath, "build", "server.dll"),
  );
  assert.equal(
    resolveServerLaunch("${workspaceFolder}/build/server.dll", extensionPath, workspacePath).serverDll,
    path.join(workspacePath, "build", "server.dll"),
  );
});

test("workspace-relative server paths require a workspace", () => {
  const extensionPath = path.resolve("extension");
  assert.throws(
    () => resolveServerLaunch("build/server.dll", extensionPath, undefined),
    /requires an open workspace folder/,
  );
  assert.throws(
    () => resolveServerLaunch("${workspaceFolder}/build/server.dll", extensionPath, undefined),
    /no workspace folder is open/,
  );
});

test("missing configured servers report an actionable error without fallback", () => {
  const launch = resolveServerLaunch(path.resolve("missing", "server.dll"), path.resolve("extension"), undefined);
  assert.match(serverPathError(launch, () => false), /Configured C~ language server does not exist/);
  assert.equal(serverPathError(launch, () => true), undefined);
});

test("external servers are staged outside the watched build output", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ctilde-server-stage-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const output = path.join(root, "bin");
  const storage = path.join(root, "storage");
  fs.mkdirSync(path.join(output, "resources"), { recursive: true });
  fs.writeFileSync(path.join(output, "CTilde.LanguageServer.dll"), "server");
  fs.writeFileSync(path.join(output, "CTilde.Compiler.dll"), "compiler");
  fs.writeFileSync(path.join(output, "resources", "value.txt"), "resource");

  const staged = stageExternalServer({
    serverDll: path.join(output, "CTilde.LanguageServer.dll"),
    workingDirectory: output,
    isExternal: true,
  }, storage);

  assert.notEqual(path.dirname(staged.launch.serverDll), output);
  assert.equal(staged.launch.workingDirectory, output);
  assert.equal(fs.readFileSync(staged.launch.serverDll, "utf8"), "server");
  assert.equal(fs.readFileSync(path.join(staged.shadowDirectory, "CTilde.Compiler.dll"), "utf8"), "compiler");
  assert.equal(fs.readFileSync(path.join(staged.shadowDirectory, "resources", "value.txt"), "utf8"), "resource");
});

test("restart coordinator serializes explicit restarts", async () => {
  let concurrent = 0;
  let maximumConcurrent = 0;
  const releases = [];
  const coordinator = new RestartCoordinator(async () => {
    concurrent++;
    maximumConcurrent = Math.max(maximumConcurrent, concurrent);
    await new Promise(resolve => releases.push(resolve));
    concurrent--;
  }, 10);

  const first = coordinator.run();
  const second = coordinator.run();
  await waitFor(() => releases.length === 1);
  releases.shift()();
  await waitFor(() => releases.length === 1);
  releases.shift()();
  await Promise.all([first, second]);
  assert.equal(maximumConcurrent, 1);
  await coordinator.dispose();
});

test("development watchers debounce changes, ignore deletion, and are replaceable", async () => {
  let restarts = 0;
  const coordinator = new RestartCoordinator(async () => { restarts++; }, 15);
  const created = [];
  const manager = new DevelopmentServerWatchManager((directory, fileName) => {
    const watcher = new FakeWatcher(directory, fileName);
    created.push(watcher);
    return watcher;
  }, coordinator);

  const firstServer = path.resolve("first", "CTilde.LanguageServer.dll");
  manager.configure(firstServer, true);
  assert.deepEqual(created.map(item => item.fileName), ["CTilde.LanguageServer.dll", "CTilde.Compiler.dll"]);
  created[0].change();
  created[1].change();
  await waitFor(() => restarts === 1);
  assert.equal(restarts, 1);
  created[0].create();
  await waitFor(() => restarts === 2);

  const oldWatchers = [...created];
  manager.configure(path.resolve("second", "CTilde.LanguageServer.dll"), true);
  assert.ok(oldWatchers.every(item => item.disposed));
  assert.equal(created.length, 4);
  created[2].remove();
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(restarts, 2);

  const activeWatchers = created.slice(2);
  activeWatchers[0].change();
  manager.configure(undefined, false);
  assert.ok(activeWatchers.every(item => item.disposed));
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(restarts, 2);
  manager.dispose();
  await coordinator.dispose();
});

test("disposing a restart coordinator cancels pending automatic restarts", async () => {
  let restarts = 0;
  const coordinator = new RestartCoordinator(async () => { restarts++; }, 15);
  coordinator.schedule();
  await coordinator.dispose();
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(restarts, 0);
});

class FakeWatcher {
  createListeners = [];
  changeListeners = [];
  disposed = false;

  constructor(directory, fileName) {
    this.directory = directory;
    this.fileName = fileName;
  }

  onDidCreate(listener) {
    this.createListeners.push(listener);
    return { dispose: () => this.createListeners.splice(this.createListeners.indexOf(listener), 1) };
  }

  onDidChange(listener) {
    this.changeListeners.push(listener);
    return { dispose: () => this.changeListeners.splice(this.changeListeners.indexOf(listener), 1) };
  }

  change() {
    for (const listener of this.changeListeners)
      listener();
  }

  create() {
    for (const listener of this.createListeners)
      listener();
  }

  remove() {
    // Deletions are deliberately not subscribed by the production manager.
  }

  dispose() {
    this.disposed = true;
  }
}

async function waitFor(predicate) {
  const deadline = Date.now() + 1000;
  while (!predicate()) {
    if (Date.now() >= deadline)
      throw new Error("Timed out waiting for test condition.");
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}
