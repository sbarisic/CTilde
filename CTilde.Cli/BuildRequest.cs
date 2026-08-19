using CTilde;

namespace CTilde.Cli;

internal sealed record BuildRequest(
    IReadOnlyCollection<string> Inputs,
    CompilationTarget Target,
    string? ManifestPath,
    string RootDirectory,
    string? GeneratedCPath,
    string? GeneratedHeaderPath,
    bool CheckOnly,
    bool Trace,
    bool BuildNative,
    CTildeNativeBuildConfiguration Configuration,
    string Compiler,
    string? ExecutablePath,
    string? EspIdfProjectDirectory,
    string? EspIdfPath)
{
    public string LockDirectory => Target == CompilationTarget.Hosted
        ? Path.GetDirectoryName(ExecutablePath!)!
        : Path.Combine(EspIdfProjectDirectory!, "build");
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
        var build = project.Configuration.Build;
        ValidateTargetOptions(options, project.Configuration.Target);
        var generatedC = options.CheckOnly ? null : Path.GetFullPath(options.Output ?? build.GeneratedCPath);
        var generatedHeader = options.CheckOnly ? null : Path.GetFullPath(options.HeaderOutput ?? build.GeneratedHeaderPath);
        var executable = project.Configuration.Target == CompilationTarget.Hosted
            ? Path.GetFullPath(options.NativeOutput ?? build.ExecutablePath!)
            : null;
        var idfProject = project.Configuration.Target == CompilationTarget.EspIdf
            ? Path.GetFullPath(options.EspIdfProject ?? build.EspIdfProjectDirectory!)
            : null;
        ValidateDistinctOutputs(generatedC, generatedHeader, executable);
        if (options.Build && idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC!, generatedHeader);
        return new BuildRequest(project.SourceFiles, project.Configuration.Target, project.ManifestPath,
            project.RootDirectory, generatedC, generatedHeader, options.CheckOnly, options.Trace, options.Build,
            options.Configuration ?? build.Configuration, options.Compiler ?? build.Compiler, executable,
            idfProject, options.EspIdfPath);
    }

    private static BuildRequest ResolveDirect(CommandLineOptions options)
    {
        if (options.Inputs.Count == 0)
            throw new CommandLineException("At least one .ct input file is required.");
        ValidateTargetOptions(options, options.Target);
        if (!options.CheckOnly && !options.Build && string.IsNullOrWhiteSpace(options.Output))
            throw new CommandLineException("-o is required unless --check or --build is used.");
        if (options.Build && options.Target == CompilationTarget.EspIdf &&
            (string.IsNullOrWhiteSpace(options.Output) || string.IsNullOrWhiteSpace(options.EspIdfProject)))
            throw new CommandLineException("Direct ESP-IDF builds require both -o and --idf-project.");

        var root = Directory.GetCurrentDirectory();
        var generatedC = options.CheckOnly ? null : Path.GetFullPath(options.Output ?? Path.Combine(root, "build", "generated", "ctilde_program.c"));
        var generatedHeader = options.CheckOnly ? null : options.HeaderOutput is not null
            ? Path.GetFullPath(options.HeaderOutput)
            : options.Build
                ? Path.Combine(Path.GetDirectoryName(generatedC)!, "ctilde_exports.h")
                : null;
        var executable = options.Target == CompilationTarget.Hosted && options.Build
            ? Path.GetFullPath(options.NativeOutput ?? Path.Combine(root, "build", $"program{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}"))
            : null;
        var idfProject = options.Target == CompilationTarget.EspIdf && options.Build
            ? Path.GetFullPath(options.EspIdfProject!)
            : null;
        ValidateDistinctOutputs(generatedC, generatedHeader, executable);
        if (idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC!, generatedHeader);
        return new BuildRequest(options.Inputs.Select(Path.GetFullPath).ToArray(), options.Target, null, root,
            generatedC, generatedHeader, options.CheckOnly, options.Trace, options.Build,
            options.Configuration ?? CTildeNativeBuildConfiguration.Debug, options.Compiler ?? "auto",
            executable, idfProject, options.EspIdfPath);
    }

    private static void ValidateCommon(CommandLineOptions options)
    {
        var hasNativeOptions = options.Configuration is not null || options.Compiler is not null ||
            options.NativeOutput is not null || options.EspIdfProject is not null || options.EspIdfPath is not null;
        if (options.CheckOnly && (options.Build || hasNativeOptions || options.HeaderOutput is not null))
            throw new CommandLineException("--check cannot be combined with --build, --header, or native-build options.");
        if (!options.Build && hasNativeOptions)
            throw new CommandLineException("Native-build options require --build.");
    }

    private static void ValidateTargetOptions(CommandLineOptions options, CompilationTarget target)
    {
        if (target == CompilationTarget.Hosted && (options.EspIdfProject is not null || options.EspIdfPath is not null))
            throw new CommandLineException("--idf-project and --idf-path are valid only for ESP-IDF builds.");
        if (target == CompilationTarget.EspIdf && (options.Compiler is not null || options.NativeOutput is not null || options.Configuration is not null))
            throw new CommandLineException("--compiler, --native-output, and --configuration are valid only for hosted builds.");
    }

    private static void ValidateDistinctOutputs(string? generatedC, string? generatedHeader, string? executable)
    {
        var paths = new[] { generatedC, generatedHeader, executable }.Where(path => path is not null).Cast<string>().ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparer).Count() != paths.Length)
            throw new CommandLineException("Generated C, generated header, and native executable must name different files.");
    }

    private static void ValidateEspOutputs(string projectDirectory, string generatedC, string? generatedHeader)
    {
        foreach (var path in new[] { generatedC, generatedHeader }.Where(path => path is not null).Cast<string>())
        {
            var relative = Path.GetRelativePath(projectDirectory, path);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new CommandLineException("ESP-IDF generated outputs must stay inside the selected ESP-IDF project directory.");
        }
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
