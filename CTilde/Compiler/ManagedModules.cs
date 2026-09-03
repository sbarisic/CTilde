using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public sealed record ManagedModuleDependencyMetadata(
    string Name,
    string Version,
    string BuildIdentity,
    string ApiHash);

public sealed record ManagedModuleTypeMetadata(
    string Fingerprint,
    string Name,
    string Kind,
    string Layout,
    int Size,
    int Alignment);

public sealed record ManagedModuleExportMetadata(
    string Identity,
    string ContainingType,
    string Member,
    string Signature,
    string Ownership,
    string Effects);

public sealed record ManagedModuleDeclarationMetadata(
    string Namespace,
    string Source);

public sealed record ManagedModuleOverlayMetadata(
    string Name,
    int PayloadBytes,
    int Alignment,
    ImmutableArray<ManagedModuleOverlayFunctionMetadata> Functions = default);

public sealed record ManagedModuleOverlayFunctionMetadata(
    string Identity,
    string BodySymbol,
    int TargetIndex);

public sealed record ManagedModuleMetadata(
    int SchemaVersion,
    string DraftVersion,
    int RuntimeAbi,
    int ModuleAbi,
    string Kind,
    string Name,
    string Version,
    string BuildIdentity,
    string ApiHash,
    ImmutableArray<ManagedModuleDependencyMetadata> Dependencies,
    ImmutableArray<ManagedModuleTypeMetadata> Types,
    ImmutableArray<ManagedModuleExportMetadata> Exports,
    ImmutableArray<ManagedModuleDeclarationMetadata> Declarations = default,
    bool HasOverlays = false,
    int MaximumOverlayBytes = 0,
    ImmutableArray<ManagedModuleOverlayMetadata> Overlays = default)
{
    internal const int MaximumNameAsciiBytes = 63;
    internal const int MaximumVersionAsciiBytes = 31;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ManagedModuleMetadata Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            using var stream = File.OpenRead(fullPath);
            var metadata = JsonSerializer.Deserialize<ManagedModuleMetadata>(stream, JsonOptions)
                ?? throw new JsonException("The metadata document is empty.");
            metadata.Validate(fullPath);
            return metadata;
        }
        catch (CTildeProjectException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read managed-module metadata '{fullPath}': {exception.Message}", exception, "CT6200");
        }
    }

    public string ToDeterministicJson()
    {
        var dependencies = Dependencies.IsDefault ? ImmutableArray<ManagedModuleDependencyMetadata>.Empty : Dependencies;
        var types = Types.IsDefault ? ImmutableArray<ManagedModuleTypeMetadata>.Empty : Types;
        var exports = Exports.IsDefault ? ImmutableArray<ManagedModuleExportMetadata>.Empty : Exports;
        var declarations = Declarations.IsDefault ? ImmutableArray<ManagedModuleDeclarationMetadata>.Empty : Declarations;
        var overlays = Overlays.IsDefault ? ImmutableArray<ManagedModuleOverlayMetadata>.Empty : Overlays;
        var canonical = this with
        {
            Dependencies = [.. dependencies.OrderBy(item => item.Name, StringComparer.Ordinal)],
            Types = [.. types.OrderBy(item => item.Fingerprint, StringComparer.Ordinal)],
            Exports = [.. exports.OrderBy(item => item.Identity, StringComparer.Ordinal)],
            Declarations = [.. declarations.OrderBy(item => item.Namespace, StringComparer.Ordinal).ThenBy(item => item.Source, StringComparer.Ordinal)],
            Overlays = [.. overlays.OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => item with { Functions = item.Functions.IsDefault ? [] : [.. item.Functions.OrderBy(function => function.Identity, StringComparer.Ordinal)] })],
        };
        return JsonSerializer.Serialize(canonical, JsonOptions) + "\n";
    }

    public void Validate(string source)
    {
        if (SchemaVersion != 3 || DraftVersion != CompilerContract.DraftVersion || RuntimeAbi != CompilerContract.RuntimeAbiVersion || ModuleAbi != CompilerContract.ManagedModuleAbiVersion)
            throw new CTildeProjectException($"Managed-module metadata '{source}' is incompatible with Draft {CompilerContract.DraftVersion}, Runtime ABI {CompilerContract.RuntimeAbiVersion}, and Module ABI {CompilerContract.ManagedModuleAbiVersion}.", "CT6201");
        if (Kind is not ("application" or "library") || !IsCanonicalName(Name) || !IsExactVersion(Version) || !IsHash(BuildIdentity) || !IsHash(ApiHash))
            throw new CTildeProjectException($"Managed-module metadata '{source}' has an incomplete exact identity.", "CT6201");
        if (Dependencies.IsDefault || Types.IsDefault || Exports.IsDefault || Declarations.IsDefault || Overlays.IsDefault)
            throw new CTildeProjectException($"Managed-module metadata '{source}' omits a required deterministic array.", "CT6201");
        if (Dependencies.Any(item => !IsCanonicalName(item.Name) || !IsExactVersion(item.Version) || !IsHash(item.BuildIdentity) || !IsHash(item.ApiHash)) ||
            Dependencies.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Dependencies.Length ||
            Dependencies.Any(item => item.Name == Name))
            throw new CTildeProjectException($"Managed-module metadata '{source}' contains an invalid, duplicate, or self-referential dependency identity.", "CT6201");
        if (Types.Any(item => !IsHash(item.Fingerprint) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Layout) || item.Size < 0 || item.Alignment <= 0 || (item.Alignment & (item.Alignment - 1)) != 0) ||
            Types.Select(item => item.Fingerprint).Distinct(StringComparer.Ordinal).Count() != Types.Length)
            throw new CTildeProjectException($"Managed-module metadata '{source}' contains an invalid or duplicate public type identity.", "CT6201");
        if (Exports.Any(item => !IsHash(item.Identity) || string.IsNullOrWhiteSpace(item.ContainingType) || string.IsNullOrWhiteSpace(item.Member) ||
                string.IsNullOrWhiteSpace(item.Signature) || string.IsNullOrWhiteSpace(item.Ownership) || string.IsNullOrWhiteSpace(item.Effects)) ||
            Exports.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() != Exports.Length)
            throw new CTildeProjectException($"Managed-module metadata '{source}' contains an invalid or duplicate managed export identity.", "CT6201");
        if (Declarations.Any(item => item.Namespace is null || string.IsNullOrWhiteSpace(item.Source)))
            throw new CTildeProjectException($"Managed-module metadata '{source}' contains an invalid public declaration.", "CT6201");
        if (MaximumOverlayBytes < 0 || HasOverlays != (Overlays.Length != 0) ||
            MaximumOverlayBytes != (Overlays.IsEmpty ? 0 : Overlays.Max(item => item.PayloadBytes)) ||
            Overlays.Any(item => !IsOverlayName(item.Name) || item.PayloadBytes < 0 || item.Alignment != 16 || item.Functions.IsDefault ||
                item.Functions.Any(function => string.IsNullOrWhiteSpace(function.Identity) || string.IsNullOrWhiteSpace(function.BodySymbol) || function.TargetIndex < 0) ||
                item.Functions.Select(function => function.Identity).Distinct(StringComparer.Ordinal).Count() != item.Functions.Length) ||
            Overlays.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Overlays.Length ||
            !Overlays.SelectMany(item => item.Functions).Select(function => function.TargetIndex)
                .Order().SequenceEqual(Enumerable.Range(0, Overlays.Sum(item => item.Functions.Length))))
            throw new CTildeProjectException($"Managed-module metadata '{source}' contains invalid overlay information.", "CT6201");

        var apiText = string.Join('\n', Types.OrderBy(item => item.Fingerprint, StringComparer.Ordinal)
            .Select(item => $"{item.Fingerprint}:{item.Layout}:{item.Size}:{item.Alignment}")
            .Concat(Exports.OrderBy(item => item.Identity, StringComparer.Ordinal)
                .Select(item => $"{item.Identity}:{item.Signature}:{item.Ownership}:{item.Effects}")));
        if (ApiHash != HashIdentity(apiText))
            throw new CTildeProjectException($"Managed-module metadata '{source}' has an API hash that does not match its public surface.", "CT6201");
    }

    internal static bool IsCanonicalName(string? value) => value is not null &&
        IsWithinAsciiCapacity(value, MaximumNameAsciiBytes) &&
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9]*(?:[.][A-Za-z][A-Za-z0-9]*)*$", RegexOptions.CultureInvariant);

    internal static bool IsExactVersion(string? value) => value is not null &&
        IsWithinAsciiCapacity(value, MaximumVersionAsciiBytes) &&
        Regex.IsMatch(value, "^(0|[1-9][0-9]*)(?:[.](0|[1-9][0-9]*)){2}(?:-[0-9A-Za-z.-]+)?(?:[+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    internal static bool IsOverlayName(string? value) => value is { Length: >= 1 and <= 31 } &&
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    private static bool IsWithinAsciiCapacity(string value, int maximumBytes) =>
        value.Length <= maximumBytes && value.All(character => character <= '\u007f');

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string HashIdentity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
