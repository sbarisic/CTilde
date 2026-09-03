using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public enum CTildeProjectKind
{
    Application,
    StandardLibrary,
}

public sealed record CTildeProjectConfiguration(
    CTildeProjectKind Kind,
    CompilationTarget Target,
    CompilationArchitecture Architecture,
    TargetEnvironment Environment,
    EspIdfChip? EspIdfChip,
    ImmutableArray<string> Sources,
    ImmutableArray<string> Exclude,
    CTildeProjectBuildConfiguration? Build,
    ImmutableArray<EspIdfBindingManifest> BindingManifests,
    ImmutableArray<CpuFeature> CpuFeatures,
    bool SimdOptimizations,
    ImmutableArray<RepositoryModuleReference> Modules,
    CTildeProjectRunConfiguration? Run,
    bool NoRecursion,
    EspIdfPanicPolicy PanicPolicy,
    EspIdfArtifact EspIdfArtifact,
    ManagedModuleConfiguration? ManagedModule,
    HostedProjectConfiguration? Hosted,
    FreestandingProjectConfiguration? Freestanding,
    CosmopolitanProjectConfiguration? Cosmopolitan);

public sealed record ManagedModuleReference(
    string MetadataPath,
    string Name,
    string Version,
    string BuildIdentity,
    string ApiHash,
    ManagedModuleMetadata? Metadata = null);

public sealed record ManagedModuleConfiguration(
    ManagedModuleKind Kind,
    string Name,
    string Version,
    ImmutableArray<ManagedModuleReference> References,
    uint MainTaskStackBytes,
    ulong? HeapLimitBytes,
    ImmutableArray<string> NativeSources = default,
    string? ProjectRoot = null,
    string? NativeComponentDirectory = null);

public enum CTildeRunExecutor
{
    Host,
    Wsl,
}

public sealed record CTildeProjectRunConfiguration(
    CTildeRunExecutor Executor,
    string? Command,
    ImmutableArray<string> Arguments,
    string WorkingDirectoryPath,
    ImmutableDictionary<string, string> Environment,
    ImmutableArray<int> SuccessExitCodes);

public enum CosmopolitanRuntimeMode
{
    Default,
    Tiny,
    Debug,
}

public sealed record CosmopolitanProjectConfiguration(CosmopolitanRuntimeMode Mode);

public enum HostedOperatingSystem
{
    Windows,
    Linux,
}

public sealed record HostedRuntimeFile(
    HostedOperatingSystem OperatingSystem,
    CompilationArchitecture Architecture,
    string SourcePath,
    string OutputFileName);

public sealed record HostedProjectConfiguration(
    ImmutableArray<string> NativeSources,
    ImmutableArray<HostedRuntimeFile> RuntimeFiles);

public sealed record FreestandingProjectConfiguration(
    string? LinkerScriptPath,
    string? EntrySymbol,
    ImmutableArray<string> NativeSources,
    ImmutableArray<string> ObjectFiles,
    ImmutableArray<string> Libraries,
    ImmutableArray<string> CompileOptions,
    ImmutableArray<string> LinkOptions);

public enum CTildeNativeBuildConfiguration
{
    Debug,
    Release,
}

public enum NativeOptimization
{
    Speed,
    Aggressive,
}

public enum NativeCpuTarget
{
    Baseline,
    Avx2,
}

public enum NativeFloatingPointMode
{
    Precise,
    Fast,
}

public enum NativePgoMode
{
    Off,
    Generate,
    Use,
}

public sealed record NativePgoConfiguration(NativePgoMode Mode, string DirectoryPath);

public sealed record CTildeProjectBuildConfiguration(
    string GeneratedCPath,
    string GeneratedHeaderPath,
    GeneratedCLayout CLayout,
    string GeneratedDirectory,
    string? SymbolMapPath,
    string? StackReportPath,
    bool Lto,
    CTildeNativeBuildConfiguration Configuration,
    string Compiler,
    string? ExecutablePath,
    string? EspIdfProjectDirectory,
    NativeOptimization? Optimization,
    NativeCpuTarget? CpuTarget,
    NativeFloatingPointMode? FloatingPoint,
    NativePgoConfiguration? Pgo);

public sealed record CTildeProject(
    string ManifestPath,
    string RootDirectory,
    CTildeProjectConfiguration Configuration,
    ImmutableArray<string> SourceFiles,
    ImmutableDictionary<string, SourceOwnerIdentity> SourceOwners);

public sealed record RepositoryModuleReference(
    string ModulePath,
    string Repository,
    string Selector,
    string? Alias,
    ImmutableArray<string> Sources,
    string? Vendor,
    RepositoryModuleUpdatePolicy UpdatePolicy);

public enum RepositoryModuleUpdatePolicy
{
    Locked,
    Refresh,
}

public sealed class CTildeProjectException : Exception
{
    public CTildeProjectException(string message, string code = "CT6001", SourceLocation? location = null, SourceLocation? relatedLocation = null)
        : base(message)
    {
        Code = code;
        Location = location;
        RelatedLocation = relatedLocation;
    }

    public CTildeProjectException(string message, Exception innerException, string code = "CT6001", SourceLocation? location = null, SourceLocation? relatedLocation = null)
        : base(message, innerException)
    {
        Code = code;
        Location = location;
        RelatedLocation = relatedLocation;
    }

    public string Code { get; }
    public SourceLocation? Location { get; }
    public SourceLocation? RelatedLocation { get; }
}

public static class CTildeProjectFile
{
    private static readonly string[] DefaultExcludes =
    [
        ".git/**", ".ctilde/**", "vendor/**", "**/bin/**", "**/obj/**", "**/build/**", "**/node_modules/**", "**/managed_components/**",
    ];

    public static CTildeProject Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        SourceText? source = null;
        ManifestLocationMap? locations = null;
        try
        {
            if (File.Exists(fullManifestPath))
            {
                source = SourceText.FromFile(fullManifestPath);
                locations = ManifestLocationMap.Create(source);
            }
            return LoadCore(fullManifestPath);
        }
        catch (CTildeProjectException exception) when (exception.Location is null)
        {
            var location = source is null
                ? new SourceLocation(fullManifestPath, new TextSpan(0, 0), 1, 1)
                : exception.InnerException is JsonException json
                    ? JsonFailureLocation(source, json)
                    : locations?.Find(InferPropertyPath(exception.Message)) ?? source.GetLocation(new TextSpan(0, 0));
            var code = exception.InnerException is JsonException or IOException or UnauthorizedAccessException || !File.Exists(fullManifestPath)
                ? "CT6000"
                : exception.Code;
            throw new CTildeProjectException(exception.Message, exception, code, location, exception.RelatedLocation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            var location = new SourceLocation(fullManifestPath, new TextSpan(0, 0), 1, 1);
            throw new CTildeProjectException($"Could not read project manifest '{fullManifestPath}': {exception.Message}", exception, "CT6000", location);
        }
    }

