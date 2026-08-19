using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace CTilde;

internal enum CTypeKind
{
    Error, Void, Bool, Byte, Sbyte, Short, Ushort, Char, Int, Uint, Long, Ulong, Float, String,
    Class, Struct, Enum, Delegate, Array, Pointer, FunctionPointer, Null,
}

internal sealed class FunctionPointerSignature(ImmutableArray<CType> parameterTypes, CType returnType) : IEquatable<FunctionPointerSignature>
{
    public ImmutableArray<CType> ParameterTypes { get; } = parameterTypes;
    public CType ReturnType { get; } = returnType;
    public bool Equals(FunctionPointerSignature? other) => other is not null && ReturnType == other.ReturnType && ParameterTypes.SequenceEqual(other.ParameterTypes);
    public override bool Equals(object? obj) => obj is FunctionPointerSignature other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReturnType);
        foreach (var parameter in ParameterTypes)
            hash.Add(parameter);
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
    public static readonly CType Float = new(CTypeKind.Float);
    public static readonly CType String = new(CTypeKind.String);
    public static readonly CType Null = new(CTypeKind.Null);

    public bool IsError => Kind == CTypeKind.Error;
    public bool IsNumeric => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float;
    public bool IsIntegral => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Enum;
    public bool IsReference => Kind is CTypeKind.Class or CTypeKind.Delegate or CTypeKind.Array or CTypeKind.String;
    public bool ContainsManagedReferences => ContainsManagedReferencesCore(this, []);
    public bool IsPointerLike => IsReference || Kind is CTypeKind.Pointer or CTypeKind.FunctionPointer;
    public bool ContainsPointer => ContainsPointerCore(this, []);
    public bool IsValueType => Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float or CTypeKind.Struct or CTypeKind.Enum;

    public string DisplayName => Kind switch
    {
        CTypeKind.Class or CTypeKind.Struct or CTypeKind.Enum or CTypeKind.Delegate => Symbol!.FullName,
        CTypeKind.Array => $"{ElementType!.DisplayName}[]",
        CTypeKind.Pointer => $"{ElementType!.DisplayName}*",
        CTypeKind.FunctionPointer => $"delegate* unmanaged<{string.Join(", ", FunctionPointer!.ParameterTypes.Select(type => type.DisplayName).Append(FunctionPointer.ReturnType.DisplayName))}>",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    private static bool ContainsPointerCore(CType type, HashSet<TypeSymbol> visited)
    {
        if (type.Kind is CTypeKind.Pointer or CTypeKind.FunctionPointer)
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
        if (type.IsReference)
            return true;
        if (type.Kind != CTypeKind.Struct || type.Symbol is null || !visited.Add(type.Symbol))
            return false;
        return type.Symbol.Fields.Any(field => !field.IsStatic && ContainsManagedReferencesCore(field.Type, visited));
    }
}

internal enum DeclaredTypeKind { Class, Struct, Enum, Delegate, StaticClass }
internal enum Accessibility { Private, Internal, Protected, Public }

