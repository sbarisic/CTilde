using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

internal static class EspIdfBindingGenerator
{
    private const int CacheSchemaVersion = 2;

    public static async Task<bool> RefreshAsync(BuildRequest request, bool verifyOnly, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var manifests = request.BindingManifests ?? [];
        if (manifests.Count == 0)
        {
            if (request.GenerateBindingsOnly || verifyOnly)
                Console.Error.WriteLine("ctilde: The project does not declare any ESP-IDF bindings.");
            return !request.GenerateBindingsOnly && !verifyOnly;
        }
        if (request.Target != CompilationTarget.EspIdf || request.ManifestPath is null || request.BindingGeneratedDirectory is null)
            throw new NativeBuildException("ESP-IDF bindings require an ESP-IDF project manifest.");

        ValidateOutputCollisions(request, manifests);
        var cacheReason = request.GenerateBindingsOnly ? "explicit generation requested" : string.Empty;
        if (!request.GenerateBindingsOnly && TryUseCache(request, manifests, out cacheReason))
        {
            var fragment = Path.Combine(request.BindingGeneratedDirectory!, "ctilde_bindings.cmake");
            WriteBindingFragment(request, manifests, fragment, useProbe: false);
            if (request.Trace)
                Console.Error.WriteLine($"trace: ESP-IDF bindings cache hit ({elapsed.ElapsedMilliseconds} ms)");
            return true;
        }
        if (request.Trace)
            Console.Error.WriteLine($"trace: ESP-IDF bindings cache miss: {cacheReason}");
        var context = PrepareBootstrap(request, manifests);
        await EspIdfBuildDriver.ReconfigureForBindingsAsync(request, cancellationToken);
        var clang = await ResolveEspClangAsync(request, cancellationToken);
        var compile = ReadCompileContext(request, context.ProbeSource);
        var fingerprint = ComputeFingerprint(request, manifests, compile);
        var nativeOrders = await InspectAdapterOrdersAsync(clang, compile, manifests, request, cancellationToken);
        var outputs = manifests.Select(manifest => Generate(manifest, fingerprint, nativeOrders)).ToArray();

        foreach (var output in outputs)
            await ValidateAdapterAsync(clang, compile, output, request, cancellationToken);

        var stale = outputs.Where(output => !File.Exists(output.Manifest.DeclarationsPath) || !File.Exists(output.Manifest.AdapterSourcePath) ||
                                           !File.ReadAllText(output.Manifest.DeclarationsPath).Equals(output.Declarations, StringComparison.Ordinal) ||
                                           !File.ReadAllText(output.Manifest.AdapterSourcePath).Equals(output.Adapter, StringComparison.Ordinal))
            .ToArray();
        if (verifyOnly && stale.Length != 0)
        {
            foreach (var output in stale)
                Console.Error.WriteLine($"ctilde: ESP-IDF binding outputs for '{output.Manifest.ManifestPath}' are stale; run --generate-bindings.");
            return false;
        }

        if (!verifyOnly)
        {
            foreach (var output in stale)
            {
                WriteAtomically(output.Manifest.DeclarationsPath, output.Declarations);
                WriteAtomically(output.Manifest.AdapterSourcePath, output.Adapter);
                if (request.Trace)
                    Console.Error.WriteLine($"trace: refreshed ESP-IDF bindings {output.Manifest.DeclarationsPath} and {output.Manifest.AdapterSourcePath}");
            }
        }
        if (request.Trace && stale.Length != outputs.Length)
            Console.Error.WriteLine($"trace: ESP-IDF binding outputs unchanged={outputs.Length - stale.Length}");
        WriteBindingFragment(request, manifests, context.FragmentPath, useProbe: false);
        await WriteCacheAsync(request, manifests, compile, clang, cancellationToken);
        if (request.Trace)
            Console.Error.WriteLine($"trace: ESP-IDF bindings refreshed and validated ({elapsed.ElapsedMilliseconds} ms)");
        return true;
    }

    private static bool TryUseCache(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests, out string reason)
    {
        var cachePath = CachePath(request);
        if (!File.Exists(cachePath))
        {
            reason = "cache state is missing";
            return false;
        }
        try
        {
            var state = JsonSerializer.Deserialize<BindingCacheState>(File.ReadAllText(cachePath));
            if (state is null || state.SchemaVersion != CacheSchemaVersion)
            {
                reason = "cache schema changed";
                return false;
            }
            if (!ManifestSignature(manifests).Equals(state.ManifestSignature, StringComparison.Ordinal))
            {
                reason = "binding manifest content changed";
                return false;
            }
            if (!File.Exists(state.ClangPath))
            {
                reason = "cached Espressif Clang is missing";
                return false;
            }
            var clang = new FileInfo(state.ClangPath);
            if (clang.Length != state.ClangLength || clang.LastWriteTimeUtc.Ticks != state.ClangWriteTicks)
            {
                reason = "Espressif Clang changed";
                return false;
            }
            foreach (var input in state.Inputs)
            {
                if (!File.Exists(input.Path) || !HashFile(input.Path).Equals(input.Hash, StringComparison.Ordinal))
                {
                    reason = $"input changed: {Path.GetFileName(input.Path)}";
                    return false;
                }
            }
            foreach (var output in state.Outputs)
            {
                if (!File.Exists(output.Path) || !HashFile(output.Path).Equals(output.Hash, StringComparison.Ordinal))
                {
                    reason = $"generated output changed: {Path.GetFileName(output.Path)}";
                    return false;
                }
            }
            var probe = Path.Combine(request.BindingGeneratedDirectory!, "ctilde_bindings_probe.c");
            var compile = ReadCompileContext(request, probe);
            if (!CompileSignature(compile).Equals(state.CompileSignature, StringComparison.Ordinal))
            {
                reason = "ESP-IDF compile context changed";
                return false;
            }
            if (manifests.Count != state.ManifestCount)
            {
                reason = "binding manifest set changed";
                return false;
            }
            if (!CacheConfigurationSignature(request).Equals(state.ConfigurationSignature, StringComparison.Ordinal))
            {
                reason = "ESP-IDF CMake or sdkconfig inputs changed";
                return false;
            }
            reason = "current";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NativeBuildException or InvalidOperationException or KeyNotFoundException)
        {
            reason = $"cache could not be validated: {exception.Message}";
            return false;
        }
    }

