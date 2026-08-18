using System.Collections.Immutable;
using System.Text;

namespace CTilde;

internal enum CTypeKind
{
    Error, Void, Bool, Byte, Sbyte, Short, Ushort, Char, Int, Uint, Float, String,
    Class, Struct, Enum, Array, Pointer, Null,
}

internal sealed record CType(CTypeKind Kind, TypeSymbol? Symbol = null, CType? ElementType = null)
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
    public static readonly CType Float = new(CTypeKind.Float);
    public static readonly CType String = new(CTypeKind.String);
    public static readonly CType Null = new(CTypeKind.Null);

    public bool IsError => Kind == CTypeKind.Error;
    public bool IsNumeric => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float;
    public bool IsIntegral => Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Enum;
    public bool IsReference => Kind is CTypeKind.Class or CTypeKind.Array or CTypeKind.String;
    public bool IsPointerLike => IsReference || Kind == CTypeKind.Pointer;
    public bool IsValueType => Kind is CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float or CTypeKind.Struct or CTypeKind.Enum;

    public string DisplayName => Kind switch
    {
        CTypeKind.Class or CTypeKind.Struct or CTypeKind.Enum => Symbol!.FullName,
        CTypeKind.Array => $"{ElementType!.DisplayName}[]",
        CTypeKind.Pointer => $"{ElementType!.DisplayName}*",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

internal enum DeclaredTypeKind { Class, Struct, Enum, StaticClass }
internal enum Accessibility { Private, Internal, Protected, Public }

internal sealed class TypeSymbol
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required DeclaredTypeKind Kind { get; init; }
    public TypeDeclarationSyntax? Syntax { get; init; }
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    public CType Type => new(Kind switch
    {
        DeclaredTypeKind.Struct => CTypeKind.Struct,
        DeclaredTypeKind.Enum => CTypeKind.Enum,
        _ => CTypeKind.Class,
    }, this);
    public List<FieldSymbol> Fields { get; } = [];
    public List<PropertySymbol> Properties { get; } = [];
    public List<MethodSymbol> Methods { get; } = [];
    public List<MethodSymbol> Constructors { get; } = [];
    public List<EnumValueSymbol> EnumValues { get; } = [];
    public bool IsStatic => Kind == DeclaredTypeKind.StaticClass;
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
}

internal sealed class ParameterSymbol
{
    public required string Name { get; init; }
    public required CType Type { get; init; }
    public required ParameterSyntax? Syntax { get; init; }
}

internal sealed class MethodSymbol : MemberSymbol
{
    public required CType ReturnType { get; init; }
    public required ImmutableArray<ParameterSymbol> Parameters { get; init; }
    public required BlockStatementSyntax? Body { get; init; }
    public bool IsConstructor { get; init; }
    public bool IsEntryPoint { get; init; }
    public string? ExternName { get; init; }
    public string CName => ExternName ?? NameMangler.Method(this);
}

internal sealed record EnumValueSymbol(string Name, long Value, EnumMemberSyntax Syntax);

internal sealed class LocalSymbol
{
    public required string Name { get; init; }
    public required CType Type { get; set; }
    public required int Id { get; init; }
    public required SyntaxNode Syntax { get; init; }
    public bool IsReadonly { get; init; }
    public bool IsConst { get; init; }
    public bool IsAssigned { get; set; }
    public int AssignmentCount { get; set; }
    public string? ConstantCode { get; set; }
    public object? ConstantValue { get; set; }
    public string CName => $"ct_l_{Id}";
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
        CTypeKind.Void => "v", CTypeKind.Bool => "b", CTypeKind.Byte => "u8", CTypeKind.Sbyte => "i8",
        CTypeKind.Short => "i16", CTypeKind.Ushort => "u16", CTypeKind.Char => "c8", CTypeKind.Int => "i32",
        CTypeKind.Uint => "u32", CTypeKind.Float => "f32", CTypeKind.String => "str",
        CTypeKind.Class => $"r{Encode(type.Symbol!.FullName)}", CTypeKind.Struct => $"s{Encode(type.Symbol!.FullName)}",
        CTypeKind.Enum => $"e{Encode(type.Symbol!.FullName)}", CTypeKind.Array => $"a{TypeCode(type.ElementType!)}",
        CTypeKind.Pointer => $"p{TypeCode(type.ElementType!)}", _ => "err",
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
        "void" => CType.Void, "bool" => CType.Bool, "byte" => CType.Byte, "sbyte" => CType.Sbyte,
        "short" => CType.Short, "ushort" => CType.Ushort, "char" => CType.Char, "int" => CType.Int,
        "uint" => CType.Uint, "float" => CType.Float, "string" => CType.String, _ => null,
    };

    public static bool CanImplicitlyConvert(CType from, CType to)
    {
        if (from.IsError || to.IsError || from == to)
            return true;
        if (from.Kind == CTypeKind.Null && to.IsPointerLike)
            return true;
        return from.Kind switch
        {
            CTypeKind.Byte => to.Kind is CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float,
            CTypeKind.Sbyte => to.Kind is CTypeKind.Short or CTypeKind.Int or CTypeKind.Float,
            CTypeKind.Short => to.Kind is CTypeKind.Int or CTypeKind.Float,
            CTypeKind.Ushort or CTypeKind.Char => to.Kind is CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float,
            CTypeKind.Int or CTypeKind.Uint => to.Kind == CTypeKind.Float,
            _ => false,
        };
    }

    public static bool CanExplicitlyConvert(CType from, CType to) =>
        CanImplicitlyConvert(from, to) || from.IsNumeric && to.IsNumeric || from.Kind == CTypeKind.Enum && to.IsIntegral || from.IsIntegral && to.Kind == CTypeKind.Enum || from.IsPointerLike && to.IsPointerLike;

    public static int ImplicitConversionScore(CType from, CType to)
    {
        if (from == to)
            return 0;
        if (!CanImplicitlyConvert(from, to))
            return 100;
        return (from.Kind, to.Kind) switch
        {
            (CTypeKind.Byte, CTypeKind.Short or CTypeKind.Ushort) => 1,
            (CTypeKind.Byte, CTypeKind.Int) => 2,
            (CTypeKind.Byte, CTypeKind.Uint) => 3,
            (CTypeKind.Byte, CTypeKind.Float) => 4,
            (CTypeKind.Sbyte, CTypeKind.Short) => 1,
            (CTypeKind.Sbyte, CTypeKind.Int) => 2,
            (CTypeKind.Sbyte, CTypeKind.Float) => 3,
            (CTypeKind.Short, CTypeKind.Int) => 1,
            (CTypeKind.Short, CTypeKind.Float) => 2,
            (CTypeKind.Ushort or CTypeKind.Char, CTypeKind.Int) => 1,
            (CTypeKind.Ushort or CTypeKind.Char, CTypeKind.Uint) => 2,
            (CTypeKind.Ushort or CTypeKind.Char, CTypeKind.Float) => 3,
            (CTypeKind.Int or CTypeKind.Uint, CTypeKind.Float) => 1,
            _ => 1,
        };
    }

    public static CType PromoteNumeric(CType left, CType right)
    {
        if (left.Kind == CTypeKind.Float || right.Kind == CTypeKind.Float)
            return CType.Float;
        if (left.Kind == CTypeKind.Uint || right.Kind == CTypeKind.Uint)
            return CType.Uint;
        return CType.Int;
    }
}
