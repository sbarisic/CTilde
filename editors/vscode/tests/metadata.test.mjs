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
  assert.equal(manifest.version, "0.11.0");
  assert.equal(manifest.engines.vscode, "^1.85.0");
  assert.equal(manifest.license, "SEE LICENSE IN LICENSE");
  assert.equal(manifest.preview, true);
  assert.equal(manifest.icon, "images/ctilde-icon.png");
  assert.deepEqual(manifest.extensionKind, ["workspace"]);
  assert.equal(manifest.capabilities.untrustedWorkspaces.supported, false);
  assert.equal(manifest.capabilities.virtualWorkspaces.supported, false);
  assert.equal(manifest.repository.directory, "editors/vscode");
  assert.equal(manifest.main, "./out/extension.js");
  assert.deepEqual(manifest.activationEvents, [
    "onLanguage:ctilde",
    "onUri:ctilde-stdlib",
    "onTaskType:ctilde",
    "onCommand:ctilde.project.check",
    "onCommand:ctilde.project.build",
    "onCommand:ctilde.project.run",
    "onCommand:ctilde.project.generateBindings",
    "onCommand:ctilde.project.debug",
    "onCommand:ctilde.project.attach",
    "onDebug:ctilde"
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
  assert.equal(configuration["ctilde.compiler.idfPath"].scope, "machine-overridable");
  assert.equal(configuration["ctilde.compiler.espClangPath"].default, "");
  assert.equal(configuration["ctilde.compiler.espClangPath"].scope, "machine-overridable");
  assert.equal(configuration["ctilde.debugger.gdbPath"].scope, "machine");
  assert.equal(configuration["ctilde.debugger.serialPort"].default, "");
  assert.equal(configuration["ctilde.debugger.baudRate"].default, 115200);
  assert.equal(configuration["ctilde.debugger.memoryDiagnostics"].default, "objects");
  assert.deepEqual(configuration["ctilde.debugger.memoryDiagnostics"].enum, ["off", "objects", "guarded"]);
  assert.equal(configuration["ctilde.debugger.showRuntimeFrames"].default, false);
  assert.match(configuration["ctilde.debugger.showRuntimeFrames"].description, /trap reports/);
  assert.deepEqual(manifest.contributes.taskDefinitions[0].required, ["project", "mode"]);
  assert.deepEqual(manifest.contributes.taskDefinitions[0].properties.mode.enum, ["check", "build", "run", "bindings"]);
  assert.ok(manifest.contributes.commands.some(command => command.command === "ctilde.project.run" && command.title === "C~: Run Project"));
  assert.equal(manifest.contributes.problemMatchers[0].name, "ctilde");
  assert.equal(manifest.contributes.problemMatchers[0].owner, "ctilde-build");
  assert.ok(manifest.files.includes("compiler/**"));
  assert.ok(manifest.files.includes("images/ctilde-icon.png"));
  assert.ok(manifest.files.includes("THIRD-PARTY-NOTICES.md"));
  assert.ok(manifest.files.includes("out/debugAdapter.js"));
  assert.deepEqual(manifest.contributes.breakpoints, [{ language: "ctilde" }]);
  const debuggerContribution = manifest.contributes.debuggers[0];
  assert.equal(debuggerContribution.type, "ctilde");
  assert.equal(debuggerContribution.program, "./out/debugAdapter.js");
  assert.deepEqual(debuggerContribution.languages, ["ctilde"]);
  assert.deepEqual(debuggerContribution.configurationAttributes.launch.required, ["project"]);
  assert.equal(debuggerContribution.configurationAttributes.launch.properties.baudRate.default, 115200);
  assert.equal(debuggerContribution.configurationAttributes.launch.properties.memoryDiagnostics.default, "objects");
  assert.equal(debuggerContribution.configurationAttributes.launch.properties.cwd.default, undefined);
  assert.equal(debuggerContribution.configurationAttributes.launch.properties.args.default, undefined);
  assert.equal(debuggerContribution.configurationAttributes.launch.properties.environment.type, "object");
  assert.equal(debuggerContribution.initialConfigurations[0].cwd, undefined);
  assert.equal(debuggerContribution.initialConfigurations[0].args, undefined);
  assert.equal(debuggerContribution.initialConfigurations.length, 2);
  assert.equal(manifest.contributes.jsonValidation[0].fileMatch, "**/ctilde.json");
  assert.equal(manifest.contributes.jsonValidation[1].fileMatch, "**/*.bindings.json");
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
  await access(path.resolve(extensionRoot, "schemas/esp-idf-bindings.schema.json"));
  await access(path.resolve(extensionRoot, "images/ctilde-icon.png"));
  await access(path.resolve(extensionRoot, "images/marketplace-hero.png"));
  await access(path.resolve(extensionRoot, "images/systems-pipeline.png"));
  await access(path.resolve(extensionRoot, "CHANGELOG.md"));
  await access(path.resolve(extensionRoot, "SUPPORT.md"));
  await access(path.resolve(extensionRoot, "THIRD-PARTY-NOTICES.md"));
});

