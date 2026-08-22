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
    bool Build,
    CTildeNativeBuildConfiguration? Configuration,
    string? Compiler,
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
    int BaudRate)
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
        string? nativeOutput = null;
        string? idfProject = null;
        string? idfPath = null;
        string? outputDirectory = null;
        string? symbolMap = null;
        string? debugMap = null;
        string? prepareDebug = null;
        string? debugTarget = null;
        string? serialPort = null;
        var baudRate = 115200;
        var check = false;
        var trace = false;
        var build = false;
        var target = CompilationTarget.Hosted;
        var targetSpecified = false;
        var lto = false;
        var debugInfo = false;
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
                case "--native-output": nativeOutput = RequireValue(); break;
                case "--idf-project": idfProject = RequireValue(); break;
                case "--idf-path": idfPath = RequireValue(); break;
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
                    target = targetValue switch
                    {
                        "hosted" => CompilationTarget.Hosted,
                        "esp-idf" => CompilationTarget.EspIdf,
                        _ => (CompilationTarget)(-1),
                    };
                    if (targetValue is not null && !Enum.IsDefined(target))
                        parseError = $"Unknown target '{targetValue}'; expected hosted or esp-idf.";
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
            targetSpecified, build, configuration, compiler, nativeOutput, idfProject, idfPath, cLayout, outputDirectory, symbolMap, lto,
            debugInfo, debugMemory, debugMap, prepareDebug, debugTarget, serialPort, baudRate);
        return true;
    }
}
