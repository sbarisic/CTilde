using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CTilde;

internal enum CTypeKind
{
    Error, Void, Bool, Byte, Sbyte, Short, Ushort, Char, Int, Uint, Long, Ulong, Nint, Nuint, Float, String,
    Class, Struct, Interface, TypeParameter, Enum, Delegate, Opaque, EspError, Array, Pointer, FunctionPointer, NativeBuffer, ReadOnlyNativeBuffer, NativeUtf8String, Null,
}

internal sealed class FunctionPointerSignature(ImmutableArray<CType> parameterTypes, ImmutableArray<ParameterPassingKind> passingKinds, CType returnType) : IEquatable<FunctionPointerSignature>
{
    public ImmutableArray<CType> ParameterTypes { get; } = parameterTypes;
    public ImmutableArray<ParameterPassingKind> PassingKinds { get; } = passingKinds;
    public CType ReturnType { get; } = returnType;
    public bool Equals(FunctionPointerSignature? other) => other is not null && ReturnType == other.ReturnType && ParameterTypes.SequenceEqual(other.ParameterTypes) && PassingKinds.SequenceEqual(other.PassingKinds);
    public override bool Equals(object? obj) => obj is FunctionPointerSignature other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReturnType);
        foreach (var parameter in ParameterTypes)
            hash.Add(parameter);
        foreach (var passingKind in PassingKinds)
            hash.Add(passingKind);
        return hash.ToHashCode();
    }
}

internal sealed record CType(CTypeKind Kind, TypeSymbol? Symbol = null, CType? ElementType = null, FunctionPointerSignature? FunctionPointer = null)
{
    public static readonly CType Error = new(CTypeKind.Error);
    public static readonly CType Void = new(CTypeKind.Void);
    public static readonly CType Bool = new(CTypeKind.Bool);
    public static readonly CType Byte = new(CTypeKind.Byte);
    public static readonly CType Sbyte = new(CTypeKind.Sbyte);
    public static readonly CType Short = new(CTypeKind.Short);
    public static readonly CType Ushort = new(CTypeKind.Ushort);
    public static readonly CType Char = new(CTypeKind.Char);
    public static readonly CType Int = new(CTypeKind.Int);
    public static readonly CType Uint = new(CTypeKind.Uint);
    public static readonly CType Long = new(CTypeKind.Long);
    public static readonly CType Ulong = new(CTypeKind.Ulong);
    public static readonly CType Nint = new(CTypeKind.Nint);
    public static readonly CType Nuint = new(CTypeKind.Nuint);
    public static readonly CType Float = new(CTypeKind.Float);
    public static readonly CType String = new(CTypeKind.String);
    public static readonly CType Null = new(CTypeKind.Null);

    public bool IsError => Kind == CTypeKind.Error;
    public bool IsNumeric => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float;
    public bool IsIntegral => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Enum;
    public bool IsReference => Kind is CTypeKind.Class or CTypeKind.Interface or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.String;
    public bool IsNativeBuffer => Kind is CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer;
    public bool IsNativeUtf8String => Kind == CTypeKind.NativeUtf8String;
    public bool IsAtomic => Kind == CTypeKind.Struct && Symbol?.GenericDefinition is { Namespace: "System.Threading", Name: "Atomic" };
    public bool ContainsAtomic => IsAtomic || Kind == CTypeKind.Struct && Symbol is not null && Symbol.Fields.Any(memberField => !memberField.IsStatic && memberField.Type.ContainsAtomic);
    public bool ContainsManagedReferences => ContainsManagedReferencesCore(this, []);
    public bool IsPointerLike => IsReference || Kind is CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.Opaque;
    public bool ContainsPointer => ContainsPointerCore(this, []);
    public bool IsValueType => Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Struct or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.NativeUtf8String;

