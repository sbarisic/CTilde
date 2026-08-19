import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function readJson(relativePath) {
  return JSON.parse(await readFile(path.join(extensionRoot, relativePath), "utf8"));
}

test("manifest registers the C~ language and grammar", async () => {
  const manifest = await readJson("package.json");
  const [language] = manifest.contributes.languages;
  const [grammar] = manifest.contributes.grammars;

  assert.equal(manifest.name, "ctilde-language");
  assert.equal(manifest.version, "0.4.0");
  assert.equal(manifest.engines.vscode, "^1.85.0");
  assert.equal(manifest.license, "Unlicense");
  assert.equal(manifest.repository.directory, "editors/vscode");
  assert.equal(manifest.main, "./out/extension.js");
  assert.deepEqual(manifest.activationEvents, [
    "onLanguage:ctilde",
    "onUri:ctilde-stdlib",
    "onTaskType:ctilde",
    "onCommand:ctilde.project.check",
    "onCommand:ctilde.project.build"
  ]);
  assert.equal(manifest.dependencies["vscode-languageclient"], "9.0.1");
  const configuration = manifest.contributes.configuration.properties;
  assert.equal(configuration["ctilde.languageServer.dotnetPath"].default, "dotnet");
  assert.equal(configuration["ctilde.languageServer.dotnetPath"].scope, "machine");
  assert.equal(configuration["ctilde.languageServer.serverPath"].default, "");
  assert.equal(configuration["ctilde.languageServer.serverPath"].scope, "window");
  assert.match(configuration["ctilde.languageServer.serverPath"].description, /\$\{workspaceFolder\}/);
  assert.equal(configuration["ctilde.languageServer.restartOnServerChange"].default, true);
  assert.equal(configuration["ctilde.languageServer.restartOnServerChange"].scope, "window");
  assert.match(configuration["ctilde.languageServer.restartOnServerChange"].description, /CTilde\.Compiler\.dll/);
  assert.equal(configuration["ctilde.compiler.compilerPath"].default, "");
  assert.equal(configuration["ctilde.compiler.compilerPath"].scope, "window");
  assert.match(configuration["ctilde.compiler.compilerPath"].description, /\$\{workspaceFolder\}/);
  assert.equal(configuration["ctilde.compiler.dotnetPath"].default, "dotnet");
  assert.equal(configuration["ctilde.compiler.nativeCompiler"].default, "");
  assert.equal(configuration["ctilde.compiler.idfPath"].default, "");
  assert.deepEqual(manifest.contributes.taskDefinitions[0].required, ["project", "mode"]);
  assert.deepEqual(manifest.contributes.taskDefinitions[0].properties.mode.enum, ["check", "build"]);
  assert.equal(manifest.contributes.problemMatchers[0].name, "ctilde");
  assert.equal(manifest.contributes.problemMatchers[0].owner, "ctilde-build");
  assert.ok(manifest.files.includes("compiler/**"));
  assert.equal(manifest.contributes.jsonValidation[0].fileMatch, "**/ctilde.json");
  const semanticScopes = manifest.contributes.semanticTokenScopes[0];
  assert.equal(semanticScopes.language, "ctilde");
  assert.deepEqual(semanticScopes.scopes.property, ["variable.other.property.ctilde"]);
  assert.deepEqual(semanticScopes.scopes["method.defaultLibrary"], ["support.function.ctilde"]);
  assert.deepEqual(language, {
    id: "ctilde",
    aliases: ["C~", "CTilde"],
    extensions: [".ct"],
    configuration: "./language-configuration.json"
  });
  assert.deepEqual(grammar, {
    language: "ctilde",
    scopeName: "source.ctilde",
    path: "./syntaxes/ctilde.tmLanguage.json"
  });

  await access(path.resolve(extensionRoot, language.configuration));
  await access(path.resolve(extensionRoot, grammar.path));
  await access(path.resolve(extensionRoot, "schemas/ctilde.schema.json"));
});

test("project schema includes native build configuration", async () => {
  const schema = await readJson("schemas/ctilde.schema.json");
  const build = schema.properties.build;
  assert.equal(build.additionalProperties, false);
  assert.deepEqual(build.properties.configuration.enum, ["debug", "release"]);
  assert.equal(build.properties.compiler.default, "auto");
  assert.equal(build.properties.espIdfProjectDirectory.default, ".");
});

test("grammar exposes the expected root scope and repositories", async () => {
  const grammar = await readJson("syntaxes/ctilde.tmLanguage.json");
  const requiredRepositories = [
    "comments",
    "strings",
    "characters",
    "attributes",
    "declarations",
    "numbers",
    "built-in-types",
    "modifiers",
    "control-keywords",
    "operators",
    "punctuation",
    "identifiers"
  ];

  assert.equal(grammar.scopeName, "source.ctilde");
  for (const repository of requiredRepositories)
    assert.ok(grammar.repository[repository], `missing grammar repository: ${repository}`);
});

test("language configuration regexes compile", async () => {
  const configuration = await readJson("language-configuration.json");
  const expressions = [
    configuration.wordPattern,
    configuration.folding.markers.start,
    configuration.folding.markers.end,
    configuration.indentationRules.increaseIndentPattern,
    configuration.indentationRules.decreaseIndentPattern,
    ...configuration.onEnterRules.flatMap(rule => [rule.beforeText, rule.afterText].filter(Boolean))
  ];

  for (const expression of expressions)
    assert.doesNotThrow(() => new RegExp(expression), `invalid editor regex: ${expression}`);

  assert.equal(configuration.comments.lineComment, "//");
  assert.deepEqual(configuration.comments.blockComment, ["/*", "*/"]);
  assert.deepEqual(configuration.brackets, [["{", "}"], ["[", "]"], ["(", ")"]]);
});
