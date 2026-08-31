using System.Diagnostics;
using System.Text.RegularExpressions;
using CTilde;

namespace CTilde.Cli;

internal sealed class BuildReporter : IDisposable
{
    private static readonly AsyncLocal<BuildReporter?> Ambient = new();
    private static readonly Regex NativeDiagnostic = new(
        @"(^|\s)(fatal error|error|warning)(\s+[A-Za-z]+\d+)?\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly BuildReporter? previous;
    private int errors;
    private int warnings;

    public BuildReporter(BuildVerbosity verbosity, bool trace)
    {
        Verbosity = verbosity;
        Trace = trace;
        previous = Ambient.Value;
        Ambient.Value = this;
    }

    public static BuildReporter? Current => Ambient.Value;
    public BuildVerbosity Verbosity { get; }
    public bool Trace { get; }

    public void Begin(BuildRequest request, string operation)
    {
        if (Verbosity < BuildVerbosity.Normal)
            return;
        if (request.ManifestPath is not null)
            Console.Out.WriteLine($"  Project: {request.ManifestPath}");
        Console.Out.WriteLine($"  Sources: {request.Inputs.Count} C~ file(s)");
        Console.Out.WriteLine($"  Target: {TargetName(request)} {ArchitectureName(request.Architecture)} | {request.Configuration} | {request.Compiler}");
        Console.Out.WriteLine($"  Output: {request.CLayout.ToString().ToLowerInvariant()} C | {OptimizationName(request)}");
        Console.Out.WriteLine($"  {operation}: compiling C~ sources...");
        if (Verbosity >= BuildVerbosity.Detailed)
            foreach (var source in request.Inputs.OrderBy(path => path, StringComparer.Ordinal))
                Console.Out.WriteLine($"    {source}");
    }

    public void BeginStandardLibrary(CTildeProject project, string operation)
    {
        if (Verbosity < BuildVerbosity.Normal)
            return;
        Console.Out.WriteLine($"  Project: {project.ManifestPath}");
        Console.Out.WriteLine($"  Sources: {project.SourceFiles.Length} C~ file(s)");
        Console.Out.WriteLine("  Target: standard-library profiles | validation");
        Console.Out.WriteLine($"  {operation}: validating standard-library sources...");
        if (Verbosity >= BuildVerbosity.Detailed)
            foreach (var source in project.SourceFiles.OrderBy(path => path, StringComparer.Ordinal))
                Console.Out.WriteLine($"    {source}");
    }

    public void Phase(string message)
    {
        if (Verbosity >= BuildVerbosity.Normal)
            Console.Out.WriteLine($"  {message}");
    }

    public void Detail(string message)
    {
        if (Verbosity >= BuildVerbosity.Detailed)
            Console.Out.WriteLine($"    {message}");
    }

    public void Diagnostic(Diagnostic diagnostic)
    {
        Count(diagnostic.Severity);
        Console.Error.WriteLine(diagnostic);
        if (diagnostic.RelatedLocation is { } related)
            Console.Error.WriteLine($"{related}: info {diagnostic.Code}: Related location");
    }

    public Diagnostic ProjectDiagnostic(CTildeProjectException exception, string? manifestPath = null)
    {
        var location = exception.Location ?? new SourceLocation(
            manifestPath is null ? "ctilde.json" : Path.GetFullPath(manifestPath), new TextSpan(0, 0), 1, 1);
        var diagnostic = new Diagnostic(exception.Code, DiagnosticSeverity.Error, exception.Message, location, exception.RelatedLocation);
        Diagnostic(diagnostic);
        return diagnostic;
    }

    public Diagnostic InfrastructureDiagnostic(string manifestPath, string code, string message)
    {
        var diagnostic = new Diagnostic(code, DiagnosticSeverity.Error, message,
            new SourceLocation(Path.GetFullPath(manifestPath), new TextSpan(0, 0), 1, 1));
        Diagnostic(diagnostic);
        return diagnostic;
    }

    public void NativeCommand(NativeProcessRequest request)
    {
        if (Verbosity < BuildVerbosity.Detailed)
            return;
        Console.Out.WriteLine($"    > {request.FileName} {string.Join(' ', request.Arguments.Select(Quote))}");
    }

    public void NativeLine(string line, bool isErrorStream, bool wasPreviouslyForwarded)
    {
        if (wasPreviouslyForwarded)
            return;
        var diagnostic = NativeDiagnostic.IsMatch(line);
        if (diagnostic)
        {
            if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) warnings++; else errors++;
        }
        if (Verbosity >= BuildVerbosity.Detailed || diagnostic)
            (isErrorStream ? Console.Error : Console.Out).WriteLine(line);
    }

    public void Complete(int exitCode, string? artifact = null)
    {
        if (exitCode == 0 && artifact is not null && Verbosity >= BuildVerbosity.Normal)
            Console.Out.WriteLine($"  C~ -> {artifact}");
        if (Verbosity >= BuildVerbosity.Normal)
            Console.Out.WriteLine($"  Build {(exitCode == 0 ? "succeeded" : "failed")} in {elapsed.Elapsed.TotalSeconds:0.000}s with {warnings} warning(s) and {errors} error(s).");
    }

    public void WaitingForLock(string directory, BuildLockOwner? owner)
    {
        if (Verbosity < BuildVerbosity.Minimal)
            return;
        var details = owner is null ? string.Empty : $" (PID {owner.ProcessId}, {owner.Operation})";
        Console.Out.WriteLine($"  Waiting for another C~ operation using '{directory}'{details}...");
    }

    public void Dispose()
    {
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = previous;
    }

    private void Count(DiagnosticSeverity severity)
    {
        if (severity == DiagnosticSeverity.Error) errors++;
        else if (severity == DiagnosticSeverity.Warning) warnings++;
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
    private static string TargetName(BuildRequest request) => request.Target switch
    {
        CompilationTarget.EspIdf when request.Environment == TargetEnvironment.Qemu && request.EspIdfChip == EspIdfChip.Esp32 => "esp32_qemu",
        CompilationTarget.EspIdf when request.Environment == TargetEnvironment.Qemu => "esp32c3_qemu",
        CompilationTarget.EspIdf => "esp-idf",
        CompilationTarget.Freestanding => "freestanding",
        CompilationTarget.Cosmopolitan => "cosmopolitan",
        _ => "hosted",
    };
    private static string ArchitectureName(CompilationArchitecture architecture) => architecture.ToString().ToLowerInvariant();
    private static string OptimizationName(BuildRequest request) =>
        $"{request.Optimization?.ToString().ToLowerInvariant() ?? (request.Configuration == CTildeNativeBuildConfiguration.Release ? "speed" : "debug")}, " +
        $"CPU {request.CpuTarget?.ToString().ToLowerInvariant() ?? "baseline"}, FP {request.FloatingPoint?.ToString().ToLowerInvariant() ?? "precise"}" +
        (request.Lto ? ", LTO" : string.Empty) + (request.PgoMode == NativePgoMode.Off ? string.Empty : $", PGO {request.PgoMode.ToString().ToLowerInvariant()}");
}
