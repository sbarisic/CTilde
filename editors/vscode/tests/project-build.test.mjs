import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const {
  compilerArguments,
  compilerPathError,
  findNearestProject,
  resolveCompilerLaunch,
  resolveDebugProjectPath,
  resolveTaskProjectPath,
} = require("../out/projectBuild.js");

test("compiler launch resolution preserves the bundled DLL fallback", () => {
  const extension = path.resolve("extension");
  const launch = resolveCompilerLaunch("", "dotnet-custom", extension, path.resolve("workspace"));
  assert.deepEqual(launch, {
    command: "dotnet-custom",
    prefixArguments: [path.join(extension, "compiler", "ctilde.dll")],
    workingDirectory: extension,
    compilerPath: path.join(extension, "compiler", "ctilde.dll"),
    isExternal: false,
  });
});

test("compiler launch accepts DLLs, executables, relative paths, and workspace variables", () => {
  const extension = path.resolve("extension");
  const workspace = path.resolve("workspace");
  const dll = resolveCompilerLaunch("${workspaceFolder}/bin/ctilde.dll", "dotnet", extension, workspace);
  assert.equal(dll.command, "dotnet");
  assert.deepEqual(dll.prefixArguments, [path.join(workspace, "bin", "ctilde.dll")]);
  const executable = resolveCompilerLaunch(path.join("tools", "ctilde.exe"), "dotnet", extension, workspace);
  assert.equal(executable.command, path.join(workspace, "tools", "ctilde.exe"));
  assert.deepEqual(executable.prefixArguments, []);
});

test("compiler launch rejects workspace paths without a workspace and reports missing files", () => {
  assert.throws(() => resolveCompilerLaunch("bin/ctilde.dll", "dotnet", path.resolve("extension"), undefined), /requires an open workspace/);
  const launch = resolveCompilerLaunch(path.resolve("missing", "ctilde.exe"), "dotnet", path.resolve("extension"), undefined);
  assert.match(compilerPathError(launch, () => false), /Configured C~ compiler does not exist/);
  assert.equal(compilerPathError(launch, () => true), undefined);
});

test("task arguments apply only target-appropriate machine settings", () => {
  const launch = resolveCompilerLaunch("", "dotnet", path.resolve("extension"), path.resolve("workspace"));
  const manifest = path.resolve("workspace", "ctilde.json");
  assert.deepEqual(
    compilerArguments(launch, manifest, "check", "hosted", { nativeCompiler: "clang", idfPath: "idf" }).slice(-3),
    ["--project", manifest, "--check"]);
  assert.deepEqual(
    compilerArguments(launch, manifest, "build", "hosted", { nativeCompiler: "clang", idfPath: "idf" }).slice(-5),
    ["--project", manifest, "--build", "--compiler", "clang"]);
  assert.deepEqual(
    compilerArguments(launch, manifest, "build", "esp-idf", { nativeCompiler: "clang", idfPath: "idf" }).slice(-5),
    ["--project", manifest, "--build", "--idf-path", "idf"]);
});

test("task project and nearest-manifest resolution are deterministic", () => {
  const workspace = path.resolve("workspace");
  assert.equal(resolveTaskProjectPath("project/ctilde.json", workspace), path.join(workspace, "project", "ctilde.json"));
  assert.throws(() => resolveTaskProjectPath("relative.json", undefined), /requires a workspace folder/);
  const source = path.join(workspace, "project", "src", "Program.ct");
  const expected = path.join(workspace, "project", "ctilde.json");
  assert.equal(findNearestProject(source, candidate => candidate === expected), expected);
  assert.equal(findNearestProject(source, () => false), undefined);
});

test("debug project resolution expands workspace variables before resolving paths", () => {
  const workspace = path.resolve("workspace");
  assert.equal(
    resolveDebugProjectPath("${workspaceFolder}/ctilde.json", workspace),
    path.join(workspace, "ctilde.json"));
  assert.equal(
    resolveDebugProjectPath("examples/TCan485/ctilde.json", workspace),
    path.join(workspace, "examples", "TCan485", "ctilde.json"));
  const absolute = path.resolve("other", "ctilde.json");
  assert.equal(resolveDebugProjectPath(absolute, workspace), absolute);
  assert.throws(
    () => resolveDebugProjectPath("${workspaceFolder}/ctilde.json", undefined),
    /no workspace folder is open/);
});