    public string DisplayName => Kind switch
    {
        CTypeKind.Class or CTypeKind.Struct or CTypeKind.Interface or CTypeKind.TypeParameter or CTypeKind.Enum or CTypeKind.Delegate or CTypeKind.Opaque or CTypeKind.EspError => Symbol!.FullName,
        CTypeKind.Array => $"{ElementType!.DisplayName}[]",
        CTypeKind.Pointer => $"{ElementType!.DisplayName}*",
        CTypeKind.FunctionPointer => $"delegate* unmanaged<{string.Join(", ", FunctionPointer!.ParameterTypes.Select((type, index) => FunctionPointer.PassingKinds[index] == ParameterPassingKind.Value ? type.DisplayName : $"{FunctionPointer.PassingKinds[index].ToString().ToLowerInvariant()} {type.DisplayName}").Append(FunctionPointer.ReturnType.DisplayName))}>",
        CTypeKind.NativeBuffer => $"System.Runtime.NativeBuffer<{ElementType!.DisplayName}>",
        CTypeKind.ReadOnlyNativeBuffer => $"System.Runtime.ReadOnlyNativeBuffer<{ElementType!.DisplayName}>",
        CTypeKind.NativeUtf8String => "System.Runtime.NativeUtf8String",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    private static bool ContainsPointerCore(CType type, HashSet<TypeSymbol> visited)
    {
        if (type.Kind is CTypeKind.Pointer or CTypeKind.FunctionPointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer)
            return true;
        if (type.ElementType is not null && ContainsPointerCore(type.ElementType, visited))
            return true;
        if (type.Symbol is null || !visited.Add(type.Symbol))
            return false;
        return type.Symbol.Fields.Any(field => ContainsPointerCore(field.Type, visited)) ||
            type.Symbol.Properties.Any(property => ContainsPointerCore(property.Type, visited));
    }

    private static bool ContainsManagedReferencesCore(CType type, HashSet<TypeSymbol> visited)
    {
        if (type.Kind == CTypeKind.NativeUtf8String)
            return true;
        if (type.IsReference)
            return true;
        if (type.Kind != CTypeKind.Struct || type.Symbol is null || !visited.Add(type.Symbol))
            return false;
        return type.Symbol.Fields.Any(field => !field.IsStatic && ContainsManagedReferencesCore(field.Type, visited));
    }
}

internal enum DeclaredTypeKind { Class, Struct, Interface, TypeParameter, Enum, Delegate, Opaque, StaticClass }
internal enum AggregateLayoutKind { Sequential, Union, Explicit }
internal enum Accessibility { Private, Internal, Protected, Public }
internal enum NativeParameterOwnership { Borrowed, Consumes, Retained, Creates }

internal sealed record GenericConstraintSet(
    bool RequiresClass = false,
    bool RequiresStruct = false,
    bool RequiresUnmanaged = false,
    bool RequiresConstructor = false,
    CType? BaseType = null,
    ImmutableArray<CType> Interfaces = default);

internal sealed class TypeSymbol
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required DeclaredTypeKind Kind { get; init; }
    public TypeDeclarationSyntax? Syntax { get; init; }
    public TypeSymbol? BaseType { get; set; }
    public List<TypeSymbol> Interfaces { get; } = [];
    public ImmutableArray<TypeSymbol> TypeParameters { get; init; } = [];
    public ImmutableDictionary<string, GenericConstraintSet> TypeParameterConstraints { get; set; } = ImmutableDictionary<string, GenericConstraintSet>.Empty;
    public ImmutableArray<CType> TypeArguments { get; init; } = [];
    public TypeSymbol? GenericDefinition { get; init; }
    public bool IsSealed { get; init; }
    public bool IsAbstract { get; init; }
    public Accessibility Accessibility { get; init; }
    public string? NativeTypeName { get; init; }
    public string? NativeHeader { get; init; }
    public AggregateLayoutKind AggregateLayout { get; set; }
    public int? Pack { get; init; }
    public bool HasNonNaturalLayout => AggregateLayout == AggregateLayoutKind.Explicit || Pack is not null;
    public string FullName
    {
        get
        {
            if (Kind == DeclaredTypeKind.TypeParameter)
                return Name;
            var baseName = string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
            if (!TypeArguments.IsDefaultOrEmpty)
                return $"{baseName}<{string.Join(", ", TypeArguments.Select(argument => argument.DisplayName))}>";
            return TypeParameters.IsDefaultOrEmpty ? baseName : $"{baseName}<{string.Join(", ", TypeParameters.Select(parameter => parameter.Name))}>";
        }
    }
    public CType Type => new(FullName == "Esp.Idf.EspError" ? CTypeKind.EspError : Kind switch
    {
        DeclaredTypeKind.Struct => CTypeKind.Struct,
        DeclaredTypeKind.Interface => CTypeKind.Interface,
        DeclaredTypeKind.TypeParameter => CTypeKind.TypeParameter,
        DeclaredTypeKind.Enum => CTypeKind.Enum,
        DeclaredTypeKind.Delegate => CTypeKind.Delegate,
        DeclaredTypeKind.Opaque => CTypeKind.Opaque,
        _ => CTypeKind.Class,
    }, this);
    public List<FieldSymbol> Fields { get; } = [];
    public List<PropertySymbol> Properties { get; } = [];
    public List<MethodSymbol> Methods { get; } = [];
    public List<MethodSymbol> Constructors { get; } = [];
    public List<EnumValueSymbol> EnumValues { get; } = [];
    public CType? DelegateReturnType { get; set; }
    public ImmutableArray<ParameterSymbol> DelegateParameters { get; set; } = [];
    public bool IsStatic => Kind == DeclaredTypeKind.StaticClass;
    public bool IsGenericDefinition => !TypeParameters.IsDefaultOrEmpty && TypeArguments.IsDefaultOrEmpty;
    public bool IsOpenConstructed => !TypeArguments.IsDefaultOrEmpty && TypeArguments.Any(ContainsTypeParameter);
    public bool IsObject => FullName == "System.Object";

