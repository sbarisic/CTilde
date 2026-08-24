# C~ Language Support for Visual Studio Code

This extension adds C~ (`.ct`) IntelliSense plus lexical and compiler-aware highlighting. It launches the repository's .NET language server as a separate process and uses the same compiler declarations, diagnostics, targets, and bundled standard library as command-line builds.

## Features

- Syntax highlighting for declarations, keywords, literals, attributes, comments, operators, and punctuation.
- Compiler-aware highlighting for resolved namespaces, classes, structs, enums, enum members, parameters, locals, properties, fields, methods, and constructors.
- Semantic modifiers for declarations, static and readonly symbols, and embedded standard-library references.
- Comment toggling for `//` and `/* */` comments.
- Bracket matching, automatic closing, surrounding pairs, brace indentation, and region folding.
- Unicode identifiers and keyword identifiers escaped with `@`.
- Draft 0.16 syntax and semantic classification, including unions, packed and explicit layouts, layout operators, interfaces, generic declarations and uses, `volatile`, `lock`, raw inline assembly, standard-library threading and atomic types, vectors, operators, and native interop.
- Target-aware hosted completion and documentation for console input, `System.IO.IOException`, and owned binary-file handles; ESP-IDF projects omit these APIs.
- Context-aware completion for keywords, types, locals, parameters, fields, properties, methods, enum members, and namespaces.
- C#-style `///` XML documentation in lazily resolved completion details, hover, and signature help, including active-parameter descriptions.
- Static/instance, inheritance, accessibility, lexical-scope, overload, and hosted/ESP-IDF filtering.
- Live compiler diagnostics with related locations.
- Hover, signature help, go-to-definition, document symbols, and workspace symbols.
- Read-only navigation into embedded `System` and `Esp.Idf` sources.
- JSON validation for `ctilde.json` projects and ESP-IDF binding manifests.
- Check, native Build, Debug, and Attach commands for every workspace project.

Supported documentation elements are `summary`, `param`, `returns`, `remarks`, `exception`, `see`, `paramref`, and sole-element `inheritdoc`. Documentation warnings remain non-blocking. Links, documentation-tag completion, XML output files, raw Markdown/HTML, and block documentation comments are not implemented.

Rename, references, formatting, code actions, auto-import edits, and incremental semantic-token deltas are not implemented.

Type-body completion includes the `operator` declaration keyword. Operator hover, go-to-definition, document/workspace symbols, semantic classification, and filtering from ordinary member completion share the same regression coverage.

## Projects

Put `ctilde.json` at a project root to select the target and source set:

```json
{
  "target": "esp-idf",
  "sources": ["Program.ct"],
  "build": {
    "generatedC": "main/generated/ctilde_program.c",
    "generatedHeader": "main/generated/ctilde_exports.h",
    "espIdfProjectDirectory": "."
  }
}
```

The nearest ancestor manifest owns a file. Source and exclusion globs are relative to the manifest and cannot escape its directory. A file excluded from that source set is analyzed independently with the manifest target. Without a manifest, the extension treats each file as a standalone hosted program.

The compiler accepts the same manifest through `ctilde --project <ctilde.json>`. `target` defaults to `hosted`; `sources` is required. The optional `build` object overrides generated output, hosted compiler/configuration/executable, or the ESP-IDF project directory. An ESP-IDF project can add `espIdf.bindings` with project-relative binding manifests. All manifest paths remain inside the project root.

## Project builds

Use **C~: Check Project** or **C~: Build Project**. The active file's nearest manifest is selected; a picker appears when several projects are open and none owns the active file. The same actions are available under **Tasks: Run Task** as one Check and Build task per manifest. Command-driven builds save dirty source and manifest files first.

Hosted Build emits C and a native header, discovers MSVC/GCC/Clang, and creates the configured executable. ESP-IDF Check and Build validate declared bindings first; the ignored `build/.ctilde/bindings` cache makes this a fast local check when manifests, headers, configuration, tools, and generated outputs have not changed. Build writes only byte-changed generated artifacts and then invokes `idf.py build`, allowing Ninja to compile only affected modules. **C~: Generate ESP-IDF Bindings** deliberately bypasses the cache and refreshes the tracked C~ declarations and C adapters. Binding-manifest completion and validation cover initializer macros, mixed native/configuration parameters, nested fields, bounded fixed UTF-8 arrays, output structures, and opaque return ownership. Target selection, flashing, and monitoring remain ESP-IDF operations.