test("ESP serial bridge opens without asserting reset control lines", async () => {
  const source = await readFile(path.join(extensionRoot, "src/debugAdapter.ts"), "utf8");
  assert.match(source, /Serial\(port=None,baudrate=/);
  assert.match(source, /connection\.dtr=False;connection\.rts=False;connection\.port=sys\.argv\[1\];connection\.open\(\)/);
});

test("project schema includes native build configuration", async () => {
  const schema = await readJson("schemas/ctilde.schema.json");
  const build = schema.properties.build;
  assert.equal(build.additionalProperties, false);
  assert.deepEqual(build.properties.configuration.enum, ["debug", "release"]);
  assert.equal(build.properties.compiler.default, "auto");
  assert.equal(build.properties.espIdfProjectDirectory.default, ".");
  assert.deepEqual(schema.properties.kind.enum, ["application", "standard-library"]);
  assert.equal(schema.properties.kind.default, "application");
  assert.equal(schema.properties.hosted.additionalProperties, false);
  assert.equal(schema.properties.hosted.properties.nativeSources.uniqueItems, true);
  assert.ok(schema.allOf.some(rule => rule.if?.properties?.kind?.const === "standard-library" &&
    rule.then?.not?.anyOf?.some(restriction => restriction.required?.includes("build"))));
  assert.deepEqual(schema.properties.espIdf.required, ["bindings"]);
  assert.ok(schema.properties.target.enum.includes("cosmopolitan"));
  assert.ok(schema.properties.target.enum.includes("esp32_qemu"));
  assert.ok(schema.properties.target.enum.includes("esp32c3_qemu"));
  assert.ok(schema.allOf.some(rule => rule.if?.properties?.target?.const === "esp32_qemu" && rule.then?.properties?.architecture?.const === "xtensa"));
  assert.ok(schema.allOf.some(rule => rule.if?.properties?.target?.const === "esp32c3_qemu" && rule.then?.properties?.architecture?.const === "riscv32"));
  assert.deepEqual(schema.properties.cosmopolitan.properties.mode.enum, ["default", "tiny", "debug"]);
  const run = schema.properties.run;
  assert.deepEqual(run.properties.executor.enum, ["host", "wsl"]);
  assert.deepEqual(run.properties.successExitCodes.default, [0]);
  assert.equal(run.properties.environment.additionalProperties.type, "string");
  const bindings = await readJson("schemas/esp-idf-bindings.schema.json");
  assert.equal(bindings.properties.schemaVersion.const, 1);
  assert.equal(bindings.$defs.import.additionalProperties, false);
  assert.ok(bindings.$defs.import.properties.configAdapters);
  assert.ok(bindings.$defs.import.properties.outputAdapters);
  assert.deepEqual(bindings.$defs.function.properties.returnOwnership.enum, ["default", "owned", "borrowed"]);
  assert.deepEqual(bindings.$defs.configField.properties.mapping.enum, ["value", "fixedUtf8"]);
  assert.equal(bindings.$defs.configField.properties.maxBytes.minimum, 1);
  assert.ok(bindings.$defs.configAdapter.properties.initializer);
  assert.ok(bindings.$defs.configAdapter.properties.parameters);
  assert.ok(bindings.$defs.outputAdapter.properties.parameters);
  assert.match(bindings.$defs.memberPath.pattern, /\\\./);
});

test("grammar exposes the expected root scope and repositories", async () => {
  const grammar = await readJson("syntaxes/ctilde.tmLanguage.json");
  const requiredRepositories = [
    "assembly-function",
    "inline-assembly",
    "inline-assembly-block",
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
