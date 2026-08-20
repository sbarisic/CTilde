using CTilde;

namespace CTilde.Cli;

internal sealed record BuildRequest(
    IReadOnlyCollection<string> Inputs,
    CompilationTarget Target,
    string? ManifestPath,
    string RootDirectory,
    string? SourceRoot,
    string? GeneratedCPath,
    string? GeneratedHeaderPath,
    bool CheckOnly,
    bool Trace,
    bool BuildNative,
    CTildeNativeBuildConfiguration Configuration,
    string Compiler,
    string? ExecutablePath,
    string? EspIdfProjectDirectory,
    string? EspIdfPath,
    GeneratedCLayout CLayout,
    string? GeneratedDirectory,
    string? SymbolMapPath,
    bool Lto)
{
    public string LockDirectory => Target == CompilationTarget.Hosted
        ? Path.GetDirectoryName(ExecutablePath!)!
        : Path.Combine(EspIdfProjectDirectory!, "build");

    public IReadOnlyList<string> GeneratedSourcePaths => CLayout == GeneratedCLayout.Unity
        ? [GeneratedCPath!]
        : Directory.EnumerateFiles(GeneratedDirectory!, "*.c", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Equals("ctilde_runtime.c", StringComparison.Ordinal) ||
                           Path.GetFileName(path).Equals("ctilde_entry.c", StringComparison.Ordinal) ||
                           Path.GetFileName(path).StartsWith("namespace_", StringComparison.Ordinal))
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
        var build = project.Configuration.Build;
        ValidateTargetOptions(options, project.Configuration.Target);
        var layout = options.CLayout ?? build.CLayout;
        if (layout == GeneratedCLayout.Modules && options.Output is not null)
            throw new CommandLineException("-o cannot be combined with modular project output; use --output-directory.");
        if (layout == GeneratedCLayout.Unity && options.OutputDirectory is not null)
            throw new CommandLineException("--output-directory requires modular C output.");
        var generatedC = options.CheckOnly || layout == GeneratedCLayout.Modules ? null : Path.GetFullPath(options.Output ?? build.GeneratedCPath);
        var generatedDirectory = options.CheckOnly || layout == GeneratedCLayout.Unity ? null : Path.GetFullPath(options.OutputDirectory ?? build.GeneratedDirectory);
        var generatedHeader = options.CheckOnly ? null : Path.GetFullPath(options.HeaderOutput ?? build.GeneratedHeaderPath);
        var symbolMap = options.CheckOnly ? null : options.SymbolMap is not null ? Path.GetFullPath(options.SymbolMap) : build.SymbolMapPath;
        var executable = project.Configuration.Target == CompilationTarget.Hosted
            ? Path.GetFullPath(options.NativeOutput ?? build.ExecutablePath!)
            : null;
        var idfProject = project.Configuration.Target == CompilationTarget.EspIdf
            ? Path.GetFullPath(options.EspIdfProject ?? build.EspIdfProjectDirectory!)
            : null;
        ValidateDistinctOutputs(generatedC, generatedHeader, executable, symbolMap);
        var configuration = options.Configuration ?? build.Configuration;
        var lto = options.Lto || build.Lto;
        if (lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CommandLineException("--lto requires a Release configuration.");
        if (options.Build && idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC, generatedHeader, generatedDirectory);
        return new BuildRequest(project.SourceFiles, project.Configuration.Target, project.ManifestPath,
            project.RootDirectory, ResolveSourceRoot(options), generatedC, generatedHeader, options.CheckOnly, options.Trace, options.Build,
            configuration, options.Compiler ?? build.Compiler, executable,
            idfProject, options.EspIdfPath, layout, generatedDirectory, symbolMap, lto);
    }

    private static BuildRequest ResolveDirect(CommandLineOptions options)
    {
        if (options.Inputs.Count == 0)
            throw new CommandLineException("At least one .ct input file is required.");
        ValidateTargetOptions(options, options.Target);
        if (!options.CheckOnly && !options.Build && options.CLayout != GeneratedCLayout.Modules && string.IsNullOrWhiteSpace(options.Output))
            throw new CommandLineException("-o is required unless --check or --build is used.");
        if (options.Build && options.Target == CompilationTarget.EspIdf &&
            ((options.CLayout != GeneratedCLayout.Modules && string.IsNullOrWhiteSpace(options.Output)) || string.IsNullOrWhiteSpace(options.EspIdfProject)))
            throw new CommandLineException("Direct ESP-IDF builds require a generated output and --idf-project.");

        var root = Directory.GetCurrentDirectory();
        var layout = options.CLayout ?? GeneratedCLayout.Unity;
        var generatedC = options.CheckOnly || layout == GeneratedCLayout.Modules ? null : Path.GetFullPath(options.Output ?? Path.Combine(root, "build", "generated", "ctilde_program.c"));
        var generatedDirectory = options.CheckOnly || layout == GeneratedCLayout.Unity ? null : Path.GetFullPath(options.OutputDirectory ?? Path.Combine(root, "build", "generated", "modules"));
        var generatedHeader = options.CheckOnly ? null : options.HeaderOutput is not null
            ? Path.GetFullPath(options.HeaderOutput)
            : options.Build
                ? Path.Combine(layout == GeneratedCLayout.Unity ? Path.GetDirectoryName(generatedC)! : generatedDirectory!, "ctilde_exports.h")
                : null;
        var symbolMap = options.CheckOnly ? null : options.SymbolMap is null ? null : Path.GetFullPath(options.SymbolMap);
        var executable = options.Target == CompilationTarget.Hosted && options.Build
            ? Path.GetFullPath(options.NativeOutput ?? Path.Combine(root, "build", $"program{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}"))
            : null;
        var idfProject = options.Target == CompilationTarget.EspIdf && options.Build
            ? Path.GetFullPath(options.EspIdfProject!)
            : null;
        ValidateDistinctOutputs(generatedC, generatedHeader, executable, symbolMap);
        var configuration = options.Configuration ?? CTildeNativeBuildConfiguration.Debug;
        if (options.Lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CommandLineException("--lto requires --configuration release.");
        if (idfProject is not null)
            ValidateEspOutputs(idfProject, generatedC, generatedHeader, generatedDirectory);
        return new BuildRequest(options.Inputs.Select(Path.GetFullPath).ToArray(), options.Target, null, root,
            ResolveSourceRoot(options),
            generatedC, generatedHeader, options.CheckOnly, options.Trace, options.Build,
            configuration, options.Compiler ?? "auto",
            executable, idfProject, options.EspIdfPath, layout, generatedDirectory, symbolMap, options.Lto);
    }

    private static void ValidateCommon(CommandLineOptions options)
    {
        var hasNativeOptions = options.Configuration is not null || options.Compiler is not null || options.Lto ||
            options.NativeOutput is not null || options.EspIdfProject is not null || options.EspIdfPath is not null;
        if (options.CheckOnly && (options.Build || hasNativeOptions || options.HeaderOutput is not null || options.SymbolMap is not null || options.OutputDirectory is not null))
            throw new CommandLineException("--check cannot be combined with build outputs or native-build options.");
        if (!options.Build && hasNativeOptions)
            throw new CommandLineException("Native-build options require --build.");
        if (options.CLayout == GeneratedCLayout.Modules && options.Output is not null)
            throw new CommandLineException("-o cannot be combined with --c-layout modules; use --output-directory.");
        if (options.CLayout == GeneratedCLayout.Unity && options.OutputDirectory is not null)
            throw new CommandLineException("--output-directory requires --c-layout modules.");
    }

    private static void ValidateTargetOptions(CommandLineOptions options, CompilationTarget target)
    {
        if (target == CompilationTarget.Hosted && (options.EspIdfProject is not null || options.EspIdfPath is not null))
            throw new CommandLineException("--idf-project and --idf-path are valid only for ESP-IDF builds.");
        if (target == CompilationTarget.EspIdf && (options.Compiler is not null || options.NativeOutput is not null || options.Configuration is not null))
            throw new CommandLineException("--compiler, --native-output, and --configuration are valid only for hosted builds.");
        if (target == CompilationTarget.EspIdf && options.Lto)
            throw new CommandLineException("--lto is a hosted Release option; configure ESP-IDF LTO through sdkconfig.");
        if (target == CompilationTarget.EspIdf && options.SourceRoot is not null)
            throw new CommandLineException("--source-root is valid only for hosted compilations.");
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

    private static void ValidateDistinctOutputs(string? generatedC, string? generatedHeader, string? executable, string? symbolMap)
    {
        var paths = new[] { generatedC, generatedHeader, executable, symbolMap }.Where(path => path is not null).Cast<string>().ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparer).Count() != paths.Length)
            throw new CommandLineException("Generated C, generated header, and native executable must name different files.");
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