The VSIX includes a framework-dependent compiler fallback. For compiler development without rebuilding the extension, configure:

```json
{
  "ctilde.compiler.compilerPath": "${workspaceFolder}/CTilde.Cli/bin/Debug/net10.0/ctilde.dll"
}
```

An external self-contained `ctilde` executable is also accepted. `ctilde.compiler.dotnetPath` selects the host for DLLs, `ctilde.compiler.nativeCompiler` optionally overrides the hosted C compiler, and `ctilde.compiler.idfPath` locates an ESP-IDF installation when its environment is not active. `ctilde.compiler.espClangPath` can select the matching Espressif Clang executable used for header-driven bindings. Both ESP settings are machine-local but workspace-overridable. The CLI process is short-lived, so rebuilding an external compiler requires no extension restart or shadow copy.

## Debugging

Use **C~: Debug Project** to save, build, and launch the nearest project. Use **C~: Attach Debugger** to validate and reuse the artifacts from its last debug launch. The extension also contributes `type: "ctilde"` Launch and Attach configurations for `launch.json`:

```json
{
  "type": "ctilde",
  "request": "launch",
  "name": "Debug C~ Project",
  "project": "${workspaceFolder}/ctilde.json",
  "backend": "auto",
  "gdbPath": "",
  "cwd": "${workspaceFolder}",
  "args": [],
  "stopAtEntry": false,
  "showRuntimeFrames": false,
  "memoryDiagnostics": "objects",
  "serialPort": "",
  "baudRate": 115200
}
```

Hosted GCC and Clang builds use the bundled C~-aware GDB/MI adapter. WSL builds start GDB in the same WSL environment. Debug Launch creates version-3 instrumented metadata; Attach validates and reuses that exact image. Version-3 maps include optional target-memory layouts for bulk logical-stop and runtime-summary reads plus constructed generic names, interface views, atomic storage, runtime thread IDs, and Thread/Mutex presentation. Source and qualified function breakpoints, conditions, positive-integer hit counts, logpoints, exception filters, and Run to Cursor use compiler-emitted logical probes instead of native instruction breakpoints. Step Into, Over, and Out follow C~ sites and method depth, so ARC and cleanup helpers do not become intermediate stops. Threads, stacks, direct locals, and target values are cached for one stop and invalidated when execution resumes. Locals are filtered by initialization point, lexical lifetime, and shadowing. Runtime and ARC frames and the native trap reports behind logical probes are hidden unless `showRuntimeFrames` is enabled. Genuine native signals and hardware-watchpoint reports remain visible.

The `C~ Runtime` scope lists live managed objects, allocation and final-release counters, identities, reference counts, allocation sites, and last ARC sites. Set `memoryDiagnostics` or `ctilde.debugger.memoryDiagnostics` to `off`, `objects`, or `guarded`; `objects` is the default. Guarded mode also displays canary and quarantine state. The reserved function breakpoints `$allocation`, `$final-release`, and `$leak` stop on ARC events without consuming instruction-breakpoint slots. Data breakpoints use GDB hardware watchpoints for addressable locals, fields, array elements, managed-reference slots, and reference counts. ESP values must meet the target's size and alignment rules, and GDB reports the actual watchpoint-slot limit. A local watchpoint is removed when its owning method activation exits.

Safe watch expressions are limited to identifiers, field chains, and array indices. Managed assignment, method or property calls, and general C~ expression evaluation are not supported. Use `$gdb <native-expression>` in the Debug Console for an explicitly raw GDB expression.

An automatically discovered MSVC build uses `cppvsdbg` and requires the Microsoft C/C++ extension. Source breakpoints and stepping map to `.ct` files, while variables and exceptions retain their generated native presentation. Manual `type: "ctilde"` configurations require GCC or Clang.

ESP-IDF debugging uses the runtime UART GDB stub. Set `ctilde.debugger.serialPort` and ensure the built `sdkconfig` contains:

```text
CONFIG_ESP_SYSTEM_GDBSTUB_RUNTIME=y
CONFIG_ESP_GDBSTUB_SUPPORT_TASKS=y
CONFIG_COMPILER_OPTIMIZATION_DEBUG=y
```