    private static async Task WriteCacheAsync(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests, CompileContext compile,
        string clangPath, CancellationToken cancellationToken)
    {
        var inputPaths = ResolveImportedHeaders(manifests, compile)
            .Concat(CacheConfigurationInputs(request, compile))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var outputPaths = manifests.SelectMany(manifest => new[] { manifest.DeclarationsPath, manifest.AdapterSourcePath })
            .Append(Path.Combine(request.BindingGeneratedDirectory!, "ctilde_bindings.cmake"))
            .Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
        var clang = new FileInfo(clangPath);
        var version = await NativeProcessRunner.RunAsync(new NativeProcessRequest(clangPath, ["--version"], request.RootDirectory, ForwardOutput: false), cancellationToken);
        if (version.ExitCode != 0)
            throw new NativeBuildException("Could not read the selected Espressif Clang version for the binding cache.");
        var state = new BindingCacheState(CacheSchemaVersion, manifests.Count, ManifestSignature(manifests), CompileSignature(compile), CacheConfigurationSignature(request), clang.FullName, clang.Length,
            clang.LastWriteTimeUtc.Ticks, version.StandardOutput.Trim(), inputPaths.Select(path => new BindingCacheFile(path, HashFile(path))).ToArray(),
            outputPaths.Select(path => new BindingCacheFile(path, HashFile(path))).ToArray());
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        AtomicFile.WriteTextIfChanged(CachePath(request), json);
    }

    private static IEnumerable<string> CacheConfigurationInputs(BuildRequest request, CompileContext compile)
    {
        yield return compile.ConfigPath;
        yield return Path.Combine(request.EspIdfProjectDirectory!, "CMakeLists.txt");
        yield return Path.Combine(request.EspIdfProjectDirectory!, "main", "CMakeLists.txt");
        yield return Path.Combine(request.EspIdfProjectDirectory!, "sdkconfig.defaults");
        yield return Path.Combine(request.EspIdfProjectDirectory!, "dependencies.lock");
    }

