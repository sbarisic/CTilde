import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const serverDll = path.join(extensionRoot, "server", "CTilde.LanguageServer.dll");

test("language server provides diagnostics, semantic tokens, completion, hover, signatures, definitions, and symbols", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = "using System;\r\n// 😀 UTF-16 prefix\r\npublic static class Program { [EntryPoint] public static void Main() { Console. } }";
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(programPath, source);

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "fixture" }],
      capabilities: { workspace: { semanticTokens: { refreshSupport: true } } }
    });
    assert.equal(initialized.capabilities.completionProvider.triggerCharacters[0], ".");
    assert.equal(initialized.capabilities.completionProvider.resolveProvider, true);
    assert.deepEqual(initialized.capabilities.semanticTokensProvider.legend.tokenTypes,
      ["namespace", "class", "struct", "enum", "enumMember", "parameter", "variable", "property", "method"]);
    assert.deepEqual(initialized.capabilities.semanticTokensProvider.legend.tokenModifiers,
      ["declaration", "static", "readonly", "defaultLibrary"]);
    assert.equal(initialized.capabilities.semanticTokensProvider.full, true);
    assert.equal(initialized.capabilities.semanticTokensProvider.range, false);
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const dotOffset = source.indexOf("Console.") + "Console.".length;
    const dotPosition = positionAt(source, dotOffset);
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: dotPosition });
    const writeLineCompletion = completion.items.find(item => item.label === "WriteLine");
    assert.ok(writeLineCompletion);
    assert.equal(writeLineCompletion.documentation, undefined);
    const resolvedWriteLine = await client.request("completionItem/resolve", writeLineCompletion);
    assert.match(resolvedWriteLine.documentation.value, /managed string followed by a line terminator|line terminator/);

    const semantic = await client.request("textDocument/semanticTokens/full", { textDocument: { uri } });
    const decoded = decodeSemanticTokens(semantic.data, initialized.capabilities.semanticTokensProvider.legend, source);
    const consoleToken = decoded.find(token => token.text === "Console");
    assert.deepEqual(consoleToken, { line: 2, character: source.split(/\r?\n/)[2].indexOf("Console"), length: 7, type: "class", modifiers: ["static", "defaultLibrary"], text: "Console" });
    assert.ok(decoded.some(token => token.text === "Program" && token.type === "class" && token.modifiers.includes("declaration")));
    await client.waitForNotification("workspace/semanticTokens/refresh", () => true);

    const consolePosition = positionAt(source, source.indexOf("Console") + 1);
    const hover = await client.request("textDocument/hover", { textDocument: { uri }, position: consolePosition });
    assert.match(hover.contents.value, /System\.Console/);
    assert.match(hover.contents.value, /Writes formatted values/);
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: consolePosition });
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
        range: { start: dotPosition, end: dotPosition },
        rangeLength: 0,
        text: "WriteLine("
      }]
    });
    client.notify("textDocument/didChange", {
      textDocument: { uri, version: 1 },
      contentChanges: [{ text: source }]
    });
    const signature = await client.request("textDocument/signatureHelp", { textDocument: { uri }, position: { line: dotPosition.line, character: dotPosition.character + "WriteLine(".length } });
    assert.ok(signature.signatures.some(item => item.label.includes("WriteLine")));
    assert.ok(signature.signatures.some(item => item.parameters.some(parameter => parameter.documentation?.value.includes("value to write"))));
    const staleCompletion = await client.request("completionItem/resolve", writeLineCompletion);
    assert.equal(staleCompletion.documentation, undefined);
    const changedSource = source.slice(0, dotOffset) + "WriteLine(" + source.slice(dotOffset);
    const changedSemantic = await client.request("textDocument/semanticTokens/full", { textDocument: { uri } });
    const changedDecoded = decodeSemanticTokens(changedSemantic.data, initialized.capabilities.semanticTokensProvider.legend, changedSource);
    assert.ok(changedDecoded.some(token => token.text === "WriteLine" && token.type === "method" && token.modifiers.includes("static") && token.modifiers.includes("defaultLibrary")));
    const diagnostics = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri && value.version === 2);
    assert.ok(diagnostics.diagnostics.some(item => item.code.startsWith("CT0")));
    await client.waitForNotification("workspace/semanticTokens/refresh", () => true);

    client.notify("workspace/didChangeWatchedFiles", { changes: [{ uri: pathToFileURL(path.join(directory, "ctilde.json")).href, type: 2 }] });
    await client.waitForNotification("workspace/semanticTokens/refresh", () => true);

    const cancellationSource = `public static class Program { [EntryPoint] public static void Main() { int value = 0; ${"value;".repeat(2000)} } }`;
    client.notify("textDocument/didChange", { textDocument: { uri, version: 3 }, contentChanges: [{ text: cancellationSource }] });
    await client.request("workspace/symbol", { query: "Program" });
    const canceledRequest = client.requestCancelable("textDocument/semanticTokens/full", { textDocument: { uri } });
    client.notify("$/cancelRequest", { id: canceledRequest.id });
    await assert.rejects(canceledRequest.promise);

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("language server exposes draft 0.12 operator declarations and usages", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-operator-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = `public struct V
{
    public int X;
    public static V operator +(V left, V right) { return left; }

}
public static class Program
{
    [EntryPoint] public static void Main() { V left = new V(); V right = new V(); V result = left + right; }
}`;
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(programPath, source);

  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", { processId: process.pid, rootUri: pathToFileURL(directory).href, capabilities: {} });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const useOffset = source.lastIndexOf("+");
    const hover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, useOffset) });
    assert.match(hover.contents.value, /operator \+/);
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: positionAt(source, useOffset) });
    assert.equal(definition.uri, uri);
    assert.deepEqual(definition.range.start, positionAt(source, source.indexOf("+")));

    const documentSymbols = await client.request("textDocument/documentSymbol", { textDocument: { uri } });
    assert.ok(documentSymbols.flatMap(symbol => symbol.children ?? []).some(symbol => symbol.name === "operator +"));
    const workspaceSymbols = await client.request("workspace/symbol", { query: "operator +" });
    assert.ok(workspaceSymbols.some(symbol => symbol.name === "operator +"));
    const memberPosition = positionAt(source, source.indexOf("\n\n") + 1);
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: memberPosition });
    assert.ok(completion.items.some(item => item.label === "operator"));

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("language server exposes draft 0.12 vector sources and documentation", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-vector-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = "public static class Program { [EntryPoint] public static void Main() { Vec3 left = Vec3.UnitX; Vec3 right = Vec3.UnitY; Vec3 result = left + right; } }";
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(programPath, source);

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", { processId: process.pid, rootUri: pathToFileURL(directory).href, capabilities: {} });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const typeOffset = source.indexOf("Vec3 left");
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: positionAt(source, typeOffset) });
    assert.match(definition.uri, /^ctilde-stdlib:\/\/\/System\/Vec3\.ct$/);
    const standardLibraryText = await client.request("ctilde/standardLibraryText", { uri: definition.uri });
    assert.match(standardLibraryText, /public struct Vec3/);

    const staticOffset = source.indexOf("Vec3.UnitX") + "Vec3.".length;
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: positionAt(source, staticOffset) });
    const unitX = completion.items.find(item => item.label === "UnitX");
    assert.ok(unitX);
    const resolved = await client.request("completionItem/resolve", unitX);
    assert.match(resolved.documentation.value, /positive X unit vector/);

    const plusOffset = source.lastIndexOf("+");
    const hover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, plusOffset) });
    assert.match(hover.contents.value, /operator \+/);
    assert.match(hover.contents.value, /Adds corresponding components/);

    const semantic = await client.request("textDocument/semanticTokens/full", { textDocument: { uri } });
    const decoded = decodeSemanticTokens(semantic.data, initialized.capabilities.semanticTokensProvider.legend, source);
    assert.ok(decoded.some(token => token.text === "Vec3" && token.type === "struct" && token.modifiers.includes("defaultLibrary")));

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("ESP-only documentation resolves from the target sidecar", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-esp-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = "using Esp.Idf; public static class Program { [EntryPoint] public static void Main() { EspError error = Ws2812.Clear(); error.ThrowIfError(); error. } }";
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "esp-idf", sources: ["*.ct"] }));
  await writeFile(programPath, source);
  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", { processId: process.pid, rootUri: pathToFileURL(directory).href, capabilities: {} });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });
    const completionOffset = source.lastIndexOf("error.") + "error.".length;
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: positionAt(source, completionOffset) });
    const throwIfError = completion.items.find(item => item.label === "ThrowIfError");
    assert.ok(throwIfError);
    const resolved = await client.request("completionItem/resolve", throwIfError);
    assert.match(resolved.documentation.value, /Throws when the result is not ESP\\_OK/);
    const hoverOffset = source.indexOf("ThrowIfError") + 1;
    const hover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, hoverOffset) });
    assert.match(hover.contents.value, /symbolic name and numeric code/);
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("hosted I/O documentation and target filtering", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-io-"));
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const source = "using System.IO; public static class Program { [EntryPoint] public static void Main() { File. } }";
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(programPath, source);
  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", { processId: process.pid, rootUri: pathToFileURL(directory).href, capabilities: {} });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });
    const completionOffset = source.indexOf("File.") + "File.".length;
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: positionAt(source, completionOffset) });
    const open = completion.items.find(item => item.label === "Open");
    assert.ok(open);
    const resolved = await client.request("completionItem/resolve", open);
    assert.match(resolved.documentation.value, /Opens, creates, or appends/);
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

