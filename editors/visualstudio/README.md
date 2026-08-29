# C~ for Visual Studio

This preview extension adds C~ editor and manifest-backed project support to Visual Studio 2022 17.14 or newer. The same AMD64 VSIX is eligible for Visual Studio 2026 under Microsoft's open-ended VSIX compatibility model, but the 2026 claim remains unverified until the checklist below passes there.

## Features

- TextMate syntax highlighting for `.ct` files.
- Theme-adaptive C#-style classifications for types, methods, locals, strings, numbers, comments, operators, and punctuation in Visual Studio.
- C~ language-server diagnostics, completion, completion resolve, hover, signatures, definitions, references, document symbols, workspace symbols, and semantic tokens.
- Read-only navigation into the bundled standard library.
- `.ctproj` CPS projects backed by an authoritative `ctilde.json` manifest.
- Check, Build, Clean, Rebuild, cancellation, and external-console Run commands under **Tools**.
- **Start Without Debugging** (`Ctrl+F5`) runs the startup C~ project through the same external-console workflow as **Run C~ Project**.
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

Optional `dotnet`, compiler, language-server, and tracing settings are under **Tools > Options > C~**. The compiler and language server are bundled and are the defaults.

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

Regular **Start** (`F5`) does not run or attach a debugger. It reports that C~ debugging is not available yet and directs you to `Ctrl+F5` or **Run C~ Project**. Standard-library projects and manifests without a supported run configuration cannot be launched.

## Deliberate v0.11 boundaries

Debugging, Attach, GDB/DAP integration, ESP serial sessions, Open Folder build commands, and command-line `.ctproj` builds are not included. Loose `.ct` files retain syntax and language-server features, but project commands require a loaded `.ctproj`.

Homepage: [ctilde.sbarisic.com](https://ctilde.sbarisic.com)  
Repository and issues: [github.com/sbarisic/CTilde](https://github.com/sbarisic/CTilde)

## Visual Studio 2022 and 2026 validation checklist

Run this identical checklist on Visual Studio 2022 17.14 and Visual Studio 2026:

1. Install the AMD64 VSIX, create the hosted-console template, and reopen the solution.
2. Wrap an existing `ctilde.json` with **Create Visual Studio Project from C~ Manifest** and verify no existing file is overwritten.
3. Verify hierarchy exclusions and lexical plus semantic highlighting.
4. Verify diagnostics, completion and resolve, hover, signatures, user and standard-library definitions, references, and symbols.
5. Verify Check, Build, Clean, Rebuild, **Run C~ Project**, and `Ctrl+F5`, including cancellation, full C~ output, duplicate-process protection, and Error List navigation.
6. Verify `F5` reports that debugging is unavailable and recommends `Ctrl+F5` or **Run C~ Project** without launching the program.
7. Verify a missing .NET 10 runtime produces actionable download guidance.
8. Select Debug and Release and verify neither rewrites nor overrides `build.configuration`.
9. Update the installed VSIX, uninstall it, and confirm the extension's files are removed.

Do not publish a Visual Studio 2026 compatibility claim until that environment passes every item.