    public IEnumerable<TypeSymbol> BaseTypesAndSelf()
    {
        var visited = new HashSet<TypeSymbol>();
        for (var current = this; current is not null && visited.Add(current); current = current.BaseType)
            yield return current;
    }

    public bool DerivesFrom(TypeSymbol other) => BaseTypesAndSelf().Skip(1).Contains(other);

    public bool Implements(TypeSymbol contract) => Interfaces.Any(candidate => candidate == contract || candidate.Implements(contract)) || BaseType?.Implements(contract) == true;

    private static bool ContainsTypeParameter(CType type) => type.Kind == CTypeKind.TypeParameter ||
        type.ElementType is not null && ContainsTypeParameter(type.ElementType) ||
        type.Symbol is { } symbol && !symbol.TypeArguments.IsDefaultOrEmpty && symbol.TypeArguments.Any(ContainsTypeParameter);
}

internal abstract class MemberSymbol
{
    public required string Name { get; init; }
    public required TypeSymbol ContainingType { get; init; }
    public required Accessibility Accessibility { get; init; }
    public required bool IsStatic { get; init; }
    public required SyntaxNode? Syntax { get; init; }
}

internal sealed class FieldSymbol : MemberSymbol
{
    public required CType Type { get; init; }
    public required bool IsReadonly { get; init; }
    public required bool IsConst { get; init; }
    public bool IsVolatile { get; init; }
    public ExpressionSyntax? Initializer { get; init; }
    public int? Offset { get; init; }
    public string? SectionName { get; init; }
    public string? ExternName { get; init; }
    public bool IsNativeVolatile { get; init; }
    public bool IsUsed { get; init; }
    public string CName => IsStatic ? ExternName ?? NameMangler.Member(this) : NameMangler.Identifier(Name);
    public string CAccessPath => !IsStatic && ContainingType.AggregateLayout == AggregateLayoutKind.Explicit
        ? $"ct_layout.ct_slot_{CName}.{CName}"
        : CName;
}

