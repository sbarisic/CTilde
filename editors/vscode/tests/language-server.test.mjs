import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
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
    assert.equal(initialized.capabilities.referencesProvider, true);
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const dotOffset = source.indexOf("Console.") + "Console.".length;
    const dotPosition = positionAt(source, dotOffset);
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: dotPosition });
    assert.equal(completion.items.filter(item => item.label === "WriteLine").length, 1);
    const writeLineCompletion = completion.items.find(item => item.label === "WriteLine");
    assert.ok(writeLineCompletion);
    assert.match(writeLineCompletion.detail, /\(\+\d+ overloads\)$/);
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

test("language server provides reference locations and lazy reference CodeLens details", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-references-"));
  const libraryPath = path.join(directory, "Library.ct");
  const programPath = path.join(directory, "Program.ct");
  const libraryUri = pathToFileURL(libraryPath).href;
  const programUri = pathToFileURL(programPath).href;
  const library = "public static class Library { public static void Ping(int value) { } public static void Unused() { } }";
  const program = [
    "public static class Program",
    "{",
    "  [EntryPoint]",
    "  public static void Main()",
    "  {",
    "    Library.Ping(1);",
    "    Library.Ping(2);",
    "  }",
    "}",
  ].join("\n");
  const changedProgram = program.replace("    Library.Ping(2);\n", "");
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(libraryPath, library);
  await writeFile(programPath, program);

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "reference-fixture" }],
      capabilities: {}
    });
    assert.equal(initialized.capabilities.referencesProvider, true);
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri: libraryUri, languageId: "ctilde", version: 1, text: library } });
    client.notify("textDocument/didOpen", { textDocument: { uri: programUri, languageId: "ctilde", version: 1, text: program } });

    const lenses = await client.request("ctilde/referenceCodeLenses", { textDocument: { uri: libraryUri } });
    const ping = lenses.find(lens => lens.name === "Ping");
    const unused = lenses.find(lens => lens.name === "Unused");
    assert.ok(ping);
    assert.equal(ping.referenceCount, 2);
    assert.equal(unused?.referenceCount, 0);
    assert.ok(ping.symbolKey);
    assert.ok(ping.revision > 0);

    const usePosition = positionAt(program, program.indexOf("Ping") + 1);
    const references = await client.request("textDocument/references", {
      textDocument: { uri: programUri }, position: usePosition, context: { includeDeclaration: false }
    });
    assert.equal(references.length, 2);
    assert.ok(references.every(reference => reference.uri === programUri));
    const withDeclaration = await client.request("textDocument/references", {
      textDocument: { uri: programUri }, position: usePosition, context: { includeDeclaration: true }
    });
    assert.equal(withDeclaration.length, 3);
    assert.ok(withDeclaration.some(reference => reference.uri === libraryUri));

    const details = await client.request("ctilde/referenceCodeLensDetails", {
      textDocument: { uri: libraryUri }, symbolKey: ping.symbolKey, revision: ping.revision
    });
    assert.equal(details.references.length, 2);
    assert.ok(details.references.every(reference => reference.referenceText.includes("Library.Ping")));
    assert.ok(details.references.every(reference => reference.referenceEnd > reference.referenceStart));
    assert.ok(details.references.every(reference => reference.referenceLongDescription.includes("Library.Ping")));
    assert.ok(details.references.some(reference => reference.textAfterReference1.includes("Library.Ping(2)")));
    assert.ok(details.references.some(reference => reference.textBeforeReference1.includes("Library.Ping(1)")));

    client.notify("textDocument/didChange", {
      textDocument: { uri: programUri, version: 2 }, contentChanges: [{ text: changedProgram }]
    });
    await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === programUri && value.version === 2);
    const referenceRefresh = await client.waitForNotification("ctilde/referenceCodeLens/refresh", value => value.revision > ping.revision);
    assert.ok(referenceRefresh.revision > ping.revision);
    const changedLenses = await client.request("ctilde/referenceCodeLenses", { textDocument: { uri: libraryUri } });
    const changedPing = changedLenses.find(lens => lens.name === "Ping");
    assert.equal(changedPing.referenceCount, 1);
    assert.notEqual(changedPing.revision, ping.revision);
    const staleDetails = await client.request("ctilde/referenceCodeLensDetails", {
      textDocument: { uri: libraryUri }, symbolKey: ping.symbolKey, revision: ping.revision
    });
    assert.deepEqual(staleDetails.references, []);
    const changedDetails = await client.request("ctilde/referenceCodeLensDetails", {
      textDocument: { uri: libraryUri }, symbolKey: changedPing.symbolKey, revision: changedPing.revision
    });
    assert.equal(changedDetails.references.length, 1);

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("HostedIo reference CodeLens details include the complete source row", async () => {
  const repositoryRoot = path.resolve(extensionRoot, "..", "..");
  const directory = path.join(repositoryRoot, "examples", "HostedIo");
  const materialsPath = path.join(directory, "Materials.ct");
  const materialsUri = pathToFileURL(materialsPath).href;
  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "HostedIo" }],
      capabilities: {}
    });
    client.notify("initialized", {});
    const lenses = await client.request("ctilde/referenceCodeLenses", { textDocument: { uri: materialsUri } });
    const metal = lenses.find(lens => lens.name === "Metal" && lens.detail.includes("Vec3"));
    assert.ok(metal);
    const details = await client.request("ctilde/referenceCodeLensDetails", {
      textDocument: { uri: materialsUri }, symbolKey: metal.symbolKey, revision: metal.revision
    });
    assert.ok(details.references.some(reference => reference.referenceText.includes("new Metal(albedo, fuzz)")));
    assert.ok(details.references.some(reference => reference.referenceText.includes("new Metal(new Vec3")));
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
  }
});

