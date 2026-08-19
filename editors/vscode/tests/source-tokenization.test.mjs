import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import oniguruma from "vscode-oniguruma";
import textmate from "vscode-textmate";

const require = createRequire(import.meta.url);
const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repositoryRoot = path.resolve(extensionRoot, "../..");
const grammarPath = path.join(extensionRoot, "syntaxes", "ctilde.tmLanguage.json");

const wasm = await readFile(require.resolve("vscode-oniguruma/release/onig.wasm"));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

const registry = new textmate.Registry({
  onigLib: Promise.resolve({
    createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
    createOnigString: value => new oniguruma.OnigString(value)
  }),
  loadGrammar: async scopeName => {
    if (scopeName !== "source.ctilde")
      return null;
    return textmate.parseRawGrammar(await readFile(grammarPath, "utf8"), grammarPath);
  }
});

const grammar = await registry.loadGrammar("source.ctilde");
assert.ok(grammar, "C~ grammar did not load");
const rootState = grammar.tokenizeLine("", textmate.INITIAL).ruleStack;

function tokenizeLine(line, state = textmate.INITIAL) {
  return grammar.tokenizeLine(line, state);
}

function scopesAt(line, offset) {
  const { tokens } = tokenizeLine(line);
  const token = tokens.find(candidate => candidate.startIndex <= offset && candidate.endIndex > offset);
  assert.ok(token, `no token at column ${offset} in: ${line}`);
  return token.scopes;
}

function expectScope(line, spelling, scope) {
  const offset = line.indexOf(spelling);
  assert.notEqual(offset, -1, `missing '${spelling}' in '${line}'`);
  assert.ok(scopesAt(line, offset).includes(scope), `'${spelling}' should have ${scope} in '${line}'`);
}

test("every draft 0.9 keyword receives its intended scope", () => {
  const groups = [
    {
      words: ["bool", "byte", "sbyte", "short", "ushort", "char", "int", "uint", "long", "ulong", "nint", "nuint", "float", "string", "object", "void"],
      scope: "storage.type.builtin.ctilde"
    },
    {
      words: ["public", "internal", "protected", "private", "static", "sealed", "readonly", "const", "unsafe", "virtual", "override", "ref", "out"],
      scope: "storage.modifier.ctilde"
    },
    {
      words: ["break", "case", "catch", "continue", "default", "defer", "do", "else", "finally", "for", "foreach", "if", "in", "return", "switch", "throw", "try", "while"],
      scope: "keyword.control.ctilde"
    },
    {
      words: ["true", "false", "null"],
      scope: "constant.language.ctilde"
    },
    {
      words: ["this", "base"],
      scope: "variable.language.ctilde"
    },
    {
      words: ["as", "is"],
      scope: "keyword.operator.type.ctilde"
    },
    {
      words: ["get", "set"],
      scope: "keyword.other.accessor.ctilde"
    }
  ];

  for (const { words, scope } of groups) {
    for (const word of words)
      expectScope(word, word, scope);
  }

  expectScope("var", "var", "storage.type.inferred.ctilde");
  expectScope("new", "new", "keyword.operator.new.ctilde");
  expectScope("stackalloc", "stackalloc", "keyword.operator.new.ctilde");
  expectScope("delegate", "delegate", "storage.type.declaration.ctilde");
  expectScope("unmanaged", "unmanaged", "keyword.other.calling-convention.ctilde");
  expectScope("using System;", "using", "keyword.control.import.ctilde");
  expectScope("namespace Examples;", "namespace", "keyword.declaration.namespace.ctilde");
  expectScope("class Example", "class", "storage.type.class.ctilde");
  expectScope("struct Example", "struct", "storage.type.struct.ctilde");
  expectScope("enum Example", "enum", "storage.type.enum.ctilde");
  expectScope("opaque Handle", "opaque", "storage.type.declaration.ctilde");
});

test("escaped and Unicode identifiers remain identifiers", () => {
  expectScope("int @class = 1;", "@", "punctuation.definition.identifier.ctilde");
  expectScope("int @class = 1;", "class", "variable.other.readwrite.ctilde");
  expectScope("int količina = 1;", "količina", "variable.other.readwrite.ctilde");
  assert.ok(!scopesAt("int @class = 1;", 5).some(scope => scope.startsWith("storage.type.class")));
});

test("representative repository sources finish in the root grammar state", async () => {
  const sourceFiles = [
    "examples/Features.ct",
    "examples/ObjectModel.ct",
    "examples/Exceptions.ct",
    ...await collectCtFiles(path.join(repositoryRoot, "CTilde", "StandardLibrary")),
    ...await collectCtFiles(path.join(repositoryRoot, "examples", "TCan485"))
  ];

  for (const sourceFile of sourceFiles) {
    const absolutePath = path.isAbsolute(sourceFile) ? sourceFile : path.join(repositoryRoot, sourceFile);
    const source = await readFile(absolutePath, "utf8");
    let state = textmate.INITIAL;
    for (const line of source.split(/\r?\n/)) {
      const result = tokenizeLine(line, state);
      assert.ok(result.tokens.every(token => token.scopes[0] === "source.ctilde"), `${absolutePath} lost its root scope`);
      state = result.ruleStack;
    }
    assert.ok(state.equals(rootState), `${path.relative(repositoryRoot, absolutePath)} ended inside an unterminated grammar rule`);
  }
});

async function collectCtFiles(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const candidate = path.join(directory, entry.name);
    if (entry.isDirectory())
      files.push(...await collectCtFiles(candidate));
    else if (entry.isFile() && entry.name.endsWith(".ct"))
      files.push(candidate);
  }
  return files;
}
