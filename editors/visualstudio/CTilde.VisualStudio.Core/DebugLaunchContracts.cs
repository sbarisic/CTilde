using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CTilde.VisualStudio.Core;

public enum CTildeDebugMemoryMode { Off, Objects, Guarded }

public sealed record CTildeDebugPreparation(
    string Target,
    string Toolchain,
    string DescriptorPath,
    IReadOnlyList<string> Arguments)
{
    public string Compiler => Toolchain;
}

public static class DebugLaunchContracts
{
    public const string EngineGuid = "a8d3fece-e5ae-4bb9-9483-23b1951fd115";
    public const string ExceptionCategoryGuid = "0cf710b9-7db1-473b-8ceb-1f981aba01e2";

    public static CTildeDebugPreparation CreatePreparation(
        string compilerDll,
        string manifestPath,
        string? compilerOverride,
        string? environmentCompiler,
        CTildeDebugMemoryMode memoryMode,
        string? espIdfPath = null,
        string? espClangPath = null)
    {
        var manifest = LoadManifest(manifestPath);
        var kind = String(manifest, "kind") ?? "application";
        var target = String(manifest, "target") ?? "hosted";
        if (kind.Equals("standard-library", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The C~ standard-library project cannot be launched.");
        var hosted = target.Equals("hosted", StringComparison.OrdinalIgnoreCase);
        var qemu = target.Equals("esp32_qemu", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("esp32c3_qemu", StringComparison.OrdinalIgnoreCase);
        if (!hosted && !qemu)
            throw new InvalidOperationException($"C~ Visual Studio debugging supports hosted, esp32_qemu, and esp32c3_qemu projects only; target '{target}' is not supported.");
        if (hosted && !manifest.TryGetProperty("run", out _) && !RunSupport.IsSupported(kind, target, false))
            throw new InvalidOperationException("This C~ project manifest does not support Run.");

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifestIdentity = ManifestIdentity(manifestPath);
        var descriptor = Path.Combine(manifestDirectory, ".ctilde", "visualstudio", "debug-targets", manifestIdentity + ".json");
        var arguments = new List<string>
        {
            Path.GetFullPath(compilerDll),
            "--project", Path.GetFullPath(manifestPath),
            "--prepare-debug", "launch",
            "--debug-target", descriptor,
            "--debug-memory", memoryMode.ToString().ToLowerInvariant(),
        };
        string toolchain;
        if (hosted)
        {
            var manifestCompiler = manifest.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object
                ? String(build, "compiler") ?? "auto"
                : "auto";
            var compiler = ResolveCompiler(compilerOverride, manifestCompiler, environmentCompiler);
            ValidateGdbCompiler(compiler);
            arguments.Add("--compiler");
            arguments.Add(compiler);
            toolchain = compiler;
        }
        else
        {
            AddOptionalPath(arguments, "--idf-path", espIdfPath);
            AddOptionalPath(arguments, "--esp-clang", espClangPath);
            toolchain = target;
        }
        return new CTildeDebugPreparation(target.ToLowerInvariant(), toolchain, descriptor, arguments);
    }

    private static void AddOptionalPath(List<string> arguments, string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        arguments.Add(option);
        arguments.Add(Path.GetFullPath(value!.Trim()));
    }

    public static string ManifestIdentity(string manifestPath)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(manifestPath)));
        return string.Concat(hash.Take(8).Select(value => value.ToString("x2")));
    }

    public static string ResolveCompiler(string? visualStudioOverride, string manifestCompiler, string? environmentCompiler)
    {
        if (!string.IsNullOrWhiteSpace(visualStudioOverride))
            return visualStudioOverride!.Trim();
        if (!string.IsNullOrWhiteSpace(manifestCompiler) && !manifestCompiler.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return manifestCompiler.Trim();
        if (!string.IsNullOrWhiteSpace(environmentCompiler))
            return environmentCompiler!.Trim();
        throw new InvalidOperationException(
            "C~ debugging requires an explicit GCC, Clang, or WSL-GCC compiler. Configure Debug compiler under Tools > Options > C~, set build.compiler in ctilde.json, or set CTILDE_CC.");
    }

    public static void ValidateGdbCompiler(string compiler)
    {
        var fileName = Path.GetFileNameWithoutExtension(compiler).ToLowerInvariant();
        var normalized = compiler.Replace('\\', '/').ToLowerInvariant();
        if (compiler.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            fileName is "cl" or "clang-cl" || normalized.EndsWith("/cl", StringComparison.Ordinal) || normalized.EndsWith("/clang-cl", StringComparison.Ordinal))
            throw new InvalidOperationException("MSVC and clang-cl do not provide the GDB backend required by C~ debugging. Select GCC, Clang, or WSL-GCC.");
        if (compiler.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            if (compiler.Length == 4)
                throw new InvalidOperationException("A WSL compiler command is required after 'wsl:'.");
            return;
        }
        if (compiler.Equals("gcc", StringComparison.OrdinalIgnoreCase) || compiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("gcc", StringComparison.Ordinal) || fileName.Contains("clang", StringComparison.Ordinal))
            return;
        throw new InvalidOperationException($"Debug compiler '{compiler}' is not recognized as GCC, Clang, or WSL-GCC.");
    }

    private static JsonElement LoadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The C~ manifest is invalid: {exception.Message}", exception);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
