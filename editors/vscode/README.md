# C~ Language Support for Visual Studio Code

This extension adds C~ (`.ct`) IntelliSense plus lexical and compiler-aware highlighting. It launches the repository's .NET language server as a separate process and uses the same compiler declarations, diagnostics, targets, and bundled standard library as command-line builds.

## Features

- Syntax highlighting for declarations, keywords, literals, attributes, comments, operators, and punctuation.
- Compiler-aware highlighting for resolved namespaces, classes, structs, enums, enum members, parameters, locals, properties, fields, methods, and constructors.
- Semantic modifiers for declarations, static and readonly symbols, and embedded standard-library references.
- Comment toggling for `//` and `/* */` comments.
- Bracket matching, automatic closing, surrounding pairs, brace indentation, and region folding.
- Unicode identifiers and keyword identifiers escaped with `@`.
- Draft 0.9 syntax and semantic classification for native integers, by-reference calls, buffers, `NativeUtf8String`, opaque declarations, native ownership attributes, exports, named delegates, callback adapters, and unmanaged function pointers.
- Context-aware completion for keywords, types, locals, parameters, fields, properties, methods, enum members, and namespaces.
- Static/instance, inheritance, accessibility, lexical-scope, overload, and hosted/ESP-IDF filtering.
- Live compiler diagnostics with related locations.
- Hover, signature help, go-to-definition, document symbols, and workspace symbols.
- Read-only navigation into embedded `System` and `Esp.Idf` sources.
- JSON validation for `ctilde.json` project manifests.

Rename, references, formatting, debugging, code actions, auto-import edits, and incremental semantic-token deltas are not implemented.

## Projects

Put `ctilde.json` at a project root to select the target and source set:

```json
{
  "target": "esp-idf",
  "sources": ["Program.ct"]
}
```

The nearest ancestor manifest owns a file. Source and exclusion globs are relative to the manifest and cannot escape its directory. A file excluded from that source set is analyzed independently with the manifest target. Without a manifest, the extension treats each file as a standalone hosted program.

The compiler accepts the same manifest through `ctilde --project <ctilde.json>`. `target` defaults to `hosted`; `sources` is required.

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

The extension requires an installed .NET 10 runtime. Set `ctilde.languageServer.dotnetPath` when `dotnet` is not on `PATH`. Semantic highlighting follows VS Code's `editor.semanticHighlighting.enabled` setting and the active theme; TextMate highlighting remains available for lexical and unresolved syntax. Use **C~: Show Language Server Output**, **C~: Restart Language Server**, or `ctilde.trace.server` when troubleshooting.

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

The incremental build updates both `CTilde.LanguageServer.dll` and `CTilde.Compiler.dll`. The extension watches those files, waits for the build writes to settle, and restarts the server automatically. This workflow does not require rebuilding the TypeScript client, repackaging the VSIX, or reinstalling the extension. Clear `ctilde.languageServer.serverPath` to return to the bundled server.

The extension always starts a built assembly with `dotnet <server.dll> --stdio`. Do not configure `dotnet run` as the server command because build or console output on standard output would corrupt the LSP protocol stream.

The extension uses the same Unlicense terms as the repository root.
