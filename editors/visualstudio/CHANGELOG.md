# Changelog

## 0.11.0 - Preview

- Added TextMate and C~ language-server editor support.
- Added the `.ctproj` CPS project type backed by `ctilde.json`.
- Added Check, Build, Clean, Rebuild, cancellation, and external-console Run commands.
- Added a hosted-console template and manifest-wrapper command.
- Added versioned read-only standard-library navigation.
- Added Visual Studio options for tool paths and protocol tracing.
- Added CPS launch integration: `Ctrl+F5` runs through the external-console project workflow, while `F5` reports that debugging is not available yet.
- Collapsed method overloads into one completion row without removing overloads from signature help.
- Fixed incomplete member-access completion and multi-file project context in non-main source files.
- Added a Visual Studio-specific TextMate classification map so methods, types, locals, literals, comments, operators, and punctuation follow the active theme's C#-style colors.

Debugging and Attach are intentionally deferred.
