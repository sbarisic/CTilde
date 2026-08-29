using System.Collections.Immutable;
using CTilde;

namespace CTilde.Cli;

internal sealed record BuildRequest(
    IReadOnlyCollection<string> Inputs,
    CompilationTarget Target,
    CompilationArchitecture Architecture,
    string? ManifestPath,
    string RootDirectory,
    string? SourceRoot,
    string? GeneratedCPath,
    string? GeneratedHeaderPath,
    bool CheckOnly,
    bool Trace,
    bool BuildNative,
    bool RunAfterBuild,
    CTildeNativeBuildConfiguration Configuration,
    string Compiler,
    string? ExecutablePath,
    string? EspIdfProjectDirectory,
    string? EspIdfPath,
    GeneratedCLayout CLayout,
    string? GeneratedDirectory,
    string? SymbolMapPath,
    bool Lto,
    DebugInformationMode DebugInformation = DebugInformationMode.None,
    DebugMemoryMode DebugMemory = DebugMemoryMode.Off,
    string? DebugMapPath = null,
    string? PrepareDebug = null,
    string? DebugTargetPath = null,
    string? SerialPort = null,
    int BaudRate = 115200,
    IReadOnlyList<EspIdfBindingManifest>? BindingManifests = null,
    string? BindingGeneratedDirectory = null,
    bool GenerateBindingsOnly = false,
    bool VerifyBindings = false,
    string? EspClangPath = null,
    bool NoRecursion = false,
    EspIdfPanicPolicy PanicPolicy = EspIdfPanicPolicy.Abort,
    FreestandingProjectConfiguration? Freestanding = null,
    CosmopolitanRuntimeMode CosmopolitanMode = CosmopolitanRuntimeMode.Default,
    IReadOnlyList<CpuFeature>? CpuFeatures = null,
    IReadOnlyDictionary<string, SourceOwnerIdentity>? SourceOwners = null,
    CTildeProjectRunConfiguration? RunConfiguration = null,
    TargetEnvironment Environment = TargetEnvironment.Native,
    EspIdfChip? EspIdfChip = null,
    HostedProjectConfiguration? Hosted = null)
{
    public string EspIdfBuildDirectory => Environment == TargetEnvironment.Qemu
        ? Path.Combine(EspIdfProjectDirectory!, "build", EspIdfChip == CTilde.EspIdfChip.Esp32 ? "esp32_qemu" : "esp32c3_qemu")
        : Path.Combine(EspIdfProjectDirectory!, "build");

    public string LockDirectory => Target is CompilationTarget.Hosted or CompilationTarget.Freestanding or CompilationTarget.Cosmopolitan
        ? Path.GetDirectoryName(ExecutablePath!)!
        : EspIdfBuildDirectory;

    public IReadOnlyList<string> GeneratedSourcePaths => CLayout == GeneratedCLayout.Unity
        ? [GeneratedCPath!]
        : Directory.EnumerateFiles(GeneratedDirectory!, "*.c", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Equals("ctilde_runtime.c", StringComparison.Ordinal) ||
                           Path.GetFileName(path).Equals("ctilde_entry.c", StringComparison.Ordinal) ||
                           Path.GetFileName(path).StartsWith("source_", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
}

internal static class BuildRequestResolver
{
    public static BuildRequest Resolve(CommandLineOptions options)
    {
        ValidateCommon(options);
        return options.ProjectManifest is not null ? ResolveProject(options) : ResolveDirect(options);
    }

    private static BuildRequest ResolveProject(CommandLineOptions options)
    {
        if (options.Inputs.Count != 0 || options.InputDirectory is not null || options.TargetSpecified)
            throw new CommandLineException("--project cannot be combined with input files, --compile-directory, or --target.");

        var project = CTildeProjectFile.Load(options.ProjectManifest!);
        var build = project.Configuration.Build ?? throw new CommandLineException("Standard-library projects are validated before build-request resolution.");
        ValidateTargetOptions(options, project.Configuration.Target, project.Configuration.Environment);
        var layout = options.CLayout ?? build.CLayout;
        if (layout == GeneratedCLayout.Modules && options.Output is not null)
            throw new CommandLineException("-o cannot be combined with modular project output; use --output-directory.");
        if (layout == GeneratedCLayout.Unity && options.OutputDirectory is not null)
            throw new CommandLineException("--output-directory requires modular C output.");
        var preparingLaunch = options.PrepareDebug == "launch";
        var preparingAttach = options.PrepareDebug == "attach";
        var checkOnly = options.CheckOnly;
        var buildNative = options.Build || options.Run || preparingLaunch;
        var configuration = preparingLaunch && project.Configuration.Target == CompilationTarget.Hosted
            ? CTildeNativeBuildConfiguration.Debug
            : options.Configuration ?? build.Configuration;
        var debugInformation = preparingLaunch ? DebugInformationMode.Instrumented : options.DebugInfo || preparingAttach ||
            (buildNative && (project.Configuration.Target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && configuration == CTildeNativeBuildConfiguration.Debug)
                ? DebugInformationMode.Source : DebugInformationMode.None;
        var debugMemory = preparingLaunch ? options.DebugMemory ?? DebugMemoryMode.Objects : DebugMemoryMode.Off;
        var generatedC = checkOnly || layout == GeneratedCLayout.Modules ? null : Path.GetFullPath(options.Output ?? build.GeneratedCPath);
        var generatedDirectory = checkOnly || layout == GeneratedCLayout.Unity ? null : Path.GetFullPath(options.OutputDirectory ?? build.GeneratedDirectory);
        var generatedHeader = options.CheckOnly ? null : Path.GetFullPath(options.HeaderOutput ?? build.GeneratedHeaderPath);
        var symbolMap = options.CheckOnly ? null : options.SymbolMap is not null ? Path.GetFullPath(options.SymbolMap) : build.SymbolMapPath;
        var debugMap = debugInformation != DebugInformationMode.None
            ? Path.GetFullPath(options.DebugMap ?? (layout == GeneratedCLayout.Modules
                ? Path.Combine(generatedDirectory!, "ctilde_debug.json")
                : Path.Combine(Path.GetDirectoryName(generatedC!)!, "ctilde_debug.json")))
            : null;
        var nativeOutput = options.NativeOutput ?? build.ExecutablePath;
        var executable = project.Configuration.Target is CompilationTarget.Hosted or CompilationTarget.Freestanding or CompilationTarget.Cosmopolitan && nativeOutput is not null
            ? Path.GetFullPath(nativeOutput)
            : null;
        var idfProject = project.Configuration.Target == CompilationTarget.EspIdf
            ? Path.GetFullPath(options.EspIdfProject ?? build.EspIdfProjectDirectory!)
            : null;
        var lto = options.Lto || (build.Lto && !preparingLaunch);
        if (lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CommandLineException("--lto requires a Release configuration.");
        if (buildNative && idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC, generatedHeader, generatedDirectory);
        var debugTarget = options.PrepareDebug is null ? null : Path.GetFullPath(options.DebugTarget ??
            (project.Configuration.Target == CompilationTarget.Hosted
                ? Path.Combine(Path.GetDirectoryName(executable!)!, ".ctilde", "ctilde-debug-target.json")
                : Path.Combine(EspBuildDirectory(idfProject!, project.Configuration.Environment, project.Configuration.EspIdfChip), ".ctilde", "ctilde-debug-target.json")));
        ValidateDistinctOutputs(generatedC, generatedHeader, executable,
            project.Configuration.Target == CompilationTarget.Cosmopolitan && executable is not null ? executable + ".dbg" : null,
            symbolMap, debugMap, debugTarget);
        var architecture = ResolveArchitecture(options.ArchitectureSpecified ? options.Architecture : project.Configuration.Architecture,
            project.Configuration.Target, options.Compiler ?? build.Compiler, idfProject, project.Configuration.Environment, project.Configuration.EspIdfChip);
        if (project.Configuration.Target == CompilationTarget.Cosmopolitan && architecture != CompilationArchitecture.X64)
            throw new CommandLineException("Draft 0.25 Cosmopolitan projects require architecture 'x64'.");
        var freestanding = project.Configuration.Target == CompilationTarget.Freestanding
            ? ResolveFreestanding(options, project.Configuration.Freestanding, project.RootDirectory, buildNative, executable)
            : null;
        return new BuildRequest(project.SourceFiles, project.Configuration.Target, architecture, project.ManifestPath,
            project.RootDirectory, ResolveSourceRoot(options), generatedC, generatedHeader, checkOnly, options.Trace, buildNative && !preparingAttach,
            options.Run, configuration, options.Compiler ?? build.Compiler, executable,
            idfProject, options.EspIdfPath, layout, generatedDirectory, symbolMap, lto, debugInformation, debugMemory, debugMap,
            options.PrepareDebug, debugTarget, options.SerialPort, options.BaudRate, project.Configuration.BindingManifests,
            build.GeneratedDirectory, options.GenerateBindings, options.VerifyBindings, options.EspClangPath,
            options.NoRecursion || project.Configuration.NoRecursion,
            options.PanicPolicySpecified ? options.PanicPolicy : project.Configuration.PanicPolicy, freestanding,
            options.CosmopolitanModeSpecified ? options.CosmopolitanMode : project.Configuration.Cosmopolitan?.Mode ?? CosmopolitanRuntimeMode.Default,
            options.CpuFeatures.Count == 0 ? project.Configuration.CpuFeatures : options.CpuFeatures,
            project.SourceOwners, project.Configuration.Run, project.Configuration.Environment, project.Configuration.EspIdfChip, project.Configuration.Hosted);
    }

    private static BuildRequest ResolveDirect(CommandLineOptions options)
    {
        if (options.Inputs.Count == 0)
            throw new CommandLineException("At least one .ct input file is required.");
        ValidateTargetOptions(options, options.Target, options.Environment);
        if (!options.CheckOnly && !options.Build && options.PrepareDebug is null && options.CLayout != GeneratedCLayout.Modules && string.IsNullOrWhiteSpace(options.Output))
            throw new CommandLineException("-o is required unless --check or --build is used.");
        if ((options.Build || options.PrepareDebug == "launch") && options.Target == CompilationTarget.EspIdf &&
            ((options.CLayout != GeneratedCLayout.Modules && string.IsNullOrWhiteSpace(options.Output)) || string.IsNullOrWhiteSpace(options.EspIdfProject)))
            throw new CommandLineException("Direct ESP-IDF builds require a generated output and --idf-project.");

        var root = Directory.GetCurrentDirectory();
        var preparingLaunch = options.PrepareDebug == "launch";
        var preparingAttach = options.PrepareDebug == "attach";
        var buildNative = options.Build || options.Run || preparingLaunch;
        if (buildNative && options.Target == CompilationTarget.Freestanding && options.NativeOutput is null)
            throw new CommandLineException("Direct freestanding builds require --native-output.");
        var layout = options.CLayout ?? GeneratedCLayout.Unity;
        var generatedC = options.CheckOnly || layout == GeneratedCLayout.Modules ? null : Path.GetFullPath(options.Output ?? Path.Combine(root, "build", "generated", "ctilde_program.c"));
        var generatedDirectory = options.CheckOnly || layout == GeneratedCLayout.Unity ? null : Path.GetFullPath(options.OutputDirectory ?? Path.Combine(root, "build", "generated", "modules"));
        var generatedHeader = options.CheckOnly ? null : options.HeaderOutput is not null
            ? Path.GetFullPath(options.HeaderOutput)
            : buildNative
                ? Path.Combine(layout == GeneratedCLayout.Unity ? Path.GetDirectoryName(generatedC)! : generatedDirectory!, "ctilde_exports.h")
                : null;
        var symbolMap = options.CheckOnly ? null : options.SymbolMap is null ? null : Path.GetFullPath(options.SymbolMap);
        var executable = options.Target is CompilationTarget.Hosted or CompilationTarget.Freestanding or CompilationTarget.Cosmopolitan && (buildNative || preparingAttach)
            ? Path.GetFullPath(options.NativeOutput ?? Path.Combine(root, "build", options.Target == CompilationTarget.Cosmopolitan
                ? "program.com"
                : $"program{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}"))
            : null;
        var idfProject = options.Target == CompilationTarget.EspIdf && (buildNative || preparingAttach)
            ? Path.GetFullPath(options.EspIdfProject!)
            : null;
        var configuration = preparingLaunch && options.Target == CompilationTarget.Hosted
            ? CTildeNativeBuildConfiguration.Debug
            : options.Configuration ?? CTildeNativeBuildConfiguration.Debug;
        if (options.Lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CommandLineException("--lto requires --configuration release.");
        if (idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC, generatedHeader, generatedDirectory);
        var debugInformation = preparingLaunch ? DebugInformationMode.Instrumented : options.DebugInfo || preparingAttach ||
            (buildNative && (options.Target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && configuration == CTildeNativeBuildConfiguration.Debug)
                ? DebugInformationMode.Source : DebugInformationMode.None;
        var debugMemory = preparingLaunch ? options.DebugMemory ?? DebugMemoryMode.Objects : DebugMemoryMode.Off;
        var debugMap = debugInformation != DebugInformationMode.None
            ? Path.GetFullPath(options.DebugMap ?? (layout == GeneratedCLayout.Modules
                ? Path.Combine(generatedDirectory!, "ctilde_debug.json")
                : Path.Combine(Path.GetDirectoryName(generatedC!)!, "ctilde_debug.json")))
            : null;
        var debugTarget = options.PrepareDebug is null ? null : Path.GetFullPath(options.DebugTarget ??
            (options.Target == CompilationTarget.Hosted
                ? Path.Combine(Path.GetDirectoryName(executable!)!, ".ctilde", "ctilde-debug-target.json")
                : Path.Combine(EspBuildDirectory(idfProject!, options.Environment, options.EspIdfChip), ".ctilde", "ctilde-debug-target.json")));
        ValidateDistinctOutputs(generatedC, generatedHeader, executable,
            options.Target == CompilationTarget.Cosmopolitan && executable is not null ? executable + ".dbg" : null,
            symbolMap, debugMap, debugTarget);
        var architecture = ResolveArchitecture(options.Architecture, options.Target, options.Compiler ?? "auto", idfProject, options.Environment, options.EspIdfChip);
        if (options.Target == CompilationTarget.Cosmopolitan && architecture != CompilationArchitecture.X64)
            throw new CommandLineException("Draft 0.25 Cosmopolitan builds require --architecture x64.");
        var freestanding = options.Target == CompilationTarget.Freestanding
            ? ResolveFreestanding(options, null, root, buildNative, executable)
            : null;
        return new BuildRequest(options.Inputs.Select(Path.GetFullPath).ToArray(), options.Target, architecture, null, root,
            ResolveSourceRoot(options),
            generatedC, generatedHeader, options.CheckOnly, options.Trace, buildNative && !preparingAttach, false,
            configuration, options.Compiler ?? "auto",
            executable, idfProject, options.EspIdfPath, layout, generatedDirectory, symbolMap, options.Lto,
            debugInformation, debugMemory, debugMap, options.PrepareDebug, debugTarget, options.SerialPort, options.BaudRate,
            null, null, false, false, null, options.NoRecursion, options.PanicPolicy, freestanding, options.CosmopolitanMode, options.CpuFeatures,
            null, null, options.Environment, options.EspIdfChip);
    }

    private static void ValidateCommon(CommandLineOptions options)
    {
        var hasNativeOptions = options.Configuration is not null || options.Compiler is not null || options.CosmopolitanModeSpecified || options.Lto ||
            options.NativeOutput is not null || options.EspIdfProject is not null ||
            options.LinkerScript is not null || options.EntrySymbol is not null || options.NativeSources.Count != 0 ||
            options.ObjectFiles.Count != 0 || options.Libraries.Count != 0 || options.CompileOptions.Count != 0 || options.LinkOptions.Count != 0 ||
            (options.EspIdfPath is not null && !options.CheckOnly && !options.GenerateBindings && !options.VerifyBindings);
        if (options.GenerateBindings && options.VerifyBindings)
            throw new CommandLineException("--generate-bindings and --verify-bindings cannot be combined.");
        if ((options.GenerateBindings || options.VerifyBindings) && (options.ProjectManifest is null || options.Inputs.Count != 0 || options.InputDirectory is not null))
            throw new CommandLineException("Binding generation requires --project and cannot be combined with direct inputs or --compile-directory.");
        if ((options.GenerateBindings || options.VerifyBindings) && (options.Build || options.Run || options.CheckOnly || options.PrepareDebug is not null))
            throw new CommandLineException("Binding-only modes cannot be combined with --build, --run, --check, or --prepare-debug.");
        if (options.Run && options.ProjectManifest is null)
            throw new CommandLineException("--run requires --project <ctilde.json>.");
        if (options.Run && (options.Build || options.CheckOnly || options.PrepareDebug is not null))
            throw new CommandLineException("--run cannot be combined with --build, --check, or --prepare-debug.");
        if (options.CheckOnly && (options.Build || options.Run || hasNativeOptions || options.HeaderOutput is not null || options.SymbolMap is not null ||
            options.OutputDirectory is not null || options.DebugInfo || options.DebugMemory is not null || options.DebugMap is not null || options.PrepareDebug is not null))
            throw new CommandLineException("--check cannot be combined with build outputs or native-build options.");
        if (!options.Build && !options.Run && options.PrepareDebug is null && hasNativeOptions)
            throw new CommandLineException("Native-build options require --build or --run.");
        if (options.DebugMap is not null && !options.DebugInfo && options.PrepareDebug is null)
            throw new CommandLineException("--debug-map requires --debug-info or --prepare-debug.");
        if (options.DebugMemory is not null && options.PrepareDebug != "launch")
            throw new CommandLineException("--debug-memory requires --prepare-debug launch.");
        if (options.DebugTarget is not null && options.PrepareDebug is null)
            throw new CommandLineException("--debug-target requires --prepare-debug.");
        if (options.PrepareDebug is not null && options.Build)
            throw new CommandLineException("--prepare-debug already performs the required build and cannot be combined with --build.");
        if (options.PrepareDebug == "attach" && options.Environment == TargetEnvironment.Qemu)
            throw new CommandLineException("QEMU targets support --prepare-debug launch only in v1; start a new debug Launch instead of attaching.");
        if (options.CLayout == GeneratedCLayout.Modules && options.Output is not null)
            throw new CommandLineException("-o cannot be combined with --c-layout modules; use --output-directory.");
        if (options.CLayout == GeneratedCLayout.Unity && options.OutputDirectory is not null)
            throw new CommandLineException("--output-directory requires --c-layout modules.");
    }

    private static CompilationArchitecture ResolveArchitecture(CompilationArchitecture requested, CompilationTarget target, string compiler, string? idfProject,
        TargetEnvironment environment, EspIdfChip? espIdfChip)
    {
        if (environment == TargetEnvironment.Qemu)
        {
            var required = espIdfChip == CTilde.EspIdfChip.Esp32 ? CompilationArchitecture.Xtensa : CompilationArchitecture.RiscV32;
            if (requested != CompilationArchitecture.Auto && requested != required)
                throw new CommandLineException($"The selected QEMU target requires architecture '{(required == CompilationArchitecture.Xtensa ? "xtensa" : "riscv32")}'.");
            return required;
        }
        if (requested != CompilationArchitecture.Auto)
            return requested;
        if (target == CompilationTarget.Hosted)
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X86 => CompilationArchitecture.X86,
                System.Runtime.InteropServices.Architecture.X64 => CompilationArchitecture.X64,
                System.Runtime.InteropServices.Architecture.Arm => CompilationArchitecture.Arm32,
                System.Runtime.InteropServices.Architecture.Arm64 => CompilationArchitecture.Arm64,
                _ => CompilationArchitecture.Auto,
            };
        if (idfProject is null)
            return CompilationArchitecture.Auto;
        var projectDescription = Path.Combine(idfProject, "build", "project_description.json");
        if (File.Exists(projectDescription))
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(projectDescription));
            if (document.RootElement.TryGetProperty("target", out var targetName))
                return targetName.GetString() is "esp32c3" or "esp32c6" or "esp32h2" ? CompilationArchitecture.RiscV32 : CompilationArchitecture.Xtensa;
        }
        var sdkconfig = Path.Combine(idfProject, "sdkconfig");
        if (File.Exists(sdkconfig))
        {
            var text = File.ReadAllText(sdkconfig);
            if (text.Contains("CONFIG_IDF_TARGET=\"esp32c3\"", StringComparison.Ordinal) ||
                text.Contains("CONFIG_IDF_TARGET=\"esp32c6\"", StringComparison.Ordinal) ||
                text.Contains("CONFIG_IDF_TARGET=\"esp32h2\"", StringComparison.Ordinal))
                return CompilationArchitecture.RiscV32;
            if (text.Contains("CONFIG_IDF_TARGET=", StringComparison.Ordinal))
                return CompilationArchitecture.Xtensa;
        }
        return CompilationArchitecture.Auto;
    }

    private static string EspBuildDirectory(string idfProject, TargetEnvironment environment, EspIdfChip? chip) => environment == TargetEnvironment.Qemu
        ? Path.Combine(idfProject, "build", chip == CTilde.EspIdfChip.Esp32 ? "esp32_qemu" : "esp32c3_qemu")
        : Path.Combine(idfProject, "build");

    private static void ValidateTargetOptions(CommandLineOptions options, CompilationTarget target, TargetEnvironment environment)
    {
        if (target != CompilationTarget.EspIdf && options.PanicPolicySpecified)
            throw new CommandLineException("--panic-policy is valid only for ESP-IDF builds.");
        if (target != CompilationTarget.EspIdf && (options.EspIdfProject is not null || options.EspIdfPath is not null))
            throw new CommandLineException("--idf-project and --idf-path are valid only for ESP-IDF builds.");
        if (target != CompilationTarget.EspIdf && (options.GenerateBindings || options.VerifyBindings || options.EspClangPath is not null))
            throw new CommandLineException("ESP-IDF binding options require an ESP-IDF project.");
        if (target == CompilationTarget.EspIdf && (options.Compiler is not null || options.NativeOutput is not null || options.Configuration is not null))
            throw new CommandLineException("--compiler, --native-output, and --configuration are valid only for hosted or Cosmopolitan builds.");
        if (target == CompilationTarget.EspIdf && options.Lto)
            throw new CommandLineException("--lto is a hosted or Cosmopolitan Release option; configure ESP-IDF LTO through sdkconfig.");
        if (target == CompilationTarget.EspIdf && options.SourceRoot is not null)
            throw new CommandLineException("--source-root is valid only for hosted or Cosmopolitan compilations.");
        if (target != CompilationTarget.Cosmopolitan && options.CosmopolitanModeSpecified)
            throw new CommandLineException("--cosmopolitan-mode is valid only for Cosmopolitan builds.");
        if (target == CompilationTarget.Cosmopolitan && options.ArchitectureSpecified && options.Architecture != CompilationArchitecture.X64)
            throw new CommandLineException("Draft 0.25 Cosmopolitan builds require --architecture x64.");
        if (target == CompilationTarget.Cosmopolitan && options.PrepareDebug is not null)
            throw new CommandLineException("Debug preparation is not available for Cosmopolitan builds in Draft 0.25; use the retained .dbg carrier with a native debugger.");
        if (target != CompilationTarget.EspIdf && options.SerialPort is not null)
            throw new CommandLineException("--serial-port is valid only for ESP-IDF debugging.");
        if (target == CompilationTarget.EspIdf && environment == TargetEnvironment.Qemu && options.PrepareDebug == "attach")
            throw new CommandLineException("QEMU targets support --prepare-debug launch only in v1; start a new debug Launch instead of attaching.");
        if (target == CompilationTarget.EspIdf && environment == TargetEnvironment.Native && options.PrepareDebug is not null && string.IsNullOrWhiteSpace(options.SerialPort))
            throw new CommandLineException("ESP-IDF debug preparation requires --serial-port.");
        if (target == CompilationTarget.EspIdf && (options.GenerateBindings || options.VerifyBindings) && options.ProjectManifest is null)
            throw new CommandLineException("ESP-IDF binding generation requires --project.");
        var hasFreestandingOptions = options.LinkerScript is not null || options.EntrySymbol is not null || options.NativeSources.Count != 0 ||
            options.ObjectFiles.Count != 0 || options.Libraries.Count != 0 || options.CompileOptions.Count != 0 || options.LinkOptions.Count != 0;
        if (target != CompilationTarget.Freestanding && hasFreestandingOptions)
            throw new CommandLineException("Freestanding linker and native-input options require --target freestanding.");
        if (target == CompilationTarget.Freestanding && options.PrepareDebug is not null)
            throw new CommandLineException("Debug preparation is unavailable for freestanding builds.");
    }

    private static FreestandingProjectConfiguration ResolveFreestanding(
        CommandLineOptions options,
        FreestandingProjectConfiguration? manifest,
        string root,
        bool buildNative,
        string? image)
    {
        string? FullPath(string? value) => value is null ? null : Path.GetFullPath(value, root);
        var linkerScript = FullPath(options.LinkerScript) ?? manifest?.LinkerScriptPath;
        var entrySymbol = options.EntrySymbol ?? manifest?.EntrySymbol;
        var nativeSources = ResolveCliFiles(options.NativeSources, manifest?.NativeSources ?? [], root);
        var objectFiles = ResolveCliFiles(options.ObjectFiles, manifest?.ObjectFiles ?? [], root);
        var libraries = ResolveCliFiles(options.Libraries, manifest?.Libraries ?? [], root);
        var compileOptions = options.CompileOptions.Count != 0 ? options.CompileOptions.ToImmutableArray() : manifest?.CompileOptions ?? [];
        var linkOptions = options.LinkOptions.Count != 0 ? options.LinkOptions.ToImmutableArray() : manifest?.LinkOptions ?? [];

        if (entrySymbol is not null && !IsPortableNativeSymbol(entrySymbol))
            throw new CommandLineException($"Freestanding entry symbol '{entrySymbol}' is not a portable native symbol name.");
        ValidateFreestandingFiles(nativeSources, "native source", path => Path.GetExtension(path) is ".c" or ".s" or ".S");
        ValidateFreestandingFiles(objectFiles, "object", path => Path.GetExtension(path).Equals(".o", StringComparison.OrdinalIgnoreCase));
        ValidateFreestandingFiles(libraries, "library", path => Path.GetExtension(path).Equals(".a", StringComparison.OrdinalIgnoreCase));

        if (buildNative)
        {
            if (image is null)
                throw new CommandLineException("Freestanding builds require build.image or --native-output.");
            if (linkerScript is null)
                throw new CommandLineException("Freestanding builds require freestanding.linkerScript or --linker-script.");
            if (entrySymbol is null)
                throw new CommandLineException("Freestanding builds require freestanding.entrySymbol or --entry-symbol.");
        }
        if (linkerScript is not null && !File.Exists(linkerScript))
            throw new CommandLineException($"Freestanding linker script '{linkerScript}' does not exist.");
        foreach (var path in nativeSources.Concat(objectFiles).Concat(libraries))
            if (!File.Exists(path))
                throw new CommandLineException($"Freestanding native input '{path}' does not exist.");
        ValidateFreestandingOptions(compileOptions, "compile");
        ValidateFreestandingOptions(linkOptions, "link");
        return new FreestandingProjectConfiguration(linkerScript, entrySymbol, nativeSources, objectFiles, libraries, compileOptions, linkOptions);
    }

    private static ImmutableArray<string> ResolveCliFiles(IReadOnlyList<string> cli, ImmutableArray<string> manifest, string root) =>
        cli.Count == 0 ? manifest : cli.Select(path => Path.GetFullPath(path, root)).ToImmutableArray();

    private static void ValidateFreestandingFiles(ImmutableArray<string> paths, string kind, Func<string, bool> validExtension)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var unique = new HashSet<string>(comparer);
        foreach (var path in paths)
        {
            if (!validExtension(path))
                throw new CommandLineException($"Freestanding {kind} '{path}' has an unsupported extension.");
            if (!unique.Add(path))
                throw new CommandLineException($"Freestanding {kind} input '{path}' is duplicated.");
        }
    }

    private static bool IsPortableNativeSymbol(string value) =>
        !string.IsNullOrWhiteSpace(value) && (char.IsAsciiLetter(value[0]) || value[0] is '_' or '$') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$');

    private static void ValidateFreestandingOptions(IEnumerable<string> options, string kind)
    {
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option) || option.StartsWith('@'))
                throw new CommandLineException($"Freestanding {kind} options cannot contain empty arguments or response files.");
            if (option is "-c" or "-S" or "-E" or "-o" or "-T" or "--output" or "--entry" or "--script" ||
                option.StartsWith("-o", StringComparison.Ordinal) || option.StartsWith("-T", StringComparison.Ordinal) ||
                option.StartsWith("--output=", StringComparison.Ordinal) || option.StartsWith("--entry=", StringComparison.Ordinal) ||
                option.StartsWith("--script=", StringComparison.Ordinal) || option.StartsWith("-Wl,-e", StringComparison.Ordinal) ||
                option.StartsWith("-Wl,-T", StringComparison.Ordinal) || option.StartsWith("-Wl,--entry", StringComparison.Ordinal) ||
                option.StartsWith("-Wl,--script", StringComparison.Ordinal) || option.StartsWith("-Wl,-o", StringComparison.Ordinal))
                throw new CommandLineException($"Freestanding {kind} option '{option}' overrides a compiler-owned build setting.");
        }
    }

    private static string? ResolveSourceRoot(CommandLineOptions options)
    {
        if (options.SourceRoot is null)
            return null;
        try
        {
            return Path.GetFullPath(options.SourceRoot, Directory.GetCurrentDirectory());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CommandLineException($"Invalid --source-root value: {exception.Message}");
        }
    }

    private static void ValidateDistinctOutputs(params string?[] outputPaths)
    {
        var paths = outputPaths.Where(path => path is not null).Cast<string>().ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparer).Count() != paths.Length)
            throw new CommandLineException("Generated C, generated header, symbol/debug maps, debug target, and native executable must name different files.");
    }

    private static void ValidateEspOutputs(string projectDirectory, string? generatedC, string? generatedHeader, string? generatedDirectory)
    {
        foreach (var path in new[] { generatedC, generatedHeader, generatedDirectory }.Where(path => path is not null).Cast<string>())
        {
            var relative = Path.GetRelativePath(projectDirectory, path);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new CommandLineException("ESP-IDF generated outputs must stay inside the selected ESP-IDF project directory.");
        }
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