    private static string CacheConfigurationSignature(BuildRequest request)
    {
        var project = request.EspIdfProjectDirectory!;
        var candidates = new[]
        {
            Path.Combine(project, "CMakeLists.txt"),
            Path.Combine(project, "sdkconfig"),
            Path.Combine(project, "dependencies.lock"),
            Path.Combine(project, "partitions.csv"),
            Path.Combine(project, "main", "CMakeLists.txt"),
            Path.Combine(project, "main", "idf_component.yml")
        }.Concat(Directory.EnumerateFiles(project, "sdkconfig.defaults*", SearchOption.TopDirectoryOnly))
         .Select(Path.GetFullPath)
         .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
         .Order(StringComparer.Ordinal)
         .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in candidates)
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            hash.AppendData(File.Exists(path) ? File.ReadAllBytes(path) : "<missing>"u8);
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CompileSignature(CompileContext compile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(compile.Target); Add(compile.IdfVersion); Add(Path.GetFullPath(compile.Directory));
        for (var index = 0; index < compile.Arguments.Count; index++)
        {
            var argument = compile.Arguments[index];
            if (argument is "-o" or "-MF" or "-MT" or "-MQ") { index++; continue; }
            if (argument.EndsWith(".c", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.GetFullPath(argument, compile.Directory))) continue;
            Add(argument);
        }
        foreach (var directory in compile.IncludeDirectories.Order(StringComparer.Ordinal)) Add(Path.GetFullPath(directory));
        foreach (var component in compile.ComponentDirectories.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { Add(component.Key); Add(Path.GetFullPath(component.Value)); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ManifestSignature(IReadOnlyList<EspIdfBindingManifest> manifests)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var manifest in manifests.OrderBy(item => item.ManifestPath, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(manifest.ManifestPath) + "\n"));
            hash.AppendData(Encoding.UTF8.GetBytes(manifest.CanonicalText()));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CachePath(BuildRequest request) =>
        Path.Combine(request.EspIdfBuildDirectory, ".ctilde", "bindings", "state.json");

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static BindingContext PrepareBootstrap(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests)
    {
        var generatedDirectory = request.BindingGeneratedDirectory!;
        Directory.CreateDirectory(generatedDirectory);
        var componentDirectory = Directory.GetParent(generatedDirectory)?.FullName ?? throw new NativeBuildException("The ESP-IDF generated directory must be inside a component directory.");
        var componentCmake = Path.Combine(componentDirectory, "CMakeLists.txt");
        if (!File.Exists(componentCmake))
            throw new NativeBuildException($"ESP-IDF binding component '{componentDirectory}' must contain CMakeLists.txt.");
        var cmake = File.ReadAllText(componentCmake);
        if (!cmake.Contains("ctilde_bindings.cmake", StringComparison.Ordinal) || !cmake.Contains("CTILDE_BINDING_SOURCES", StringComparison.Ordinal) || !cmake.Contains("CTILDE_BINDING_REQUIRES", StringComparison.Ordinal))
            throw new NativeBuildException("ESP-IDF component CMakeLists.txt must include generated/ctilde_bindings.cmake and pass CTILDE_BINDING_SOURCES and CTILDE_BINDING_REQUIRES to idf_component_register.");
        var probe = Path.Combine(generatedDirectory, "ctilde_bindings_probe.c");
        WriteAtomically(probe, "/* C~ ESP-IDF binding compile-context probe. */\nvoid ct_idf_binding_probe(void) { }\n");
        var fragment = Path.Combine(generatedDirectory, "ctilde_bindings.cmake");
        WriteBindingFragment(request, manifests, fragment, useProbe: true);
        return new BindingContext(probe, fragment);
    }

    private static void WriteBindingFragment(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests, string path, bool useProbe)
    {
        var directory = Path.GetDirectoryName(path)!;
        var sources = useProbe ? [Path.Combine(directory, "ctilde_bindings_probe.c")] : manifests.Select(manifest => manifest.AdapterSourcePath).ToArray();
        var builder = new StringBuilder("# Generated by the C~ ESP-IDF binding generator. Do not edit.\nset(CTILDE_BINDING_SOURCES\n");
        foreach (var source in sources.Order(StringComparer.Ordinal))
            builder.Append("    ${CMAKE_CURRENT_LIST_DIR}/").Append(Path.GetRelativePath(directory, source).Replace('\\', '/')).Append('\n');
        builder.Append(")\nset(CTILDE_BINDING_REQUIRES\n");
        foreach (var component in manifests.SelectMany(manifest => manifest.Components).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            builder.Append("    ").Append(component).Append('\n');
        builder.Append(")\n");
        WriteAtomically(path, builder.ToString());
    }

    private static CompileContext ReadCompileContext(BuildRequest request, string probeSource)
    {
        var compileCommands = Path.Combine(request.EspIdfBuildDirectory, "compile_commands.json");
        if (!File.Exists(compileCommands))
            throw new NativeBuildException($"ESP-IDF did not produce compile commands: {compileCommands}");
        using var document = JsonDocument.Parse(File.ReadAllText(compileCommands));
        JsonElement? selected = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var file = entry.GetProperty("file").GetString();
            if (file is not null && PathsEqual(file, probeSource))
            {
                selected = entry;
                break;
            }
        }
        selected ??= document.RootElement.EnumerateArray().FirstOrDefault(entry =>
        {
            var file = entry.GetProperty("file").GetString();
            return file is not null && IsInside(Path.GetFullPath(file), Path.GetDirectoryName(request.BindingGeneratedDirectory!)!);
        });
        if (selected is null || selected.Value.ValueKind == JsonValueKind.Undefined)
            throw new NativeBuildException("Could not find an ESP-IDF component compile command for binding validation.");
        var entryValue = selected.Value;
        var arguments = entryValue.TryGetProperty("arguments", out var array)
            ? array.EnumerateArray().Select(value => value.GetString()!).ToArray()
            : SplitCommandLine(entryValue.GetProperty("command").GetString()!);
        var directory = entryValue.GetProperty("directory").GetString()!;
        var includeDirectories = ExtractIncludeDirectories(arguments, directory);
        var projectDescription = Path.Combine(request.EspIdfBuildDirectory, "project_description.json");
        using var description = JsonDocument.Parse(File.ReadAllText(projectDescription));
        var target = description.RootElement.GetProperty("target").GetString()!;
        var version = description.RootElement.GetProperty("version").GetString() ?? "unknown";
        var config = description.RootElement.GetProperty("config_file").GetString()!;
        var componentNames = description.RootElement.GetProperty("build_components").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var componentPaths = description.RootElement.GetProperty("build_component_paths").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var components = componentNames.Zip(componentPaths)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.First) && !string.IsNullOrWhiteSpace(pair.Second))
            .ToDictionary(pair => pair.First, pair => Path.GetFullPath(pair.Second), StringComparer.Ordinal);
        return new CompileContext(directory, arguments, includeDirectories, target, version, config, components);
    }