function decodeSemanticTokens(data, legend, source) {
  const lines = source.split(/\r?\n/);
  const result = [];
  let line = 0;
  let character = 0;
  for (let index = 0; index < data.length; index += 5) {
    line += data[index];
    character = data[index] === 0 ? character + data[index + 1] : data[index + 1];
    const length = data[index + 2];
    const modifierBits = data[index + 4];
    result.push({
      line,
      character,
      length,
      type: legend.tokenTypes[data[index + 3]],
      modifiers: legend.tokenModifiers.filter((_, modifier) => (modifierBits & (1 << modifier)) !== 0),
      text: lines[line].slice(character, character + length)
    });
  }
  return result;
}

function positionAt(source, offset) {
  const before = source.slice(0, offset);
  const lines = before.split(/\r?\n/);
  return { line: lines.length - 1, character: lines.at(-1).length };
}

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
    return this.requestCancelable(method, params).promise;
  }

  requestCancelable(method, params) {
    const id = this.#nextId++;
    const promise = new Promise((resolve, reject) => this.#requests.set(id, { resolve, reject }));
    this.#send({ jsonrpc: "2.0", id, method, ...(params === undefined ? {} : { params }) });
    return { id, promise };
  }

  notify(method, params) {
    this.#send({ jsonrpc: "2.0", method, ...(params === undefined ? {} : { params }) });
  }

  waitForNotification(method, predicate) {
    const existingIndex = this.#notifications.findIndex(item => item.method === method && predicate(item.params));
    if (existingIndex >= 0)
      return Promise.resolve(this.#notifications.splice(existingIndex, 1)[0].params);
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
      if (message.id !== undefined && message.method !== undefined) {
        this.#notifications.push(message);
        this.#send({ jsonrpc: "2.0", id: message.id, result: null });
        const index = this.#waiters.findIndex(waiter => waiter.method === message.method && waiter.predicate(message.params));
        if (index >= 0) {
          const [waiter] = this.#waiters.splice(index, 1);
          waiter.resolve(message.params);
        }
      } else if (message.id !== undefined) {
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
