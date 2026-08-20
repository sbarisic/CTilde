# C~ Language Support for Visual Studio Code

This extension adds C~ (`.ct`) IntelliSense plus lexical and compiler-aware highlighting. It launches the repository's .NET language server as a separate process and uses the same compiler declarations, diagnostics, targets, and bundled standard library as command-line builds.

## Features

- Syntax highlighting for declarations, keywords, literals, attributes, comments, operators, and punctuation.
- Compiler-aware highlighting for resolved namespaces, classes, structs, enums, enum members, parameters, locals, properties, fields, methods, and constructors.
- Semantic modifiers for declarations, static and readonly symbols, and embedded standard-library references.
- Comment toggling for `//` and `/* */` comments.
- Bracket matching, automatic closing, surrounding pairs, brace indentation, and region folding.
- Unicode identifiers and keyword identifiers escaped with `@`.
- Draft 0.14 syntax and semantic classification, including draft 0.13 raw inline-assembly blocks and operand navigation, standard-library fault exceptions and vectors, operator declarations, and the native integer, interop, delegate, and callback surface.
- Target-aware hosted completion and documentation for console input, `System.IO.IOException`, and owned binary-file handles; ESP-IDF projects omit these APIs.
- Context-aware completion for keywords, types, locals, parameters, fields, properties, methods, enum members, and namespaces.
- C#-style `///` XML documentation in lazily resolved completion details, hover, and signature help, including active-parameter descriptions.
- Static/instance, inheritance, accessibility, lexical-scope, overload, and hosted/ESP-IDF filtering.
- Live compiler diagnostics with related locations.
- Hover, signature help, go-to-definition, document symbols, and workspace symbols.
- Read-only navigation into embedded `System` and `Esp.Idf` sources.
- JSON validation for `ctilde.json` project manifests.
- Check and native Build commands plus discoverable tasks for every workspace project.

Supported documentation elements are `summary`, `param`, `returns`, `remarks`, `exception`, `see`, `paramref`, and sole-element `inheritdoc`. Documentation warnings remain non-blocking. Links, documentation-tag completion, XML output files, raw Markdown/HTML, and block documentation comments are not implemented.

Rename, references, formatting, debugging, code actions, auto-import edits, and incremental semantic-token deltas are not implemented.

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

The compiler accepts the same manifest through `ctilde --project <ctilde.json>`. `target` defaults to `hosted`; `sources` is required. The optional `build` object overrides generated output, hosted compiler/configuration/executable, or the ESP-IDF project directory. All manifest paths remain inside the project root.

## Project builds

Use **C~: Check Project** or **C~: Build Project**. The active file's nearest manifest is selected; a picker appears when several projects are open and none owns the active file. The same actions are available under **Tasks: Run Task** as one Check and Build task per manifest. Command-driven builds save dirty source and manifest files first.

Hosted Build emits C and a native header, discovers MSVC/GCC/Clang, and creates the configured executable. ESP-IDF Build emits into the component and invokes `idf.py build`; target selection, flashing, and monitoring remain ESP-IDF operations.

The VSIX includes a framework-dependent compiler fallback. For compiler development without rebuilding the extension, configure:

```json
{
  "ctilde.compiler.compilerPath": "${workspaceFolder}/CTilde.Cli/bin/Debug/net10.0/ctilde.dll"
}
```

An external self-contained `ctilde` executable is also accepted. `ctilde.compiler.dotnetPath` selects the host for DLLs, `ctilde.compiler.nativeCompiler` optionally overrides the hosted C compiler, and `ctilde.compiler.idfPath` locates an ESP-IDF installation when its environment is not active. The CLI process is short-lived, so rebuilding an external compiler requires no extension restart or shadow copy.

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