test("language server resolves a non-main document through its loaded project and recovers incomplete member completion", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-multi-project-"));
  const helloDirectory = path.join(directory, "Hello");
  const hostedDirectory = path.join(directory, "HostedIo");
  const unrelatedDirectory = path.join(directory, "Unrelated");
  await mkdir(helloDirectory);
  await mkdir(hostedDirectory);
  await mkdir(unrelatedDirectory);
  const helloManifest = path.join(helloDirectory, "ctilde.json");
  const hostedManifest = path.join(hostedDirectory, "ctilde.json");
  const scenePath = path.join(hostedDirectory, "Scene.ct");
  const siblingPath = path.join(hostedDirectory, "Sibling.ct");
  const sceneUri = pathToFileURL(scenePath).href;
  const helloProgramPath = path.join(helloDirectory, "Program.ct");
  const validScene = "using System; namespace Hosted; public static class Scene { public static void Build() { Console.WriteLine(); Sibling value = new Sibling(); } }";
  const incompleteScene = validScene.replace("Console.WriteLine();", "Console.Wri\n");
  await writeFile(helloManifest, JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  const helloProgram = "using System; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(); } }";
  await writeFile(helloProgramPath, helloProgram);
  await writeFile(hostedManifest, JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(path.join(hostedDirectory, "Program.ct"), "namespace Hosted; public static class Program { [EntryPoint] public static void Main() { Scene.Build(); } }");
  await writeFile(siblingPath, "namespace Hosted; public class Sibling { }");
  await writeFile(scenePath, validScene);
  await writeFile(path.join(unrelatedDirectory, "ctilde.json"), JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(path.join(unrelatedDirectory, "Program.ct"), "using System; public static class Unrelated { public static void Run() { Console.WriteLine(); } }");

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "multi-project-fixture" }],
      capabilities: {}
    });
    client.notify("initialized", {});
    client.notify("ctilde/didChangeProjects", {
      projects: [
        { projectUri: pathToFileURL(path.join(helloDirectory, "Hello.ctproj")).href, manifestUri: pathToFileURL(helloManifest).href },
        { projectUri: pathToFileURL(path.join(hostedDirectory, "HostedIo.ctproj")).href, manifestUri: pathToFileURL(hostedManifest).href }
      ],
      activeManifestUri: pathToFileURL(helloManifest).href
    });
    client.notify("textDocument/didOpen", { textDocument: { uri: sceneUri, languageId: "ctilde", version: 1, text: validScene } });

    const siblingOffset = validScene.indexOf("Sibling value") + 1;
    const definition = await client.request("textDocument/definition", { textDocument: { uri: sceneUri }, position: positionAt(validScene, siblingOffset) });
    assert.equal(path.normalize(fileURLToPath(definition.uri)).toLowerCase(), path.normalize(await realpath(siblingPath)).toLowerCase());
    const semantic = await client.request("textDocument/semanticTokens/full", { textDocument: { uri: sceneUri } });
    const decoded = decodeSemanticTokens(semantic.data, initialized.capabilities.semanticTokensProvider.legend, validScene);
    assert.ok(decoded.some(token => token.text === "Sibling" && token.type === "class"));
    const writeLineOffset = validScene.indexOf("WriteLine") + 1;
    const crossProjectReferences = await client.request("textDocument/references", {
      textDocument: { uri: sceneUri }, position: positionAt(validScene, writeLineOffset), context: { includeDeclaration: false }
    });
    assert.ok(crossProjectReferences.length >= 2);
    assert.equal(crossProjectReferences.filter(reference => reference.uri.startsWith("file:") && fileURLToPath(reference.uri).startsWith(helloDirectory)).length, 1);
    assert.equal(crossProjectReferences.filter(reference => reference.uri.startsWith("file:") && fileURLToPath(reference.uri).startsWith(hostedDirectory)).length, 1);
    assert.equal(crossProjectReferences.filter(reference => reference.uri.startsWith("file:") && fileURLToPath(reference.uri).startsWith(unrelatedDirectory)).length, 0);

    await writeFile(helloProgramPath, helloProgram.replace("Console.WriteLine();", ""));
    client.notify("textDocument/didChange", { textDocument: { uri: sceneUri, version: 2 }, contentChanges: [{ text: validScene + " " }] });
    await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === sceneUri && value.version === 2);
    const editRefresh = await client.waitForNotification("ctilde/referenceCodeLens/refresh", value => value.revision > 0);
    const retainedReferences = await client.request("textDocument/references", {
      textDocument: { uri: sceneUri }, position: positionAt(validScene, writeLineOffset), context: { includeDeclaration: false }
    });
    assert.equal(retainedReferences.filter(reference => reference.uri.startsWith("file:") && fileURLToPath(reference.uri).startsWith(helloDirectory)).length, 1);
    client.notify("workspace/didChangeWatchedFiles", { changes: [{ uri: pathToFileURL(helloProgramPath).href, type: 2 }] });
    await client.waitForNotification("ctilde/referenceCodeLens/refresh", value => value.revision > editRefresh.revision);
    const invalidatedReferences = await client.request("textDocument/references", {
      textDocument: { uri: sceneUri }, position: positionAt(validScene, writeLineOffset), context: { includeDeclaration: false }
    });
    assert.equal(invalidatedReferences.filter(reference => reference.uri.startsWith("file:") && fileURLToPath(reference.uri).startsWith(helloDirectory)).length, 0);

    client.notify("textDocument/didChange", { textDocument: { uri: sceneUri, version: 3 }, contentChanges: [{ text: incompleteScene }] });
    const completionOffset = incompleteScene.indexOf("Console.Wri") + "Console.Wri".length;
    const completion = await client.request("textDocument/completion", { textDocument: { uri: sceneUri }, position: positionAt(incompleteScene, completionOffset) });
    const writeLines = completion.items.filter(item => item.label === "WriteLine");
    assert.equal(writeLines.length, 1);
    assert.match(writeLines[0].detail, /\(\+\d+ overloads\)$/);

    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("language server exposes draft 0.25 freestanding assembly functions and constant data", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-freestanding-"));
  const programPath = path.join(directory, "Kernel.ct");
  const uri = pathToFileURL(programPath).href;
  const source = `public static class LowLevel
{

    [ConstInit]
    private static readonly uint Header = 42u;

    [NoRuntime]
    [NoBlock]
    public static unsafe asm uint Read(uint port)
        (in("d") port as source, out("a") result as value)
    {
        inl source, value
    }

    [Naked]
    [Export("_start")]
    [NoAlloc]
    public static unsafe asm void Start()
    {
        hlt
    }
}`;
  await writeFile(path.join(directory, "ctilde.json"), JSON.stringify({ target: "freestanding", architecture: "x86", sources: ["*.ct"] }));
  await writeFile(programPath, source);

  const client = new LspClient(serverDll);
  try {
    const initialized = await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "freestanding-fixture" }],
      capabilities: {}
    });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });

    const diagnostics = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri && value.version === 1);
    assert.deepEqual(diagnostics.diagnostics, []);

    const memberOffset = source.indexOf("\n\n") + 1;
    const completion = await client.request("textDocument/completion", { textDocument: { uri }, position: positionAt(source, memberOffset) });
    for (const label of ["asm", "ConstInit", "Naked"])
      assert.ok(completion.items.some(item => item.label === label), `missing ${label} member completion`);

    const bodyOffset = source.indexOf("{", source.indexOf("public static unsafe asm uint Read")) + 1;
    const bodyCompletion = await client.request("textDocument/completion", { textDocument: { uri }, position: positionAt(source, bodyOffset) });
    for (const label of ["source", "value"])
      assert.ok(bodyCompletion.items.some(item => item.label === label), `missing ${label} assembly alias completion`);

    const constInitOffset = source.indexOf("ConstInit") + 1;
    const constInitHover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, constInitOffset) });
    assert.match(constInitHover.contents.value, /immutable unmanaged static readonly data/);

    const sourceAliasOffset = source.indexOf("inl source") + "inl ".length;
    const parameterHover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, sourceAliasOffset) });
    assert.match(parameterHover.contents.value, /uint port/);
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: positionAt(source, sourceAliasOffset) });
    assert.deepEqual(definition.range.start, positionAt(source, source.indexOf("port)")));

    const resultAliasOffset = source.indexOf("value\n");
    const resultHover = await client.request("textDocument/hover", { textDocument: { uri }, position: positionAt(source, resultAliasOffset) });
    assert.match(resultHover.contents.value, /uint value \(assembly-function result\)/);

    const semantic = await client.request("textDocument/semanticTokens/full", { textDocument: { uri } });
    const decoded = decodeSemanticTokens(semantic.data, initialized.capabilities.semanticTokensProvider.legend, source);
    assert.ok(decoded.some(token => token.text === "Header" && token.type === "property" && token.modifiers.includes("static") && token.modifiers.includes("readonly")));
    assert.ok(decoded.some(token => token.text === "result" && token.type === "variable"));
    assert.ok(decoded.some(token => token.text === "value" && token.type === "variable"));
    assert.ok(decoded.some(token => token.text === "source" && token.type === "parameter"));

    const documentSymbols = await client.request("textDocument/documentSymbol", { textDocument: { uri } });
    const lowLevel = documentSymbols.find(symbol => symbol.name === "LowLevel");
    assert.ok(lowLevel?.children.some(symbol => symbol.name === "Header"));
    assert.ok(lowLevel?.children.some(symbol => symbol.name === "Read"));
    assert.ok(lowLevel?.children.some(symbol => symbol.name === "Start"));

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
    assert.equal(path.normalize(fileURLToPath(definition.uri)).toLowerCase(), path.normalize(await realpath(programPath)).toLowerCase());
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

