using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CTilde;

internal static class ManagedModuleMetadataEmitter
{
    private static readonly Regex LocalInclude = new("^\\s*#\\s*include\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

    public static ManagedModuleMetadata Emit(Compilation compilation, BoundProgram program, ManagedModuleConfiguration configuration)
    {
        if (compilation.Options.ManagedModuleKind != configuration.Kind)
            throw new ArgumentException("The managed-module build configuration does not match the compilation kind.", nameof(configuration));

        var model = program.Model;
        var types = model.ProjectTypes.Where(type => type.Accessibility == Accessibility.Public)
            .Select(type =>
            {
                var layout = LayoutIdentity(type);
                var (size, alignment) = ManagedLayout(type.Type, []);
                return new ManagedModuleTypeMetadata(
                    ManagedModuleMetadata.HashIdentity("type:" + type.FullName + ":" + layout),
                    type.FullName,
                    type.Kind.ToString().ToLowerInvariant(),
                    layout,
                    size,
                    alignment);
            })
            .OrderBy(type => type.Fingerprint, StringComparer.Ordinal)
            .ToImmutableArray();

        var exports = model.ProjectTypes.Where(type => type.Accessibility == Accessibility.Public)
            .SelectMany(Exports)
            .OrderBy(export => export.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
        var apiText = string.Join('\n', types.Select(type => $"{type.Fingerprint}:{type.Layout}:{type.Size}:{type.Alignment}")
            .Concat(exports.Select(export => $"{export.Identity}:{export.Signature}:{export.Ownership}:{export.Effects}")));
        var apiHash = ManagedModuleMetadata.HashIdentity(apiText);
        var dependencies = configuration.References.Select(reference => new ManagedModuleDependencyMetadata(
            reference.Name, reference.Version, reference.BuildIdentity, reference.ApiHash)).OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
        var sourceIdentity = string.Join('\n', compilation.SyntaxTrees.OrderBy(tree => tree.Text.FilePath, StringComparer.Ordinal)
            .Select(tree => $"{tree.Text.FilePath.Replace('\\', '/')}:{ManagedModuleMetadata.HashIdentity(tree.Text.Text)}"));
        var nativeIdentity = NativeIdentity(configuration);
        var buildText = string.Join('\n',
            $"draft={CompilerContract.DraftVersion}",
            $"runtime={CompilerContract.RuntimeAbiVersion}",
            $"module={CompilerContract.ManagedModuleAbiVersion}",
            $"kind={configuration.Kind}",
            $"name={configuration.Name}",
            $"version={configuration.Version}",
            $"architecture={compilation.Options.Architecture}",
            $"api={apiHash}",
            sourceIdentity,
            nativeIdentity,
            string.Join('\n', dependencies.Select(dependency => $"dep={dependency.Name}:{dependency.Version}:{dependency.BuildIdentity}:{dependency.ApiHash}")));

        return new ManagedModuleMetadata(1, CompilerContract.DraftVersion, CompilerContract.RuntimeAbiVersion,
            CompilerContract.ManagedModuleAbiVersion, configuration.Kind.ToString().ToLowerInvariant(), configuration.Name,
            configuration.Version, ManagedModuleMetadata.HashIdentity(buildText), apiHash, dependencies, types, exports);
    }

    private static string NativeIdentity(ManagedModuleConfiguration configuration)
    {
        if (configuration.NativeSources.IsDefaultOrEmpty)
            return string.Empty;
        var root = Path.GetFullPath(configuration.ProjectRoot ?? throw new InvalidOperationException(
            "Managed-module native sources require a project identity root."));
        var inputs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Queue<string>(configuration.NativeSources.Select(Path.GetFullPath));
        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            if (!inputs.Add(current))
                continue;
            foreach (var line in File.ReadLines(current))
            {
                var match = LocalInclude.Match(line);
                if (!match.Success)
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, match.Groups[1].Value));
                if (IsInside(candidate, root) && File.Exists(candidate))
                    pending.Enqueue(candidate);
            }
        }
        return string.Join('\n', inputs.Order(StringComparer.Ordinal).Select(path =>
            $"native={Path.GetRelativePath(root, path).Replace('\\', '/')}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}"));
    }

    private static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static IEnumerable<ManagedModuleExportMetadata> Exports(TypeSymbol type)
    {
        foreach (var constructor in type.Constructors.Where(member => member.Accessibility == Accessibility.Public))
            yield return MethodExport(constructor);
        foreach (var method in type.Methods.Where(member => member.Accessibility == Accessibility.Public && !member.IsEntryPoint))
            yield return MethodExport(method);
        foreach (var field in type.Fields.Where(member => member.Accessibility == Accessibility.Public))
        {
            var signature = $"field:{type.FullName}::{field.Name}:{NameMangler.CanonicalType(field.Type)}:static={field.IsStatic}:readonly={field.IsReadonly}";
            yield return Export(signature, type.FullName, field.Name, signature, field.Type.ContainsManagedReferences ? "managed" : "value", "none");
        }
        foreach (var property in type.Properties.Where(member => member.Accessibility == Accessibility.Public))
        {
            var signature = $"property:{type.FullName}::{property.Name}:{NameMangler.CanonicalType(property.Type)}:static={property.IsStatic}:get={property.Getter is not null}:set={property.Setter is not null}";
            yield return Export(signature, type.FullName, property.Name, signature, property.Type.ContainsManagedReferences ? "managed" : "value", EffectText(property.DeclaredEffects));
        }
    }

    private static ManagedModuleExportMetadata MethodExport(MethodSymbol method)
    {
        var signature = NameMangler.MethodIdentity(method);
        var ownership = string.Join(',', method.Parameters.Select(parameter =>
            $"{parameter.PassingKind.ToString().ToLowerInvariant()}:{(parameter.Type.ContainsManagedReferences ? "managed" : "value")}:retained={parameter.IsRetained}"));
        if (method.ReturnType.ContainsManagedReferences)
            ownership += ";result=managed";
        return Export(signature, method.ContainingType.FullName, method.IsConstructor ? ".ctor" : method.Name, signature, ownership, EffectText(method.DeclaredEffects));
    }

    private static ManagedModuleExportMetadata Export(string identitySource, string type, string member, string signature, string ownership, string effects) =>
        new(ManagedModuleMetadata.HashIdentity(identitySource), type, member, signature, ownership, effects);

    private static string EffectText(EffectContract effects) => effects == EffectContract.None
        ? "none"
        : string.Join(',', Enum.GetValues<EffectContract>().Where(value => value != EffectContract.None && effects.HasFlag(value)).Select(value => value.ToString().ToLowerInvariant()));

    private static string LayoutIdentity(TypeSymbol type)
    {
        var fields = type.Fields.Where(field => !field.IsStatic && !field.IsBitView).Select(field =>
            $"{field.Name}:{NameMangler.CanonicalType(field.Type)}:offset={field.Offset?.ToString(CultureInfo.InvariantCulture) ?? "auto"}:align={field.Alignment?.ToString(CultureInfo.InvariantCulture) ?? "auto"}");
        return $"{type.AggregateLayout.ToString().ToLowerInvariant()}:pack={type.Pack?.ToString(CultureInfo.InvariantCulture) ?? "natural"}:align={type.Alignment?.ToString(CultureInfo.InvariantCulture) ?? "natural"}:base={type.BaseType?.FullName ?? "none"}:fields=[{string.Join(';', fields)}]";
    }

    private static (int Size, int Alignment) ManagedLayout(CType type, HashSet<TypeSymbol> visiting)
    {
        switch (type.Kind)
        {
            case CTypeKind.Bool:
            case CTypeKind.Byte:
            case CTypeKind.Sbyte:
            case CTypeKind.Char: return (1, 1);
            case CTypeKind.Short:
            case CTypeKind.Ushort: return (2, 2);
            case CTypeKind.Rune:
            case CTypeKind.Int:
            case CTypeKind.Uint:
            case CTypeKind.Float:
            case CTypeKind.EspError: return (4, 4);
            case CTypeKind.Long:
            case CTypeKind.Ulong:
            case CTypeKind.Double: return (8, 8);
            case CTypeKind.Nint:
            case CTypeKind.Nuint:
            case CTypeKind.Pointer:
            case CTypeKind.FunctionPointer:
            case CTypeKind.Opaque:
            case CTypeKind.String:
            case CTypeKind.Class:
            case CTypeKind.Interface:
            case CTypeKind.Delegate:
            case CTypeKind.Array: return (4, 4);
            case CTypeKind.NativeBuffer:
            case CTypeKind.ReadOnlyNativeBuffer:
            case CTypeKind.NativeUtf8String: return (8, 4);
            case CTypeKind.InlineArray:
                var inline = ManagedLayout(type.ElementType!, visiting);
                return (inline.Size * type.InlineArrayLength, inline.Alignment);
            case CTypeKind.Enum:
                return ManagedLayout(type.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type, visiting);
            case CTypeKind.Newtype:
                var underlying = ManagedLayout(type.Symbol!.UnderlyingType!, visiting);
                return (Align(underlying.Size, type.Symbol.Alignment ?? underlying.Alignment), type.Symbol.Alignment ?? underlying.Alignment);
            case CTypeKind.Struct:
                return ManagedAggregate(type.Symbol!, visiting, objectHeader: false);
            default: return (0, 1);
        }
    }

    private static (int Size, int Alignment) ManagedAggregate(TypeSymbol symbol, HashSet<TypeSymbol> visiting, bool objectHeader)
    {
        if (!visiting.Add(symbol))
            return (0, 1);
        var offset = objectHeader ? 16 : 0;
        var maximum = objectHeader ? 4 : 1;
        if (symbol.BaseType is not null && !symbol.IsObject)
        {
            var baseLayout = ManagedAggregate(symbol.BaseType, visiting, objectHeader: true);
            offset = baseLayout.Size;
            maximum = baseLayout.Alignment;
        }
        var unionSize = 0;
        foreach (var field in symbol.Fields.Where(field => !field.IsStatic && !field.IsBitView))
        {
            var layout = ManagedLayout(field.Type, visiting);
            var alignment = Math.Min(field.Alignment ?? layout.Alignment, symbol.Pack ?? int.MaxValue);
            maximum = Math.Max(maximum, alignment);
            if (symbol.AggregateLayout == AggregateLayoutKind.Union)
                unionSize = Math.Max(unionSize, layout.Size);
            else if (symbol.AggregateLayout == AggregateLayoutKind.Explicit)
                offset = Math.Max(offset, (field.Offset ?? 0) + layout.Size);
            else
            {
                offset = Align(offset, alignment);
                offset += layout.Size;
            }
        }
        visiting.Remove(symbol);
        var size = symbol.AggregateLayout == AggregateLayoutKind.Union ? unionSize : offset;
        var finalAlignment = symbol.Alignment ?? maximum;
        return (Align(Math.Max(size, 1), finalAlignment), finalAlignment);
    }

    private static int Align(int value, int alignment) => alignment <= 1 ? value : checked((value + alignment - 1) / alignment * alignment);
}
