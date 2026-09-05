using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

// Each build owns one context. Dependency resolution runs before every lookup,
// including hits, so a new header earlier on an include path invalidates the key.
internal sealed class NativeObjectCache(string command, IReadOnlyList<string> prefix,
    IReadOnlyDictionary<string, string>? environment, bool msvc, string? wslCompiler, string workingDirectory)
{
    private string? compilerIdentity;

    public async Task<Entry> PrepareAsync(BuildRequest request, string source, IReadOnlyList<string> flags,
        string extension, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(request.ExecutablePath!)!, ".ctilde-cache");
        Directory.CreateDirectory(directory);
        var scanFile = Path.Combine(directory, "scan-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            compilerIdentity ??= await GetCompilerIdentityAsync(cancellationToken);
            var toolSource = await ToolPathAsync(source, cancellationToken);
            IReadOnlyList<string> dependencies;
            if (Path.GetExtension(source) == ".s")
                dependencies = [toolSource];
            else if (msvc)
            {
                var result = await RunAsync(flags.Concat(["/Zs", "/sourceDependencies", scanFile, source]), cancellationToken);
                if (result.ExitCode != 0 || !File.Exists(scanFile)) return Uncached(directory, extension);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(scanFile, cancellationToken));
                dependencies = document.RootElement.GetProperty("Data").GetProperty("Includes")
                    .EnumerateArray().Select(item => item.GetString()!).Append(source).ToArray();
            }
            else
            {
                var scanFlags = flags.Where(flag => flag is not ("-fstack-usage" or "-fcallgraph-info=su")).ToArray();
                var result = await RunAsync(scanFlags.Concat(["-M", "-MT", "ctilde_object", toolSource]), cancellationToken);
                if (result.ExitCode != 0 && result.StandardError.Contains("gnu23", StringComparison.Ordinal))
                    result = await RunAsync(scanFlags.Select(flag => flag == "-std=gnu23" ? "-std=gnu2x" : flag)
                        .Concat(["-M", "-MT", "ctilde_object", toolSource]), cancellationToken);
                if (result.ExitCode != 0) return Uncached(directory, extension);
                dependencies = ParseDependencies(result.StandardOutput).Append(toolSource).ToArray();
                if (dependencies.Count <= 1) return Uncached(directory, extension);
            }
            var identity = new StringBuilder("native-object-v2\n")
                .Append(CompilerContract.DraftVersion).Append('\n').Append(compilerIdentity).Append('\n')
                .Append(Path.GetFullPath(source)).Append('\n').Append(Path.GetFullPath(workingDirectory)).Append('\n')
                .Append(request.Target).Append(':').Append(request.Architecture).Append(':').Append(request.Configuration).Append('\n')
                .Append(NativeOptimizationSettings.Describe(request)).Append('\n').AppendJoin('\n', flags).Append('\n');
            foreach (var name in new[] { "INCLUDE", "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "SDKROOT", "PATH", "LIB", "LIBPATH", "WSLENV", "CL", "_CL_", "GCC_EXEC_PREFIX", "COMPILER_PATH" })
                identity.Append(name).Append('=').Append(environment?.GetValueOrDefault(name) ?? System.Environment.GetEnvironmentVariable(name)).Append('\n');
            var paths = dependencies.Distinct(msvc ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (wslCompiler is not null)
            {
                var hashes = await NativeProcessRunner.RunAsync(new NativeProcessRequest(command,
                    new[] { "--exec", "sha256sum", "--" }.Concat(paths).ToArray(), workingDirectory, ForwardOutput: false), cancellationToken);
                if (hashes.ExitCode != 0) return Uncached(directory, extension);
                identity.Append(hashes.StandardOutput);
            }
            else
                foreach (var dependency in paths)
                {
                    var path = Path.GetFullPath(dependency, workingDirectory);
                    identity.Append(path).Append(':').Append(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)))).Append('\n');
                }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
            return new Entry(Path.Combine(directory, key + extension));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or NativeBuildException)
        {
            if (request.Trace) Console.Error.WriteLine("trace: native dependency scan unavailable: " + exception.Message);
            return Uncached(directory, extension);
        }
        finally { if (File.Exists(scanFile)) File.Delete(scanFile); }
    }

    private static Entry Uncached(string directory, string extension) => new(Path.Combine(directory, "uncached-" + Guid.NewGuid().ToString("N") + extension));

    private Task<NativeProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken token) =>
        NativeProcessRunner.RunAsync(new NativeProcessRequest(command, prefix.Concat(arguments).ToArray(), workingDirectory, environment, false), token);

    private async Task<string> ToolPathAsync(string path, CancellationToken token)
    {
        if (wslCompiler is null) return Path.GetFullPath(path);
        var result = await NativeProcessRunner.RunAsync(new NativeProcessRequest(command,
            ["--exec", "wslpath", "-a", "-u", path], workingDirectory, ForwardOutput: false), token);
        if (result.ExitCode != 0) throw new NativeBuildException("Could not translate dependency source path.");
        return result.StandardOutput.Trim();
    }

    private async Task<string> GetCompilerIdentityAsync(CancellationToken token)
    {
        var version = await RunAsync(msvc ? ["/Bv"] : ["--version"], token);
        var identity = command + "\n" + string.Join('\n', prefix) + "\n" + version.StandardOutput + version.StandardError;
        if (wslCompiler is not null)
        {
            var resolved = await NativeProcessRunner.RunAsync(new NativeProcessRequest(command,
                ["--exec", "which", wslCompiler], workingDirectory, ForwardOutput: false), token);
            if (resolved.ExitCode != 0) throw new NativeBuildException("Could not identify WSL compiler.");
            var hash = await NativeProcessRunner.RunAsync(new NativeProcessRequest(command,
                ["--exec", "sha256sum", "--", resolved.StandardOutput.Trim()], workingDirectory, ForwardOutput: false), token);
            if (hash.ExitCode != 0) throw new NativeBuildException("Could not hash WSL compiler.");
            return identity + hash.StandardOutput;
        }
        return identity + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(command, token)));
    }

    internal static IReadOnlyList<string> ParseDependencies(string text)
    {
        text = text.Replace("\\\r\n", "").Replace("\\\n", "");
        var colon = text.IndexOf(':');
        if (colon < 0) throw new InvalidOperationException("Missing dependency target.");
        var paths = new List<string>();
        var value = new StringBuilder();
        for (var index = colon + 1; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < text.Length && (char.IsWhiteSpace(text[index + 1]) || text[index + 1] is '\\' or '#' or ':'))
                value.Append(text[++index]);
            else if (character == '$' && index + 1 < text.Length && text[index + 1] == '$')
            { value.Append('$'); index++; }
            else if (char.IsWhiteSpace(character))
            { if (value.Length != 0) { paths.Add(value.ToString()); value.Clear(); } }
            else value.Append(character);
        }
        if (value.Length != 0) paths.Add(value.ToString());
        return paths;
    }

    internal sealed class Entry(string objectPath) : IDisposable
    {
        public string ObjectPath { get; } = objectPath;
        public string CompilePath { get; } = Path.Combine(Path.GetDirectoryName(objectPath)!, "pending-" + Guid.NewGuid().ToString("N") + Path.GetExtension(objectPath));
        public void Publish()
        {
            foreach (var extension in new[] { ".su", ".ci", ".pdb" })
            {
                var source = Path.ChangeExtension(CompilePath, extension);
                if (File.Exists(source)) File.Move(source, Path.ChangeExtension(ObjectPath, extension), overwrite: true);
            }
            File.Move(CompilePath, ObjectPath, overwrite: true);
        }
        public void Dispose()
        {
            foreach (var path in new[] { CompilePath, Path.ChangeExtension(CompilePath, ".su"), Path.ChangeExtension(CompilePath, ".ci"), Path.ChangeExtension(CompilePath, ".pdb") })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
