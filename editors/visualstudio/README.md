# C~ for Visual Studio

Version 0.15.0 of this preview extension supports C~ Draft 0.38 hosted x64 geometry optimization, `Vec3x4` debug presentation, SIMD128, matrices, quaternions, and manifest-backed projects in Visual Studio 2022 17.14 or newer. The same AMD64 VSIX is eligible for Visual Studio 2026 under Microsoft's open-ended VSIX compatibility model, but the 2026 claim remains unverified until the checklist below passes there.

## Features

- TextMate syntax highlighting for `.ct` files.
- Theme-adaptive C#-style classifications for types, methods, locals, strings, numbers, comments, operators, and punctuation in Visual Studio.
- C~ language-server diagnostics, completion, completion resolve, hover, signatures, definitions, references, document symbols, workspace symbols, and semantic tokens.
- C#-style `0 references`, `1 reference`, and plural reference CodeLens indicators above named declarations, with lazy reference details and source navigation.
- Read-only navigation into the bundled standard library.
- `.ctproj` CPS projects backed by an authoritative `ctilde.json` manifest.
- Check, Build, Clean, Rebuild, cancellation, and external-console Run commands under **Tools**.
- **Start Without Debugging** (`Ctrl+F5`) runs the startup C~ project through the same external-console workflow as **Run C~ Project**.
- **Start Debugging** (`F5`) launches the bundled C~ debug adapter for hosted projects built explicitly with GCC, Clang, or WSL-GCC.
- A **C~ Hosted Console Application** project template.
- **Create Visual Studio Project from C~ Manifest** for existing projects.

Visual Studio's Debug/Release selection is for solution organization only. C~ always uses `build.configuration` and all other build settings from `ctilde.json`.

Completion collapses method overloads into one C#-style row while preserving every overload in signature help. Each source document is analyzed with the loaded manifest that contains it, so completion and navigation use sibling files even when another project is selected as the startup project.

Syntax colors use Visual Studio's built-in classifications instead of fixed C~ colors. Method declarations and calls therefore follow the active theme's **method name** color, while types follow its **type** color. Light, dark, custom, and high-contrast themes remain authoritative.

## Requirements