    private static GeneratedBinding Generate(EspIdfBindingManifest manifest, string fingerprint, IReadOnlyDictionary<string, string[]> nativeOrders)
    {
        var declarations = new StringBuilder($"// <auto-generated binding-schema=\"1\" manifest-fingerprint=\"{manifest.ManifestFingerprint}\" fingerprint=\"{fingerprint}\">\nusing Esp.Idf;\nusing System.Runtime;\nnamespace {manifest.Namespace};\n\n");
        var adapter = new StringBuilder($"/* <auto-generated binding-schema=\"1\" manifest-fingerprint=\"{manifest.ManifestFingerprint}\" fingerprint=\"{fingerprint}\"> */\n#include <stdbool.h>\n#include <stddef.h>\n#include <stdint.h>\n#include <string.h>\n");
        var nativeTypes = manifest.Imports.SelectMany(import => import.OpaqueTypes)
            .ToDictionary(type => type.Name, type => type.Symbol, StringComparer.Ordinal);
        var delegates = manifest.Imports.SelectMany(import => import.Delegates)
            .ToDictionary(callback => callback.Name, StringComparer.Ordinal);
        foreach (var header in manifest.Imports.Select(import => import.Header).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            adapter.Append("#include <").Append(header).Append(">\n");
        adapter.Append('\n');

        foreach (var import in manifest.Imports)
        {
            foreach (var opaque in import.OpaqueTypes)
                declarations.Append("[NativeType(\"").Append(opaque.Symbol).Append("\", \"").Append(import.Header).Append("\")]\npublic opaque ").Append(opaque.Name).Append(";\n\n");
            foreach (var callback in import.Delegates)
                declarations.Append("public delegate ").Append(callback.ReturnType).Append(' ').Append(callback.Name).Append('(')
                    .Append(string.Join(", ", callback.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"))).Append(");\n\n");
        }

        foreach (var import in manifest.Imports)
        {
            declarations.Append("public static class ").Append(import.Container).Append("\n{\n");
            foreach (var function in import.Functions)
            {
                var symbol = AdapterSymbol(manifest, import, "function", function.Symbol, function.Name);
                AppendDocumentation(declarations, function.Symbol, 1);
                declarations.Append("    [Extern(\"").Append(symbol).Append("\")]\n");
                if (function.NoAlloc) declarations.Append("    [NoAlloc]\n");
                AppendReturnAttributes(declarations, function.ReturnOwnership, function.ReturnNullable, 1);
                declarations.Append("    public static ");
                if (function.Parameters.Any(parameter => RequiresUnsafe(parameter.Type)) || RequiresUnsafe(function.ReturnType)) declarations.Append("unsafe ");
                declarations.Append(function.ReturnType).Append(' ').Append(function.Name).Append('(')
                    .Append(string.Join(", ", function.Parameters.Select(CtParameter))).Append(");\n\n");
                adapter.Append(CType(function.ReturnType, nativeTypes)).Append(' ').Append(symbol).Append('(')
                    .Append(string.Join(", ", function.Parameters.SelectMany(parameter => CParameters(parameter, nativeTypes, delegates)))).Append(")\n{\n    ");
                if (function.ReturnType != "void") adapter.Append("return ");
                adapter.Append(function.Symbol).Append('(').Append(string.Join(", ", function.Parameters.SelectMany(CArgumentNames))).Append(");\n}\n\n");
            }
            foreach (var config in import.ConfigAdapters)
            {
                var symbol = AdapterSymbol(manifest, import, "config", config.Function, config.Name);
                AppendDocumentation(declarations, config.Function, 1);
                declarations.Append("    [Extern(\"").Append(symbol).Append("\")]\n");
                if (config.NoAlloc) declarations.Append("    [NoAlloc]\n");
                AppendReturnAttributes(declarations, config.ReturnOwnership, config.ReturnNullable, 1);
                declarations.Append("    public static ");
                if (config.Parameters.Any(parameter => RequiresUnsafe(parameter.Type)) || RequiresUnsafe(config.ReturnType)) declarations.Append("unsafe ");
                declarations.Append(config.ReturnType).Append(' ').Append(config.Name).Append('(')
                    .Append(string.Join(", ", config.Parameters.Select(CtParameter).Concat(config.Fields.Select(field => $"{field.Type} {field.Name}")))).Append(");\n\n");
                adapter.Append(CType(config.ReturnType, nativeTypes)).Append(' ').Append(symbol).Append('(')
                    .Append(string.Join(", ", config.Parameters.SelectMany(parameter => CParameters(parameter, nativeTypes, delegates))
                        .Concat(config.Fields.Select(field => $"{CType(field.Type, nativeTypes)} {field.Name}")))).Append(")\n{\n    ")
                    .Append(config.Struct).Append(" ct_config = ");
                if (config.Initializer is null)
                    adapter.Append("{ 0 };\n");
                else
                    adapter.Append(config.Initializer).Append("();\n");
                foreach (var value in config.Defaults)
                    adapter.Append("    ct_config.").Append(value.Field).Append(" = ").Append(value.Symbol).Append(";\n");
                foreach (var field in config.Fields.Where(field => field.Mapping == "value"))
                    adapter.Append("    ct_config.").Append(field.Field).Append(" = (__typeof__(ct_config.").Append(field.Field).Append("))").Append(field.Name).Append(";\n");
                foreach (var field in config.Fields.Where(field => field.Mapping == "fixedUtf8"))
                {
                    adapter.Append("    size_t ct_").Append(field.Name).Append("_length = strnlen(").Append(field.Name).Append(", ").Append(field.MaxBytes!.Value + 1).Append("u);\n")
                        .Append("    if (ct_").Append(field.Name).Append("_length > ").Append(field.MaxBytes.Value).Append("u) return ESP_ERR_INVALID_ARG;\n")
                        .Append("    memset(ct_config.").Append(field.Field).Append(", 0, sizeof(ct_config.").Append(field.Field).Append("));\n")
                        .Append("    memcpy(ct_config.").Append(field.Field).Append(", ").Append(field.Name).Append(", ct_").Append(field.Name).Append("_length);\n");
                }
                adapter.Append("    ");
                if (config.ReturnType != "void") adapter.Append("return ");
                adapter.Append(config.Function).Append('(').Append(string.Join(", ", OrderedAdapterArguments(config.Parameters, config.StructParameter, "&ct_config", nativeOrders[config.Function]))).Append(");\n}\n\n");
            }
            foreach (var output in import.OutputAdapters)
            {
                var symbol = AdapterSymbol(manifest, import, "output", output.Function, output.Name);
                AppendDocumentation(declarations, output.Function, 1);
                declarations.Append("    [Extern(\"").Append(symbol).Append("\")]\n");
                if (output.NoAlloc) declarations.Append("    [NoAlloc]\n");
                declarations.Append("    public static ");
                if (output.Parameters.Any(parameter => RequiresUnsafe(parameter.Type))) declarations.Append("unsafe ");
                declarations.Append(output.ReturnType).Append(' ').Append(output.Name).Append('(')
                    .Append(string.Join(", ", output.Parameters.Select(CtParameter).Concat(output.Fields.Select(field => $"out {field.Type} {field.Name}")))).Append(");\n\n");
                adapter.Append(CType(output.ReturnType, nativeTypes)).Append(' ').Append(symbol).Append('(')
                    .Append(string.Join(", ", output.Parameters.SelectMany(parameter => CParameters(parameter, nativeTypes, delegates))
                        .Concat(output.Fields.Select(field => $"{CType(field.Type, nativeTypes)}* {field.Name}")))).Append(")\n{\n    ")
                    .Append(output.Struct).Append(" ct_output = { 0 };\n");
                foreach (var field in output.Fields)
                    adapter.Append("    *").Append(field.Name).Append(" = (").Append(CType(field.Type, nativeTypes)).Append(")0;\n");
                var call = output.Function + "(" + string.Join(", ", OrderedAdapterArguments(output.Parameters, output.StructParameter, "&ct_output", nativeOrders[output.Function])) + ")";
                if (output.ReturnType == "void")
                    adapter.Append("    ").Append(call).Append(";\n");
                else
                    adapter.Append("    ").Append(CType(output.ReturnType, nativeTypes)).Append(" ct_result = ").Append(call).Append(";\n");
                if (output.ReturnType is "EspError" or "Esp.Idf.EspError")
                    adapter.Append("    if (ct_result == ESP_OK) {\n");
                foreach (var field in output.Fields)
                    adapter.Append(output.ReturnType is "EspError" or "Esp.Idf.EspError" ? "        " : "    ").Append('*').Append(field.Name).Append(" = (").Append(CType(field.Type, nativeTypes)).Append(")ct_output.").Append(field.Field).Append(";\n");
                if (output.ReturnType is "EspError" or "Esp.Idf.EspError")
                    adapter.Append("    }\n");
                if (output.ReturnType != "void")
                    adapter.Append("    return ct_result;\n");
                adapter.Append("}\n\n");
            }
            foreach (var constant in import.Constants)
            {
                var symbol = AdapterSymbol(manifest, import, "constant", constant.Symbol, constant.Name);
                if (constant.NoAlloc) declarations.Append("    [NoAlloc]\n");
                declarations.Append("    public static ").Append(constant.Type).Append(' ').Append(constant.Name).Append("\n    {\n");
                declarations.Append("        get { return __Get").Append(constant.Name).Append("(); }\n    }\n\n");
                declarations.Append("    [Extern(\"").Append(symbol).Append("\")]\n");
                if (constant.NoAlloc) declarations.Append("    [NoAlloc]\n");
                declarations.Append("    private static ").Append(constant.Type).Append(" __Get").Append(constant.Name).Append("();\n\n");
                adapter.Append(CType(constant.Type, nativeTypes)).Append(' ').Append(symbol).Append("(void)\n{\n    return (").Append(CType(constant.Type, nativeTypes)).Append(")(").Append(constant.Symbol).Append(");\n}\n\n");
            }
            declarations.Append("}\n\n");
        }
        var declarationText = declarations.ToString().TrimEnd() + "\n";
        return new GeneratedBinding(
            manifest,
            CTildeFormatter.Format(SourceText.From(declarationText, manifest.DeclarationsPath)),
            adapter.ToString().TrimEnd() + "\n");
    }

    private static void AppendReturnAttributes(StringBuilder declarations, string ownership, bool nullable, int indent)
    {
        var prefix = new string(' ', indent * 4);
        if (ownership == "owned") declarations.Append(prefix).Append("[ReturnsOwned]\n");
        else if (ownership == "borrowed") declarations.Append(prefix).Append("[ReturnsBorrowed]\n");
        if (nullable) declarations.Append(prefix).Append("[ReturnsNullable]\n");
    }

    private static IEnumerable<string> OrderedAdapterArguments(ImmutableArray<EspIdfBindingParameter> parameters, string structParameter, string structureArgument, IReadOnlyList<string> nativeOrder)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { [structParameter] = structureArgument };
        foreach (var parameter in parameters)
        {
            var nativeNames = parameter.NativeNames;
            var values = CArgumentNames(parameter).ToArray();
            if (nativeNames.Length != values.Length)
                throw new NativeBuildException($"Binding parameter '{parameter.Name}' does not map one-to-one to its native argument names.");
            for (var index = 0; index < nativeNames.Length; index++)
                arguments.Add(nativeNames[index], values[index]);
        }
        foreach (var nativeName in nativeOrder)
            if (arguments.TryGetValue(nativeName, out var argument)) yield return argument;
            else throw new NativeBuildException($"Binding adapter does not map native parameter '{nativeName}'.");
        if (arguments.Count != nativeOrder.Count)
            throw new NativeBuildException("Binding adapter maps native parameters that are absent from the selected declaration.");
    }

    private static string CtParameter(EspIdfBindingParameter parameter)
    {
        var attributes = new List<string>();
        if (parameter.Ownership != "borrowed") attributes.Add(parameter.Ownership switch { "consumes" => "[Consumes]", "retained" => "[Retained]", "creates" => "[Creates]", _ => string.Empty });
        if (parameter.Nullable) attributes.Add("[Nullable]");
        if (parameter.SynchronousCallback) attributes.Add("[SynchronousCallback]");
        return $"{string.Join(' ', attributes.Where(value => value.Length != 0))}{(attributes.Count != 0 ? " " : string.Empty)}{parameter.Type} {parameter.Name}";
    }

    private static IEnumerable<string> CParameters(EspIdfBindingParameter parameter, IReadOnlyDictionary<string, string> nativeTypes, IReadOnlyDictionary<string, EspIdfBindingDelegate> delegates)
    {
        if (TryBufferElement(parameter.Type, out var element, out var readOnly))
        {
            yield return $"{(readOnly ? "const " : string.Empty)}{CType(element!, nativeTypes)}* {parameter.Name}_data";
            yield return $"size_t {parameter.Name}_length";
        }
        else if (parameter.SynchronousCallback && delegates.TryGetValue(parameter.Type, out var callback))
        {
            var callbackParameters = callback.Parameters.Select(value => CType(value.Type, nativeTypes)).Append("void*");
            yield return $"{CType(callback.ReturnType, nativeTypes)} (*{parameter.Name})({string.Join(", ", callbackParameters)})";
            yield return $"void* {parameter.Name}_context";
        }
        else if (parameter.Type == "NativeUtf8String")
            yield return $"const char* {parameter.Name}";
        else
            yield return $"{CType(parameter.Type, nativeTypes)} {parameter.Name}";
    }

    private static IEnumerable<string> CArgumentNames(EspIdfBindingParameter parameter)
    {
        if (TryBufferElement(parameter.Type, out _, out _))
        {
            yield return $"{parameter.Name}_data";
            yield return $"{parameter.Name}_length";
        }
        else if (parameter.SynchronousCallback)
        {
            yield return parameter.Name;
            yield return $"{parameter.Name}_context";
        }
        else
            yield return parameter.Name;
    }

    private static string CType(string type, IReadOnlyDictionary<string, string>? nativeTypes = null)
    {
        var trimmed = type.Trim();
        if (trimmed.EndsWith('*')) return $"{CType(trimmed[..^1].Trim(), nativeTypes)}*";
        if (nativeTypes is not null && nativeTypes.TryGetValue(trimmed, out var nativeType)) return nativeType;
        return trimmed switch
        {
            "void" => "void",
            "bool" => "bool",
            "byte" => "uint8_t",
            "sbyte" => "int8_t",
            "char" => "uint8_t",
            "short" => "int16_t",
            "ushort" => "uint16_t",
            "int" => "int32_t",
            "uint" => "uint32_t",
            "long" => "int64_t",
            "ulong" => "uint64_t",
            "nint" => "intptr_t",
            "nuint" => "size_t",
            "float" => "float",
            "EspError" or "Esp.Idf.EspError" => "esp_err_t",
            "NativeUtf8String" => "const char*",
            _ => throw new NativeBuildException($"ESP-IDF binding type '{type}' is not supported by schema version 1."),
        };
    }

    private static bool TryBufferElement(string type, out string? element, out bool readOnly)
    {
        readOnly = type.StartsWith("ReadOnlyNativeBuffer<", StringComparison.Ordinal);
        var prefix = readOnly ? "ReadOnlyNativeBuffer<" : "NativeBuffer<";
        if (!type.StartsWith(prefix, StringComparison.Ordinal) || !type.EndsWith('>')) { element = null; return false; }
        element = type[prefix.Length..^1].Trim();
        return true;
    }

    private static bool RequiresUnsafe(string type) =>
        type.Contains('*', StringComparison.Ordinal) ||
        type.StartsWith("NativeBuffer<", StringComparison.Ordinal) ||
        type.StartsWith("ReadOnlyNativeBuffer<", StringComparison.Ordinal);

    private static async Task ValidateAdapterAsync(string clang, CompileContext compile, GeneratedBinding output, BuildRequest request, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(request.EspIdfBuildDirectory, ".ctilde", "bindings");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"validate-{SHA256.HashData(Encoding.UTF8.GetBytes(output.Adapter)).AsSpan(0, 6).ToArray().ToHex()}.c");
        WriteAtomically(path, output.Adapter);
        var arguments = FilterCompileArguments(compile.Arguments, path, compile.Target);
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(clang, arguments, compile.Directory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0)
            throw new NativeBuildException($"Espressif Clang rejected generated binding adapter '{path}':\n{result.StandardError.Trim()}");

        foreach (var declaration in output.Manifest.Imports.SelectMany(import => import.Functions)
                     .Where(function => !function.Callable)
                     .Select(function => (Symbol: function.Symbol, NativeNames: function.Parameters.SelectMany(parameter => parameter.NativeNames).ToArray()))
                     .DistinctBy(declaration => declaration.Symbol))
            await ValidateAstDeclarationAsync(clang, arguments, compile.Directory, declaration.Symbol, declaration.NativeNames, cancellationToken);
        foreach (var declaration in output.Manifest.Imports.SelectMany(import => import.ConfigAdapters)
                     .Select(config => (Symbol: config.Function, NativeNames: config.Parameters.SelectMany(parameter => parameter.NativeNames).Append(config.StructParameter).ToArray()))
                     .Concat(output.Manifest.Imports.SelectMany(import => import.OutputAdapters).Select(adapter => (Symbol: adapter.Function, NativeNames: adapter.Parameters.SelectMany(parameter => parameter.NativeNames).Append(adapter.StructParameter).ToArray())))
                     .DistinctBy(declaration => declaration.Symbol))
            await ValidateAstMappedDeclarationAsync(clang, arguments, compile.Directory, declaration.Symbol, declaration.NativeNames, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, string[]>> InspectAdapterOrdersAsync(string clang, CompileContext compile, IReadOnlyList<EspIdfBindingManifest> manifests, BuildRequest request, CancellationToken cancellationToken)
    {
        var adapters = manifests.SelectMany(manifest => manifest.Imports)
            .SelectMany(import => import.ConfigAdapters.Select(adapter => adapter.Function).Concat(import.OutputAdapters.Select(adapter => adapter.Function)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (adapters.Length == 0)
            return new Dictionary<string, string[]>(StringComparer.Ordinal);

        var directory = Path.Combine(request.EspIdfBuildDirectory, ".ctilde", "bindings");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "inspect-adapters.c");
        var headers = manifests.SelectMany(manifest => manifest.Imports).Select(import => import.Header).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        WriteAtomically(source, string.Join(string.Empty, headers.Select(header => $"#include <{header}>\n")));
        var arguments = FilterCompileArguments(compile.Arguments, source, compile.Target);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var symbol in adapters)
            result.Add(symbol, await ReadAstParameterNamesAsync(clang, arguments, compile.Directory, symbol, cancellationToken));
        return result;
    }

    private static async Task ValidateAstDeclarationAsync(string clang, IReadOnlyList<string> validationArguments, string directory, string symbol, IReadOnlyList<string> nativeNames, CancellationToken cancellationToken)
    {
        var actualNames = await ReadAstParameterNamesAsync(clang, validationArguments, directory, symbol, cancellationToken);
        if (!actualNames.SequenceEqual(nativeNames, StringComparer.Ordinal))
            throw new NativeBuildException($"Selected ESP-IDF function '{symbol}' has native parameters ({string.Join(", ", actualNames)}), but the manifest maps ({string.Join(", ", nativeNames)}).");
    }

    private static async Task ValidateAstMappedDeclarationAsync(string clang, IReadOnlyList<string> validationArguments, string directory, string symbol, IReadOnlyList<string> nativeNames, CancellationToken cancellationToken)
    {
        var actualNames = await ReadAstParameterNamesAsync(clang, validationArguments, directory, symbol, cancellationToken);
        if (actualNames.Length != nativeNames.Count || !actualNames.Order(StringComparer.Ordinal).SequenceEqual(nativeNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new NativeBuildException($"Selected ESP-IDF function '{symbol}' has native parameters ({string.Join(", ", actualNames)}), but the manifest maps ({string.Join(", ", nativeNames)}).");
    }

    private static async Task<string[]> ReadAstParameterNamesAsync(string clang, IReadOnlyList<string> validationArguments, string directory, string symbol, CancellationToken cancellationToken)
    {
        var arguments = validationArguments.ToList();
        var syntaxIndex = arguments.IndexOf("-fsyntax-only");
        if (syntaxIndex < 0) syntaxIndex = arguments.Count - 1;
        arguments.InsertRange(syntaxIndex, ["-Xclang", "-ast-dump=json", "-Xclang", $"-ast-dump-filter={symbol}"]);
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(clang, arguments, directory, ForwardOutput: false), cancellationToken);
        if (result.ExitCode != 0)
            throw new NativeBuildException($"Espressif Clang could not inspect declaration '{symbol}':\n{result.StandardError.Trim()}");
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(result.StandardOutput), new JsonReaderOptions { AllowMultipleValues = true });
            string[]? actualNames = null;
            while (reader.Read())
            {
                using var document = JsonDocument.ParseValue(ref reader);
                var function = FindAstFunction(document.RootElement, symbol);
                if (function is null) continue;
                actualNames = function.Value.TryGetProperty("inner", out var inner)
                    ? inner.EnumerateArray()
                        .Where(child => child.TryGetProperty("kind", out var kind) && kind.GetString() == "ParmVarDecl")
                        .Select(child => child.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty)
                        .ToArray()
                    : [];
                break;
            }
            if (actualNames is null)
                throw new NativeBuildException($"Selected ESP-IDF declaration '{symbol}' is not a function in the configured public headers.");
            return actualNames;
        }
        catch (JsonException exception)
        {
            throw new NativeBuildException($"Espressif Clang returned invalid AST JSON for '{symbol}': {exception.Message}");
        }
    }

    private static JsonElement? FindAstFunction(JsonElement element, string symbol)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("kind", out var kind) && kind.GetString() == "FunctionDecl" &&
                element.TryGetProperty("name", out var name) && name.GetString() == symbol)
                return element;
            foreach (var property in element.EnumerateObject())
                if (FindAstFunction(property.Value, symbol) is { } found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                if (FindAstFunction(child, symbol) is { } found) return found;
        return null;
    }

