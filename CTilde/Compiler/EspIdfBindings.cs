using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CTilde;

public sealed record EspIdfBindingManifest(
    string ManifestPath,
    string Namespace,
    string DeclarationsPath,
    string AdapterSourcePath,
    ImmutableArray<EspIdfBindingImport> Imports)
{
    public string ManifestFingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText()))).ToLowerInvariant();

    public ImmutableArray<string> Components => Imports.Select(import => import.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

    public static EspIdfBindingManifest Load(string manifestPath, string projectRoot)
    {
        var fullPath = ResolvePath(manifestPath, projectRoot, "ESP-IDF binding manifest");
        if (!File.Exists(fullPath))
            throw new CTildeProjectException($"ESP-IDF binding manifest '{fullPath}' does not exist.");
        BindingDocument? document;
        try
        {
            using var stream = File.OpenRead(fullPath);
            document = JsonSerializer.Deserialize<BindingDocument>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CTildeProjectException($"Could not read ESP-IDF binding manifest '{fullPath}': {exception.Message}", exception);
        }

        if (document?.SchemaVersion != 1)
            throw new CTildeProjectException($"ESP-IDF binding manifest '{fullPath}' requires schemaVersion 1.");
        ValidateQualifiedName(document.Namespace, "namespace", fullPath);
        var declarations = ResolvePath(document.Declarations, projectRoot, "declarations");
        var adapter = ResolvePath(document.AdapterSource, projectRoot, "adapterSource");
        if (!Path.GetExtension(declarations).Equals(".ct", StringComparison.OrdinalIgnoreCase))
            throw new CTildeProjectException($"Property 'declarations' in '{fullPath}' must name a .ct file.");
        if (!Path.GetExtension(adapter).Equals(".c", StringComparison.OrdinalIgnoreCase))
            throw new CTildeProjectException($"Property 'adapterSource' in '{fullPath}' must name a .c file.");
        if (PathsEqual(declarations, adapter))
            throw new CTildeProjectException($"Binding outputs in '{fullPath}' must name distinct files.");
        if (document.Imports is not { Length: > 0 })
            throw new CTildeProjectException($"ESP-IDF binding manifest '{fullPath}' requires a non-empty imports array.");

        var imports = document.Imports.Select(import => ParseImport(import, fullPath)).ToImmutableArray();
        return new EspIdfBindingManifest(fullPath, document.Namespace!, declarations, adapter, imports);
    }

    public string CanonicalText()
    {
        var builder = new StringBuilder($"schema=1\nnamespace={Namespace}\ndeclarations={NormalizeRelative(DeclarationsPath)}\nadapter={NormalizeRelative(AdapterSourcePath)}\n");
        foreach (var import in Imports)
        {
            builder.Append("import=").Append(import.Component).Append('|').Append(import.Header).Append('|').Append(import.Container).Append('|').Append(import.AllowUnstable).Append('\n');
            foreach (var opaque in import.OpaqueTypes)
                builder.Append("opaque=").Append(opaque.Symbol).Append('|').Append(opaque.Name).Append('\n');
            foreach (var callback in import.Delegates)
            {
                builder.Append("delegate=").Append(callback.Symbol).Append('|').Append(callback.Name).Append('|').Append(callback.ReturnType).Append('\n');
                foreach (var parameter in callback.Parameters)
                    builder.Append("delegate-parameter=").Append(parameter.Name).Append('|').Append(parameter.Type).Append('\n');
            }
            foreach (var function in import.Functions)
            {
                builder.Append("function=").Append(function.Symbol).Append('|').Append(function.Name).Append('|').Append(function.ReturnType).Append('|').Append(function.NoAlloc).Append('|').Append(function.Callable).Append('|').Append(function.ReturnOwnership).Append('|').Append(function.ReturnNullable).Append('\n');
                foreach (var parameter in function.Parameters)
                    builder.Append("parameter=").Append(parameter.Name).Append('|').Append(parameter.Type).Append('|').Append(string.Join(',', parameter.NativeNames)).Append('|').Append(parameter.Ownership).Append('|').Append(parameter.Nullable).Append('|').Append(parameter.SynchronousCallback).Append('\n');
            }
            foreach (var constant in import.Constants)
                builder.Append("constant=").Append(constant.Symbol).Append('|').Append(constant.Name).Append('|').Append(constant.Type).Append('|').Append(constant.NoAlloc).Append('\n');
            foreach (var adapter in import.ConfigAdapters)
            {
                builder.Append("config=").Append(adapter.Function).Append('|').Append(adapter.Struct).Append('|').Append(adapter.StructParameter).Append('|').Append(adapter.Name).Append('|').Append(adapter.ReturnType).Append('|').Append(adapter.NoAlloc).Append('|').Append(adapter.Initializer).Append('|').Append(adapter.ReturnOwnership).Append('|').Append(adapter.ReturnNullable).Append('\n');
                foreach (var parameter in adapter.Parameters)
                    builder.Append("config-parameter=").Append(parameter.Name).Append('|').Append(parameter.Type).Append('|').Append(string.Join(',', parameter.NativeNames)).Append('|').Append(parameter.Ownership).Append('|').Append(parameter.Nullable).Append('|').Append(parameter.SynchronousCallback).Append('\n');
                foreach (var field in adapter.Fields)
                    builder.Append("field=").Append(field.Field).Append('|').Append(field.Name).Append('|').Append(field.Type).Append('|').Append(field.Mapping).Append('|').Append(field.MaxBytes).Append('\n');
                foreach (var value in adapter.Defaults)
                    builder.Append("default=").Append(value.Field).Append('|').Append(value.Symbol).Append('\n');
            }
            foreach (var adapter in import.OutputAdapters)
            {
                builder.Append("output=").Append(adapter.Function).Append('|').Append(adapter.Struct).Append('|').Append(adapter.StructParameter).Append('|').Append(adapter.Name).Append('|').Append(adapter.ReturnType).Append('|').Append(adapter.NoAlloc).Append('\n');
                foreach (var parameter in adapter.Parameters)
                    builder.Append("output-parameter=").Append(parameter.Name).Append('|').Append(parameter.Type).Append('|').Append(string.Join(',', parameter.NativeNames)).Append('|').Append(parameter.Ownership).Append('|').Append(parameter.Nullable).Append('|').Append(parameter.SynchronousCallback).Append('\n');
                foreach (var field in adapter.Fields)
                    builder.Append("output-field=").Append(field.Field).Append('|').Append(field.Name).Append('|').Append(field.Type).Append('\n');
            }
        }
        return builder.ToString();
    }

    private static EspIdfBindingImport ParseImport(ImportDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Component, "component", manifestPath);
        ValidateHeader(document.Header, manifestPath);
        ValidateIdentifier(document.Container, "container", manifestPath);
        var opaqueTypes = (document.OpaqueTypes ?? []).Select(value => ParseOpaqueType(value, manifestPath)).ToImmutableArray();
        var delegates = (document.Delegates ?? []).Select(value => ParseDelegate(value, manifestPath)).ToImmutableArray();
        var functions = (document.Functions ?? []).Select(value => ParseFunction(value, manifestPath)).ToImmutableArray();
        var constants = (document.Constants ?? []).Select(value => ParseConstant(value, manifestPath)).ToImmutableArray();
        var configs = (document.ConfigAdapters ?? []).Select(value => ParseConfig(value, manifestPath)).ToImmutableArray();
        var outputs = (document.OutputAdapters ?? []).Select(value => ParseOutput(value, manifestPath)).ToImmutableArray();
        if (opaqueTypes.IsEmpty && delegates.IsEmpty && functions.IsEmpty && constants.IsEmpty && configs.IsEmpty && outputs.IsEmpty)
            throw new CTildeProjectException($"Import '{document.Header}' in '{manifestPath}' must select at least one declaration.");
        var publicHeader = document.Header!.Replace('\\', '/');
        if (!document.AllowUnstable && (publicHeader.StartsWith("private/", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("/private/", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("private_include/", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("esp_private/", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("example", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("preview", StringComparison.OrdinalIgnoreCase) || publicHeader.Contains("experimental", StringComparison.OrdinalIgnoreCase)))
            throw new CTildeProjectException($"Header '{document.Header}' in '{manifestPath}' is private or unstable; set allowUnstable to true to opt in.");
        return new EspIdfBindingImport(document.Component!, publicHeader, document.Container!, document.AllowUnstable, opaqueTypes, delegates, functions, constants, configs, outputs);
    }

    private static EspIdfBindingOpaqueType ParseOpaqueType(OpaqueTypeDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Symbol, "opaque typedef", manifestPath);
        ValidateIdentifier(document.Name, "opaque type name", manifestPath);
        return new EspIdfBindingOpaqueType(document.Symbol!, document.Name!);
    }

    private static EspIdfBindingDelegate ParseDelegate(DelegateDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Symbol, "callback typedef", manifestPath);
        ValidateIdentifier(document.Name, "callback delegate name", manifestPath);
        ValidateType(document.ReturnType, "callback returnType", manifestPath);
        var parameters = (document.Parameters ?? []).Select(parameter =>
        {
            ValidateIdentifier(parameter.Name, "callback parameter name", manifestPath);
            ValidateType(parameter.Type, "callback parameter type", manifestPath);
            return new EspIdfBindingDelegateParameter(parameter.Name!, parameter.Type!);
        }).ToImmutableArray();
        return new EspIdfBindingDelegate(document.Symbol!, document.Name!, document.ReturnType!, parameters);
    }

    private static EspIdfBindingFunction ParseFunction(FunctionDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Symbol, "function symbol", manifestPath);
        ValidateIdentifier(document.Name, "function name", manifestPath);
        ValidateType(document.ReturnType, "returnType", manifestPath);
        var parameters = (document.Parameters ?? []).Select(parameter => ParseParameter(parameter, manifestPath)).ToImmutableArray();
        if (parameters.SelectMany(parameter => parameter.NativeNames).Distinct(StringComparer.Ordinal).Count() != parameters.Sum(parameter => parameter.NativeNames.Length))
            throw new CTildeProjectException($"Function '{document.Symbol}' in '{manifestPath}' maps a native parameter more than once.");
        var returnOwnership = ParseReturnOwnership(document.ReturnOwnership, document.ReturnType!, manifestPath);
        return new EspIdfBindingFunction(document.Symbol!, document.Name!, document.ReturnType!, parameters, document.NoAlloc, document.Callable, returnOwnership, document.ReturnNullable);
    }

    private static EspIdfBindingParameter ParseParameter(ParameterDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Name, "parameter name", manifestPath);
        ValidateType(document.Type, "parameter type", manifestPath);
        if (document.NativeNames is not { Length: > 0 })
            throw new CTildeProjectException($"Parameter '{document.Name}' in '{manifestPath}' requires nativeNames.");
        foreach (var name in document.NativeNames)
            ValidateIdentifier(name, "native parameter", manifestPath);
        var ownership = document.Ownership ?? "borrowed";
        if (ownership is not ("borrowed" or "consumes" or "retained" or "creates"))
            throw new CTildeProjectException($"Unknown ownership '{ownership}' in '{manifestPath}'.");
        return new EspIdfBindingParameter(document.Name!, document.Type!, [.. document.NativeNames], ownership, document.Nullable, document.SynchronousCallback);
    }

    private static EspIdfBindingConstant ParseConstant(ConstantDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Symbol, "constant symbol", manifestPath);
        ValidateIdentifier(document.Name, "constant name", manifestPath);
        ValidateType(document.Type, "constant type", manifestPath);
        if (document.Type == "void")
            throw new CTildeProjectException($"Constant '{document.Symbol}' in '{manifestPath}' cannot have type void.");
        return new EspIdfBindingConstant(document.Symbol!, document.Name!, document.Type!, document.NoAlloc);
    }

    private static EspIdfBindingConfigAdapter ParseConfig(ConfigDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Function, "configuration function", manifestPath);
        ValidateIdentifier(document.Struct, "configuration structure", manifestPath);
        ValidateIdentifier(document.StructParameter, "configuration structure parameter", manifestPath);
        ValidateIdentifier(document.Name, "configuration adapter name", manifestPath);
        ValidateType(document.ReturnType, "returnType", manifestPath);
        if (document.Initializer is not null)
            ValidateIdentifier(document.Initializer, "configuration initializer", manifestPath);
        var parameters = (document.Parameters ?? []).Select(parameter => ParseParameter(parameter, manifestPath)).ToImmutableArray();
        var fields = (document.Fields ?? []).Select(field =>
        {
            ValidateMemberPath(field.Field, "configuration field", manifestPath);
            ValidateIdentifier(field.Name, "configuration parameter", manifestPath);
            ValidateType(field.Type, "configuration parameter type", manifestPath);
            var mapping = field.Mapping ?? "value";
            if (mapping is not ("value" or "fixedUtf8"))
                throw new CTildeProjectException($"Configuration field '{field.Field}' in '{manifestPath}' has unknown mapping '{mapping}'.");
            if (mapping == "fixedUtf8" && (field.Type != "NativeUtf8String" || field.MaxBytes is null or <= 0))
                throw new CTildeProjectException($"Fixed UTF-8 field '{field.Field}' in '{manifestPath}' requires type NativeUtf8String and a positive maxBytes value.");
            if (mapping == "value" && field.MaxBytes is not null)
                throw new CTildeProjectException($"Configuration field '{field.Field}' in '{manifestPath}' uses maxBytes without fixedUtf8 mapping.");
            return new EspIdfBindingConfigField(field.Field!, field.Name!, field.Type!, mapping, field.MaxBytes);
        }).ToImmutableArray();
        var defaults = (document.Defaults ?? []).Select(value =>
        {
            ValidateMemberPath(value.Field, "default field", manifestPath);
            ValidateIdentifier(value.Symbol, "default symbol", manifestPath);
            return new EspIdfBindingConfigDefault(value.Field!, value.Symbol!);
        }).ToImmutableArray();
        if (fields.IsEmpty && defaults.IsEmpty && document.Initializer is null)
            throw new CTildeProjectException($"Configuration adapter '{document.Name}' in '{manifestPath}' requires at least one field.");
        if (fields.Select(field => field.Field).Concat(defaults.Select(value => value.Field)).Distinct(StringComparer.Ordinal).Count() != fields.Length + defaults.Length)
            throw new CTildeProjectException($"Configuration adapter '{document.Name}' in '{manifestPath}' assigns a field more than once.");
        if (fields.Any(field => field.Mapping == "fixedUtf8") && document.ReturnType is not ("EspError" or "Esp.Idf.EspError"))
            throw new CTildeProjectException($"Configuration adapter '{document.Name}' in '{manifestPath}' must return EspError when using fixedUtf8 fields.");
        var mappedNativeNames = parameters.SelectMany(parameter => parameter.NativeNames).Append(document.StructParameter!).ToArray();
        if (mappedNativeNames.Distinct(StringComparer.Ordinal).Count() != mappedNativeNames.Length)
            throw new CTildeProjectException($"Configuration adapter '{document.Name}' in '{manifestPath}' maps a native parameter more than once.");
        var returnOwnership = ParseReturnOwnership(document.ReturnOwnership, document.ReturnType!, manifestPath);
        return new EspIdfBindingConfigAdapter(document.Function!, document.Struct!, document.StructParameter!, document.Name!, document.ReturnType!, parameters, fields, defaults, document.Initializer, document.NoAlloc, returnOwnership, document.ReturnNullable);
    }

    private static EspIdfBindingOutputAdapter ParseOutput(OutputDocument document, string manifestPath)
    {
        ValidateIdentifier(document.Function, "output function", manifestPath);
        ValidateIdentifier(document.Struct, "output structure", manifestPath);
        ValidateIdentifier(document.StructParameter, "output structure parameter", manifestPath);
        ValidateIdentifier(document.Name, "output adapter name", manifestPath);
        ValidateType(document.ReturnType, "returnType", manifestPath);
        var parameters = (document.Parameters ?? []).Select(parameter => ParseParameter(parameter, manifestPath)).ToImmutableArray();
        var fields = (document.Fields ?? []).Select(field =>
        {
            ValidateMemberPath(field.Field, "output field", manifestPath);
            ValidateIdentifier(field.Name, "output parameter", manifestPath);
            ValidateType(field.Type, "output parameter type", manifestPath);
            if (field.Type == "void")
                throw new CTildeProjectException($"Output field '{field.Field}' in '{manifestPath}' cannot have type void.");
            return new EspIdfBindingOutputField(field.Field!, field.Name!, field.Type!);
        }).ToImmutableArray();
        if (fields.IsEmpty)
            throw new CTildeProjectException($"Output adapter '{document.Name}' in '{manifestPath}' requires at least one selected field.");
        if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new CTildeProjectException($"Output adapter '{document.Name}' in '{manifestPath}' declares a duplicate output name.");
        var mappedNativeNames = parameters.SelectMany(parameter => parameter.NativeNames).Append(document.StructParameter!).ToArray();
        if (mappedNativeNames.Distinct(StringComparer.Ordinal).Count() != mappedNativeNames.Length)
            throw new CTildeProjectException($"Output adapter '{document.Name}' in '{manifestPath}' maps a native parameter more than once.");
        return new EspIdfBindingOutputAdapter(document.Function!, document.Struct!, document.StructParameter!, document.Name!, document.ReturnType!, parameters, fields, document.NoAlloc);
    }

    private static string ParseReturnOwnership(string? value, string returnType, string manifestPath)
    {
        var ownership = value ?? "default";
        if (ownership is not ("default" or "owned" or "borrowed"))
            throw new CTildeProjectException($"Unknown return ownership '{ownership}' in '{manifestPath}'.");
        if (returnType == "void" && ownership != "default")
            throw new CTildeProjectException($"Void return in '{manifestPath}' cannot declare ownership.");
        return ownership;
    }

    private static string ResolvePath(string? value, string root, string property)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new CTildeProjectException($"Property '{property}' must be a non-empty project-relative path.");
        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            throw new CTildeProjectException($"Property '{property}' must stay within the project directory.");
        var full = Path.GetFullPath(Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CTildeProjectException($"Property '{property}' must stay within the project directory.");
        return full;
    }

    private static void ValidateQualifiedName(string? value, string property, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Any(part => !IdentifierRegex.IsMatch(part)))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a qualified C~ identifier.");
    }

    private static void ValidateIdentifier(string? value, string property, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex.IsMatch(value))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a portable identifier.");
    }

    private static void ValidateMemberPath(string? value, string property, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Any(part => !IdentifierRegex.IsMatch(part)))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must be a dotted portable identifier path.");
    }

    private static void ValidateHeader(string? value, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal) || value.Contains('"') || value.Contains('<') || value.Contains('>'))
            throw new CTildeProjectException($"Header in '{manifestPath}' must be a safe include path.");
    }

    private static void ValidateType(string? value, string property, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.' or '<' or '>' or '*' or ',' or ' ')))
            throw new CTildeProjectException($"Property '{property}' in '{manifestPath}' must use C~ type syntax without source fragments.");
    }

    private static bool PathsEqual(string left, string right) => left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string NormalizeRelative(string path) => Path.GetFileName(path).Replace('\\', '/');
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private sealed record BindingDocument(int SchemaVersion, string? Namespace, string? Declarations, string? AdapterSource, ImportDocument[]? Imports);
    private sealed record ImportDocument(string? Component, string? Header, string? Container, bool AllowUnstable, OpaqueTypeDocument[]? OpaqueTypes, DelegateDocument[]? Delegates, FunctionDocument[]? Functions, ConstantDocument[]? Constants, ConfigDocument[]? ConfigAdapters, OutputDocument[]? OutputAdapters);
    private sealed record OpaqueTypeDocument(string? Symbol, string? Name);
    private sealed record DelegateDocument(string? Symbol, string? Name, string? ReturnType, DelegateParameterDocument[]? Parameters);
    private sealed record DelegateParameterDocument(string? Name, string? Type);
    private sealed record FunctionDocument(string? Symbol, string? Name, string? ReturnType, ParameterDocument[]? Parameters, bool NoAlloc, bool Callable, string? ReturnOwnership, bool ReturnNullable);
    private sealed record ParameterDocument(string? Name, string? Type, string[]? NativeNames, string? Ownership, bool Nullable, bool SynchronousCallback);
    private sealed record ConstantDocument(string? Symbol, string? Name, string? Type, bool NoAlloc);
    private sealed record ConfigDocument(string? Function, string? Struct, string? StructParameter, string? Name, string? ReturnType, ParameterDocument[]? Parameters, ConfigFieldDocument[]? Fields, ConfigDefaultDocument[]? Defaults, string? Initializer, bool NoAlloc, string? ReturnOwnership, bool ReturnNullable);
    private sealed record ConfigFieldDocument(string? Field, string? Name, string? Type, string? Mapping, int? MaxBytes);
    private sealed record ConfigDefaultDocument(string? Field, string? Symbol);
    private sealed record OutputDocument(string? Function, string? Struct, string? StructParameter, string? Name, string? ReturnType, ParameterDocument[]? Parameters, OutputFieldDocument[]? Fields, bool NoAlloc);
    private sealed record OutputFieldDocument(string? Field, string? Name, string? Type);
}

