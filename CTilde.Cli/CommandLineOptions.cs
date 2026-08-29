using CTilde;

namespace CTilde.Cli;

internal sealed record CommandLineOptions(
    IReadOnlyList<string> Inputs,
    string? Output,
    string? HeaderOutput,
    string? InputDirectory,
    string? ProjectManifest,
    string? SourceRoot,
    bool CheckOnly,
    bool Trace,
    CompilationTarget Target,
    bool TargetSpecified,
    CompilationArchitecture Architecture,
    bool ArchitectureSpecified,
    bool Build,
    bool Run,
    CTildeNativeBuildConfiguration? Configuration,
    string? Compiler,
    CosmopolitanRuntimeMode CosmopolitanMode,
    bool CosmopolitanModeSpecified,
    string? NativeOutput,
    string? EspIdfProject,
    string? EspIdfPath,
    GeneratedCLayout? CLayout,
    string? OutputDirectory,
    string? SymbolMap,
    bool Lto,
    bool DebugInfo,
    DebugMemoryMode? DebugMemory,
    string? DebugMap,
    string? PrepareDebug,
    string? DebugTarget,
    string? SerialPort,
    int BaudRate,
    bool GenerateBindings,
    bool VerifyBindings,
    string? EspClangPath,
    bool NoRecursion,
    EspIdfPanicPolicy PanicPolicy,
    bool PanicPolicySpecified,
    string? LinkerScript,
    string? EntrySymbol,
    IReadOnlyList<string> NativeSources,
    IReadOnlyList<string> ObjectFiles,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> CompileOptions,
    IReadOnlyList<string> LinkOptions,
    IReadOnlyList<CpuFeature> CpuFeatures,
    TargetEnvironment Environment,
    EspIdfChip? EspIdfChip)
{
    public static bool TryParse(string[] args, out CommandLineOptions? options, out string? error, out bool showHelp)
    {
        options = null;
        error = null;
        string? parseError = null;
        showHelp = args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal);
        if (args.Length == 0 || showHelp)
            return true;

        var inputs = new List<string>();
        string? output = null;
        string? header = null;
        string? directory = null;
        string? project = null;
        string? sourceRoot = null;
        string? compiler = null;
        var cosmopolitanMode = CosmopolitanRuntimeMode.Default;
        var cosmopolitanModeSpecified = false;
        string? nativeOutput = null;
        string? idfProject = null;
        string? idfPath = null;
        string? outputDirectory = null;
        string? symbolMap = null;
        string? debugMap = null;
        string? prepareDebug = null;
        string? debugTarget = null;
        string? serialPort = null;
        string? espClangPath = null;
        string? linkerScript = null;
        string? entrySymbol = null;
        var nativeSources = new List<string>();
        var objectFiles = new List<string>();
        var libraries = new List<string>();
        var compileOptions = new List<string>();
        var linkOptions = new List<string>();
        var cpuFeatures = new List<CpuFeature>();
        var baudRate = 115200;
        var check = false;
        var trace = false;
        var build = false;
        var run = false;
        var target = CompilationTarget.Hosted;
        var environment = TargetEnvironment.Native;
        EspIdfChip? espIdfChip = null;
        var targetSpecified = false;
        var architecture = CompilationArchitecture.Auto;
        var architectureSpecified = false;
        var lto = false;
        var debugInfo = false;
        var generateBindings = false;
        var verifyBindings = false;
        var noRecursion = false;
        var panicPolicy = EspIdfPanicPolicy.Abort;
        var panicPolicySpecified = false;
        DebugMemoryMode? debugMemory = null;
        GeneratedCLayout? cLayout = null;
        CTildeNativeBuildConfiguration? configuration = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? RequireValue()
            {
                if (++index < args.Length)
                    return args[index];
                parseError = $"{argument} requires a value.";
                return null;
            }

            switch (argument)
            {
                case "-o": output = RequireValue(); break;
                case "--header": header = RequireValue(); break;
                case "--compile-directory": directory = RequireValue(); break;
                case "--project": project = RequireValue(); break;
                case "--source-root": sourceRoot = RequireValue(); break;
                case "--compiler": compiler = RequireValue(); break;
                case "--cosmopolitan-mode":
                    cosmopolitanModeSpecified = true;
                    var cosmopolitanModeValue = RequireValue();
                    cosmopolitanMode = cosmopolitanModeValue switch
                    {
                        "default" => CosmopolitanRuntimeMode.Default,
                        "tiny" => CosmopolitanRuntimeMode.Tiny,
                        "debug" => CosmopolitanRuntimeMode.Debug,
                        _ => (CosmopolitanRuntimeMode)(-1),
                    };
                    if (cosmopolitanModeValue is not null && !Enum.IsDefined(cosmopolitanMode))
                        parseError = $"Unknown Cosmopolitan mode '{cosmopolitanModeValue}'; expected default, tiny, or debug.";
                    break;
                case "--native-output": nativeOutput = RequireValue(); break;
                case "--idf-project": idfProject = RequireValue(); break;
                case "--idf-path": idfPath = RequireValue(); break;
                case "--esp-clang": espClangPath = RequireValue(); break;
                case "--linker-script": linkerScript = RequireValue(); break;
                case "--entry-symbol": entrySymbol = RequireValue(); break;
                case "--native-source":
                    if (RequireValue() is { } nativeSource) nativeSources.Add(nativeSource);
                    break;
                case "--object":
                    if (RequireValue() is { } objectFile) objectFiles.Add(objectFile);
                    break;
                case "--library":
                    if (RequireValue() is { } library) libraries.Add(library);
                    break;
                case "--compile-option":
                    if (RequireValue() is { } compileOption) compileOptions.Add(compileOption);
                    break;
                case "--cpu-feature":
                    var cpuFeatureValue = RequireValue();
                    var cpuFeature = cpuFeatureValue switch
                    {
                        "simd128" => CpuFeature.Simd128,
                        _ => (CpuFeature)(-1),
                    };
                    if (cpuFeatureValue is not null && !Enum.IsDefined(cpuFeature))
                        parseError = $"Unknown CPU feature '{cpuFeatureValue}'; expected simd128.";
                    else if (cpuFeatureValue is not null && cpuFeatures.Contains(cpuFeature))
                        parseError = $"CPU feature '{cpuFeatureValue}' was specified more than once.";
                    else if (cpuFeatureValue is not null)
                        cpuFeatures.Add(cpuFeature);
                    break;
                case "--link-option":
                    if (RequireValue() is { } linkOption) linkOptions.Add(linkOption);
                    break;
                case "--output-directory": outputDirectory = RequireValue(); break;
                case "--symbol-map": symbolMap = RequireValue(); break;
                case "--debug-map": debugMap = RequireValue(); break;
                case "--debug-target": debugTarget = RequireValue(); break;
                case "--serial-port": serialPort = RequireValue(); break;
                case "--debug-info": debugInfo = true; break;
                case "--debug-memory":
                    var memoryValue = RequireValue();
                    debugMemory = memoryValue switch
                    {
                        "off" => DebugMemoryMode.Off,
                        "objects" => DebugMemoryMode.Objects,
                        "guarded" => DebugMemoryMode.Guarded,
                        null => null,
                        _ => (DebugMemoryMode)(-1),
                    };
                    if (debugMemory is not null && !Enum.IsDefined(debugMemory.Value))
                        parseError = $"Unknown debug memory mode '{memoryValue}'; expected off, objects, or guarded.";
                    break;
                case "--prepare-debug":
                    prepareDebug = RequireValue();
                    if (prepareDebug is not null && prepareDebug is not ("launch" or "attach"))
                        parseError = $"Unknown debug request '{prepareDebug}'; expected launch or attach.";
                    break;
                case "--baud-rate":
                    var baudValue = RequireValue();
                    if (baudValue is not null && (!int.TryParse(baudValue, out baudRate) || baudRate <= 0))
                        parseError = $"Invalid baud rate '{baudValue}'; expected a positive integer.";
                    break;
                case "--lto": lto = true; break;
                case "--c-layout":
                    var layoutValue = RequireValue();
                    cLayout = layoutValue switch
                    {
                        "unity" => GeneratedCLayout.Unity,
                        "modules" => GeneratedCLayout.Modules,
                        null => null,
                        _ => (GeneratedCLayout)(-1),
                    };
                    if (cLayout is not null && !Enum.IsDefined(cLayout.Value))
                        parseError = $"Unknown C layout '{layoutValue}'; expected unity or modules.";
                    break;
                case "--check": check = true; break;
                case "--trace": trace = true; break;
                case "--build": build = true; break;
                case "--run": run = true; break;
                case "--generate-bindings": generateBindings = true; break;
                case "--verify-bindings": verifyBindings = true; break;
                case "--no-recursion": noRecursion = true; break;
                case "--panic-policy":
                    panicPolicySpecified = true;
                    var panicValue = RequireValue();
                    panicPolicy = panicValue switch
                    {
                        "abort" => EspIdfPanicPolicy.Abort,
                        "restart" => EspIdfPanicPolicy.Restart,
                        "halt" => EspIdfPanicPolicy.Halt,
                        _ => (EspIdfPanicPolicy)(-1),
                    };
                    if (panicValue is not null && !Enum.IsDefined(panicPolicy))
                        parseError = $"Unknown panic policy '{panicValue}'; expected abort, restart, or halt.";
                    break;
                case "--configuration":
                    var value = RequireValue();
                    configuration = value switch
                    {
                        "debug" => CTildeNativeBuildConfiguration.Debug,
                        "release" => CTildeNativeBuildConfiguration.Release,
                        null => null,
                        _ => (CTildeNativeBuildConfiguration)(-1),
                    };
                    if (configuration is not null && !Enum.IsDefined(configuration.Value))
                        parseError = $"Unknown configuration '{value}'; expected debug or release.";
                    break;
                case "--target":
                    targetSpecified = true;
                    var targetValue = RequireValue();
                    environment = targetValue is "esp32_qemu" or "esp32c3_qemu" ? TargetEnvironment.Qemu : TargetEnvironment.Native;
                    espIdfChip = targetValue switch
                    {
                        "esp32_qemu" => CTilde.EspIdfChip.Esp32,
                        "esp32c3_qemu" => CTilde.EspIdfChip.Esp32C3,
                        _ => null,
                    };
                    target = targetValue switch
                    {
                        "hosted" => CompilationTarget.Hosted,
                        "esp-idf" => CompilationTarget.EspIdf,
                        "esp32_qemu" => CompilationTarget.EspIdf,
                        "esp32c3_qemu" => CompilationTarget.EspIdf,
                        "freestanding" => CompilationTarget.Freestanding,
                        "cosmopolitan" => CompilationTarget.Cosmopolitan,
                        _ => (CompilationTarget)(-1),
                    };
                    if (targetValue is not null && !Enum.IsDefined(target))
                        parseError = $"Unknown target '{targetValue}'; expected hosted, esp-idf, esp32_qemu, esp32c3_qemu, freestanding, or cosmopolitan.";
                    break;
                case "--architecture":
                    architectureSpecified = true;
                    var architectureValue = RequireValue();
                    architecture = architectureValue switch
                    {
                        "auto" => CompilationArchitecture.Auto,
                        "x86" => CompilationArchitecture.X86,
                        "x64" => CompilationArchitecture.X64,
                        "arm32" => CompilationArchitecture.Arm32,
                        "arm64" => CompilationArchitecture.Arm64,
                        "xtensa" => CompilationArchitecture.Xtensa,
                        "riscv32" => CompilationArchitecture.RiscV32,
                        "riscv64" => CompilationArchitecture.RiscV64,
                        _ => (CompilationArchitecture)(-1),
                    };
                    if (architectureValue is not null && !Enum.IsDefined(architecture))
                        parseError = $"Unknown architecture '{architectureValue}'; expected auto, x86, x64, arm32, arm64, xtensa, riscv32, or riscv64.";
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                        parseError = $"Unknown option '{argument}'.";
                    else
                        inputs.Add(argument);
                    break;
            }

            if (parseError is not null)
            {
                error = parseError;
                return false;
            }
        }

        options = new CommandLineOptions(inputs, output, header, directory, project, sourceRoot, check, trace, target,
            targetSpecified, architecture, architectureSpecified, build, run, configuration, compiler, cosmopolitanMode, cosmopolitanModeSpecified, nativeOutput, idfProject, idfPath, cLayout, outputDirectory, symbolMap, lto,
            debugInfo, debugMemory, debugMap, prepareDebug, debugTarget, serialPort, baudRate, generateBindings, verifyBindings, espClangPath, noRecursion,
            panicPolicy, panicPolicySpecified, linkerScript, entrySymbol, nativeSources, objectFiles, libraries, compileOptions, linkOptions, cpuFeatures,
            environment, espIdfChip);
        return true;
    }
}