    private static CTildeProject LoadCore(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' does not exist.");

        ProjectDocument? document;
        try
        {
            using var stream = File.OpenRead(fullManifestPath);
            document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read project manifest '{fullManifestPath}': {exception.Message}", exception);
        }

        if (document?.Sources is not { Length: > 0 })
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' requires a non-empty 'sources' array.");

        var kind = document.Kind switch
        {
            null or "application" => CTildeProjectKind.Application,
            "standard-library" => CTildeProjectKind.StandardLibrary,
            _ => throw new CTildeProjectException($"Unknown project kind '{document.Kind}' in '{fullManifestPath}'; expected application or standard-library."),
        };
        if (kind == CTildeProjectKind.StandardLibrary)
            ValidateStandardLibraryDocument(document, fullManifestPath);

        var target = document.Target switch
        {
            null or "hosted" => CompilationTarget.Hosted,
            "esp-idf" => CompilationTarget.EspIdf,
            "esp32_qemu" => CompilationTarget.EspIdf,
            "esp32c3_qemu" => CompilationTarget.EspIdf,
            "freestanding" => CompilationTarget.Freestanding,
            "cosmopolitan" => CompilationTarget.Cosmopolitan,
            _ => throw new CTildeProjectException($"Unknown target '{document.Target}' in '{fullManifestPath}'; expected hosted, esp-idf, esp32_qemu, esp32c3_qemu, freestanding, or cosmopolitan."),
        };
        var environment = document.Target is "esp32_qemu" or "esp32c3_qemu" ? TargetEnvironment.Qemu : TargetEnvironment.Native;
        EspIdfChip? espIdfChip = document.Target switch
        {
            "esp32_qemu" => EspIdfChip.Esp32,
            "esp32c3_qemu" => EspIdfChip.Esp32C3,
            _ => null,
        };
        var architecture = ParseArchitecture(document.Architecture, fullManifestPath);
        if (espIdfChip is not null)
        {
            var requiredArchitecture = espIdfChip == EspIdfChip.Esp32 ? CompilationArchitecture.Xtensa : CompilationArchitecture.RiscV32;
            if (architecture != CompilationArchitecture.Auto && architecture != requiredArchitecture)
                throw new CTildeProjectException($"Target '{document.Target}' in '{fullManifestPath}' requires architecture '{ArchitectureName(requiredArchitecture)}'.");
            architecture = requiredArchitecture;
        }
        var cpuFeatures = ParseCpuFeatures(document.CpuFeatures, fullManifestPath);
        var simdOptimizations = document.SimdOptimizations ?? false;
        var root = Path.GetDirectoryName(fullManifestPath)!;
        var modules = ParseModules(document.Modules, fullManifestPath);
        var sources = ValidatePatterns(document.Sources, "sources", fullManifestPath);
        var excludes = ValidatePatterns([.. DefaultExcludes, .. document.Exclude ?? []], "exclude", fullManifestPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        ImmutableArray<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.ct", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path =>
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    return sources.Any(pattern => GlobMatches(pattern, relative)) && !excludes.Any(pattern => GlobMatches(pattern, relative));
                })
                .Distinct(comparer)
                .OrderBy(path => path, comparer)
                .ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CTildeProjectException($"Could not enumerate sources for '{fullManifestPath}': {exception.Message}", exception);
        }

        if (files.IsEmpty)
            throw new CTildeProjectException($"Project manifest '{fullManifestPath}' did not match any .ct source files.");

        if (kind == CTildeProjectKind.StandardLibrary)
        {
            return new CTildeProject(fullManifestPath, root,
                new CTildeProjectConfiguration(kind, CompilationTarget.Hosted, CompilationArchitecture.Auto, TargetEnvironment.Native, null,
                    sources, excludes, null, [], [], false, [], null, false, EspIdfPanicPolicy.Abort,
                    EspIdfArtifact.Firmware, null, null, null, null),
                files, ImmutableDictionary<string, SourceOwnerIdentity>.Empty);
        }

        if (target != CompilationTarget.EspIdf && document.EspIdf is not null)
            throw new CTildeProjectException($"Property 'espIdf' in '{fullManifestPath}' is valid only for ESP-IDF projects.");
        if (target != CompilationTarget.EspIdf && document.ManagedModule is not null)
            throw new CTildeProjectException($"Property 'managedModule' in '{fullManifestPath}' is valid only for ESP-IDF managed-module projects.");
        if (target != CompilationTarget.Hosted && document.Hosted is not null)
            throw new CTildeProjectException($"Property 'hosted' in '{fullManifestPath}' is valid only for hosted projects.");
        if (target != CompilationTarget.Freestanding && document.Freestanding is not null)
            throw new CTildeProjectException($"Property 'freestanding' in '{fullManifestPath}' is valid only for freestanding projects.");
        if (target != CompilationTarget.Cosmopolitan && document.Cosmopolitan is not null)
            throw new CTildeProjectException($"Property 'cosmopolitan' in '{fullManifestPath}' is valid only for Cosmopolitan projects.");
        if (simdOptimizations && target != CompilationTarget.Hosted)
            throw new CTildeProjectException($"Property 'simdOptimizations' in '{fullManifestPath}' is currently valid only for hosted projects.");
        if (simdOptimizations && architecture is not (CompilationArchitecture.Auto or CompilationArchitecture.X64))
            throw new CTildeProjectException($"Property 'simdOptimizations' in '{fullManifestPath}' currently requires architecture 'x64' or 'auto'.");
        var bindingPaths = document.EspIdf?.Bindings ?? [];
        var bindingManifests = bindingPaths.Select(path => EspIdfBindingManifest.Load(path, root)).OrderBy(binding => binding.ManifestPath, comparer).ToImmutableArray();
        if (bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }).Distinct(comparer).Count() != bindingManifests.Length * 2)
            throw new CTildeProjectException($"ESP-IDF binding outputs in '{fullManifestPath}' must be distinct.");
        foreach (var declaration in bindingManifests.Select(binding => binding.DeclarationsPath))
            if (files.Contains(declaration, comparer))
                throw new CTildeProjectException($"ESP-IDF binding declaration '{declaration}' cannot overwrite an ordinary project source in '{fullManifestPath}'.");
        files = files.Concat(bindingManifests.Select(binding => binding.DeclarationsPath).Where(File.Exists)).Distinct(comparer).OrderBy(path => path, comparer).ToImmutableArray();
        var restoredModules = RepositoryModules.LoadLocked(root, modules);
        files = files.Concat(restoredModules.SourceFiles).Distinct(comparer).OrderBy(path => path, comparer).ToImmutableArray();
        var build = CreateBuildConfiguration(document.Build, target, root, fullManifestPath, files);
        var espIdfArtifact = ParseEspIdfArtifact(document.EspIdf?.Artifact, target, fullManifestPath);
        var managedModule = CreateManagedModuleConfiguration(document.ManagedModule, espIdfArtifact, target, root, fullManifestPath, build);
        if (espIdfArtifact == EspIdfArtifact.ManagedModule && build.CLayout != GeneratedCLayout.Modules)
            throw new CTildeProjectException($"ESP-IDF managed-module project '{fullManifestPath}' requires build.cLayout 'modules'.", "CT6202");
        var run = CreateRunConfiguration(document.Run, build, root, fullManifestPath);
        foreach (var output in bindingManifests.SelectMany(binding => new[] { binding.DeclarationsPath, binding.AdapterSourcePath }))
            if (PathsEqual(output, build.GeneratedCPath) || PathsEqual(output, build.GeneratedHeaderPath) || IsInsideDirectory(output, build.GeneratedDirectory))
                throw new CTildeProjectException($"ESP-IDF binding output '{output}' conflicts with compiler output in '{fullManifestPath}'.");
        var panicPolicy = ParsePanicPolicy(document.PanicPolicy, target, fullManifestPath);
        var hosted = target == CompilationTarget.Hosted
            ? CreateHostedConfiguration(document.Hosted, root, fullManifestPath, build, files)
            : null;
        var freestanding = target == CompilationTarget.Freestanding
            ? CreateFreestandingConfiguration(document.Freestanding, root, fullManifestPath)
            : null;
        var cosmopolitan = target == CompilationTarget.Cosmopolitan
            ? CreateCosmopolitanConfiguration(document.Cosmopolitan, fullManifestPath)
            : null;
        return new CTildeProject(fullManifestPath, root, new CTildeProjectConfiguration(kind, target, architecture, environment, espIdfChip, sources, excludes, build, bindingManifests, cpuFeatures, simdOptimizations, modules, run,
            document.NoRecursion ?? false, panicPolicy, espIdfArtifact, managedModule, hosted, freestanding, cosmopolitan), files, restoredModules.SourceOwners);
    }

    private static SourceLocation JsonFailureLocation(SourceText source, JsonException exception)
    {
        var line = (int)Math.Clamp(exception.LineNumber ?? 0, 0, source.LineCount - 1);
        var lineStart = source.GetPosition(line, 0);
        var lineEnd = line + 1 < source.LineCount ? source.GetPosition(line + 1, 0) : source.Length;
        var lineText = source.Text.AsSpan(lineStart, Math.Max(0, lineEnd - lineStart));
        var bytes = System.Text.Encoding.UTF8.GetBytes(lineText.ToString());
        var byteColumn = (int)Math.Clamp(exception.BytePositionInLine ?? 0, 0, bytes.Length);
        var column = System.Text.Encoding.UTF8.GetCharCount(bytes, 0, byteColumn);
        return source.GetLocation(new TextSpan(Math.Min(source.Length, lineStart + column), 1));
    }

    private static string? InferPropertyPath(string message)
    {
        var property = Regex.Match(message, @"Property '([^']+)'");
        if (property.Success)
            return property.Groups[1].Value;
        if (message.Contains("build configuration", StringComparison.OrdinalIgnoreCase)) return "build.configuration";
        if (message.Contains("build optimization", StringComparison.OrdinalIgnoreCase)) return "build.optimization";
        if (message.Contains("CPU target", StringComparison.OrdinalIgnoreCase)) return "build.cpuTarget";
        if (message.Contains("floating-point mode", StringComparison.OrdinalIgnoreCase)) return "build.floatingPoint";
        if (message.Contains("PGO mode", StringComparison.OrdinalIgnoreCase)) return "build.pgo.mode";
        if (message.Contains("C layout", StringComparison.OrdinalIgnoreCase)) return "build.cLayout";
        if (message.Contains("architecture", StringComparison.OrdinalIgnoreCase)) return "architecture";
        if (message.Contains("target", StringComparison.OrdinalIgnoreCase)) return "target";
        if (message.Contains("project kind", StringComparison.OrdinalIgnoreCase)) return "kind";
        if (message.Contains("runtime file", StringComparison.OrdinalIgnoreCase)) return "hosted.runtimeFiles";
        if (message.Contains("sources", StringComparison.OrdinalIgnoreCase)) return "sources";
        return null;
    }

    private sealed class ManifestLocationMap
    {
        private readonly SourceText source;
        private readonly Dictionary<string, SourceLocation> values;

        private ManifestLocationMap(SourceText source, Dictionary<string, SourceLocation> values)
        {
            this.source = source;
            this.values = values;
        }

        public static ManifestLocationMap? Create(SourceText source)
        {
            try
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(source.Text);
                var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var paths = new Stack<string>();
                paths.Push(string.Empty);
                string? pending = null;
                var result = new Dictionary<string, SourceLocation>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var name = reader.GetString() ?? string.Empty;
                        pending = string.IsNullOrEmpty(paths.Peek()) ? name : paths.Peek() + "." + name;
                        result.TryAdd(pending, Location(source, utf8, reader.TokenStartIndex, reader.BytesConsumed));
                    }
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        if (pending is not null)
                        {
                            paths.Push(pending);
                            pending = null;
                        }
                        else if (reader.CurrentDepth > 0)
                        {
                            paths.Push(paths.Peek());
                        }
                    }
                    else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        if (paths.Count > 1)
                            paths.Pop();
                        pending = null;
                    }
                    else if (pending is not null)
                    {
                        result[pending] = Location(source, utf8, reader.TokenStartIndex, reader.BytesConsumed);
                        pending = null;
                    }
                }
                return new ManifestLocationMap(source, result);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public SourceLocation Find(string? path)
        {
            if (path is not null && values.TryGetValue(path, out var location))
                return location;
            if (path is not null)
            {
                var match = values.FirstOrDefault(entry => entry.Key.EndsWith("." + path, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Key))
                    return match.Value;
            }
            return source.GetLocation(new TextSpan(0, 0));
        }

        private static SourceLocation Location(SourceText source, byte[] utf8, long byteStart, long byteEnd)
        {
            var startBytes = (int)Math.Clamp(byteStart, 0, utf8.Length);
            var endBytes = (int)Math.Clamp(byteEnd, startBytes, utf8.Length);
            var start = System.Text.Encoding.UTF8.GetCharCount(utf8, 0, startBytes);
            var end = start + System.Text.Encoding.UTF8.GetCharCount(utf8, startBytes, endBytes - startBytes);
            return source.GetLocation(TextSpan.FromBounds(start, end));
        }
    }

    private static void ValidateStandardLibraryDocument(ProjectDocument document, string manifestPath)
    {
        var unsupported = new List<string>();
        if (document.Target is not null) unsupported.Add("target");
        if (document.Architecture is not null) unsupported.Add("architecture");
        if (document.CpuFeatures is not null) unsupported.Add("cpuFeatures");
        if (document.SimdOptimizations is not null) unsupported.Add("simdOptimizations");
        if (document.Modules is not null) unsupported.Add("modules");
        if (document.NoRecursion is not null) unsupported.Add("noRecursion");
        if (document.PanicPolicy is not null) unsupported.Add("panicPolicy");
        if (document.Build is not null) unsupported.Add("build");
        if (document.Run is not null) unsupported.Add("run");
        if (document.Hosted is not null) unsupported.Add("hosted");
        if (document.EspIdf is not null) unsupported.Add("espIdf");
        if (document.ManagedModule is not null) unsupported.Add("managedModule");
        if (document.Freestanding is not null) unsupported.Add("freestanding");
        if (document.Cosmopolitan is not null) unsupported.Add("cosmopolitan");
        if (unsupported.Count != 0)
            throw new CTildeProjectException($"Standard-library manifest '{manifestPath}' supports only kind, sources, and exclude; remove {string.Join(", ", unsupported)}.");
    }

    public static (string RootDirectory, ImmutableArray<RepositoryModuleReference> Modules) ReadModuleReferences(string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        ProjectDocument document;
        try
        {
            using var stream = File.OpenRead(fullManifestPath);
            document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions) ?? throw new JsonException("Empty project manifest.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read project manifest '{fullManifestPath}': {exception.Message}", exception);
        }
        return (Path.GetDirectoryName(fullManifestPath)!, ParseModules(document.Modules, fullManifestPath));
    }

    private static ImmutableArray<RepositoryModuleReference> ParseModules(ModuleDocument[]? documents, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<RepositoryModuleReference>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in documents ?? [])
        {
            if (string.IsNullOrWhiteSpace(module.Path) || module.Path.StartsWith(".", StringComparison.Ordinal) || module.Path.Contains('\\') || module.Path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new CTildeProjectException($"Repository module path '{module.Path}' in '{manifestPath}' must be a canonical slash-separated module identity.");
            if (!paths.Add(module.Path))
                throw new CTildeProjectException($"Repository module path '{module.Path}' appears more than once in '{manifestPath}'.");
            if (string.IsNullOrWhiteSpace(module.Repository) || string.IsNullOrWhiteSpace(module.Selector))
                throw new CTildeProjectException($"Repository module '{module.Path}' in '{manifestPath}' requires repository and selector values.");
            if (module.Alias is not null && (!Regex.IsMatch(module.Alias, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) || !aliases.Add(module.Alias)))
                throw new CTildeProjectException($"Repository module alias '{module.Alias}' in '{manifestPath}' is invalid or duplicated.");
            var updatePolicy = module.UpdatePolicy switch
            {
                null or "locked" => RepositoryModuleUpdatePolicy.Locked,
                "refresh" => RepositoryModuleUpdatePolicy.Refresh,
                _ => throw new CTildeProjectException($"Repository module '{module.Path}' in '{manifestPath}' has unknown updatePolicy '{module.UpdatePolicy}'; expected locked or refresh."),
            };
            result.Add(new RepositoryModuleReference(module.Path, module.Repository, module.Selector, module.Alias,
                ValidatePatterns(module.Sources is { Length: > 0 } ? module.Sources : ["**/*.ct"], "modules.sources", manifestPath),
                module.Vendor, updatePolicy));
        }
        return result.ToImmutable();
    }

    private static CompilationArchitecture ParseArchitecture(string? value, string manifestPath) => value switch
    {
        null or "auto" => CompilationArchitecture.Auto,
        "x86" => CompilationArchitecture.X86,
        "x64" => CompilationArchitecture.X64,
        "arm32" => CompilationArchitecture.Arm32,
        "arm64" => CompilationArchitecture.Arm64,
        "xtensa" => CompilationArchitecture.Xtensa,
        "riscv32" => CompilationArchitecture.RiscV32,
        "riscv64" => CompilationArchitecture.RiscV64,
        _ => throw new CTildeProjectException($"Unknown architecture '{value}' in '{manifestPath}'; expected auto, x86, x64, arm32, arm64, xtensa, riscv32, or riscv64."),
    };

    private static string ArchitectureName(CompilationArchitecture architecture) => architecture switch
    {
        CompilationArchitecture.Xtensa => "xtensa",
        CompilationArchitecture.RiscV32 => "riscv32",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    private static ImmutableArray<CpuFeature> ParseCpuFeatures(string[]? values, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<CpuFeature>();
        foreach (var value in values ?? [])
        {
            var feature = value switch
            {
                "simd128" => CpuFeature.Simd128,
                _ => throw new CTildeProjectException($"Unknown CPU feature '{value}' in '{manifestPath}'; expected simd128."),
            };
            if (result.Contains(feature))
                throw new CTildeProjectException($"CPU feature '{value}' appears more than once in '{manifestPath}'.");
            result.Add(feature);
        }
        return result.ToImmutable();
    }

    private static EspIdfPanicPolicy ParsePanicPolicy(string? value, CompilationTarget target, string manifestPath)
    {
        if (target != CompilationTarget.EspIdf && value is not null)
            throw new CTildeProjectException($"Property 'panicPolicy' in '{manifestPath}' is valid only for ESP-IDF projects.");
        return value switch
        {
            null or "abort" => EspIdfPanicPolicy.Abort,
            "restart" => EspIdfPanicPolicy.Restart,
            "halt" => EspIdfPanicPolicy.Halt,
            _ => throw new CTildeProjectException($"Unknown panic policy '{value}' in '{manifestPath}'; expected abort, restart, or halt."),
        };
    }

    private static EspIdfArtifact ParseEspIdfArtifact(string? value, CompilationTarget target, string manifestPath)
    {
        if (target != CompilationTarget.EspIdf && value is not null)
            throw new CTildeProjectException($"Property 'espIdf.artifact' in '{manifestPath}' is valid only for ESP-IDF projects.", "CT6202");
        return value switch
        {
            null or "firmware" => EspIdfArtifact.Firmware,
            "managed-module" when target == CompilationTarget.EspIdf => EspIdfArtifact.ManagedModule,
            "managed-module" => throw new CTildeProjectException($"ESP-IDF managed modules require target 'esp-idf' in '{manifestPath}'.", "CT6202"),
            _ => throw new CTildeProjectException($"Unknown ESP-IDF artifact '{value}' in '{manifestPath}'; expected firmware or managed-module.", "CT6202"),
        };
    }

    private static ManagedModuleConfiguration? CreateManagedModuleConfiguration(
        ManagedModuleDocument? document,
        EspIdfArtifact artifact,
        CompilationTarget target,
        string root,
        string manifestPath,
        CTildeProjectBuildConfiguration build)
    {
        if (artifact != EspIdfArtifact.ManagedModule)
        {
            if (document is not null)
                throw new CTildeProjectException($"Property 'managedModule' in '{manifestPath}' requires espIdf.artifact 'managed-module'.", "CT6202");
            return null;
        }
        if (target != CompilationTarget.EspIdf || document is null)
            throw new CTildeProjectException($"ESP-IDF managed-module project '{manifestPath}' requires a 'managedModule' block.", "CT6202");

        var kind = document.Kind switch
        {
            "application" => ManagedModuleKind.Application,
            "library" => ManagedModuleKind.Library,
            _ => throw new CTildeProjectException($"Managed module in '{manifestPath}' requires kind application or library.", "CT6202"),
        };
        if (document.Name is { Length: > ManagedModuleMetadata.MaximumNameAsciiBytes })
            throw new CTildeProjectException($"Managed-module name in '{manifestPath}' exceeds the Managed Module ABI {CompilerContract.ManagedModuleAbiVersion} limit of {ManagedModuleMetadata.MaximumNameAsciiBytes} ASCII bytes.", "CT6202");
        if (string.IsNullOrWhiteSpace(document.Name) || !ManagedModuleMetadata.IsCanonicalName(document.Name))
            throw new CTildeProjectException($"Managed-module name '{document.Name}' in '{manifestPath}' is not canonical.", "CT6202");
        if (document.Version is { Length: > ManagedModuleMetadata.MaximumVersionAsciiBytes })
            throw new CTildeProjectException($"Managed-module version in '{manifestPath}' exceeds the Managed Module ABI {CompilerContract.ManagedModuleAbiVersion} limit of {ManagedModuleMetadata.MaximumVersionAsciiBytes} ASCII bytes.", "CT6202");
        if (string.IsNullOrWhiteSpace(document.Version) || !ManagedModuleMetadata.IsExactVersion(document.Version))
            throw new CTildeProjectException($"Managed-module version '{document.Version}' in '{manifestPath}' must be an exact semantic version.", "CT6202");
        var stack = document.MainTaskStackBytes ?? 8192;
        if (stack < 2048 || stack % 16 != 0)
            throw new CTildeProjectException($"managedModule.mainTaskStackBytes in '{manifestPath}' must be at least 2048 and divisible by 16.", "CT6202");
        if (document.HeapLimitBytes is > 0 and < 1024)
            throw new CTildeProjectException($"managedModule.heapLimitBytes in '{manifestPath}' must be zero/unlimited or at least 1024.", "CT6202");

        var nativeSources = ResolveExistingFiles(document.NativeSources ?? [], "managedModule.nativeSources", root, manifestPath,
            path => Path.GetExtension(path).Equals(".c", StringComparison.Ordinal));
        var mainDirectory = Path.Combine(build.EspIdfProjectDirectory!, "main");
        foreach (var source in nativeSources)
        {
            if (!IsInsideDirectory(source, mainDirectory))
                throw new CTildeProjectException($"Managed-module native source '{source}' in '{manifestPath}' must be inside the ESP-IDF main component '{mainDirectory}'.", "CT6202");
            if (IsInsideDirectory(source, build.GeneratedDirectory))
                throw new CTildeProjectException($"Managed-module native source '{source}' in '{manifestPath}' cannot be inside the generated C directory.", "CT6202");
        }
        if (Directory.Exists(mainDirectory))
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var declared = nativeSources.ToHashSet(comparer);
            var undeclared = Directory.EnumerateFiles(mainDirectory, "*.c", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => !IsInsideDirectory(path, build.GeneratedDirectory) && !declared.Contains(path))
                .Order(comparer)
                .FirstOrDefault();
            if (undeclared is not null)
                throw new CTildeProjectException($"Managed-module C source '{undeclared}' in '{manifestPath}' must be declared in managedModule.nativeSources.", "CT6202");
        }

        var references = ImmutableArray.CreateBuilder<ManagedModuleReference>();
        var referenceMetadata = ImmutableArray.CreateBuilder<ManagedModuleMetadata>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in document.References ?? [])
        {
            var path = ResolveReferencePath(value, manifestPath);
            if (!path.EndsWith(".ctmeta.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new CTildeProjectException($"Managed-module reference '{path}' in '{manifestPath}' must be an existing .ctmeta.json file.", "CT6200");
            var metadata = ManagedModuleMetadata.Load(path);
            if (!names.Add(metadata.Name))
                throw new CTildeProjectException($"Managed-module references in '{manifestPath}' contain module '{metadata.Name}' more than once.", "CT6202");
            if (metadata.Name == document.Name)
                throw new CTildeProjectException($"Managed module '{document.Name}' cannot reference itself.", "CT6202");
            references.Add(new ManagedModuleReference(path, metadata.Name, metadata.Version, metadata.BuildIdentity, metadata.ApiHash, metadata));
            referenceMetadata.Add(metadata);
        }
        ValidateManagedReferenceGraph(document.Name, referenceMetadata.ToImmutable(), manifestPath);
        return new ManagedModuleConfiguration(kind, document.Name, document.Version, references.ToImmutable(), stack,
            document.HeapLimitBytes is null or 0 ? null : document.HeapLimitBytes, nativeSources, root, mainDirectory);
    }

    private static void ValidateManagedReferenceGraph(string rootName, ImmutableArray<ManagedModuleMetadata> references, string manifestPath)
    {
        var byName = references.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var module in references)
        {
            foreach (var dependency in module.Dependencies)
            {
                if (dependency.Name == rootName)
                    throw new CTildeProjectException($"Managed-module references in '{manifestPath}' contain a dependency cycle through '{rootName}'.", "CT6202");
                if (byName.TryGetValue(dependency.Name, out var resolved) &&
                    (resolved.Version != dependency.Version || resolved.BuildIdentity != dependency.BuildIdentity || resolved.ApiHash != dependency.ApiHash))
                    throw new CTildeProjectException($"Managed-module dependency '{module.Name}' -> '{dependency.Name}' in '{manifestPath}' does not match the referenced module's exact identity.", "CT6202");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(ManagedModuleMetadata module)
        {
            if (!visiting.Add(module.Name)) return false;
            foreach (var dependency in module.Dependencies)
                if (byName.TryGetValue(dependency.Name, out var next) && !visited.Contains(next.Name) && !Visit(next)) return false;
            visiting.Remove(module.Name);
            visited.Add(module.Name);
            return true;
        }

        foreach (var module in references)
            if (!visited.Contains(module.Name) && !Visit(module))
                throw new CTildeProjectException($"Managed-module references in '{manifestPath}' contain a dependency cycle involving '{module.Name}'.", "CT6202");
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    public static string? FindNearest(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : Path.GetDirectoryName(fullPath) ?? fullPath;
        for (var current = new DirectoryInfo(directory!); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "ctilde.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static ImmutableArray<string> ValidatePatterns(IEnumerable<string> patterns, string property, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var value in patterns)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot contain an empty pattern.");
            var normalized = value.Replace('\\', '/');
            if (Path.IsPathRooted(value) || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
                throw new CTildeProjectException($"Pattern '{value}' in '{manifestPath}' must stay within the project directory.");
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            result.Add(normalized);
        }
        return result.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static CTildeProjectBuildConfiguration CreateBuildConfiguration(
        BuildDocument? document,
        CompilationTarget target,
        string root,
        string manifestPath,
        ImmutableArray<string> sourceFiles)
    {
        if ((target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && document?.EspIdfProjectDirectory is not null)
            throw new CTildeProjectException($"Property 'build.espIdfProjectDirectory' in '{manifestPath}' is valid only for ESP-IDF projects.");
        if (target == CompilationTarget.EspIdf &&
            (document?.Compiler is not null || document?.Configuration is not null || document?.Executable is not null || document?.Image is not null || document?.Lto == true))
            throw new CTildeProjectException($"Properties 'build.compiler', 'build.configuration', 'build.executable', and 'build.lto' in '{manifestPath}' are valid only for hosted or Cosmopolitan projects.");
        if ((target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan) && document?.Image is not null)
            throw new CTildeProjectException($"Property 'build.image' in '{manifestPath}' is valid only for freestanding projects.");
        if (target == CompilationTarget.Freestanding && (document?.Executable is not null || document?.EspIdfProjectDirectory is not null))
            throw new CTildeProjectException($"Properties 'build.executable' and 'build.espIdfProjectDirectory' in '{manifestPath}' are invalid for freestanding projects.");

        var cLayout = document?.CLayout switch
        {
            null or "unity" => GeneratedCLayout.Unity,
            "modules" => GeneratedCLayout.Modules,
            _ => throw new CTildeProjectException($"Unknown C layout '{document.CLayout}' in '{manifestPath}'; expected unity or modules."),
        };

        var generatedCDefault = target == CompilationTarget.EspIdf
            ? "main/generated/ctilde_program.c"
            : "build/generated/ctilde_program.c";
        var generatedHeaderDefault = target == CompilationTarget.EspIdf
            ? "main/generated/ctilde_exports.h"
            : "build/generated/ctilde_exports.h";
        var generatedC = ResolveProjectPath(document?.GeneratedC ?? generatedCDefault, "build.generatedC", root, manifestPath, isDirectory: false);
        var generatedHeader = ResolveProjectPath(document?.GeneratedHeader ?? generatedHeaderDefault, "build.generatedHeader", root, manifestPath, isDirectory: false);
        var generatedDirectoryDefault = target == CompilationTarget.EspIdf ? "main/generated" : "build/generated/modules";
        var generatedDirectory = ResolveProjectPath(document?.GeneratedDirectory ?? generatedDirectoryDefault, "build.generatedDirectory", root, manifestPath, isDirectory: true);
        var symbolMap = document?.SymbolMap is null ? null : ResolveProjectPath(document.SymbolMap, "build.symbolMap", root, manifestPath, isDirectory: false);
        var stackReport = document?.StackReport is null ? null : ResolveProjectPath(document.StackReport, "build.stackReport", root, manifestPath, isDirectory: false);
        if (PathsEqual(generatedC, generatedHeader))
            throw new CTildeProjectException($"Properties 'build.generatedC' and 'build.generatedHeader' in '{manifestPath}' must name different files.");
        if (sourceFiles.Any(path => PathsEqual(path, generatedC) || PathsEqual(path, generatedHeader)))
            throw new CTildeProjectException($"Generated output paths in '{manifestPath}' must not overwrite a project source file.");
        if (stackReport is not null && (sourceFiles.Any(path => PathsEqual(path, stackReport)) ||
            PathsEqual(stackReport, generatedC) || PathsEqual(stackReport, generatedHeader) ||
            symbolMap is not null && PathsEqual(stackReport, symbolMap)))
            throw new CTildeProjectException($"Property 'build.stackReport' in '{manifestPath}' must name a distinct non-source file.");

        var configuration = document?.Configuration switch
        {
            null or "debug" => CTildeNativeBuildConfiguration.Debug,
            "release" => CTildeNativeBuildConfiguration.Release,
            _ => throw new CTildeProjectException($"Unknown build configuration '{document.Configuration}' in '{manifestPath}'; expected debug or release."),
        };
        var lto = document?.Lto ?? false;
        if (lto && configuration != CTildeNativeBuildConfiguration.Release)
            throw new CTildeProjectException($"Property 'build.lto' in '{manifestPath}' requires build.configuration 'release'.");
        var compiler = document?.Compiler ?? "auto";
        if (string.IsNullOrWhiteSpace(compiler))
            throw new CTildeProjectException($"Property 'build.compiler' in '{manifestPath}' cannot be empty.");
        if (!compiler.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase) &&
            (compiler.Contains(Path.DirectorySeparatorChar) || compiler.Contains(Path.AltDirectorySeparatorChar)))
            compiler = ResolveProjectPath(compiler, "build.compiler", root, manifestPath, isDirectory: false);

        NativeOptimization? optimization = document?.Optimization switch
        {
            null => null,
            "speed" => NativeOptimization.Speed,
            "aggressive" => NativeOptimization.Aggressive,
            _ => throw new CTildeProjectException($"Unknown build optimization '{document.Optimization}' in '{manifestPath}'; expected speed or aggressive."),
        };
        NativeCpuTarget? cpuTarget = document?.CpuTarget switch
        {
            null => null,
            "baseline" => NativeCpuTarget.Baseline,
            "avx2" => NativeCpuTarget.Avx2,
            _ => throw new CTildeProjectException($"Unknown build CPU target '{document.CpuTarget}' in '{manifestPath}'; expected baseline or avx2."),
        };
        NativeFloatingPointMode? floatingPoint = document?.FloatingPoint switch
        {
            null => null,
            "precise" => NativeFloatingPointMode.Precise,
            "fast" => NativeFloatingPointMode.Fast,
            _ => throw new CTildeProjectException($"Unknown floating-point mode '{document.FloatingPoint}' in '{manifestPath}'; expected precise or fast."),
        };
        NativePgoConfiguration? pgo = null;
        if (document?.Pgo is not null)
        {
            var pgoMode = document.Pgo.Mode switch
            {
                null or "off" => NativePgoMode.Off,
                "generate" => NativePgoMode.Generate,
                "use" => NativePgoMode.Use,
                _ => throw new CTildeProjectException($"Unknown PGO mode '{document.Pgo.Mode}' in '{manifestPath}'; expected off, generate, or use."),
            };
            var pgoDirectory = ResolveProjectPath(document.Pgo.Directory ?? "build/pgo", "build.pgo.directory", root, manifestPath, isDirectory: true);
            pgo = new NativePgoConfiguration(pgoMode, pgoDirectory);
        }

        string? executable = null;
        string? espIdfProjectDirectory = null;
        if (target is CompilationTarget.Hosted or CompilationTarget.Cosmopolitan)
        {
            var projectName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(projectName))
                projectName = "program";
            var extension = target == CompilationTarget.Cosmopolitan ? ".com" : OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var defaultExecutable = $"build/{projectName}{extension}";
            executable = ResolveProjectPath(document?.Executable ?? defaultExecutable, "build.executable", root, manifestPath, isDirectory: false);
            if (PathsEqual(executable, generatedC) || PathsEqual(executable, generatedHeader) || sourceFiles.Any(path => PathsEqual(path, executable)))
                throw new CTildeProjectException($"Property 'build.executable' in '{manifestPath}' must name a distinct non-source file.");
        }
        else if (target == CompilationTarget.EspIdf)
        {
            espIdfProjectDirectory = ResolveProjectPath(document?.EspIdfProjectDirectory ?? ".", "build.espIdfProjectDirectory", root, manifestPath, isDirectory: true);
        }
        else if (document?.Image is not null)
        {
            executable = ResolveProjectPath(document.Image, "build.image", root, manifestPath, isDirectory: false);
            if (PathsEqual(executable, generatedC) || PathsEqual(executable, generatedHeader) || sourceFiles.Any(path => PathsEqual(path, executable)))
                throw new CTildeProjectException($"Property 'build.image' in '{manifestPath}' must name a distinct non-source file.");
        }

        return new CTildeProjectBuildConfiguration(generatedC, generatedHeader, cLayout, generatedDirectory, symbolMap, stackReport, lto,
            configuration, compiler, executable, espIdfProjectDirectory, optimization, cpuTarget, floatingPoint, pgo);
    }

    private static CTildeProjectRunConfiguration? CreateRunConfiguration(
        RunDocument? document,
        CTildeProjectBuildConfiguration build,
        string root,
        string manifestPath)
    {
        if (document is null)
            return null;
        var executor = document.Executor switch
        {
            null or "host" => CTildeRunExecutor.Host,
            "wsl" => CTildeRunExecutor.Wsl,
            _ => throw new CTildeProjectException($"Unknown run executor '{document.Executor}' in '{manifestPath}'; expected host or wsl."),
        };
        if (document.Command is not null)
        {
            ValidateRunTemplate(document.Command, "run.command", manifestPath);
            if (Path.IsPathRooted(document.Command))
                throw new CTildeProjectException($"Property 'run.command' in '{manifestPath}' must be a PATH command, project-relative path, or supported placeholder expression.");
        }
        var arguments = (document.Arguments ?? []).Select((value, index) =>
        {
            ValidateRunTemplate(value, $"run.args[{index}]", manifestPath, allowEmpty: true);
            return value;
        }).ToImmutableArray();
        var workingDirectory = ExpandRunPath(document.WorkingDirectory ?? ".", "run.workingDirectory", root, build.ExecutablePath, manifestPath);
        var environment = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.Environment ?? [])
        {
            if (!Regex.IsMatch(entry.Key, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                throw new CTildeProjectException($"Environment variable name '{entry.Key}' in 'run.environment' in '{manifestPath}' is invalid.");
            if (entry.Value.Contains('\0'))
                throw new CTildeProjectException($"Environment variable '{entry.Key}' in 'run.environment' in '{manifestPath}' contains a null character.");
            ValidateRunTemplate(entry.Value, $"run.environment.{entry.Key}", manifestPath, allowEmpty: true);
            environment.Add(entry.Key, entry.Value);
        }
        var successExitCodes = (document.SuccessExitCodes ?? [0]).ToImmutableArray();
        if (successExitCodes.IsEmpty)
            throw new CTildeProjectException($"Property 'run.successExitCodes' in '{manifestPath}' requires at least one exit code.");
        if (successExitCodes.Distinct().Count() != successExitCodes.Length)
            throw new CTildeProjectException($"Property 'run.successExitCodes' in '{manifestPath}' cannot contain duplicate exit codes.");
        return new CTildeProjectRunConfiguration(executor, document.Command, arguments, workingDirectory,
            environment.ToImmutable(), successExitCodes);
    }

    private static string ExpandRunPath(string value, string property, string root, string? buildOutput, string manifestPath)
    {
        ValidateRunTemplate(value, property, manifestPath);
        var expanded = value.Replace("${projectRoot}", root, StringComparison.Ordinal);
        if (expanded.Contains("${buildOutput}", StringComparison.Ordinal))
        {
            if (buildOutput is null)
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' uses ${{buildOutput}}, but this target has no executable or image output.");
            expanded = expanded.Replace("${buildOutput}", buildOutput, StringComparison.Ordinal);
        }
        var fullPath = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must stay within the project directory.");
        return fullPath;
    }

    private static void ValidateRunTemplate(string value, string property, string manifestPath, bool allowEmpty = false)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot be empty.");
        if (value.Contains('\0'))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains a null character.");
        var remainder = Regex.Replace(value, @"\$\{(projectRoot|buildOutput)\}", string.Empty, RegexOptions.CultureInvariant);
        if (remainder.Contains("${", StringComparison.Ordinal))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains an unknown or malformed placeholder; expected ${{projectRoot}} or ${{buildOutput}}.");
    }

    private static FreestandingProjectConfiguration CreateFreestandingConfiguration(
        FreestandingDocument? document,
        string root,
        string manifestPath)
    {
        string? linkerScript = null;
        if (document?.LinkerScript is not null)
        {
            linkerScript = ResolveProjectPath(document.LinkerScript, "freestanding.linkerScript", root, manifestPath, isDirectory: false);
            if (!File.Exists(linkerScript))
                throw new CTildeProjectException($"Freestanding linker script '{linkerScript}' in '{manifestPath}' does not exist.");
        }

        var entrySymbol = document?.EntrySymbol;
        if (entrySymbol is not null && !IsPortableNativeSymbol(entrySymbol))
            throw new CTildeProjectException($"Property 'freestanding.entrySymbol' in '{manifestPath}' must be a portable native symbol name.");

        var nativeSources = ResolveExistingFiles(document?.NativeSources ?? [], "freestanding.nativeSources", root, manifestPath,
            path => Path.GetExtension(path) is ".c" or ".s" or ".S");
        var objectFiles = ResolveExistingFiles(document?.ObjectFiles ?? [], "freestanding.objectFiles", root, manifestPath,
            path => Path.GetExtension(path).Equals(".o", StringComparison.OrdinalIgnoreCase));
        var libraries = ResolveExistingFiles(document?.Libraries ?? [], "freestanding.libraries", root, manifestPath,
            path => Path.GetExtension(path).Equals(".a", StringComparison.OrdinalIgnoreCase));
        var compileOptions = ValidateNativeOptions(document?.CompileOptions ?? [], "freestanding.compileOptions", manifestPath);
        var linkOptions = ValidateNativeOptions(document?.LinkOptions ?? [], "freestanding.linkOptions", manifestPath);
        return new FreestandingProjectConfiguration(linkerScript, entrySymbol, nativeSources, objectFiles, libraries, compileOptions, linkOptions);
    }

    private static HostedProjectConfiguration CreateHostedConfiguration(
        HostedDocument? document,
        string root,
        string manifestPath,
        CTildeProjectBuildConfiguration build,
        ImmutableArray<string> sourceFiles)
    {
        var nativeSources = ResolveExistingFiles(document?.NativeSources ?? [], "hosted.nativeSources", root, manifestPath,
            path => Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase));
        var runtimeFiles = ImmutableArray.CreateBuilder<HostedRuntimeFile>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtimeFile in document?.RuntimeFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(runtimeFile.OperatingSystem) ||
                string.IsNullOrWhiteSpace(runtimeFile.Architecture) ||
                string.IsNullOrWhiteSpace(runtimeFile.Source) ||
                string.IsNullOrWhiteSpace(runtimeFile.Output))
                throw new CTildeProjectException($"Each 'hosted.runtimeFiles' entry in '{manifestPath}' requires non-empty os, architecture, source, and output properties.");
            var operatingSystem = runtimeFile.OperatingSystem switch
            {
                "windows" => HostedOperatingSystem.Windows,
                "linux" => HostedOperatingSystem.Linux,
                _ => throw new CTildeProjectException($"Unknown hosted runtime-file operating system '{runtimeFile.OperatingSystem}' in '{manifestPath}'; expected windows or linux."),
            };
            var architecture = ParseArchitecture(runtimeFile.Architecture, manifestPath);
            if (architecture == CompilationArchitecture.Auto)
                throw new CTildeProjectException($"Property 'hosted.runtimeFiles.architecture' in '{manifestPath}' must name a concrete architecture.");
            var source = ResolveExplicitInputPath(runtimeFile.Source, "hosted.runtimeFiles.source", root, manifestPath);
            if (!File.Exists(source))
                throw new CTildeProjectException($"Hosted runtime file '{source}' in '{manifestPath}' does not exist.");
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
                throw new CTildeProjectException($"Hosted runtime file '{source}' in '{manifestPath}' must name a file.");
            var output = runtimeFile.Output;
            if (string.IsNullOrWhiteSpace(output) || output is "." or ".." || Path.GetFileName(output) != output ||
                output.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || output.Contains(Path.DirectorySeparatorChar) || output.Contains(Path.AltDirectorySeparatorChar))
                throw new CTildeProjectException($"Property 'hosted.runtimeFiles.output' in '{manifestPath}' must be a portable file name without directory separators.");
            var destinationKey = $"{operatingSystem}:{architecture}:{output}";
            if (!destinations.Add(destinationKey))
                throw new CTildeProjectException($"Hosted runtime destination '{output}' for {operatingSystem.ToString().ToLowerInvariant()} {ArchitectureName(architecture)} appears more than once in '{manifestPath}'.");
            var destination = Path.Combine(Path.GetDirectoryName(build.ExecutablePath!)!, output);
            if (PathsEqual(source, destination) || PathsEqual(destination, build.ExecutablePath!) ||
                PathsEqual(destination, build.GeneratedCPath) || PathsEqual(destination, build.GeneratedHeaderPath) ||
                sourceFiles.Any(path => PathsEqual(path, destination)) || nativeSources.Any(path => PathsEqual(path, destination)))
                throw new CTildeProjectException($"Hosted runtime destination '{destination}' in '{manifestPath}' conflicts with a source or compiler-owned output.");
            runtimeFiles.Add(new HostedRuntimeFile(operatingSystem, architecture, source, output));
        }
        return new HostedProjectConfiguration(nativeSources, runtimeFiles.ToImmutable());
    }

    private static string ResolveExplicitInputPath(string value, string property, string root, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || Path.EndsInDirectorySeparator(value) ||
            value.IndexOfAny(['*', '?']) >= 0)
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a non-empty explicit relative file path.");
        return Path.GetFullPath(Path.Combine(root, value));
    }

    private static CosmopolitanProjectConfiguration CreateCosmopolitanConfiguration(
        CosmopolitanDocument? document,
        string manifestPath)
    {
        var mode = document?.Mode switch
        {
            null or "default" => CosmopolitanRuntimeMode.Default,
            "tiny" => CosmopolitanRuntimeMode.Tiny,
            "debug" => CosmopolitanRuntimeMode.Debug,
            _ => throw new CTildeProjectException($"Unknown Cosmopolitan mode '{document.Mode}' in '{manifestPath}'; expected default, tiny, or debug."),
        };
        return new CosmopolitanProjectConfiguration(mode);
    }

    private static ImmutableArray<string> ResolveExistingFiles(
        IEnumerable<string> values,
        string property,
        string root,
        string manifestPath,
        Func<string, bool> validExtension)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(comparer);
        foreach (var value in values)
        {
            var path = ResolveProjectPath(value, property, root, manifestPath, isDirectory: false);
            if (!validExtension(path))
                throw new CTildeProjectException($"File '{value}' in '{property}' has an unsupported extension.");
            if (!File.Exists(path))
                throw new CTildeProjectException($"File '{path}' in '{property}' does not exist.");
            if (!unique.Add(path))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' contains duplicate file '{value}'.");
            result.Add(path);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<string> ValidateNativeOptions(IEnumerable<string> values, string property, string manifestPath)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('@'))
                throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' cannot contain empty arguments or response files.");
            if (value is "-c" or "-S" or "-E" or "-o" or "-T" or "--output" or "--entry" or "--script" ||
                value.StartsWith("-o", StringComparison.Ordinal) || value.StartsWith("-T", StringComparison.Ordinal) ||
                value.StartsWith("--output=", StringComparison.Ordinal) || value.StartsWith("--entry=", StringComparison.Ordinal) ||
                value.StartsWith("--script=", StringComparison.Ordinal) || value.StartsWith("-Wl,-e", StringComparison.Ordinal) ||
                value.StartsWith("-Wl,-T", StringComparison.Ordinal) || value.StartsWith("-Wl,--entry", StringComparison.Ordinal) ||
                value.StartsWith("-Wl,--script", StringComparison.Ordinal) || value.StartsWith("-Wl,-o", StringComparison.Ordinal))
                throw new CTildeProjectException($"Option '{value}' in '{property}' overrides a compiler-owned build setting.");
            result.Add(value);
        }
        return result.ToImmutable();
    }

    private static bool IsPortableNativeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !(char.IsAsciiLetter(value[0]) || value[0] is '_' or '$'))
            return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$');
    }

    private static string ResolveProjectPath(string value, string property, string root, string manifestPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a non-empty relative path.");
        var fullPath = Path.GetFullPath(Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must stay within the project directory.");
        if (!isDirectory && Path.EndsInDirectorySeparator(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must name a file.");
        return fullPath;
    }

    private static string ResolveReferencePath(string value, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || Path.EndsInDirectorySeparator(value))
            throw new CTildeProjectException($"Property 'managedModule.references' in '{manifestPath}' must contain relative file paths.");
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, value));
    }

    private static bool PathsEqual(string left, string right) =>
        left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool GlobMatches(string pattern, string relativePath)
    {
        var regex = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    regex.Append("(?:.*/)?");
                }
                else
                    regex.Append(".*");
            }
            else if (character == '*')
                regex.Append("[^/]*");
            else if (character == '?')
                regex.Append("[^/]");
            else
                regex.Append(Regex.Escape(character.ToString()));
        }
        regex.Append('$');
        return Regex.IsMatch(relativePath, regex.ToString(), OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record ProjectDocument(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("target")] string? Target,
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("cpuFeatures")] string[]? CpuFeatures,
        [property: JsonPropertyName("simdOptimizations")] bool? SimdOptimizations,
        [property: JsonPropertyName("modules")] ModuleDocument[]? Modules,
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("exclude")] string[]? Exclude,
        [property: JsonPropertyName("noRecursion")] bool? NoRecursion,
        [property: JsonPropertyName("panicPolicy")] string? PanicPolicy,
        [property: JsonPropertyName("build")] BuildDocument? Build,
        [property: JsonPropertyName("run")] RunDocument? Run,
        [property: JsonPropertyName("hosted")] HostedDocument? Hosted,
        [property: JsonPropertyName("espIdf")] EspIdfDocument? EspIdf,
        [property: JsonPropertyName("managedModule")] ManagedModuleDocument? ManagedModule,
        [property: JsonPropertyName("freestanding")] FreestandingDocument? Freestanding,
        [property: JsonPropertyName("cosmopolitan")] CosmopolitanDocument? Cosmopolitan);

    private sealed record ModuleDocument(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("selector")] string Selector,
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("sources")] string[]? Sources,
        [property: JsonPropertyName("vendor")] string? Vendor,
        [property: JsonPropertyName("updatePolicy")] string? UpdatePolicy);

    private sealed record EspIdfDocument(
        [property: JsonPropertyName("artifact")] string? Artifact,
        [property: JsonPropertyName("bindings")] string[]? Bindings);

    private sealed record ManagedModuleDocument(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("references")] string[]? References,
        [property: JsonPropertyName("nativeSources")] string[]? NativeSources,
        [property: JsonPropertyName("mainTaskStackBytes")] uint? MainTaskStackBytes,
        [property: JsonPropertyName("heapLimitBytes")] ulong? HeapLimitBytes);

    private sealed record HostedDocument(
        [property: JsonPropertyName("nativeSources")] string[]? NativeSources,
        [property: JsonPropertyName("runtimeFiles")] HostedRuntimeFileDocument[]? RuntimeFiles);

    private sealed record HostedRuntimeFileDocument(
        [property: JsonPropertyName("os")] string OperatingSystem,
        [property: JsonPropertyName("architecture")] string Architecture,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("output")] string Output);

    private sealed record FreestandingDocument(
        [property: JsonPropertyName("linkerScript")] string? LinkerScript,
        [property: JsonPropertyName("entrySymbol")] string? EntrySymbol,
        [property: JsonPropertyName("nativeSources")] string[]? NativeSources,
        [property: JsonPropertyName("objectFiles")] string[]? ObjectFiles,
        [property: JsonPropertyName("libraries")] string[]? Libraries,
        [property: JsonPropertyName("compileOptions")] string[]? CompileOptions,
        [property: JsonPropertyName("linkOptions")] string[]? LinkOptions);

    private sealed record CosmopolitanDocument([property: JsonPropertyName("mode")] string? Mode);

    private sealed record BuildDocument(
        [property: JsonPropertyName("generatedC")] string? GeneratedC,
        [property: JsonPropertyName("generatedHeader")] string? GeneratedHeader,
        [property: JsonPropertyName("cLayout")] string? CLayout,
        [property: JsonPropertyName("generatedDirectory")] string? GeneratedDirectory,
        [property: JsonPropertyName("symbolMap")] string? SymbolMap,
        [property: JsonPropertyName("stackReport")] string? StackReport,
        [property: JsonPropertyName("lto")] bool? Lto,
        [property: JsonPropertyName("configuration")] string? Configuration,
        [property: JsonPropertyName("compiler")] string? Compiler,
        [property: JsonPropertyName("optimization")] string? Optimization,
        [property: JsonPropertyName("cpuTarget")] string? CpuTarget,
        [property: JsonPropertyName("floatingPoint")] string? FloatingPoint,
        [property: JsonPropertyName("pgo")] PgoDocument? Pgo,
        [property: JsonPropertyName("executable")] string? Executable,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("espIdfProjectDirectory")] string? EspIdfProjectDirectory);

    private sealed record PgoDocument(
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("directory")] string? Directory);

    private sealed record RunDocument(
        [property: JsonPropertyName("executor")] string? Executor,
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("args")] string[]? Arguments,
        [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
        [property: JsonPropertyName("environment")] Dictionary<string, string>? Environment,
        [property: JsonPropertyName("successExitCodes")] int[]? SuccessExitCodes);
}