internal sealed class PropertySymbol : MemberSymbol
{
    public required CType Type { get; init; }
    public required AccessorSyntax? Getter { get; init; }
    public required AccessorSyntax? Setter { get; init; }
    public required FieldSymbol? BackingField { get; init; }
    public required Accessibility GetterAccessibility { get; init; }
    public required Accessibility SetterAccessibility { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsOverride { get; init; }
    public bool IsSealedOverride { get; init; }
    public bool IsNoAlloc { get; set; }
    public PropertySymbol? OverriddenProperty { get; set; }
    public List<PropertySymbol> ImplementedInterfaceProperties { get; } = [];
}

internal sealed class ParameterSymbol
{
    public required string Name { get; init; }
    public required CType Type { get; init; }
    public required ParameterSyntax? Syntax { get; init; }
    public ParameterPassingKind PassingKind { get; init; }
    public bool IsRetained { get; init; }
    public NativeParameterOwnership NativeOwnership { get; init; }
    public bool IsNullable { get; init; }
    public bool IsSynchronousCallback { get; init; }
}

internal sealed class MethodSymbol : MemberSymbol
{
    public required CType ReturnType { get; init; }
    public required ImmutableArray<ParameterSymbol> Parameters { get; init; }
    public required BlockStatementSyntax? Body { get; init; }
    public bool IsConstructor { get; init; }
    public bool IsEntryPoint { get; init; }
    public bool IsNoAlloc { get; set; }
    public bool IsUnsafe { get; init; }
    public bool ReturnsBorrowed { get; init; }
    public bool ReturnsOwned { get; init; }
    public bool ReturnsNullable { get; init; }
    public string? ExternName { get; init; }
    public string? ExportName { get; init; }
    public string? SectionName { get; init; }
    public bool IsUsed { get; init; }
    public uint? TaskStackSize { get; init; }
    public bool IsTrustedExtern { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsOverride { get; init; }
    public bool IsSealedOverride { get; init; }
    public bool IsOperator { get; init; }
    public ImmutableArray<TypeSymbol> TypeParameters { get; init; } = [];
    public ImmutableDictionary<string, GenericConstraintSet> TypeParameterConstraints { get; set; } = ImmutableDictionary<string, GenericConstraintSet>.Empty;
    public ImmutableArray<CType> TypeArguments { get; init; } = [];
    public MethodSymbol? GenericDefinition { get; init; }
    public ImmutableDictionary<string, CType> TypeSubstitutions { get; init; } = ImmutableDictionary<string, CType>.Empty;
    public bool IsGenericDefinition => !TypeParameters.IsDefaultOrEmpty && TypeArguments.IsDefaultOrEmpty;
    public SyntaxKind OperatorKind { get; init; }
    public MethodSymbol? OverriddenMethod { get; set; }
    public List<MethodSymbol> ImplementedInterfaceMethods { get; } = [];
    public ConstructorInitializerSyntax? ConstructorInitializer { get; init; }
    public MethodSymbol? ConstructorInitializerTarget { get; set; }
    public string CName => ExternName ?? NameMangler.Method(this);
}

internal sealed record EnumValueSymbol(string Name, BigInteger Value, EnumMemberSyntax Syntax);

internal enum NativeResourceState { None, Borrowed, Owned, Deferred, Moved }

internal sealed class LocalSymbol
{
    public required string Name { get; init; }
    public required CType Type { get; set; }
    public required int Id { get; init; }
    public required SyntaxNode Syntax { get; init; }
    public bool IsReadonly { get; init; }
    public bool IsConst { get; init; }
    public int LoopDepthAtDeclaration { get; init; }
    public bool IsAssigned { get; set; }
    public int AssignmentCount { get; set; }
    public string? ConstantCode { get; set; }
    public object? ConstantValue { get; set; }
    public bool IsKnownNonNull { get; set; }
    public int? KnownLength { get; set; }
    public bool IsDurable { get; init; }
    public NativeResourceState NativeResourceState { get; set; }
    public string StorageName => IsDurable ? $"ct_lp_{Id}" : $"ct_l_{Id}";
    public string CName => IsDurable ? $"ct_state.{StorageName}" : StorageName;
}

internal static class NameMangler
{
    public static string Type(TypeSymbol type) => Compact("ct_t_", TypeIdentity(type));
    public static string Array(CType elementType) => $"ct_a_{TypeCode(elementType)}";
    public static string Member(MemberSymbol member) => Compact("ct_f_", MemberIdentity(member));
    public static string Method(MethodSymbol method)
    {
        var prefix = method.IsOperator ? "ct_o_" : method.IsConstructor ? "ct_c_" : "ct_m_";
        return Compact(prefix, MethodIdentity(method));
    }
    public static string Getter(PropertySymbol property) => Compact("ct_g_", PropertyIdentity(property, true));
    public static string Setter(PropertySymbol property) => Compact("ct_s_", PropertyIdentity(property, false));
    public static string Artifact(string prefix, string identity) => Compact(prefix, identity);
    public static string Identifier(string identifier) => $"u{Encode(identifier)}";