public sealed record EspIdfBindingImport(string Component, string Header, string Container, bool AllowUnstable, ImmutableArray<EspIdfBindingOpaqueType> OpaqueTypes, ImmutableArray<EspIdfBindingDelegate> Delegates, ImmutableArray<EspIdfBindingFunction> Functions, ImmutableArray<EspIdfBindingConstant> Constants, ImmutableArray<EspIdfBindingConfigAdapter> ConfigAdapters, ImmutableArray<EspIdfBindingOutputAdapter> OutputAdapters);
public sealed record EspIdfBindingOpaqueType(string Symbol, string Name);
public sealed record EspIdfBindingDelegate(string Symbol, string Name, string ReturnType, ImmutableArray<EspIdfBindingDelegateParameter> Parameters);
public sealed record EspIdfBindingDelegateParameter(string Name, string Type);
public sealed record EspIdfBindingFunction(string Symbol, string Name, string ReturnType, ImmutableArray<EspIdfBindingParameter> Parameters, bool NoAlloc, bool Callable, string ReturnOwnership, bool ReturnNullable);
public sealed record EspIdfBindingParameter(string Name, string Type, ImmutableArray<string> NativeNames, string Ownership, bool Nullable, bool SynchronousCallback);
public sealed record EspIdfBindingConstant(string Symbol, string Name, string Type, bool NoAlloc);
public sealed record EspIdfBindingConfigAdapter(string Function, string Struct, string StructParameter, string Name, string ReturnType, ImmutableArray<EspIdfBindingParameter> Parameters, ImmutableArray<EspIdfBindingConfigField> Fields, ImmutableArray<EspIdfBindingConfigDefault> Defaults, string? Initializer, bool NoAlloc, string ReturnOwnership, bool ReturnNullable);
public sealed record EspIdfBindingConfigField(string Field, string Name, string Type, string Mapping, int? MaxBytes);
public sealed record EspIdfBindingConfigDefault(string Field, string Symbol);
public sealed record EspIdfBindingOutputAdapter(string Function, string Struct, string StructParameter, string Name, string ReturnType, ImmutableArray<EspIdfBindingParameter> Parameters, ImmutableArray<EspIdfBindingOutputField> Fields, bool NoAlloc);
public sealed record EspIdfBindingOutputField(string Field, string Name, string Type);
