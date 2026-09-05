using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public sealed record RepositoryModuleResolution(
    ImmutableArray<string> SourceFiles,
    ImmutableDictionary<string, SourceOwnerIdentity> SourceOwners);

/// <summary>Restores and resolves exact repository-backed project modules.</summary>
public static class RepositoryModules
{
    public const string LockFileName = "ctilde.lock.json";
    public const string LocalFileName = "ctilde.local.json";
    private const string VendorMetadataFileName = ".ctilde-module.json";
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static RepositoryModuleResolution LoadLocked(string projectRoot, IReadOnlyList<RepositoryModuleReference> modules)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        if (modules.Count == 0)
            return new([], ImmutableDictionary.Create<string, SourceOwnerIdentity>(PathComparer));

        var lockFile = ReadLock(projectRoot, required: true)!;
        ValidateLock(modules, lockFile, projectRoot);
        var replacements = ReadReplacements(projectRoot);
        var sources = ImmutableArray.CreateBuilder<string>();
        var owners = ImmutableDictionary.CreateBuilder<string, SourceOwnerIdentity>(PathComparer);

        foreach (var module in modules)
        {
            var locked = lockFile.Modules.Single(candidate => candidate.Path == module.ModulePath);
            string contentRoot;
            string ownerRevision;
            if (replacements.TryGetValue(module.ModulePath, out var replacement))
            {
                contentRoot = ResolveInsideOrOutsideProject(projectRoot, replacement);
                if (!Directory.Exists(contentRoot))
                    throw new CTildeProjectException($"Local replacement for module '{module.ModulePath}' does not exist: '{contentRoot}'.");
                ownerRevision = "local:" + ComputeContentHash(contentRoot);
            }
            else
            {
                var vendorRoot = VendorRoot(projectRoot, module);
                if (Directory.Exists(vendorRoot))
                {
                    VerifyVendor(vendorRoot, locked);
                    contentRoot = vendorRoot;
                }
                else
                {
                    contentRoot = CacheRoot(projectRoot, module.ModulePath);
                    VerifyCache(contentRoot, locked);
                }
                ownerRevision = locked.Revision;
            }

            var moduleSources = EnumerateSources(contentRoot, module.Sources);
            if (moduleSources.IsEmpty)
                throw new CTildeProjectException($"Repository module '{module.ModulePath}' did not match any .ct source files in '{contentRoot}'.");
            var owner = new SourceOwnerIdentity(module.ModulePath, contentRoot, contentRoot, false, ownerRevision);
            foreach (var source in moduleSources)
            {
                sources.Add(source);
                owners.Add(source, owner);
            }
        }
        return new(sources.ToImmutable(), owners.ToImmutable());
    }

    /// <summary>Materializes locked modules. Update additionally resolves selectors to new exact revisions.</summary>
    public static void Restore(string projectRoot, IReadOnlyList<RepositoryModuleReference> modules, bool update) =>
        RestoreAsync(projectRoot, modules, update).GetAwaiter().GetResult();

    public static async Task RestoreAsync(string projectRoot, IReadOnlyList<RepositoryModuleReference> modules, bool update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        projectRoot = Path.GetFullPath(projectRoot);
        var existing = ReadLock(projectRoot, required: false);
        if (modules.Count == 0)
        {
            WriteLock(projectRoot, new ModuleLockDocument(1, []));
            return;
        }

        var resolved = new List<LockedModuleDocument>();
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = existing?.Modules.FirstOrDefault(candidate => candidate.Path == module.ModulePath &&
                candidate.Repository == module.Repository && candidate.Selector == module.Selector);
            var refresh = update || module.UpdatePolicy == RepositoryModuleUpdatePolicy.Refresh || old is null;
            var cacheRoot = CacheRoot(projectRoot, module.ModulePath);
            await EnsureCheckoutAsync(cacheRoot, module.Repository, cancellationToken).ConfigureAwait(false);
            if (refresh)
                await RunGitAsync(cacheRoot, cancellationToken, "fetch", "--tags", "--prune", "origin").ConfigureAwait(false);
            var revision = refresh ? await ResolveSelectorAsync(cacheRoot, module.Selector, cancellationToken).ConfigureAwait(false) : old!.Revision;
            await RunGitAsync(cacheRoot, cancellationToken, "checkout", "--detach", "--force", revision).ConfigureAwait(false);
            await RunGitAsync(cacheRoot, cancellationToken, "clean", "-fdx").ConfigureAwait(false);
            var actualRevision = (await RunGitAsync(cacheRoot, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            if (!IsRevision(actualRevision))
                throw new CTildeProjectException($"Git returned invalid revision '{actualRevision}' for module '{module.ModulePath}'.");
            resolved.Add(new LockedModuleDocument(module.ModulePath, module.Repository, module.Selector, actualRevision, ComputeContentHash(cacheRoot)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteLock(projectRoot, new ModuleLockDocument(1, resolved.OrderBy(module => module.Path, StringComparer.Ordinal).ToArray()));
    }

    /// <summary>Copies exact locked checkouts into verified project vendor directories.</summary>
    public static void Vendor(string projectRoot, IReadOnlyList<RepositoryModuleReference> modules)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var lockFile = ReadLock(projectRoot, required: true)!;
        ValidateLock(modules, lockFile, projectRoot);
        foreach (var module in modules)
        {
            var locked = lockFile.Modules.Single(candidate => candidate.Path == module.ModulePath);
            var cacheRoot = CacheRoot(projectRoot, module.ModulePath);
            VerifyCache(cacheRoot, locked);
            var vendorRoot = VendorRoot(projectRoot, module);
            if (Directory.Exists(vendorRoot))
            {
                var metadataPath = Path.Combine(vendorRoot, VendorMetadataFileName);
                if (!File.Exists(metadataPath))
                    throw new CTildeProjectException($"Refusing to replace unverified vendor directory '{vendorRoot}'.");
                Directory.Delete(vendorRoot, recursive: true);
            }
            CopyTree(cacheRoot, vendorRoot);
            var metadata = new VendorMetadataDocument(module.ModulePath, locked.Revision, locked.ContentHash);
            File.WriteAllText(Path.Combine(vendorRoot, VendorMetadataFileName), JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private static async Task EnsureCheckoutAsync(string cacheRoot, string repository, CancellationToken cancellationToken)
    {
        if (Directory.Exists(cacheRoot))
        {
            if (!Directory.Exists(Path.Combine(cacheRoot, ".git")))
                throw new CTildeProjectException($"Module cache path '{cacheRoot}' exists but is not a Git checkout.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
        await RunGitAsync(Path.GetDirectoryName(cacheRoot)!, cancellationToken, "clone", "--no-checkout", "--", repository, cacheRoot).ConfigureAwait(false);
    }

    private static async Task<string> ResolveSelectorAsync(string checkout, string selector, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { selector, "refs/tags/" + selector, "refs/remotes/origin/" + selector, "origin/" + selector })
        {
            var result = await TryRunGitAsync(checkout, cancellationToken, "rev-parse", "--verify", candidate + "^{commit}").ConfigureAwait(false);
            if (result.ExitCode == 0 && IsRevision(result.Output.Trim()))
                return result.Output.Trim();
        }
        throw new CTildeProjectException($"Could not resolve module selector '{selector}' in '{checkout}' as a commit, tag, or branch.");
    }

    private static void VerifyCache(string cacheRoot, LockedModuleDocument locked)
    {
        if (!Directory.Exists(Path.Combine(cacheRoot, ".git")))
            throw new CTildeProjectException($"Exact module '{locked.Path}' is not restored. Run 'ctilde restore --project ctilde.json'.");
        var revision = RunGit(cacheRoot, "rev-parse", "HEAD").Trim();
        if (!revision.Equals(locked.Revision, StringComparison.OrdinalIgnoreCase))
            throw new CTildeProjectException($"Module cache for '{locked.Path}' is at {revision}, but the lock requires {locked.Revision}. Run restore.");
        if (RunGit(cacheRoot, "status", "--porcelain", "--untracked-files=all").Length != 0)
            throw new CTildeProjectException($"Module cache for '{locked.Path}' has local changes. Restore it before building.");
        var hash = ComputeContentHash(cacheRoot);
        if (!hash.Equals(locked.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new CTildeProjectException($"Module cache content for '{locked.Path}' does not match its lock file.");
    }

    private static void VerifyVendor(string vendorRoot, LockedModuleDocument locked)
    {
        var metadataPath = Path.Combine(vendorRoot, VendorMetadataFileName);
        VendorMetadataDocument metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<VendorMetadataDocument>(File.ReadAllText(metadataPath), JsonOptions)
                ?? throw new JsonException("Empty vendor metadata.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Vendor directory '{vendorRoot}' is not verified: {exception.Message}", exception);
        }
        var hash = ComputeContentHash(vendorRoot);
        if (metadata.Path != locked.Path || !metadata.Revision.Equals(locked.Revision, StringComparison.OrdinalIgnoreCase) ||
            !metadata.ContentHash.Equals(locked.ContentHash, StringComparison.OrdinalIgnoreCase) || !hash.Equals(locked.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new CTildeProjectException($"Vendor content for module '{locked.Path}' does not match its exact lock.");
    }

    private static void ValidateLock(IReadOnlyList<RepositoryModuleReference> modules, ModuleLockDocument lockFile, string projectRoot)
    {
        if (lockFile.Version != 1)
            throw new CTildeProjectException($"Unsupported module lock version {lockFile.Version} in '{Path.Combine(projectRoot, LockFileName)}'.");
        if (lockFile.Modules.Length != modules.Count)
            throw new CTildeProjectException($"Module lock does not match the project manifest. Run 'ctilde restore --project ctilde.json'.");
        foreach (var module in modules)
        {
            var locked = lockFile.Modules.FirstOrDefault(candidate => candidate.Path == module.ModulePath);
            if (locked is null || locked.Repository != module.Repository || locked.Selector != module.Selector || !IsRevision(locked.Revision) || locked.ContentHash.Length != 64)
                throw new CTildeProjectException($"Module lock entry for '{module.ModulePath}' is missing or stale. Run restore.");
        }
    }

    private static ModuleLockDocument? ReadLock(string projectRoot, bool required)
    {
        var path = Path.Combine(projectRoot, LockFileName);
        if (!File.Exists(path))
        {
            if (required)
                throw new CTildeProjectException($"Project modules require exact lock file '{path}'. Run 'ctilde restore --project ctilde.json'.");
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ModuleLockDocument>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("Empty module lock.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read module lock '{path}': {exception.Message}", exception);
        }
    }

    private static void WriteLock(string projectRoot, ModuleLockDocument document)
    {
        var path = Path.Combine(projectRoot, LockFileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static ImmutableDictionary<string, string> ReadReplacements(string projectRoot)
    {
        var path = Path.Combine(projectRoot, LocalFileName);
        if (!File.Exists(path))
            return ImmutableDictionary<string, string>.Empty;
        try
        {
            var document = JsonSerializer.Deserialize<LocalReplacementDocument>(File.ReadAllText(path), JsonOptions);
            return (document?.Replacements ?? new Dictionary<string, string>()).ToImmutableDictionary(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new CTildeProjectException($"Could not read local module replacements '{path}': {exception.Message}", exception);
        }
    }

    private static ImmutableArray<string> EnumerateSources(string root, ImmutableArray<string> patterns)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.ct", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').StartsWith(".git/", StringComparison.Ordinal) &&
                    patterns.Any(pattern => GlobMatches(pattern, Path.GetRelativePath(root, path).Replace('\\', '/'))))
                .Distinct(PathComparer).OrderBy(path => path, PathComparer).ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CTildeProjectException($"Could not enumerate module sources in '{root}': {exception.Message}", exception);
        }
    }

    private static bool GlobMatches(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string ComputeContentHash(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !IsIgnoredContent(root, path))
                     .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            using var stream = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsIgnoredContent(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == VendorMetadataFileName || relative.StartsWith(".git/", StringComparison.Ordinal);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(path => !IsIgnoredContent(source, path)))
        {
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string CacheRoot(string projectRoot, string modulePath) =>
        Path.Combine(projectRoot, ".ctilde", "modules", StableName(modulePath));

    private static string VendorRoot(string projectRoot, RepositoryModuleReference module)
    {
        var relative = module.Vendor ?? Path.Combine("vendor", module.Alias ?? module.ModulePath.Split('/').Last());
        var full = Path.GetFullPath(relative, projectRoot);
        EnsureInside(projectRoot, full, "vendor");
        return full;
    }

    private static string ResolveInsideOrOutsideProject(string projectRoot, string path) => Path.GetFullPath(path, projectRoot);

    private static void EnsureInside(string root, string path, string kind)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Module {kind} path '{path}' must stay inside project root '{root}'.");
    }

    private static string StableName(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    private static bool IsRevision(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string RunGit(string workingDirectory, params string[] arguments) =>
        RunGitAsync(workingDirectory, CancellationToken.None, arguments).GetAwaiter().GetResult();

    private static async Task<string> RunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await TryRunGitAsync(workingDirectory, cancellationToken, arguments).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new CTildeProjectException($"git {string.Join(' ', arguments)} failed in '{workingDirectory}': {result.Error.Trim()}");
        return result.Output;
    }

    private static async Task<ProcessResult> TryRunGitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            return await CaptureProcessAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new CTildeProjectException($"Could not run Git: {exception.Message}", exception);
        }
    }

    internal static async Task<ProcessResult> CaptureProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start subprocess.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ModuleLockDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("modules")] LockedModuleDocument[] Modules);

    private sealed record LockedModuleDocument(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("selector")] string Selector,
        [property: JsonPropertyName("revision")] string Revision,
        [property: JsonPropertyName("contentHash")] string ContentHash);

    private sealed record VendorMetadataDocument(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("revision")] string Revision,
        [property: JsonPropertyName("contentHash")] string ContentHash);

    private sealed record LocalReplacementDocument(
        [property: JsonPropertyName("replacements")] Dictionary<string, string>? Replacements);

    internal readonly record struct ProcessResult(int ExitCode, string Output, string Error);
}