test("explicit same-root project contexts follow the active manifest", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-context-"));
  const programPath = path.join(directory, "Program.ct");
  const hostedManifest = path.join(directory, "ctilde.json");
  const freestandingManifest = path.join(directory, "ctilde.freestanding.json");
  const uri = pathToFileURL(programPath).href;
  const source = "using System; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(42); } }";
  await writeFile(hostedManifest, JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(freestandingManifest, JSON.stringify({ target: "freestanding", architecture: "x64", sources: ["*.ct"] }));
  await writeFile(programPath, source);
  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", { processId: process.pid, rootUri: pathToFileURL(directory).href, capabilities: {} });
    client.notify("initialized", {});
    client.notify("ctilde/didChangeProjects", {
      projects: [
        { projectUri: pathToFileURL(path.join(directory, "Hosted.ctproj")).href, manifestUri: pathToFileURL(hostedManifest).href },
        { projectUri: pathToFileURL(path.join(directory, "Freestanding.ctproj")).href, manifestUri: pathToFileURL(freestandingManifest).href }
      ],
      activeManifestUri: pathToFileURL(freestandingManifest).href
    });
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });
    const freestanding = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri && value.diagnostics.some(item => item.severity === 1));
    assert.ok(freestanding.diagnostics.some(item => item.code === "CT1107" || item.code === "CT4115"));

    client.notify("ctilde/didChangeActiveProject", { manifestUri: pathToFileURL(hostedManifest).href });
    const hosted = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri && value.diagnostics.length === 0);
    assert.deepEqual(hosted.diagnostics, []);
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("Visual Studio diagnostics wait for project contexts and discard obsolete generations", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ctilde-lsp-vs-diagnostics-"));
  const projectPath = path.join(directory, "Hosted.ctproj");
  const manifestPath = path.join(directory, "ctilde.json");
  const programPath = path.join(directory, "Program.ct");
  const uri = pathToFileURL(programPath).href;
  const invalid = "public static class Program { [EntryPoint] public static void Main() { Missing(); } }";
  const valid = "public static class Program { [EntryPoint] public static void Main() { } }";
  await writeFile(manifestPath, JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
  await writeFile(projectPath, "<Project />");
  await writeFile(programPath, invalid);
  const client = new LspClient(serverDll, { CTILDE_LANGUAGE_SERVER_TESTING: "1" });
  try {
    await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "fixture" }],
      capabilities: { workspace: { semanticTokens: { refreshSupport: true } } },
      initializationOptions: { ctilde: { client: "visualstudio", testPostAnalysisDelayMilliseconds: 500 } }
    });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: invalid } });
    await client.assertNoNotification("textDocument/publishDiagnostics", value => value.uri === uri, 350);

    client.notify("ctilde/didChangeProjects", {
      projects: [{ projectUri: pathToFileURL(projectPath).href, manifestUri: pathToFileURL(manifestPath).href }],
      activeManifestUri: pathToFileURL(manifestPath).href
    });
    await new Promise(resolve => setTimeout(resolve, 250));
    client.notify("textDocument/didChange", {
      textDocument: { uri, version: 2 },
      contentChanges: [{ text: valid }]
    });
    const current = await client.waitForNotification("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === 2);
    assert.deepEqual(current.diagnostics, []);
    await client.assertNoNotification("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === 1, 300);
    client.clearNotifications("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === 2);

    await writeFile(manifestPath, "{");
    client.notify("workspace/didChangeWatchedFiles", {
      changes: [{ uri: pathToFileURL(manifestPath).href, type: 2 }]
    });
    await client.assertNoNotification("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === 2, 850);

    await writeFile(manifestPath, JSON.stringify({ target: "hosted", sources: ["*.ct"] }));
    client.notify("workspace/didChangeWatchedFiles", {
      changes: [{ uri: pathToFileURL(manifestPath).href, type: 2 }]
    });
    client.notify("textDocument/didChange", {
      textDocument: { uri, version: 3 },
      contentChanges: [{ text: invalid }]
    });
    const error = await client.waitForNotification("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === 3);
    assert.ok(error.diagnostics.some(item => item.severity === 1));

    client.notify("textDocument/didClose", { textDocument: { uri } });
    const closed = await client.waitForNotification("textDocument/publishDiagnostics",
      value => value.uri === uri && value.version === undefined && value.diagnostics.length === 0);
    assert.deepEqual(closed.diagnostics, []);
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
    await rm(directory, { recursive: true, force: true });
  }
});

test("physical standard-library projects navigate without embedded duplicates", async () => {
  const directory = path.resolve(extensionRoot, "..", "..", "CTilde", "StandardLibrary");
  const programPath = path.join(directory, "System", "Vec2.ct");
  const uri = pathToFileURL(programPath).href;
  const source = await readFile(programPath, "utf8");
  const client = new LspClient(serverDll);
  try {
    await client.request("initialize", {
      processId: process.pid,
      rootUri: pathToFileURL(directory).href,
      workspaceFolders: [{ uri: pathToFileURL(directory).href, name: "standard-library" }],
      capabilities: {}
    });
    client.notify("initialized", {});
    client.notify("textDocument/didOpen", { textDocument: { uri, languageId: "ctilde", version: 1, text: source } });
    const diagnostics = await client.waitForNotification("textDocument/publishDiagnostics", value => value.uri === uri);
    assert.deepEqual(diagnostics.diagnostics.filter(item => item.severity === 1), []);
    const mathOffset = source.indexOf("Math.Sqrt") + 1;
    const definition = await client.request("textDocument/definition", { textDocument: { uri }, position: positionAt(source, mathOffset) });
    assert.equal(fileURLToPath(definition.uri), path.join(directory, "System", "Math.ct"));
    const lenses = await client.request("ctilde/referenceCodeLenses", { textDocument: { uri } });
    const minLine = positionAt(source, source.indexOf("public static Vec2 Min")).line;
    const minParameters = lenses.filter(item => item.kind === 26 && (item.name === "left" || item.name === "right") && item.selectionRange.start.line === minLine);
    assert.equal(minParameters.length, 2);
    assert.deepEqual(minParameters.map(item => item.referenceCount), [2, 2]);
    const maxOffset = source.indexOf("Max(Vec2 left") + 1;
    const maxReferences = await client.request("textDocument/references", {
      textDocument: { uri },
      position: positionAt(source, maxOffset),
      context: { includeDeclaration: false }
    });
    assert.equal(maxReferences.length, 1);
    assert.equal(fileURLToPath(maxReferences[0].uri), programPath);
    await client.request("shutdown");
    client.notify("exit");
    assert.equal(await client.exited, 0);
  } finally {
    client.dispose();
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

  constructor(dll, environment = {}) {
    this.#process = spawn("dotnet", [dll], { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, ...environment } });
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
      const timer = setTimeout(() => reject(new Error(`Timed out waiting for ${method}; queued: ${JSON.stringify(this.#notifications.filter(item => item.method === method).map(item => item.params))}`)), 10000);
      this.#waiters.push({ method, predicate, resolve: value => { clearTimeout(timer); resolve(value); } });
    });
  }

  async assertNoNotification(method, predicate, milliseconds) {
    if (this.#notifications.some(item => item.method === method && predicate(item.params)))
      assert.fail(`Unexpected ${method} notification`);
    await new Promise(resolve => setTimeout(resolve, milliseconds));
    if (this.#notifications.some(item => item.method === method && predicate(item.params)))
      assert.fail(`Unexpected ${method} notification`);
  }

  clearNotifications(method, predicate) {
    this.#notifications = this.#notifications.filter(item => item.method !== method || !predicate(item.params));
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