Launch builds and flashes an instrumented image. Before runtime initialization, that image waits up to 15 seconds for the UART debugger; it starts normally after the timeout. With `stopAtEntry`, the adapter stops before runtime and module initialization. Source breakpoints can then stop at the first C~ statement. Attach reuses existing ELF and version-3 debug-map artifacts, rejects changed sources or older metadata, and does not rebuild or flash. The adapter selects the architecture-specific GDB from ESP-IDF project metadata; it never substitutes a host GDB. Source, function, log, and exception breakpoints use logical probes and therefore do not consume the ESP32's two instruction-breakpoint slots. Only requested data breakpoints consume target hardware watchpoint resources.

The ESP runtime stub is an all-stop debugger and does not identify the stopping FreeRTOS task in every stop packet. The adapter resumes the actual interrupted frame directly, so inspecting other task stacks before continuing does not change or corrupt the frame that triggered a C~ probe.

An ESP-IDF-Python serial bridge keeps the port open for the complete GDB session, avoiding a lossy Windows close/reopen handoff. Runtime-stub mode therefore owns UART input during the session, so the same port cannot be used as an interactive application console. Instrumented C~ output serializes `Console.Write` and `Console.WriteLine` as GDB target-output packets while the session is active, including with ESP-IDF 6 projects that use Picolibc, and the adapter forwards those packets immediately to the VS Code Debug Console. Output produced before Attach cannot be recovered. Pressing Stop removes hardware watchpoints, clears every logical probe, step, event, and startup-gate setting, advances past an active logical trap once, and asks the ESP GDB stub to continue the firmware without a debugger. The ended Debug Console no longer receives output; subsequent C~ output uses the ordinary ROM UART console. If the debugger process dies while the target is stopped in a trap, reset the board. OpenOCD, JTAG, panic-only postmortem sessions, reverse execution, and ISR entry are outside this debugger profile.

## Development

Install dependencies, build the TypeScript client and .NET server, and run the grammar and protocol tests:

```powershell
cd .\editors\vscode
npm install
npm test
npm run test:extension
npm run build
npm run package
```

The bundled language server and compiler require an installed .NET 10 runtime. Set their respective `dotnetPath` settings when `dotnet` is not on `PATH`. Semantic highlighting follows VS Code's `editor.semanticHighlighting.enabled` setting and the active theme; TextMate highlighting remains available for lexical and unresolved syntax. Use **C~: Show Language Server Output**, **C~: Restart Language Server**, or `ctilde.trace.server` when troubleshooting.

To try the extension, run `npm run build`, open `editors/vscode` in Visual Studio Code, and press F5. In the Extension Development Host, open a `.ct` file and request completion after `Console.`.

### Use a development compiler from an installed extension

The packaged extension normally runs its bundled language server. During compiler development, point the installed extension at the language-server build in this repository instead. When the repository root is the VS Code workspace, add:

```json
{
  "ctilde.languageServer.serverPath": "${workspaceFolder}/CTilde.LanguageServer/bin/Debug/net10.0/CTilde.LanguageServer.dll",
  "ctilde.languageServer.restartOnServerChange": true
}
```

When `editors/vscode` is the workspace, use `${workspaceFolder}/../../CTilde.LanguageServer/bin/Debug/net10.0/CTilde.LanguageServer.dll` instead. Absolute paths and paths relative to the first workspace folder are also accepted.

Build after changing the compiler or language server:

```powershell
dotnet build .\CTilde.LanguageServer\CTilde.LanguageServer.csproj
```

The incremental build updates both `CTilde.LanguageServer.dll` and `CTilde.Compiler.dll`. The extension watches those files, waits for the build writes to settle, copies the completed output to private extension storage, and restarts from that shadow copy. The server therefore does not lock the repository build output on Windows. This workflow does not require rebuilding the TypeScript client, repackaging the VSIX, or reinstalling the extension. Clear `ctilde.languageServer.serverPath` to return to the bundled server.

The extension always starts a built assembly with `dotnet <server.dll> --stdio`. Do not configure `dotnet run` as the server command because build or console output on standard output would corrupt the LSP protocol stream.

The extension uses the same Unlicense terms as the repository root.
