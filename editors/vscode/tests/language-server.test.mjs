import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const serverDll = path.join(extensionRoot, "server", "CTilde.LanguageServer.dll");

test("language server provides diagnostics, completion, hover, signatures, definitions, and symbols", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = "using System; public static class Program { [EntryPoint] public static void Main() { Console. } }";
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(programPath, source);

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "fixture" }],
      capabilities: {}
    });
    assert.equal(initialized.capabilities.completionProvider.triggerCharacters[0], ".");
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const dotPosition = source.indexOf("Console.") + "Console.".length;
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: { line: 0, character: dotPosition } });
    assert.ok(completion.items.some(item => item.label === "WriteLine"));

    const consolePosition = source.indexOf("Console") + 1;
    const hover = await client.request("textDocument/hover", { textDocument: { uri }, position: { line: 0, character: consolePosition } });
    assert.match(hover.contents.value, /System\.Console/);
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: { line: 0, character: consolePosition } });
    assert.match(definition.uri, /^ctilde-stdlib:\/\/\/System\/Console\.ct$/);
    const standardLibraryText = await client.request("ctilde/standardLibraryText", { uri: definition.uri });
    assert.match(standardLibraryText, /static class Console/);

    const documentSymbols = await client.request("textDocument/documentSymbol", { textDocument: { uri } });
    assert.ok(documentSymbols.some(symbol => symbol.name === "Program"));
    const workspaceSymbols = await client.request("workspace/symbol", { query: "Prog" });
    assert.ok(workspaceSymbols.some(symbol => symbol.name === "Program"));

    client.notify("textDocument/didChange", {
      textDocument: { uri, version: 2 },
      contentChanges: [{
        range: { start: { line: 0, character: dotPosition }, end: { line: 0, character: dotPosition } },
        rangeLength: 0,
        text: "WriteLine("
      }]
    });
    client.notify("textDocument/didChange", {
      textDocument: { uri, version: 1 },
      contentChanges: [{ text: source }]
    });
    const signature = await client.request("textDocument/signatureHelp", { textDocument: { uri }, position: { line: 0, character: dotPosition + "WriteLine(".length } });
    assert.ok(signature.signatures.some(item => item.label.includes("WriteLine")));
    const diagnostics = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri && value.version === 2);
    assert.ok(diagnostics.diagnostics.some(item => item.code.startsWith("CT0")));

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

class LspClient {
  #process;
  #buffer = Buffer.alloc(0);
  #nextId = 1;
  #requests = new Map();
  #notifications = [];
  #waiters = [];

  constructor(dll) {
    this.#process = spawn("dotnet", [dll], { stdio: ["pipe", "pipe", "pipe"] });
    this.exited = new Promise(resolve => this.#process.once("exit", code => resolve(code)));
    this.#process.stdout.on("data", data => this.#receive(data));
    this.#process.stderr.on("data", data => process.stderr.write(data));
  }

  request(method, params) {
    const id = this.#nextId++;
    const promise = new Promise((resolve, reject) => this.#requests.set(id, { resolve, reject }));
    this.#send({ jsonrpc: "2.0", id, method, ...(params === undefined ? {} : { params }) });
    return promise;
  }

  notify(method, params) {
    this.#send({ jsonrpc: "2.0", method, ...(params === undefined ? {} : { params }) });
  }

  waitForNotification(method, predicate) {
    const existing = this.#notifications.find(item => item.method === method && predicate(item.params));
    if (existing !== undefined)
      return Promise.resolve(existing.params);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`Timed out waiting for ${method}`)), 10000);
      this.#waiters.push({ method, predicate, resolve: value => { clearTimeout(timer); resolve(value); } });
    });
  }

  dispose() {
    if (!this.#process.killed)
      this.#process.kill();
  }

  #send(message) {
    const body = JSON.stringify(message);
    this.#process.stdin.write(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`);
  }

  #receive(data) {
    this.#buffer = Buffer.concat([this.#buffer, data]);
    for (;;) {
      const headerEnd = this.#buffer.indexOf("\r\n\r\n");
      if (headerEnd < 0)
        return;
      const match = /Content-Length: (\d+)/i.exec(this.#buffer.subarray(0, headerEnd).toString());
      if (match === null)
        throw new Error("Language server returned an invalid header.");
      const length = Number(match[1]);
      const messageEnd = headerEnd + 4 + length;
      if (this.#buffer.length < messageEnd)
        return;
      const message = JSON.parse(this.#buffer.subarray(headerEnd + 4, messageEnd).toString());
      this.#buffer = this.#buffer.subarray(messageEnd);
      if (message.id !== undefined) {
        const request = this.#requests.get(message.id);
        this.#requests.delete(message.id);
        if (message.error !== undefined)
          request?.reject(new Error(JSON.stringify(message.error)));
        else
          request?.resolve(message.result);
      } else if (message.method !== undefined) {
        this.#notifications.push(message);
        const index = this.#waiters.findIndex(waiter => waiter.method === message.method && waiter.predicate(message.params));
        if (index >= 0) {
          const [waiter] = this.#waiters.splice(index, 1);
          waiter.resolve(message.params);
        }
      }
    }
  }
}