    public static string TypeIdentity(TypeSymbol type) => $"type:{type.FullName}";
    public static string MemberIdentity(MemberSymbol member) => member switch
    {
        FieldSymbol field => $"field:{field.ContainingType.FullName}::{field.Name}:{CanonicalType(field.Type)}",
        PropertySymbol property => $"property:{property.ContainingType.FullName}::{property.Name}:{CanonicalType(property.Type)}",
        MethodSymbol method => MethodIdentity(method),
        _ => $"member:{member.ContainingType.FullName}::{member.Name}",
    };
    public static string PropertyIdentity(PropertySymbol property, bool getter) =>
        $"{(getter ? "getter" : "setter")}:{property.ContainingType.FullName}::{property.Name}:{CanonicalType(property.Type)}";
    public static string MethodIdentity(MethodSymbol method)
    {
        var name = method.IsOperator
            ? OperatorFacts.MetadataName(method.OperatorKind, method.Parameters.Length)
            : method.IsConstructor ? ".ctor" : method.Name;
        if (!method.TypeArguments.IsDefaultOrEmpty)
            name += $"<{string.Join(",", method.TypeArguments.Select(CanonicalType))}>";
        else if (!method.TypeParameters.IsDefaultOrEmpty)
            name += $"`{method.TypeParameters.Length}";
        var parameters = string.Join(",", method.Parameters.Select(parameter => $"{PassingCode(parameter.PassingKind)}:{CanonicalType(parameter.Type)}"));
        return $"method:{method.ContainingType.FullName}::{name}({parameters})->{CanonicalType(method.IsConstructor ? method.ContainingType.Type : method.ReturnType)}";
    }

    public static string CanonicalType(CType type) => type.Kind switch
    {
        CTypeKind.Class or CTypeKind.Struct or CTypeKind.Interface or CTypeKind.TypeParameter or CTypeKind.Enum or CTypeKind.Delegate or CTypeKind.Opaque or CTypeKind.EspError => type.Symbol!.FullName,
        CTypeKind.Array => $"array<{CanonicalType(type.ElementType!)}>",
        CTypeKind.Pointer => $"pointer<{CanonicalType(type.ElementType!)}>",
        CTypeKind.FunctionPointer => $"fn({string.Join(",", type.FunctionPointer!.ParameterTypes.Select((parameter, index) => $"{PassingCode(type.FunctionPointer.PassingKinds[index])}:{CanonicalType(parameter)}"))})->{CanonicalType(type.FunctionPointer.ReturnType)}",
        CTypeKind.NativeBuffer => $"native-buffer<{CanonicalType(type.ElementType!)}>",
        CTypeKind.ReadOnlyNativeBuffer => $"readonly-native-buffer<{CanonicalType(type.ElementType!)}>",
        CTypeKind.NativeUtf8String => "native-utf8",
        _ => type.Kind.ToString().ToLowerInvariant(),
    };

    public static string TypeCode(CType type) => type.Kind switch
    {
        CTypeKind.Void => "v",
        CTypeKind.Bool => "b",
        CTypeKind.Byte => "u8",
        CTypeKind.Sbyte => "i8",
        CTypeKind.Short => "i16",
        CTypeKind.Ushort => "u16",
        CTypeKind.Char => "c8",
        CTypeKind.Int => "i32",
        CTypeKind.Uint => "u32",
        CTypeKind.Long => "i64",
        CTypeKind.Ulong => "u64",
        CTypeKind.Nint => "ni",
        CTypeKind.Nuint => "nu",
        CTypeKind.Float => "f32",
        CTypeKind.String => "str",
        CTypeKind.Class => $"r{Hash96(CanonicalType(type))}",
        CTypeKind.Interface => $"i{Hash96(CanonicalType(type))}",
        CTypeKind.TypeParameter => $"t{Hash96(CanonicalType(type))}",
        CTypeKind.Struct => $"s{Hash96(CanonicalType(type))}",
        CTypeKind.Enum => $"e{Hash96(CanonicalType(type))}",
        CTypeKind.Delegate => $"d{Hash96(CanonicalType(type))}",
        CTypeKind.Opaque => $"o{Hash96(CanonicalType(type))}",
        CTypeKind.EspError => "esperr",
        CTypeKind.Array => $"a{Hash96(CanonicalType(type))}",
        CTypeKind.Pointer => $"p{Hash96(CanonicalType(type))}",
        CTypeKind.FunctionPointer => $"f{Hash96(CanonicalType(type))}",
        CTypeKind.NativeBuffer => $"n{Hash96(CanonicalType(type))}",
        CTypeKind.ReadOnlyNativeBuffer => $"q{Hash96(CanonicalType(type))}",
        CTypeKind.NativeUtf8String => "nu8",
        _ => "err",
    };

