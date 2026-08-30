using System.Collections.Immutable;

namespace CTilde;

internal enum SimdLaneKind
{
    Float32,
    Int32,
    UInt32,
    Mask32,
}

internal enum SimdOperationKind
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Abs,
    Minimum,
    Maximum,
    Sqrt,
    MultiplyAdd,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    BitwiseNot,
    BitwiseAndNot,
    CompareEqual,
    CompareNotEqual,
    CompareLessThan,
    CompareLessThanOrEqual,
    CompareGreaterThan,
    CompareGreaterThanOrEqual,
    Select,
    ShiftLeft,
    ShiftRight,
    ConvertInt32ToFloat,
    ConvertUInt32ToFloat,
    MaskAny,
    MaskAll,
    MaskNone,
    MaskMove,
    Splat,
    Create,
}

/// <summary>A target-independent SIMD operation selected after semantic binding.</summary>
internal readonly record struct SimdOperation(
    SimdOperationKind Kind,
    SimdLaneKind LaneKind,
    int LaneWidth,
    int LaneCount,
    int InputCount,
    ImmutableArray<int> ConstantImmediates)
{
    private const EffectContract PureEffects = EffectContract.NoAlloc | EffectContract.NoThrow |
        EffectContract.NoBlock | EffectContract.NoRuntime;

    public static bool IsFusionValue(CType type) =>
        type.Symbol is { Namespace: "System.Simd" } symbol &&
        symbol.Name is "F32x4" or "I32x4" or "U32x4" or "Mask32x4" or "Vec3x4";

    public static bool IsPureFusionKernel(MethodSymbol method) =>
        !method.IsVirtual && !method.IsUnsafe &&
        (method.DeclaredEffects & PureEffects) == PureEffects &&
        method.ContainingType.Namespace == "System.Simd" &&
        (IsFusionValue(method.ReturnType) || method.ReturnType == CType.Bool || method.ReturnType == CType.Uint) &&
        method.Parameters.All(parameter => parameter.PassingKind == ParameterPassingKind.Value &&
            !parameter.Type.ContainsManagedReferences);

    public static bool TryClassify(MethodSymbol method, out SimdOperation operation)
    {
        operation = default;
        if (method.ContainingType.Namespace != "System.Simd")
            return false;

        var laneKind = method.ContainingType.Name switch
        {
            "F32x4" => SimdLaneKind.Float32,
            "I32x4" => SimdLaneKind.Int32,
            "U32x4" => SimdLaneKind.UInt32,
            "Mask32x4" => SimdLaneKind.Mask32,
            _ => (SimdLaneKind?)null,
        };
        if (laneKind is null)
            return false;

        SimdOperationKind? kind = null;
        if (method.IsOperator)
            kind = method.OperatorKind switch
            {
                SyntaxKind.PlusToken => SimdOperationKind.Add,
                SyntaxKind.MinusToken when method.Parameters.Length == 2 => SimdOperationKind.Subtract,
                SyntaxKind.StarToken => SimdOperationKind.Multiply,
                SyntaxKind.SlashToken => SimdOperationKind.Divide,
                _ => null,
            };
        else
            kind = method.Name switch
            {
                "Abs" => SimdOperationKind.Abs,
                "Min" => SimdOperationKind.Minimum,
                "Max" => SimdOperationKind.Maximum,
                "Sqrt" => SimdOperationKind.Sqrt,
                "MultiplyAdd" => SimdOperationKind.MultiplyAdd,
                "And" => SimdOperationKind.BitwiseAnd,
                "Or" => SimdOperationKind.BitwiseOr,
                "Xor" => SimdOperationKind.BitwiseXor,
                "Not" => SimdOperationKind.BitwiseNot,
                "AndNot" => SimdOperationKind.BitwiseAndNot,
                "CompareEqual" => SimdOperationKind.CompareEqual,
                "CompareNotEqual" => SimdOperationKind.CompareNotEqual,
                "CompareLessThan" => SimdOperationKind.CompareLessThan,
                "CompareLessThanOrEqual" => SimdOperationKind.CompareLessThanOrEqual,
                "CompareGreaterThan" => SimdOperationKind.CompareGreaterThan,
                "CompareGreaterThanOrEqual" => SimdOperationKind.CompareGreaterThanOrEqual,
                "Select" => SimdOperationKind.Select,
                "ShiftLeft" => SimdOperationKind.ShiftLeft,
                "ShiftRight" => SimdOperationKind.ShiftRight,
                "FromI32" when laneKind == SimdLaneKind.Float32 => SimdOperationKind.ConvertInt32ToFloat,
                "FromU32" when laneKind == SimdLaneKind.Float32 => SimdOperationKind.ConvertUInt32ToFloat,
                "Any" when laneKind == SimdLaneKind.Mask32 => SimdOperationKind.MaskAny,
                "All" when laneKind == SimdLaneKind.Mask32 => SimdOperationKind.MaskAll,
                "None" when laneKind == SimdLaneKind.Mask32 => SimdOperationKind.MaskNone,
                "MoveMask" when laneKind == SimdLaneKind.Mask32 => SimdOperationKind.MaskMove,
                "Splat" => SimdOperationKind.Splat,
                "Create" => SimdOperationKind.Create,
                _ => null,
            };
        if (kind is null)
            return false;

        var immediates = method.TypeArguments
            .Where(argument => argument.Kind == CTypeKind.Constant && argument.ConstantValue is not null)
            .Select(argument => checked((int)argument.ConstantValue!.Value)).ToImmutableArray();
        operation = new SimdOperation(kind.Value, laneKind.Value, 32, 4, method.Parameters.Length, immediates);
        return true;
    }
}

internal static class SimdBackendTable
{
    public static string? Intrinsic(CompilationArchitecture architecture, SimdOperation operation) =>
        (architecture, operation.LaneKind, operation.Kind) switch
        {
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Float32, SimdOperationKind.Add) => "_mm_add_ps",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Float32, SimdOperationKind.Subtract) => "_mm_sub_ps",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Float32, SimdOperationKind.Multiply) => "_mm_mul_ps",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Float32, SimdOperationKind.Divide) => "_mm_div_ps",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Float32, SimdOperationKind.Sqrt) => "_mm_sqrt_ps",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Int32 or SimdLaneKind.UInt32, SimdOperationKind.Add) => "_mm_add_epi32",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Int32 or SimdLaneKind.UInt32, SimdOperationKind.Subtract) => "_mm_sub_epi32",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseAnd) => "_mm_and_si128",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseOr) => "_mm_or_si128",
            (CompilationArchitecture.X86 or CompilationArchitecture.X64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseXor) => "_mm_xor_si128",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Float32, SimdOperationKind.Add) => "vaddq_f32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Float32, SimdOperationKind.Subtract) => "vsubq_f32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Float32, SimdOperationKind.Multiply) => "vmulq_f32",
            (CompilationArchitecture.Arm64, SimdLaneKind.Float32, SimdOperationKind.Sqrt) => "vsqrtq_f32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Float32, SimdOperationKind.Abs) => "vabsq_f32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Int32 or SimdLaneKind.UInt32, SimdOperationKind.Add) => "vaddq_u32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Int32 or SimdLaneKind.UInt32, SimdOperationKind.Subtract) => "vsubq_u32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseAnd) => "vandq_u32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseOr) => "vorrq_u32",
            (CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64, SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32, SimdOperationKind.BitwiseXor) => "veorq_u32",
            _ => null,
        };
}