internal sealed class TypeSymbol
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required DeclaredTypeKind Kind { get; init; }
    public TypeDeclarationSyntax? Syntax { get; init; }
    public TypeSymbol? BaseType { get; set; }
    public bool IsSealed { get; init; }
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    public CType Type => new(Kind switch
    {
        DeclaredTypeKind.Struct => CTypeKind.Struct,
        DeclaredTypeKind.Enum => CTypeKind.Enum,
        DeclaredTypeKind.Delegate => CTypeKind.Delegate,
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
    public bool IsObject => FullName == "System.Object";

    public IEnumerable<TypeSymbol> BaseTypesAndSelf()
    {
        var visited = new HashSet<TypeSymbol>();
        for (var current = this; current is not null && visited.Add(current); current = current.BaseType)
            yield return current;
    }

    public bool DerivesFrom(TypeSymbol other) => BaseTypesAndSelf().Skip(1).Contains(other);
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
    public ExpressionSyntax? Initializer { get; init; }
    public string CName => IsStatic ? NameMangler.Member(this) : NameMangler.Identifier(Name);
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
    public bool IsOverride { get; init; }
    public bool IsSealedOverride { get; init; }
    public bool IsNoAlloc { get; set; }
    public PropertySymbol? OverriddenProperty { get; set; }
}

internal sealed class ParameterSymbol
{
    public required string Name { get; init; }
    public required CType Type { get; init; }
    public required ParameterSyntax? Syntax { get; init; }
    public bool IsRetained { get; init; }
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
    public string? ExternName { get; init; }
    public bool IsTrustedExtern { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsSealedOverride { get; init; }
    public MethodSymbol? OverriddenMethod { get; set; }
    public ConstructorInitializerSyntax? ConstructorInitializer { get; init; }
    public MethodSymbol? ConstructorInitializerTarget { get; set; }
    public string CName => ExternName ?? NameMangler.Method(this);
}

internal sealed record EnumValueSymbol(string Name, BigInteger Value, EnumMemberSyntax Syntax);

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
    public bool IsDurable { get; init; }
    public string StorageName => IsDurable ? $"ct_lp_{Id}" : $"ct_l_{Id}";
    public string CName => IsDurable ? $"ct_state.{StorageName}" : StorageName;
}

internal static class NameMangler
{
    public static string Type(TypeSymbol type) => $"ct_t{Encode(type.Namespace)}{Encode(type.Name)}";
    public static string Array(CType elementType) => $"ct_a_{TypeCode(elementType)}";
    public static string Member(MemberSymbol member) => $"ct_f_{Encode(member.ContainingType.FullName)}{Encode(member.Name)}";
    public static string Method(MethodSymbol method)
    {
        var parameters = string.Concat(method.Parameters.Select(parameter => $"_{TypeCode(parameter.Type)}"));
        var prefix = method.IsConstructor ? "ct_ctor_" : "ct_m_";
        return $"{prefix}{Encode(method.ContainingType.FullName)}{Encode(method.Name)}{parameters}";
    }
    public static string Getter(PropertySymbol property) => $"ct_get_{Encode(property.ContainingType.FullName)}{Encode(property.Name)}";
    public static string Setter(PropertySymbol property) => $"ct_set_{Encode(property.ContainingType.FullName)}{Encode(property.Name)}";
    public static string Identifier(string identifier) => $"u{Encode(identifier)}";

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
        CTypeKind.Float => "f32",
        CTypeKind.String => "str",
        CTypeKind.Class => $"r{Encode(type.Symbol!.FullName)}",
        CTypeKind.Struct => $"s{Encode(type.Symbol!.FullName)}",
        CTypeKind.Enum => $"e{Encode(type.Symbol!.FullName)}",
        CTypeKind.Delegate => $"d{Encode(type.Symbol!.FullName)}",
        CTypeKind.Array => $"a{TypeCode(type.ElementType!)}",
        CTypeKind.Pointer => $"p{TypeCode(type.ElementType!)}",
        CTypeKind.FunctionPointer => $"fp{string.Concat(type.FunctionPointer!.ParameterTypes.Select(parameter => "_" + TypeCode(parameter)))}_r{TypeCode(type.FunctionPointer.ReturnType)}",
        _ => "err",
    };

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
        if (to.Kind == CTypeKind.Class && to.Symbol?.IsObject == true && from.Kind is not CTypeKind.Void and not CTypeKind.Null and not CTypeKind.Error and not CTypeKind.FunctionPointer)
            return true;
        if (from.Kind == CTypeKind.Class && to.Kind == CTypeKind.Class && from.Symbol is not null && to.Symbol is not null && from.Symbol.DerivesFrom(to.Symbol))
            return true;
        return from.Kind switch
        {
            CTypeKind.Byte => to.Kind is CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float,
            CTypeKind.Sbyte => to.Kind is CTypeKind.Short or CTypeKind.Int or CTypeKind.Long or CTypeKind.Float,
            CTypeKind.Short => to.Kind is CTypeKind.Int or CTypeKind.Long or CTypeKind.Float,
            CTypeKind.Ushort or CTypeKind.Char => to.Kind is CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float,
            CTypeKind.Int => to.Kind is CTypeKind.Long or CTypeKind.Float,
            CTypeKind.Uint => to.Kind is CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float,
            CTypeKind.Long or CTypeKind.Ulong => to.Kind == CTypeKind.Float,
            _ => false,
        };
    }

    public static bool CanExplicitlyConvert(CType from, CType to) =>
        CanImplicitlyConvert(from, to) || from.IsNumeric && to.IsNumeric ||
        from.Kind == CTypeKind.Enum && to.IsIntegral || from.IsIntegral && to.Kind == CTypeKind.Enum ||
        from.Kind == CTypeKind.Pointer && to.Kind == CTypeKind.Pointer ||
        from.Kind == CTypeKind.FunctionPointer && to.Kind == CTypeKind.FunctionPointer && from == to ||
        IsExplicitObjectConversion(from, to) || IsExplicitClassConversion(from, to);

    private static bool IsExplicitObjectConversion(CType from, CType to) =>
        from.Kind == CTypeKind.Class && from.Symbol?.IsObject == true && to.Kind is not CTypeKind.Void and not CTypeKind.Null and not CTypeKind.Error;

    private static bool IsExplicitClassConversion(CType from, CType to) =>
        from.Kind == CTypeKind.Class && to.Kind == CTypeKind.Class && from.Symbol is not null && to.Symbol is not null &&
        (from.Symbol.DerivesFrom(to.Symbol) || to.Symbol.DerivesFrom(from.Symbol));

    public static CType PromoteNumeric(CType left, CType right)
    {
        if (left.Kind == CTypeKind.Float || right.Kind == CTypeKind.Float)
            return CType.Float;
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