    private static string PassingCode(ParameterPassingKind kind) => kind switch
    {
        ParameterPassingKind.Ref => "ref",
        ParameterPassingKind.In => "in",
        ParameterPassingKind.Out => "out",
        _ => string.Empty,
    };

    private static string Compact(string prefix, string identity) => prefix + Hash96(identity);

    private static string Hash96(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).AsSpan(0, 24).ToString().ToLowerInvariant();

    private static string Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = new StringBuilder();
        result.Append('_').Append(bytes.Length).Append('_');
        foreach (var value in bytes)
        {
            if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')
                result.Append((char)value);
            else
                result.Append('_').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}

internal static class TypeFacts
{
    public static CType? BuiltIn(string name) => name switch
    {
        "void" => CType.Void,
        "bool" => CType.Bool,
        "byte" => CType.Byte,
        "sbyte" => CType.Sbyte,
        "short" => CType.Short,
        "ushort" => CType.Ushort,
        "char" => CType.Char,
        "int" => CType.Int,
        "uint" => CType.Uint,
        "long" => CType.Long,
        "ulong" => CType.Ulong,
        "nint" => CType.Nint,
        "nuint" => CType.Nuint,
        "float" => CType.Float,
        "string" => CType.String,
        _ => null,
    };

    public static bool CanImplicitlyConvert(CType from, CType to)
    {
        if (from.IsError || to.IsError || from == to)
            return true;
        if (from.Kind == CTypeKind.Null && to.IsPointerLike)
            return true;
        if (from.Kind == CTypeKind.NativeBuffer && to.Kind == CTypeKind.ReadOnlyNativeBuffer && from.ElementType == to.ElementType)
            return true;
        if (from.Kind == CTypeKind.Pointer && to.Kind == CTypeKind.Pointer && to.ElementType == CType.Void)
            return true;
        if (to.Kind == CTypeKind.Class && to.Symbol?.IsObject == true && from.Kind is not CTypeKind.Void and not CTypeKind.Null and not CTypeKind.Error and not CTypeKind.FunctionPointer and not CTypeKind.NativeBuffer and not CTypeKind.ReadOnlyNativeBuffer and not CTypeKind.Opaque and not CTypeKind.NativeUtf8String)
            return true;
        if (from.Kind == CTypeKind.Class && to.Kind == CTypeKind.Class && from.Symbol is not null && to.Symbol is not null && from.Symbol.DerivesFrom(to.Symbol))
            return true;
        if (to.Kind == CTypeKind.Interface && to.Symbol is not null && from.Symbol is not null && from.Symbol.Implements(to.Symbol))
            return true;
        if (from.Kind == CTypeKind.Interface && to.Kind == CTypeKind.Interface && from.Symbol is not null && to.Symbol is not null && from.Symbol.Implements(to.Symbol))
            return true;
        return from.Kind switch
        {
            CTypeKind.Byte => to.Kind is CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float,
            CTypeKind.Sbyte => to.Kind is CTypeKind.Short or CTypeKind.Int or CTypeKind.Long or CTypeKind.Nint or CTypeKind.Float,
            CTypeKind.Short => to.Kind is CTypeKind.Int or CTypeKind.Long or CTypeKind.Nint or CTypeKind.Float,
            CTypeKind.Ushort or CTypeKind.Char => to.Kind is CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float,
            CTypeKind.Int => to.Kind is CTypeKind.Long or CTypeKind.Nint or CTypeKind.Float,
            CTypeKind.Uint => to.Kind is CTypeKind.Long or CTypeKind.Nuint or CTypeKind.Float,
            CTypeKind.Nint => to.Kind is CTypeKind.Long or CTypeKind.Float,
            CTypeKind.Nuint => to.Kind is CTypeKind.Ulong or CTypeKind.Float,
            CTypeKind.Long or CTypeKind.Ulong => to.Kind == CTypeKind.Float,
            _ => false,
        };
    }