    private static IReadOnlyList<string> FilterCompileArguments(IReadOnlyList<string> source, string validationSource, string target)
    {
        var result = new List<string> { target == "esp32" ? "--target=xtensa-esp-unknown-elf" : "--target=riscv32-esp-unknown-elf" };
        if (source.Count > 0)
        {
            var compilerRoot = Path.GetDirectoryName(Path.GetDirectoryName(source[0]));
            var picolibcInclude = compilerRoot is null ? null : Path.Combine(compilerRoot, "picolibc", "include");
            if (picolibcInclude is not null && Directory.Exists(picolibcInclude))
            {
                result.Add("-nostdlibinc");
                result.Add("-isystem");
                result.Add(picolibcInclude);
            }
        }
        for (var index = 1; index < source.Count; index++)
        {
            var argument = source[index];
            if (argument is "-I" or "-isystem")
            {
                if (index + 1 < source.Count) { result.Add("-isystem"); result.Add(source[++index]); }
                continue;
            }
            if (argument == "-include")
            {
                if (index + 1 < source.Count) { result.Add(argument); result.Add(source[++index]); }
                continue;
            }
            if (argument.StartsWith("-I", StringComparison.Ordinal))
            {
                result.Add("-isystem");
                result.Add(argument[2..]);
                continue;
            }
            if (argument.StartsWith("-D", StringComparison.Ordinal) ||
                argument.StartsWith("-std=", StringComparison.Ordinal))
                result.Add(argument);
        }
        result.Add("-Wall");
        result.Add("-Wextra");
        result.Add("-Werror");
        result.Add("-fsyntax-only");
        result.Add(validationSource);
        return result;
    }

