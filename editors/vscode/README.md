# C~ Language Support for Visual Studio Code

This extension adds syntax highlighting and basic editor behavior for C~ (`.ct`) source files. Its grammar follows the implemented draft 0.5 language in the CTilde compiler repository.

## Features

- Syntax highlighting for declarations, keywords, literals, attributes, comments, operators, and punctuation.
- Comment toggling for `//` and `/* */` comments.
- Bracket matching, automatic closing, surrounding pairs, brace indentation, and region folding.
- Unicode identifiers and keyword identifiers escaped with `@`.

The extension is intentionally declarative. It does not provide completion, compiler diagnostics, navigation, formatting, debugging, snippets, or semantic highlighting.

## Development

Install the test dependencies and run the grammar tests:

```powershell
cd .\editors\vscode
npm install
npm test
```

To try the extension, open `editors/vscode` as a folder in Visual Studio Code and press F5. In the Extension Development Host window, open any `.ct` file and use **Developer: Inspect Editor Tokens and Scopes** to inspect its TextMate scopes.

The extension uses the same Unlicense terms as the repository root.