- Windows x64.
- Visual Studio 2022 17.14 or newer with the Core Editor.
- The [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- The native toolchain required by the selected manifest target.

Optional `dotnet`, compiler, language-server, and tracing settings are under **Tools > Options > C~**. Debugger settings add the debug compiler, GDB path, memory diagnostics, stop-at-entry, runtime-frame visibility, and DAP/GDB tracing. The compiler, language server, and debug adapter are bundled; .NET 10 and the native debugger remain external requirements.

Reference CodeLens is enabled by default under **Tools > Options > C~ > Language server > Show reference CodeLens** and also follows Visual Studio's global CodeLens setting. Counts cover all loaded C~ projects, refresh after saved or unsaved source changes, and exclude the declaration itself. **Find All References** uses the same semantic index.

## Project contract

`.ctproj` is an IDE wrapper, not a command-line build format:

```xml
<Project ToolsVersion="Current" DefaultTargets="Build">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
  <PropertyGroup>
    <ProjectGuid>{project-guid}</ProjectGuid>
    <CTildeManifest>ctilde.json</CTildeManifest>
    <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
    <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
    <OutputPath>$(MSBuildProjectDirectory)\obj\$(Configuration)\</OutputPath>
    <DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>
    <DefineCommonItemSchemas>true</DefineCommonItemSchemas>
    <CTildeProjectItemExcludes>$(DefaultItemExcludes);$(DefaultExcludesInProjectFolder);.git\**;**\.git\**;.vs\**;**\.vs\**;.ctilde\**;**\.ctilde\**;.ctilde-cache\**;**\.ctilde-cache\**;bin\**;**\bin\**;obj\**;**\obj\**;build\**;**\build\**;node_modules\**;**\node_modules\**;main\generated\**;**\main\generated\**;managed_components\**;**\managed_components\**;**\*.ctproj</CTildeProjectItemExcludes>
  </PropertyGroup>
  <ItemGroup Label="ProjectConfigurations">
    <ProjectConfiguration Include="Debug|AnyCPU"><Configuration>Debug</Configuration><Platform>AnyCPU</Platform></ProjectConfiguration>
    <ProjectConfiguration Include="Release|AnyCPU"><Configuration>Release</Configuration><Platform>AnyCPU</Platform></ProjectConfiguration>
  </ItemGroup>
  <ItemGroup>
    <ProjectCapability Include="CTilde" />
    <ProjectCapability Include="UseFileGlobs" />
    <ProjectCapability Include="OpenProjectFile" />
    <ProjectCapability Include="HandlesOwnReload" />
    <ProjectCapability Include="ProjectConfigurationsDeclaredAsItems" />
  </ItemGroup>
  <ItemGroup>
    <None Include="**\*" Exclude="$(CTildeProjectItemExcludes)" />
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.Common.targets" />
  <Import Project="$(CTildeProjectSystemPath)\CTilde.targets" Condition="Exists('$(CTildeProjectSystemPath)\CTilde.targets')" />
</Project>
```

The common imports and item schemas are required by CPS for the project hierarchy and property catalogs; they do not make `.ctproj` a supported command-line build format. `CTildeManifest` is project-relative and defaults to `ctilde.json`. Command-line and CI builds continue to use `dotnet ctilde.dll --project ctilde.json` or the corresponding installed `ctilde` command.

The repository solution contains 13 ready-to-load C~ projects under **C~**: the physical standard library and 12 example/target entries. Select the intended project before running a command. This is required for the three T-CAN wrappers because they share one directory but reference different manifests. The entries deliberately omit `Build.0`, so **Build Solution** continues to build only the .NET solution projects.

## Running and debugging

Use **Start Without Debugging** (`Ctrl+F5`) or **Tools > Run C~ Project** to build and run a runnable manifest in a new external console. Both entry points share path resolution, missing-runtime reporting, and one-running-process-per-project protection.

Regular **Start** (`F5`) supports hosted C~ applications with an explicit GDB-capable compiler and the `esp32_qemu` and `esp32c3_qemu` targets. For hosted projects, configure `gcc`, `clang`, `wsl:gcc`, or a matching executable under **Tools > Options > C~ > Debugger**, in `build.compiler`, or through `CTILDE_CC` when the manifest compiler is `auto`. The Visual Studio override wins, followed by the manifest and then `CTILDE_CC`. F5 never silently replaces `auto`, MSVC, or clang-cl with another toolchain.

Each new F5 session rebuilds an instrumented image and validates the version-3 descriptor, debug map, executable, control-block layout, site count, and every recorded source hash before GDB starts. Breakpoints, conditions, hit counts, logpoints, function and data breakpoints, C~-mapped stacks and logical source stepping, threads, live lexical locals, arguments, statics, ARC/runtime diagnostics, watches, memory reads, runtime exception filters, pause, restart, and stop are available. The yellow arrow identifies the next C~ statement; its variables and Runtime probe entry are captured from that exact logical site. Watches intentionally accept identifiers, fields, and array indices rather than arbitrary C~ expression execution. Hosted inferiors run in an external native or WSL console so stdin, Unicode, colors, signals, arguments, environment, and the manifest working directory behave like a direct launch.

ESP QEMU sessions always use the cross-GDB and owned `idf.py qemu --gdb` command recorded by the prepared descriptor. Configure an optional ESP-IDF root and Espressif Clang executable under **Tools > Options > C~ > ESP-IDF**; blank values fall back to `IDF_PATH`, `CTILDE_ESP_CLANG`, and the compiler's normal discovery. The adapter verifies that `127.0.0.1:3333` is free, forwards QEMU output to the Debug Console, synchronizes logical state at `ct_debug_qemu_ready`, and uses `ct_debug_qemu_trap` for later logical stops. Restart creates a fresh emulator and GDB session. Stop closes the owned Windows Job Object so the complete emulator process tree is terminated and the fixed port is released.

Install the ESP-IDF-managed emulator packages before the first QEMU session:

```powershell
python "C:\esp\v6.0.2\esp-idf\tools\idf_tools.py" install qemu-xtensa qemu-riscv32
```

Run uses a project lease; Debug uses a manifest-specific descriptor lease so same-directory target variants do not overwrite each other. The fixed QEMU endpoint still permits only one ESP QEMU session at a time, and a second session receives an explicit port-conflict error. Standard-library and unsupported targets receive a specific unsupported-launch error.

## Deliberate v0.12 boundaries

Attach, physical ESP/UART debugging, generic CLI QEMU run, peripheral emulation, MSVC/clang-cl debugging, freestanding, Cosmopolitan, mixed-mode and child-process debugging, arbitrary watch expression execution, Open Folder build commands, and command-line `.ctproj` builds are not included. Loose `.ct` files retain syntax and language-server features, but project commands require a loaded `.ctproj`.

Homepage: [ctilde.sbarisic.com](https://ctilde.sbarisic.com)  
Repository and issues: [github.com/sbarisic/CTilde](https://github.com/sbarisic/CTilde)

## Visual Studio 2022 and 2026 validation checklist

Run this identical checklist on Visual Studio 2022 17.14 and Visual Studio 2026:

1. Install the AMD64 VSIX, create the hosted-console template, and reopen the solution.
2. Wrap an existing `ctilde.json` with **Create Visual Studio Project from C~ Manifest** and verify no existing file is overwritten.
3. Verify hierarchy exclusions and lexical plus semantic highlighting.
4. Verify diagnostics, completion and resolve, hover, signatures, user and standard-library definitions, Find All References, symbols, and reference CodeLens counts/details/navigation. Add and remove a call in another file and verify the count refreshes without restarting Visual Studio.
5. Verify Check, Build, Clean, Rebuild, **Run C~ Project**, and `Ctrl+F5`, including cancellation, full C~ output, duplicate-process protection, and Error List navigation.
6. Configure GCC, Clang, or WSL-GCC and verify `F5` source/function/data breakpoints, conditions, logpoints, cross-file stepping, locals, arrays, objects, statics, watches, memory, runtime exceptions, restart, stop, and external-console I/O. Verify `auto`, MSVC, and missing GDB report actionable errors before adapter launch.
7. Configure ESP-IDF and Espressif Clang, then verify both T-CAN485 QEMU targets reach `CTILDE_ESP_QEMU_OK`, stop on mapped C~ breakpoints, step, restart, stop, leave no owned QEMU/GDB process, and release port 3333.
8. Verify QEMU Attach, a generic GDB override, a busy port 3333, and missing emulator packages produce actionable errors.
9. Verify a missing .NET 10 runtime produces actionable download guidance.
10. Select Debug and Release and verify neither rewrites nor overrides `build.configuration`.
11. Update the installed VSIX, uninstall it, and confirm the extension's files are removed.

Do not publish a Visual Studio 2026 compatibility claim until that environment passes every item.

Visual Studio does not currently expose document formatting or format-on-save for C~. Use `ctilde format <path>` to write canonical source or `ctilde format --check <path>` for validation.