    private static async Task<string> ResolveEspClangAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<string?> { request.EspClangPath, Environment.GetEnvironmentVariable("CTILDE_ESP_CLANG") };
        foreach (var root in EspIdfEnvironment.ToolsRoots(request.EspIdfPath))
            if (Directory.Exists(Path.Combine(root, "esp-clang")))
                candidates.AddRange(Directory.EnumerateFiles(Path.Combine(root, "esp-clang"), OperatingSystem.IsWindows() ? "clang.exe" : "clang", SearchOption.AllDirectories).OrderDescending());
        candidates.Add(NativeToolDiscovery.FindOnPath(OperatingSystem.IsWindows() ? "clang.exe" : "clang"));
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(candidate);
            if (!File.Exists(path)) continue;
            var version = await NativeProcessRunner.RunAsync(new NativeProcessRequest(path, ["--version"], request.RootDirectory, ForwardOutput: false), cancellationToken);
            if (version.ExitCode == 0 &&
                (version.StandardOutput.Contains("Espressif", StringComparison.OrdinalIgnoreCase) ||
                 version.StandardOutput.Contains("esp-", StringComparison.OrdinalIgnoreCase)))
                return path;
        }
        throw new NativeBuildException("Espressif Clang is required for ESP-IDF binding generation. Pass --esp-clang or set CTILDE_ESP_CLANG.");
    }

    private static string ComputeFingerprint(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests, CompileContext compile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
        Add("ctilde-esp-idf-bindings-v1\n"); Add(compile.Target); Add(compile.IdfVersion);
        foreach (var manifest in manifests) Add(manifest.CanonicalText());
        if (File.Exists(compile.ConfigPath)) hash.AppendData(File.ReadAllBytes(compile.ConfigPath));
        foreach (var header in ResolveImportedHeaders(manifests, compile))
            hash.AppendData(File.ReadAllBytes(header));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string[] ResolveImportedHeaders(IReadOnlyList<EspIdfBindingManifest> manifests, CompileContext compile) =>
        manifests.SelectMany(manifest => manifest.Imports)
            .OrderBy(import => import.Header, StringComparer.Ordinal)
            .Select(import =>
            {
                if (!compile.ComponentDirectories.TryGetValue(import.Component, out var componentDirectory))
                    throw new NativeBuildException($"ESP-IDF component '{import.Component}' was not present in the configured project.");
                return ResolveHeader(import.Header, compile.IncludeDirectories, componentDirectory);
            })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

    private static string ResolveHeader(string header, IReadOnlyList<string> includeDirectories, string componentDirectory)
    {
        var matches = includeDirectories.Select(directory => Path.Combine(directory, header.Replace('/', Path.DirectorySeparatorChar))).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length != 1)
            throw new NativeBuildException(matches.Length == 0 ? $"ESP-IDF public header '{header}' was not found in the selected component context." : $"ESP-IDF public header '{header}' is ambiguous in the selected component context.");
        if (!IsInside(matches[0], componentDirectory))
            throw new NativeBuildException($"ESP-IDF public header '{header}' does not belong to the selected component '{Path.GetFileName(componentDirectory)}'.");
        return matches[0];
    }

    private static IReadOnlyList<string> ExtractIncludeDirectories(IReadOnlyList<string> arguments, string directory)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            string? value = null;
            if (arguments[index] == "-I" && index + 1 < arguments.Count) value = arguments[++index];
            else if (arguments[index].StartsWith("-I", StringComparison.Ordinal) && arguments[index].Length > 2) value = arguments[index][2..];
            if (value is not null) result.Add(Path.GetFullPath(value.Trim('"'), directory));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SplitCommandLine(string command)
    {
        var result = new List<string>(); var current = new StringBuilder(); var quoted = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\\' && index + 1 < command.Length && command[index + 1] == '"')
            {
                current.Append('"');
                index++;
                continue;
            }
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted) { if (current.Length != 0) { result.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(character);
        }
        if (current.Length != 0) result.Add(current.ToString());
        return [.. result];
    }

    private static void ValidateOutputCollisions(BuildRequest request, IReadOnlyList<EspIdfBindingManifest> manifests)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var outputs = manifests.SelectMany(manifest => new[] { manifest.DeclarationsPath, manifest.AdapterSourcePath }).ToArray();
        if (outputs.Distinct(comparer).Count() != outputs.Length)
            throw new NativeBuildException("ESP-IDF binding outputs must be distinct.");
        foreach (var output in outputs)
            if (!IsInside(output, request.RootDirectory)) throw new NativeBuildException($"ESP-IDF binding output '{output}' leaves the project directory.");
    }

    private static string AdapterSymbol(EspIdfBindingManifest manifest, EspIdfBindingImport import, string kind, string native, string managed)
    {
        var identity = $"{manifest.CanonicalText()}|{import.Component}|{import.Header}|{import.Container}|{kind}|{native}|{managed}";
        return "ct_idf_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 12)).ToLowerInvariant();
    }

    private static void AppendDocumentation(StringBuilder builder, string symbol, int indent) => builder.Append(' ', indent * 4).Append("/// <summary>Generated binding for ").Append(symbol).Append(".</summary>\n");
    private static bool IsInside(string path, string root) { var relative = Path.GetRelativePath(root, path); return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
    private static bool PathsEqual(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void WriteAtomically(string path, string contents) => AtomicFile.WriteTextIfChanged(path, contents);

    private sealed record BindingContext(string ProbeSource, string FragmentPath);
    private sealed record CompileContext(string Directory, IReadOnlyList<string> Arguments, IReadOnlyList<string> IncludeDirectories, string Target, string IdfVersion, string ConfigPath, IReadOnlyDictionary<string, string> ComponentDirectories);
    private sealed record GeneratedBinding(EspIdfBindingManifest Manifest, string Declarations, string Adapter);
    private sealed record BindingCacheFile(string Path, string Hash);
    private sealed record BindingCacheState(int SchemaVersion, int ManifestCount, string ManifestSignature, string CompileSignature, string ConfigurationSignature, string ClangPath,
        long ClangLength, long ClangWriteTicks, string ClangVersion, BindingCacheFile[] Inputs, BindingCacheFile[] Outputs);
}

file static class BindingHexExtensions
{
    public static string ToHex(this byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