    public static bool CanExplicitlyConvert(CType from, CType to) =>
        CanImplicitlyConvert(from, to) || from.IsNumeric && to.IsNumeric ||
        from.Kind == CTypeKind.Enum && to.IsIntegral || from.IsIntegral && to.Kind == CTypeKind.Enum ||
        from.Kind == CTypeKind.Pointer && to.Kind == CTypeKind.Pointer ||
        from.Kind == CTypeKind.FunctionPointer && to.Kind == CTypeKind.FunctionPointer && from == to ||
        IsExplicitObjectConversion(from, to) || IsExplicitClassConversion(from, to) || IsExplicitInterfaceConversion(from, to);

    private static bool IsExplicitObjectConversion(CType from, CType to) =>
        from.Kind == CTypeKind.Class && from.Symbol?.IsObject == true && to.Kind is not CTypeKind.Void and not CTypeKind.Null and not CTypeKind.Error and not CTypeKind.NativeBuffer and not CTypeKind.ReadOnlyNativeBuffer and not CTypeKind.Opaque and not CTypeKind.NativeUtf8String;

    private static bool IsExplicitClassConversion(CType from, CType to) =>
        from.Kind == CTypeKind.Class && to.Kind == CTypeKind.Class && from.Symbol is not null && to.Symbol is not null &&
        (from.Symbol.DerivesFrom(to.Symbol) || to.Symbol.DerivesFrom(from.Symbol));

    private static bool IsExplicitInterfaceConversion(CType from, CType to) =>
        from.Kind == CTypeKind.Interface && (to.IsReference || to.IsValueType) ||
        to.Kind == CTypeKind.Interface && (from.IsReference || from.IsValueType);

    public static CType PromoteNumeric(CType left, CType right)
    {
        if (left.Kind == CTypeKind.Float || right.Kind == CTypeKind.Float)
            return CType.Float;
        if (left.Kind == CTypeKind.Nint || right.Kind == CTypeKind.Nint)
        {
            var other = left.Kind == CTypeKind.Nint ? right : left;
            if (other.Kind == CTypeKind.Long)
                return CType.Long;
            return other.Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Nint
                ? CType.Nint : CType.Error;
        }
        if (left.Kind == CTypeKind.Nuint || right.Kind == CTypeKind.Nuint)
        {
            var other = left.Kind == CTypeKind.Nuint ? right : left;
            if (other.Kind == CTypeKind.Ulong)
                return CType.Ulong;
            return other.Kind is CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Uint or CTypeKind.Nuint
                ? CType.Nuint : CType.Error;
        }
        if (left.Kind == CTypeKind.Ulong || right.Kind == CTypeKind.Ulong)
        {
            var other = left.Kind == CTypeKind.Ulong ? right : left;
            return IsSignedIntegral(other) ? CType.Error : CType.Ulong;
        }
        if (left.Kind == CTypeKind.Long || right.Kind == CTypeKind.Long ||
            left.Kind == CTypeKind.Uint && IsSignedIntegral(right) ||
            right.Kind == CTypeKind.Uint && IsSignedIntegral(left))
            return CType.Long;
        if (left.Kind == CTypeKind.Uint || right.Kind == CTypeKind.Uint)
            return CType.Uint;
        return CType.Int;
    }

    private static bool IsSignedIntegral(CType type) => type.Kind is CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int or CTypeKind.Long;
}

internal static class OperatorFacts
{
    public static bool IsSupported(SyntaxKind kind) => kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken;

    public static string Text(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        _ => "?",
    };

    public static string MetadataName(SyntaxKind kind, int arity) => (kind, arity) switch
    {
        (SyntaxKind.PlusToken, 1) => "UnaryPlus",
        (SyntaxKind.MinusToken, 1) => "UnaryNegation",
        (SyntaxKind.PlusToken, 2) => "Addition",
        (SyntaxKind.MinusToken, 2) => "Subtraction",
        (SyntaxKind.StarToken, 2) => "Multiplication",
        (SyntaxKind.SlashToken, 2) => "Division",
        _ => "Invalid",
    };

    public static string DisplayName(SyntaxKind kind) => $"operator {Text(kind)}";
}
