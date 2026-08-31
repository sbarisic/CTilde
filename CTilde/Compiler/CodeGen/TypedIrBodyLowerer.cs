using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed class IrExpressionValue
{
    public required CType Type { get; init; }
    public required string Code { get; init; }
    public List<string> Prelude { get; init; } = [];
    public IrValueStorage? LValue { get; init; }
    public TypeSymbol? TypeReceiver { get; init; }
    public bool IsConstant { get; init; }
    public object? ConstantValue { get; init; }
    public bool IsBaseReceiver { get; init; }
    public OwnershipKind Ownership { get; init; }
    public MethodGroupBinding? MethodGroup { get; init; }
    public LambdaExpressionSyntax? Lambda { get; init; }
    public bool IsFunctionAddress { get; init; }
    public object? Symbol { get; init; }
    public bool IsConstInitStorage { get; init; }
    public bool IsKnownNonNull { get; set; }
    public int? KnownLength { get; set; }
    public string? OwnedCleanupRecord { get; set; }
}

internal sealed record LayoutConstantValue;

internal sealed record MethodGroupBinding(ImmutableArray<MethodSymbol> Candidates, IrExpressionValue? Receiver, bool IsBaseReceiver);

internal enum OwnershipKind { None, Borrowed, Owned, Immortal }

internal sealed class IrValueStorage
{
    public required Func<string, string> Store { get; init; }
    public string? Address { get; init; }
    public LocalSymbol? Local { get; init; }
    public FieldSymbol? Field { get; init; }
    public PropertySymbol? Property { get; init; }
    public ParameterSymbol? Parameter { get; init; }
    public bool IsBaseReceiver { get; init; }
    public bool IsConstInitStorage { get; init; }
    public bool UsesVirtualDispatch { get; init; }
}

internal sealed partial class TypedIrBodyLowerer
{
    private readonly ILoweringServices _emitter;
    private readonly CompilationModel _model;
    private readonly DiagnosticBag _diagnostics;
    private readonly MethodSymbol _method;
    private readonly string? _nameOverride;
    private readonly PropertySymbol? _property;
    private readonly bool _isGetter;
    private readonly string _temporaryPrefix;
    private readonly Stack<Dictionary<string, LocalSymbol>> _scopes = [];
    private readonly Stack<int> _debugScopeEnds = [];
    private readonly Stack<string?> _cleanupBoundaries = [];
    private readonly Stack<string?> _breakCleanupBoundaries = [];
    private readonly Stack<string?> _continueCleanupBoundaries = [];
    private readonly HashSet<string> _cleanupRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterSymbol> _parameters;
    private readonly Dictionary<ParameterSymbol, string> _durableParameters = [];
    private readonly Dictionary<string, CType> _durableSlots = new(StringComparer.Ordinal);
    private readonly Stack<string> _breakLabels = [];
    private readonly Stack<string> _continueLabels = [];
    private readonly Stack<List<AssignmentSnapshot>> _breakAssignmentStates = [];
    private readonly Stack<List<AssignmentSnapshot>> _continueAssignmentStates = [];
    private readonly Stack<string> _catchExceptions = [];
    private readonly List<ActiveHandler> _activeExceptionFrames = [];
    private readonly Stack<FinallyContext> _finallyContexts = [];
    private readonly Stack<(int BreakDepth, int ContinueDepth)> _finallyBarriers = [];
    private readonly Dictionary<DeferStatementSyntax, IrExpressionValue> _deferredCalls = [];
    private readonly List<DirectDeferThunk> _directDefers = [];
    private readonly HashSet<MethodSymbol> _deferTargets = [];
    private readonly HashSet<FieldSymbol> _assignedFields = [];
    private readonly Dictionary<FieldSymbol, int> _fieldAssignmentCounts = [];
    private readonly HashSet<ParameterSymbol> _assignedOutParameters = [];
    private readonly HashSet<FieldSymbol> _constantFieldsBeingEvaluated = [];
    private readonly Dictionary<SyntaxNode, BoundSemanticEntry> _semanticEntries = [];
    private int _localId;
    private int _tempId;
    private int _labelId;
    private int _unsafeDepth;
    private int _repeatableLoopDepth;
    private int _tryId;
    private int _deferId;
    private int _lockId;
    private int _cleanupId;
    private readonly int _tryCount;
    private readonly int _externUseStart;
    private readonly bool _analysisOnly;
    private readonly ImmutableDictionary<SyntaxNode, BoundSemanticEntry>? _semanticHints;
    private readonly IrOptimizationFacts _optimizationFacts;
    private bool _capturingDirectDefer;
    private readonly bool _isIteratorMethod;
    private readonly CType? _iteratorElementType;
    private readonly CType? _iteratorListType;
    private readonly CType? _iteratorEnumerableType;
    private const string IteratorBuilderName = "ct_iterator_values";
    private bool EmitDebugInformation => _emitter.EmitDebugInformation && !_method.IsInterruptCode;
    private bool EmitDebugInstrumentation => _emitter.EmitDebugInstrumentation && !_method.IsInterruptCode;

    private CType ResolveType(TypeSyntax syntax) => _model.ResolveType(syntax, TreeFor(syntax), _method.TypeSubstitutions);

    private bool RequiresVirtualDispatch(MethodSymbol method, IrExpressionValue? receiver, bool isBaseReceiver = false)
    {
        if (!method.IsVirtual || isBaseReceiver || method.IsSealedOverride)
            return false;
        var receiverType = receiver?.Type.Symbol ?? (!_method.IsStatic ? _method.ContainingType : null);
        return receiverType?.Kind != DeclaredTypeKind.Class || !receiverType.IsSealed;
    }

    private bool RequiresVirtualDispatch(PropertySymbol property, IrExpressionValue? receiver, bool isBaseReceiver = false)
    {
        if (!property.IsVirtual || isBaseReceiver || property.IsSealedOverride)
            return false;
        var receiverType = receiver?.Type.Symbol ?? (!_method.IsStatic ? _method.ContainingType : null);
        return receiverType?.Kind != DeclaredTypeKind.Class || !receiverType.IsSealed;
    }

    private static bool RequiresVirtualDispatch(MethodSymbol method, CType receiverType) =>
        method.IsVirtual && !method.IsSealedOverride &&
        (receiverType.Kind != CTypeKind.Class || receiverType.Symbol?.IsSealed != true);

    private static bool RequiresVirtualDispatch(PropertySymbol property, CType receiverType) =>
        property.IsVirtual && !property.IsSealedOverride &&
        (receiverType.Kind != CTypeKind.Class || receiverType.Symbol?.IsSealed != true);

    public TypedIrBodyLowerer(ILoweringServices emitter, MethodSymbol method, string? nameOverride = null, PropertySymbol? property = null, bool isGetter = false, string temporaryPrefix = "", bool analysisOnly = false, ImmutableDictionary<SyntaxNode, BoundSemanticEntry>? semanticHints = null, IrOptimizationFacts? optimizationFacts = null)
    {
        _emitter = emitter;
        _model = emitter.Model;
        _diagnostics = emitter.Diagnostics;
        _method = method;
        _nameOverride = nameOverride;
        _property = property;
        _isGetter = isGetter;
        _temporaryPrefix = temporaryPrefix;
        _analysisOnly = analysisOnly;
        _semanticHints = semanticHints;
        _optimizationFacts = optimizationFacts ?? IrOptimizationFacts.Empty;
        _isIteratorMethod = ContainsYield(method.Body);
        if (_isIteratorMethod)
        {
            ValidateYieldPlacement(method.Body!, method.IsConstructor || method.IsOperator || property is not null);
            if (method.ReturnType.Kind != CTypeKind.Interface || method.ReturnType.Symbol?.GenericDefinition?.FullName != "System.IEnumerable<T>" || method.ReturnType.Symbol.TypeArguments.Length != 1)
                _diagnostics.Add("CT2212", "An iterator method must return System.IEnumerable<T>.", method.Syntax!.Source, method.Syntax.Span);
            else
            {
                _iteratorElementType = method.ReturnType.Symbol.TypeArguments[0];
                _iteratorListType = _model.ConstructStandardGeneric("System.Collections.List", [_iteratorElementType], method.Syntax!);
                _iteratorEnumerableType = _model.ConstructStandardGeneric("System.IteratorEnumerable", [_iteratorElementType], method.Syntax!);
            }
        }
        _parameters = method.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        _unsafeDepth = HasModifier(method.Syntax, "unsafe") ? 1 : 0;
        _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
        _debugScopeEnds.Push(method.Body?.Span.End ?? method.Syntax?.Span.End ?? int.MaxValue);
        _cleanupBoundaries.Push("ct_cleanup_method");
        _externUseStart = _emitter.ExternUses.Count();
        _tryCount = CountTryStatements(method.Body) + (_analysisOnly ? CountDeferStatements(method.Body) : 0);
        if (EmitDebugInstrumentation && !_analysisOnly)
            _cleanupRecords.Add("ct_cleanup_debug_frame");
        if (_tryCount != 0)
        {
            for (var index = 0; index < method.Parameters.Length; index++)
                if (method.Parameters[index].PassingKind == ParameterPassingKind.Value && RequiresDurableStorage(method.Parameters[index].Name, -1))
                    _durableParameters[method.Parameters[index]] = $"ct_pp_{index}";
        }
        if (_tryCount != 0 || ContainsThrow(method.Body))
            _emitter.RegisterExceptions();
    }

    public string EmitDefinition()
    {
        if (!_analysisOnly && TryEmitHardwareSimdOperation(out var simdDefinition))
            return simdDefinition;
        if (!_analysisOnly && TryEmitHardwareGeometryKernel(out var geometryDefinition))
            return geometryDefinition;
        if (_method.IsAssemblyFunction && !_method.IsNaked)
            return EmitAssemblyFunctionDefinition();
        if (_method.IsNaked)
        {
            if (_analysisOnly)
            {
                if (_method.IsAssemblyFunction)
                    RecordAssemblyFunctionEffect(_method.AssemblyBody!);
                else if (_method.Body is not null)
                    _ = EmitStatements(NullLoweringWriter.Instance, _method.Body.Statements);
                return string.Empty;
            }
            return EmitNakedDefinition();
        }
        if (_method.IsConstructor && _method.ContainingType.Kind == DeclaredTypeKind.Class)
            return EmitClassConstructorDefinition();
        var body = CreateWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            EmitDebugMethodEnter(body);
            EmitConstructorPrologue(body);
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            if (!_method.IsStatic && !_method.IsConstructor)
                body.WriteLine("(void)ct_self;");
            foreach (var parameter in _method.Parameters)
            {
                var name = NameMangler.Identifier(parameter.Name);
                if (parameter.Type.IsNativeBuffer)
                {
                    body.WriteLine($"(void){name}_data;");
                    body.WriteLine($"(void){name}_length;");
                }
                else
                    body.WriteLine($"(void){name};");
            }
            EmitInstanceFieldInitializers(body);
            if (_isIteratorMethod)
                EmitIteratorBuilder(body);
            if (_property is not null && _method.Body is null)
                EmitAutomaticAccessor(body);
            else if (_method.Body is not null)
            {
                var flow = EmitStatements(body, _method.Body.Statements);
                if (_isIteratorMethod && flow.FallsThrough)
                {
                    EmitIteratorReturn(body, _method.Body);
                    flow = new FlowResult((flow.Exits & ~FlowExit.FallThrough) | FlowExit.Return);
                }
                if (!_isIteratorMethod && !_method.IsConstructor && _method.ReturnType != CType.Void && !flow.AlwaysReturns)
                    Report("CT3100", $"Not every reachable path returns a value from '{(_method.IsOperator ? OperatorFacts.DisplayName(_method.OperatorKind) : _method.Name)}'.", _method.Syntax ?? _method.Body);
                if (flow.FallsThrough)
                {
                    ValidateOutParameters(_method.Body);
                    ValidateNativeResourceObligations();
                }
            }

            if (_method.IsConstructor)
            {
                ValidateConstructorAssignments();
                if (_method.ContainingType.Kind == DeclaredTypeKind.Struct && _method.ContainingType.Type.ContainsManagedReferences)
                    body.WriteLine("ct_cleanup_disarm(&ct_cleanup_struct_constructor);");
                body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                body.WriteLine(_method.ContainingType.Kind == DeclaredTypeKind.Struct ? "return ct_value;" : "return ct_self;");
            }
            else if (_method.ReturnType == CType.Void && (_property is null || !_isGetter))
            {
                body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                body.WriteLine("return;");
            }
        }
        return _analysisOnly ? string.Empty : RenderFunction(_emitter.MethodSignature(_method, _nameOverride), body);
    }

    private static bool ContainsYield(SyntaxNode? syntax)
    {
        if (syntax is null)
            return false;
        if (syntax is YieldStatementSyntax)
            return true;
        return syntax.ChildNodesAndTokens().Any(child => child.Node is not null && ContainsYield(child.Node));
    }

    private void ValidateYieldPlacement(SyntaxNode syntax, bool forbiddenRegion)
    {
        if (syntax is YieldStatementSyntax && forbiddenRegion)
        {
            _diagnostics.Add("CT2213", "yield cannot suspend across an accessor, constructor, operator, try, catch, finally, lock, or defer cleanup region.", syntax.Source, syntax.Span);
            return;
        }
        var childForbidden = forbiddenRegion || syntax is TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax or LockStatementSyntax or DeferStatementSyntax;
        foreach (var child in syntax.ChildNodesAndTokens())
            if (child.Node is not null)
                ValidateYieldPlacement(child.Node, childForbidden);
    }

    private bool TryEmitHardwareSimdOperation(out string definition)
    {
        definition = string.Empty;
        if (!_emitter.HasCpuFeature(CpuFeature.Simd128) || !SimdOperation.TryClassify(_method, out var operation))
            return false;
        if (operation.Kind == SimdOperationKind.MultiplyAdd && operation.LaneKind == SimdLaneKind.Float32 && operation.InputCount == 3)
            return TryEmitHardwareMultiplyAdd(out definition);
        if (operation.Kind == SimdOperationKind.Abs && operation.LaneKind == SimdLaneKind.Float32 && _emitter.Architecture is CompilationArchitecture.X86 or CompilationArchitecture.X64)
            return TryEmitX86FloatAbs(out definition);
        if (_emitter.Architecture is CompilationArchitecture.X86 or CompilationArchitecture.X64 &&
            TryEmitX86SimdSpecial(operation, out definition))
            return true;
        var intrinsic = SimdBackendTable.Intrinsic(_emitter.Architecture, operation);
        if (intrinsic is null)
            return false;
        var arguments = string.Join(", ", _method.Parameters.Select(parameter => $"{NameMangler.Identifier(parameter.Name)}.ct_simd"));
        var type = _emitter.CTypeName(_method.ReturnType);
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n    ct_result.ct_simd = {intrinsic}({arguments});\n    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitX86SimdSpecial(SimdOperation operation, out string definition)
    {
        definition = string.Empty;
        var type = _emitter.CTypeName(_method.ReturnType);
        var parameters = _method.Parameters.Select(parameter => NameMangler.Identifier(parameter.Name)).ToArray();
        string? expression = null;
        string? body = null;
        var floatLanes = operation.LaneKind == SimdLaneKind.Float32;

        if (operation.Kind == SimdOperationKind.Multiply && operation.LaneKind is SimdLaneKind.Int32 or SimdLaneKind.UInt32)
        {
            body = $"""
                __m128i ct_even = _mm_mul_epu32({parameters[0]}.ct_simd, {parameters[1]}.ct_simd);
                __m128i ct_odd = _mm_mul_epu32(_mm_srli_si128({parameters[0]}.ct_simd, 4), _mm_srli_si128({parameters[1]}.ct_simd, 4));
                __m128i ct_even_low = _mm_shuffle_epi32(ct_even, _MM_SHUFFLE(0, 0, 2, 0));
                __m128i ct_odd_low = _mm_shuffle_epi32(ct_odd, _MM_SHUFFLE(0, 0, 2, 0));
                ct_result.ct_simd = _mm_unpacklo_epi32(ct_even_low, ct_odd_low);
                """;
        }
        else if (operation.Kind is SimdOperationKind.ShiftLeft or SimdOperationKind.ShiftRight &&
            operation.LaneKind is SimdLaneKind.Int32 or SimdLaneKind.UInt32 && operation.ConstantImmediates.Length == 1)
        {
            var intrinsic = operation.Kind == SimdOperationKind.ShiftLeft ? "_mm_slli_epi32" :
                operation.LaneKind == SimdLaneKind.Int32 ? "_mm_srai_epi32" : "_mm_srli_epi32";
            expression = $"{intrinsic}(ct_self->ct_simd, {operation.ConstantImmediates[0]})";
        }
        else if (operation.Kind == SimdOperationKind.BitwiseNot && operation.LaneKind is SimdLaneKind.Int32 or SimdLaneKind.UInt32 or SimdLaneKind.Mask32)
            expression = $"_mm_xor_si128({parameters[0]}.ct_simd, _mm_set1_epi32(-1))";
        else if (operation.Kind == SimdOperationKind.BitwiseAndNot && operation.LaneKind == SimdLaneKind.Mask32)
            expression = $"_mm_andnot_si128({parameters[0]}.ct_simd, {parameters[1]}.ct_simd)";
        else if (operation.Kind == SimdOperationKind.Select)
        {
            if (floatLanes)
                expression = $"_mm_or_ps(_mm_and_ps(_mm_castsi128_ps({parameters[0]}.ct_simd), {parameters[1]}.ct_simd), _mm_andnot_ps(_mm_castsi128_ps({parameters[0]}.ct_simd), {parameters[2]}.ct_simd))";
            else if (operation.LaneKind is SimdLaneKind.Int32 or SimdLaneKind.UInt32)
                expression = $"_mm_or_si128(_mm_and_si128({parameters[0]}.ct_simd, {parameters[1]}.ct_simd), _mm_andnot_si128({parameters[0]}.ct_simd, {parameters[2]}.ct_simd))";
        }
        else if (operation.Kind is >= SimdOperationKind.CompareEqual and <= SimdOperationKind.CompareGreaterThanOrEqual)
            expression = X86ComparisonExpression(operation, parameters);
        else if (operation.Kind == SimdOperationKind.ConvertInt32ToFloat)
            expression = $"_mm_cvtepi32_ps({parameters[0]}.ct_simd)";
        else if (operation.Kind == SimdOperationKind.ConvertUInt32ToFloat)
        {
            body = $"""
                __m128i ct_half = _mm_srli_epi32({parameters[0]}.ct_simd, 1);
                __m128i ct_low = _mm_and_si128({parameters[0]}.ct_simd, _mm_set1_epi32(1));
                __m128 ct_half_float = _mm_cvtepi32_ps(ct_half);
                ct_result.ct_simd = _mm_add_ps(_mm_add_ps(ct_half_float, ct_half_float), _mm_cvtepi32_ps(ct_low));
                """;
        }
        else if (operation.Kind is SimdOperationKind.Minimum or SimdOperationKind.Maximum &&
            operation.LaneKind == SimdLaneKind.Float32)
        {
            var intrinsic = operation.Kind == SimdOperationKind.Minimum ? "_mm_min_ps" : "_mm_max_ps";
            var zeroMerge = operation.Kind == SimdOperationKind.Minimum ? "_mm_or_ps" : "_mm_and_ps";
            body = $"""
                __m128 ct_left = {parameters[0]}.ct_simd;
                __m128 ct_right = {parameters[1]}.ct_simd;
                __m128 ct_value = {intrinsic}(ct_left, ct_right);
                __m128 ct_right_nan = _mm_cmpunord_ps(ct_right, ct_right);
                ct_value = _mm_or_ps(_mm_and_ps(ct_right_nan, ct_left), _mm_andnot_ps(ct_right_nan, ct_value));
                __m128 ct_equal_zero = _mm_and_ps(_mm_cmpeq_ps(ct_left, ct_right), _mm_cmpeq_ps(ct_left, _mm_setzero_ps()));
                __m128 ct_zero = {zeroMerge}(ct_left, ct_right);
                ct_result.ct_simd = _mm_or_ps(_mm_and_ps(ct_equal_zero, ct_zero), _mm_andnot_ps(ct_equal_zero, ct_value));
                """;
        }
        else if (operation.Kind is SimdOperationKind.MaskAny or SimdOperationKind.MaskAll or SimdOperationKind.MaskNone or SimdOperationKind.MaskMove)
        {
            var comparison = operation.Kind switch
            {
                SimdOperationKind.MaskAny => " != 0",
                SimdOperationKind.MaskAll => " == 15",
                SimdOperationKind.MaskNone => " == 0",
                _ => string.Empty,
            };
            var cast = operation.Kind == SimdOperationKind.MaskMove ? "(uint32_t)" : string.Empty;
            definition = $"{_emitter.MethodSignature(_method)}\n{{\n    return {cast}(_mm_movemask_ps(_mm_castsi128_ps(ct_self->ct_simd)){comparison});\n}}";
            return true;
        }
        else if (operation.Kind == SimdOperationKind.Splat && parameters.Length == 1)
            expression = floatLanes ? $"_mm_set1_ps({parameters[0]})" : $"_mm_set1_epi32((int32_t){parameters[0]})";
        else if (operation.Kind == SimdOperationKind.Create && parameters.Length == 4)
            expression = floatLanes
                ? $"_mm_setr_ps({string.Join(", ", parameters)})"
                : $"_mm_setr_epi32((int32_t){parameters[0]}, (int32_t){parameters[1]}, (int32_t){parameters[2]}, (int32_t){parameters[3]})";

        if (expression is null && body is null)
            return false;
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n    {(body ?? $"ct_result.ct_simd = {expression};")}\n    return ct_result;\n}}";
        return true;
    }

    private static string? X86ComparisonExpression(SimdOperation operation, IReadOnlyList<string> parameters)
    {
        var left = $"{parameters[0]}.ct_simd";
        var right = $"{parameters[1]}.ct_simd";
        if (operation.LaneKind == SimdLaneKind.Float32)
        {
            var intrinsic = operation.Kind switch
            {
                SimdOperationKind.CompareEqual => "_mm_cmpeq_ps",
                SimdOperationKind.CompareNotEqual => "_mm_cmpneq_ps",
                SimdOperationKind.CompareLessThan => "_mm_cmplt_ps",
                SimdOperationKind.CompareLessThanOrEqual => "_mm_cmple_ps",
                SimdOperationKind.CompareGreaterThan => "_mm_cmpgt_ps",
                SimdOperationKind.CompareGreaterThanOrEqual => "_mm_cmpge_ps",
                _ => null,
            };
            return intrinsic is null ? null : $"_mm_castps_si128({intrinsic}({left}, {right}))";
        }
        if (operation.LaneKind is not (SimdLaneKind.Int32 or SimdLaneKind.UInt32))
            return null;
        if (operation.LaneKind == SimdLaneKind.UInt32)
        {
            left = $"_mm_xor_si128({left}, _mm_set1_epi32((int)0x80000000u))";
            right = $"_mm_xor_si128({right}, _mm_set1_epi32((int)0x80000000u))";
        }
        return operation.Kind switch
        {
            SimdOperationKind.CompareEqual => $"_mm_cmpeq_epi32({left}, {right})",
            SimdOperationKind.CompareNotEqual => $"_mm_xor_si128(_mm_cmpeq_epi32({left}, {right}), _mm_set1_epi32(-1))",
            SimdOperationKind.CompareLessThan => $"_mm_cmplt_epi32({left}, {right})",
            SimdOperationKind.CompareLessThanOrEqual => $"_mm_xor_si128(_mm_cmpgt_epi32({left}, {right}), _mm_set1_epi32(-1))",
            SimdOperationKind.CompareGreaterThan => $"_mm_cmpgt_epi32({left}, {right})",
            SimdOperationKind.CompareGreaterThanOrEqual => $"_mm_xor_si128(_mm_cmplt_epi32({left}, {right}), _mm_set1_epi32(-1))",
            _ => null,
        };
    }

    private bool TryEmitX86FloatAbs(out string definition)
    {
        var value = NameMangler.Identifier(_method.Parameters[0].Name);
        var type = _emitter.CTypeName(_method.ReturnType);
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n    ct_result.ct_simd = _mm_andnot_ps(_mm_set1_ps(-0.0f), {value}.ct_simd);\n    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitHardwareMultiplyAdd(out string definition)
    {
        var left = NameMangler.Identifier(_method.Parameters[0].Name);
        var right = NameMangler.Identifier(_method.Parameters[1].Name);
        var addend = NameMangler.Identifier(_method.Parameters[2].Name);
        var type = _emitter.CTypeName(_method.ReturnType);
        var expression = _emitter.Architecture switch
        {
            CompilationArchitecture.X86 or CompilationArchitecture.X64 => $"#if defined(__FMA__) || (defined(_MSC_VER) && defined(__AVX2__))\n    ct_result.ct_simd = _mm_fmadd_ps({left}.ct_simd, {right}.ct_simd, {addend}.ct_simd);\n#else\n    ct_result.ct_simd = _mm_add_ps(_mm_mul_ps({left}.ct_simd, {right}.ct_simd), {addend}.ct_simd);\n#endif",
            CompilationArchitecture.Arm64 => $"#if defined(__ARM_FEATURE_FMA)\n    ct_result.ct_simd = vfmaq_f32({addend}.ct_simd, {left}.ct_simd, {right}.ct_simd);\n#else\n    ct_result.ct_simd = vaddq_f32(vmulq_f32({left}.ct_simd, {right}.ct_simd), {addend}.ct_simd);\n#endif",
            CompilationArchitecture.Arm32 => $"ct_result.ct_simd = vaddq_f32(vmulq_f32({left}.ct_simd, {right}.ct_simd), {addend}.ct_simd);",
            _ => string.Empty,
        };
        if (expression.Length == 0)
        {
            definition = string.Empty;
            return false;
        }
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n    {expression}\n    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitHardwareGeometryKernel(out string definition)
    {
        definition = string.Empty;
        if (!_emitter.HasCpuFeature(CpuFeature.Simd128))
            return false;
        if (_emitter.SimdOptimizations && _emitter.Architecture == CompilationArchitecture.X64 &&
            TryEmitExtendedX64GeometryKernel(out definition))
            return true;
        if (_method.IsOperator && _method.IsStatic && _method.OperatorKind == SyntaxKind.StarToken && _method.Parameters.Length == 2)
        {
            if (_method.ContainingType.FullName == "System.Matrix4x4" && _method.Parameters.All(parameter => parameter.Type.Symbol?.FullName == "System.Matrix4x4"))
                return TryEmitMatrix4Multiply(out definition);
            if (_method.ContainingType.FullName == "System.Quaternion" && _method.Parameters.All(parameter => parameter.Type.Symbol?.FullName == "System.Quaternion"))
                return TryEmitQuaternionMultiply(out definition);
        }
        if (!_method.IsStatic && _method.ContainingType.FullName == "System.Matrix4x4" && _method.Name == "Transform" && _method.Parameters.Length == 1 && _method.ReturnType.Symbol?.FullName == "System.Vec4")
            return TryEmitMatrix4VectorTransform(out definition);
        return false;
    }

    private bool TryEmitMatrix4Multiply(out string definition)
    {
        var left = NameMangler.Identifier(_method.Parameters[0].Name);
        var right = NameMangler.Identifier(_method.Parameters[1].Name);
        var type = _emitter.CTypeName(_method.ReturnType);
        string body;
        if (_emitter.Architecture is CompilationArchitecture.X86 or CompilationArchitecture.X64)
        {
            body = $"""
                __m128 ct_b0 = _mm_loadu_ps(&{right}.u_3_M11);
                __m128 ct_b1 = _mm_loadu_ps(&{right}.u_3_M21);
                __m128 ct_b2 = _mm_loadu_ps(&{right}.u_3_M31);
                __m128 ct_b3 = _mm_loadu_ps(&{right}.u_3_M41);
                #if defined(__FMA__) || (defined(_MSC_VER) && defined(__AVX2__))
                #define CT_MAT4_ROW(ROW) _mm_fmadd_ps(_mm_set1_ps({left}.u_3_M##ROW##4), ct_b3, _mm_fmadd_ps(_mm_set1_ps({left}.u_3_M##ROW##3), ct_b2, _mm_fmadd_ps(_mm_set1_ps({left}.u_3_M##ROW##2), ct_b1, _mm_mul_ps(_mm_set1_ps({left}.u_3_M##ROW##1), ct_b0))))
                #else
                #define CT_MAT4_ROW(ROW) _mm_add_ps(_mm_add_ps(_mm_mul_ps(_mm_set1_ps({left}.u_3_M##ROW##1), ct_b0), _mm_mul_ps(_mm_set1_ps({left}.u_3_M##ROW##2), ct_b1)), _mm_add_ps(_mm_mul_ps(_mm_set1_ps({left}.u_3_M##ROW##3), ct_b2), _mm_mul_ps(_mm_set1_ps({left}.u_3_M##ROW##4), ct_b3)))
                #endif
                _mm_storeu_ps(&ct_result.u_3_M11, CT_MAT4_ROW(1));
                _mm_storeu_ps(&ct_result.u_3_M21, CT_MAT4_ROW(2));
                _mm_storeu_ps(&ct_result.u_3_M31, CT_MAT4_ROW(3));
                _mm_storeu_ps(&ct_result.u_3_M41, CT_MAT4_ROW(4));
                #undef CT_MAT4_ROW
                """;
        }
        else if (_emitter.Architecture is CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64)
        {
            body = $"""
                float32x4_t ct_b0 = vld1q_f32(&{right}.u_3_M11);
                float32x4_t ct_b1 = vld1q_f32(&{right}.u_3_M21);
                float32x4_t ct_b2 = vld1q_f32(&{right}.u_3_M31);
                float32x4_t ct_b3 = vld1q_f32(&{right}.u_3_M41);
                #if defined(__ARM_FEATURE_FMA) && defined(__aarch64__)
                #define CT_MAT4_ROW(ROW) vfmaq_n_f32(vfmaq_n_f32(vfmaq_n_f32(vmulq_n_f32(ct_b0, {left}.u_3_M##ROW##1), ct_b1, {left}.u_3_M##ROW##2), ct_b2, {left}.u_3_M##ROW##3), ct_b3, {left}.u_3_M##ROW##4)
                #else
                #define CT_MAT4_ROW(ROW) vaddq_f32(vaddq_f32(vmulq_n_f32(ct_b0, {left}.u_3_M##ROW##1), vmulq_n_f32(ct_b1, {left}.u_3_M##ROW##2)), vaddq_f32(vmulq_n_f32(ct_b2, {left}.u_3_M##ROW##3), vmulq_n_f32(ct_b3, {left}.u_3_M##ROW##4)))
                #endif
                vst1q_f32(&ct_result.u_3_M11, CT_MAT4_ROW(1));
                vst1q_f32(&ct_result.u_3_M21, CT_MAT4_ROW(2));
                vst1q_f32(&ct_result.u_3_M31, CT_MAT4_ROW(3));
                vst1q_f32(&ct_result.u_3_M41, CT_MAT4_ROW(4));
                #undef CT_MAT4_ROW
                """;
        }
        else
        {
            definition = string.Empty;
            return false;
        }
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n{IndentKernel(body)}    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitMatrix4VectorTransform(out string definition)
    {
        var value = NameMangler.Identifier(_method.Parameters[0].Name);
        var type = _emitter.CTypeName(_method.ReturnType);
        string body;
        if (_emitter.Architecture is CompilationArchitecture.X86 or CompilationArchitecture.X64)
        {
            body = $"""
                __m128 ct_r0 = _mm_loadu_ps(&ct_self->u_3_M11);
                __m128 ct_r1 = _mm_loadu_ps(&ct_self->u_3_M21);
                __m128 ct_r2 = _mm_loadu_ps(&ct_self->u_3_M31);
                __m128 ct_r3 = _mm_loadu_ps(&ct_self->u_3_M41);
                #if defined(__FMA__) || (defined(_MSC_VER) && defined(__AVX2__))
                __m128 ct_value = _mm_fmadd_ps(_mm_set1_ps({value}.u_1_W), ct_r3, _mm_fmadd_ps(_mm_set1_ps({value}.u_1_Z), ct_r2, _mm_fmadd_ps(_mm_set1_ps({value}.u_1_Y), ct_r1, _mm_mul_ps(_mm_set1_ps({value}.u_1_X), ct_r0))));
                #else
                __m128 ct_value = _mm_add_ps(_mm_add_ps(_mm_mul_ps(_mm_set1_ps({value}.u_1_X), ct_r0), _mm_mul_ps(_mm_set1_ps({value}.u_1_Y), ct_r1)), _mm_add_ps(_mm_mul_ps(_mm_set1_ps({value}.u_1_Z), ct_r2), _mm_mul_ps(_mm_set1_ps({value}.u_1_W), ct_r3)));
                #endif
                _mm_storeu_ps(&ct_result.u_1_X, ct_value);
                """;
        }
        else if (_emitter.Architecture is CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64)
        {
            body = $"""
                float32x4_t ct_r0 = vld1q_f32(&ct_self->u_3_M11);
                float32x4_t ct_r1 = vld1q_f32(&ct_self->u_3_M21);
                float32x4_t ct_r2 = vld1q_f32(&ct_self->u_3_M31);
                float32x4_t ct_r3 = vld1q_f32(&ct_self->u_3_M41);
                #if defined(__ARM_FEATURE_FMA) && defined(__aarch64__)
                float32x4_t ct_value = vfmaq_n_f32(vfmaq_n_f32(vfmaq_n_f32(vmulq_n_f32(ct_r0, {value}.u_1_X), ct_r1, {value}.u_1_Y), ct_r2, {value}.u_1_Z), ct_r3, {value}.u_1_W);
                #else
                float32x4_t ct_value = vaddq_f32(vaddq_f32(vmulq_n_f32(ct_r0, {value}.u_1_X), vmulq_n_f32(ct_r1, {value}.u_1_Y)), vaddq_f32(vmulq_n_f32(ct_r2, {value}.u_1_Z), vmulq_n_f32(ct_r3, {value}.u_1_W)));
                #endif
                vst1q_f32(&ct_result.u_1_X, ct_value);
                """;
        }
        else
        {
            definition = string.Empty;
            return false;
        }
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n{IndentKernel(body)}    return ct_result;\n}}";
        return true;
    }

    private bool TryEmitQuaternionMultiply(out string definition)
    {
        var left = NameMangler.Identifier(_method.Parameters[0].Name);
        var right = NameMangler.Identifier(_method.Parameters[1].Name);
        var type = _emitter.CTypeName(_method.ReturnType);
        var x = $"{left}.u_1_W*{right}.u_1_X+{left}.u_1_X*{right}.u_1_W+{left}.u_1_Y*{right}.u_1_Z-{left}.u_1_Z*{right}.u_1_Y";
        var y = $"{left}.u_1_W*{right}.u_1_Y-{left}.u_1_X*{right}.u_1_Z+{left}.u_1_Y*{right}.u_1_W+{left}.u_1_Z*{right}.u_1_X";
        var z = $"{left}.u_1_W*{right}.u_1_Z+{left}.u_1_X*{right}.u_1_Y-{left}.u_1_Y*{right}.u_1_X+{left}.u_1_Z*{right}.u_1_W";
        var w = $"{left}.u_1_W*{right}.u_1_W-{left}.u_1_X*{right}.u_1_X-{left}.u_1_Y*{right}.u_1_Y-{left}.u_1_Z*{right}.u_1_Z";
        var lanes = $"""
            #if defined(__FMA__) || (defined(_MSC_VER) && defined(__AVX2__)) || defined(__ARM_FEATURE_FMA)
            float ct_x = fmaf({left}.u_1_W, {right}.u_1_X, fmaf({left}.u_1_X, {right}.u_1_W, fmaf({left}.u_1_Y, {right}.u_1_Z, -{left}.u_1_Z*{right}.u_1_Y)));
            float ct_y = fmaf({left}.u_1_W, {right}.u_1_Y, fmaf(-{left}.u_1_X, {right}.u_1_Z, fmaf({left}.u_1_Y, {right}.u_1_W, {left}.u_1_Z*{right}.u_1_X)));
            float ct_z = fmaf({left}.u_1_W, {right}.u_1_Z, fmaf({left}.u_1_X, {right}.u_1_Y, fmaf(-{left}.u_1_Y, {right}.u_1_X, {left}.u_1_Z*{right}.u_1_W)));
            float ct_w = fmaf({left}.u_1_W, {right}.u_1_W, fmaf(-{left}.u_1_X, {right}.u_1_X, fmaf(-{left}.u_1_Y, {right}.u_1_Y, -{left}.u_1_Z*{right}.u_1_Z)));
            #else
            float ct_x = {x};
            float ct_y = {y};
            float ct_z = {z};
            float ct_w = {w};
            #endif
            """;
        string body;
        if (_emitter.Architecture is CompilationArchitecture.X86 or CompilationArchitecture.X64)
            body = $"{lanes}\n_mm_storeu_ps(&ct_result.u_1_X, _mm_set_ps(ct_w, ct_z, ct_y, ct_x));\n";
        else if (_emitter.Architecture is CompilationArchitecture.Arm32 or CompilationArchitecture.Arm64)
            body = $"{lanes}\nfloat ct_lanes[4] = {{ ct_x, ct_y, ct_z, ct_w }};\n    vst1q_f32(&ct_result.u_1_X, vld1q_f32(ct_lanes));\n";
        else
        {
            definition = string.Empty;
            return false;
        }
        definition = $"{_emitter.MethodSignature(_method)}\n{{\n    {type} ct_result;\n    {body}    return ct_result;\n}}";
        return true;
    }

    private static string IndentKernel(string body) => string.Join("\n", body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Where(line => line.Length != 0).Select(line => "    " + line)) + "\n";

    private string EmitNakedDefinition()
    {
        var assemblyBody = _method.AssemblyBody?.Body ?? ((InlineAssemblyStatementSyntax)_method.Body!.Statements[0]).Body;
        var writer = new CWriter();
        var section = _method.SectionName is null
            ? string.Empty
            : NativeSection.MacroName(NativeSectionKind.Code, _method.SectionName) + " ";
        writer.WriteLine($"{section}__attribute__((naked, noreturn, used)) void {_method.ExportName}(void)");
        using (writer.Block())
            writer.WriteLine($"__asm__(\"{EscapeNakedAssembly(assemblyBody)}\");");
        writer.WriteLine();
        return writer.ToString();
    }

    private static string EscapeNakedAssembly(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r\n", "\\n\\t", StringComparison.Ordinal)
        .Replace("\r", "\\n\\t", StringComparison.Ordinal)
        .Replace("\n", "\\n\\t", StringComparison.Ordinal);

    private string EmitAssemblyFunctionDefinition()
    {
        var syntax = _method.AssemblyBody!;
        RecordAssemblyFunctionEffect(syntax);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new HashSet<ParameterSymbol>(ReferenceEqualityComparer.Instance);
        var lowered = new List<(InlineAssemblyOperandSyntax Syntax, string Code, CType Type, string Constraint)>();
        var resultCount = 0;

        for (var index = 0; index < syntax.Operands.Length; index++)
        {
            var operand = syntax.Operands[index];
            if (!aliases.Add(operand.Name))
                Report("CT2192", $"Assembly function operand name '{operand.Name}' is already declared.", operand);

            CType type;
            string code;
            BoundSemanticEntry semantic;
            if (operand.Variable.Name == "result")
            {
                resultCount++;
                type = _method.ReturnType;
                code = "ct_asm_result";
                semantic = new BoundSemanticEntry(operand.Variable, type, null, null, OwnershipKind.None, BoundValueCategory.Variable);
                _semanticEntries[operand.Variable] = semantic;
                if (_method.ReturnType == CType.Void || operand.Kind != InlineAssemblyOperandKind.Output)
                    Report("CT2217", "The reserved assembly-function result must be one out operand of a non-void function.", operand);
            }
            else if (_parameters.TryGetValue(operand.Variable.Name, out var parameter))
            {
                if (!parameters.Add(parameter))
                    Report("CT2217", $"Assembly function parameter '{parameter.Name}' must appear exactly once in the operand clause.", operand);
                var expected = parameter.PassingKind switch
                {
                    ParameterPassingKind.Ref => InlineAssemblyOperandKind.InputOutput,
                    ParameterPassingKind.Out => InlineAssemblyOperandKind.Output,
                    _ => InlineAssemblyOperandKind.Input,
                };
                if (operand.Kind != expected)
                    Report("CT2217", $"Assembly operand '{parameter.Name}' must use role '{AssemblyRole(expected)}' for its parameter passing kind.", operand);
                var expression = operand.Kind == InlineAssemblyOperandKind.Output ? LowerAssignable(operand.Variable) : LowerExpression(operand.Variable);
                type = expression.Type;
                code = expression.Code;
                semantic = _semanticEntries.GetValueOrDefault(operand.Variable) ??
                    new BoundSemanticEntry(operand.Variable, type, parameter, null, OwnershipKind.None, BoundValueCategory.Variable);
            }
            else
            {
                type = CType.Error;
                code = "0";
                semantic = new BoundSemanticEntry(operand.Variable, type, null, null, OwnershipKind.None, BoundValueCategory.Error);
                _semanticEntries[operand.Variable] = semantic;
                Report("CT2193", $"Assembly function operand '{operand.Variable.Name}' must name a parameter or the reserved result.", operand.Variable);
            }

            if (!IsInlineAssemblyType(type))
                Report("CT2195", $"Type '{type.DisplayName}' is not a supported assembly operand type.", operand.Variable);
            if (operand.Constraint is null && type.Kind is CTypeKind.Float or CTypeKind.Double)
                Report("CT2196", "A floating-point assembly operand requires an explicit GNU constraint.", operand);
            var constraint = operand.Constraint ?? "r";
            ValidateAssemblyConstraint(constraint, operand);
            var emittedConstraint = operand.Kind switch
            {
                InlineAssemblyOperandKind.Output => $"={constraint}",
                InlineAssemblyOperandKind.InputOutput => $"+{constraint}",
                _ => constraint,
            };
            lowered.Add((operand, code, type, emittedConstraint));
            foreach (var reference in syntax.References.Where(reference => reference.OperandIndex == index))
                _semanticEntries[reference] = semantic with { Syntax = reference };
        }

        foreach (var parameter in _method.Parameters.Where(parameter => !parameters.Contains(parameter)))
            Report("CT2217", $"Assembly function parameter '{parameter.Name}' must appear exactly once in the operand clause.", parameter.Syntax ?? _method.Syntax!);
        if (_method.ReturnType == CType.Void && resultCount != 0 || _method.ReturnType != CType.Void && resultCount != 1)
            Report("CT2217", _method.ReturnType == CType.Void
                ? "A void assembly function cannot declare a result operand."
                : "A non-void assembly function requires exactly one out result operand.", syntax);

        ValidateAssemblyClobbers(syntax.Clobbers, syntax);
        if (_analysisOnly)
            return string.Empty;

        var writer = new CWriter();
        if (_method.ReturnType != CType.Void)
            writer.WriteLine($"{_emitter.CTypeName(_method.ReturnType)} ct_asm_result;");
        writer.WriteLine("__asm__ volatile (");
        writer.WriteLine($"    \"{BuildAssemblyTemplate(syntax.Body, syntax.BodySpan, syntax.References)}\"");
        var outputs = lowered.Select((item, index) => (item, index))
            .Where(pair => pair.item.Syntax.Kind is InlineAssemblyOperandKind.Output or InlineAssemblyOperandKind.InputOutput)
            .Select(pair => $"[ct_asm_{pair.index}] \"{EscapeInlineAssemblyCString(pair.item.Constraint)}\" ({pair.item.Code})");
        var inputs = lowered.Select((item, index) => (item, index))
            .Where(pair => pair.item.Syntax.Kind == InlineAssemblyOperandKind.Input)
            .Select(pair => $"[ct_asm_{pair.index}] \"{EscapeInlineAssemblyCString(pair.item.Constraint)}\" ({pair.item.Code})");
        writer.WriteLine($"    : {string.Join(", ", outputs)}");
        writer.WriteLine($"    : {string.Join(", ", inputs)}");
        writer.WriteLine($"    : {string.Join(", ", syntax.Clobbers.Select(clobber => $"\"{EscapeInlineAssemblyCString(clobber)}\""))});");
        writer.WriteLine(_method.ReturnType == CType.Void ? "return;" : "return ct_asm_result;");
        return RenderFunction(_emitter.MethodSignature(_method, _nameOverride), writer);
    }

    private void RecordAssemblyFunctionEffect(AssemblyFunctionBodySyntax syntax) =>
        _emitter.Effects.Record(_method, syntax, EffectKind.All, "assembly function boundary", _method.DeclaredEffects);

    private void ValidateAssemblyConstraint(string constraint, SyntaxNode syntax)
    {
        if (constraint.Length == 0 || constraint.Contains('\0') || constraint.Contains('\r') || constraint.Contains('\n') ||
            constraint.Contains('=') || constraint.Contains('+'))
            Report("CT2197", "Assembly constraints must be non-empty single-line strings and omit '=' and '+'.", syntax);
    }

    private void ValidateAssemblyClobbers(IEnumerable<string> clobberSequence, SyntaxNode syntax)
    {
        var clobbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clobber in clobberSequence)
            if (clobber.Length == 0 || clobber.Contains('\0') || clobber.Contains('\r') || clobber.Contains('\n') || !clobbers.Add(clobber))
                Report("CT2199", $"Assembly clobber '{clobber}' must be unique, non-empty, and single-line.", syntax);
    }

    private static string AssemblyRole(InlineAssemblyOperandKind kind) => kind switch
    {
        InlineAssemblyOperandKind.Output => "out",
        InlineAssemblyOperandKind.InputOutput => "ref",
        _ => "in",
    };

    private string EmitClassConstructorDefinition()
    {
        var writer = CreateWriter();
        var typeName = NameMangler.Type(_method.ContainingType);
        var parameterNames = _method.Parameters.Select(parameter => NameMangler.Identifier(parameter.Name)).ToArray();
        if (EmitDebugInformation && _method.Syntax is not null)
            writer.WriteLine(_emitter.DebugSourceDirective(_method.Syntax));
        writer.WriteLine(_emitter.MethodSignature(_method, _nameOverride));
        using (writer.Block())
        {
            if (EmitDebugInformation)
                writer.WriteLine(_emitter.DebugGeneratedDirective());
            var source = _method.Syntax ?? _method.ContainingType.Syntax!;
            writer.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            writer.WriteLine("(void)ct_cleanup_method;");
            writer.WriteLine("ct_cleanup_record ct_cleanup_constructor = {0};");
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {_emitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
            writer.WriteLine("ct_cleanup_push(&ct_cleanup_constructor, (void*)&ct_self, ct_drop_ref_value);");
            writer.WriteLine($"{CEmitter.ConstructorInitializerName(_method)}(ct_self{(parameterNames.Length == 0 ? string.Empty : ", " + string.Join(", ", parameterNames))});");
            writer.WriteLine("ct_cleanup_disarm(&ct_cleanup_constructor);");
            writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
            writer.WriteLine("return ct_self;");
        }
        writer.WriteLine();
        var initializerParameters = new[] { $"{typeName}* ct_self" }
            .Concat(_method.Parameters.Select(parameter => _emitter.CParameterDeclaration(parameter, NameMangler.Identifier(parameter.Name))));
        var body = CreateWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            EmitDebugMethodEnter(body);
            body.WriteLine("(void)ct_self;");
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            foreach (var parameter in _method.Parameters)
            {
                var name = NameMangler.Identifier(parameter.Name);
                if (parameter.Type.IsNativeBuffer)
                {
                    body.WriteLine($"(void){name}_data;");
                    body.WriteLine($"(void){name}_length;");
                }
                else
                    body.WriteLine($"(void){name};");
            }
            var delegatesToThis = EmitConstructorInitializer(body);
            if (!delegatesToThis)
                EmitInstanceFieldInitializers(body);
            if (_method.Body is not null)
                _ = EmitStatements(body, _method.Body.Statements);
            if (!delegatesToThis)
                ValidateConstructorAssignments();
            body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
            body.WriteLine("return;");
        }
        writer.WriteBlock(RenderFunction($"static void {CEmitter.ConstructorInitializerName(_method)}({string.Join(", ", initializerParameters)})", body).TrimEnd().Split('\n'));
        return writer.ToString() ?? string.Empty;
    }

    private string RenderFunction(string signature, ILoweringWriter body)
    {
        var writer = CreateWriter();
        if (_directDefers.Count != 0)
            _emitter.RegisterDirectDeferState(_method, _durableSlots, _directDefers);
        if (EmitDebugInformation && _method.Syntax is not null)
            writer.WriteLine(_emitter.DebugSourceDirective(_method.Syntax));
        writer.WriteLine($"{(EmitDebugInstrumentation ? "CT_DEBUG_USER_NOINLINE " : string.Empty)}{signature}");
        using (writer.Block())
        {
            if (EmitDebugInformation)
                writer.WriteLine(_emitter.DebugGeneratedDirective());
            if (_durableSlots.Count != 0)
            {
                if (_directDefers.Count == 0)
                {
                    writer.WriteLine("volatile struct");
                    using (writer.Block())
                    {
                        foreach (var slot in _durableSlots)
                            writer.WriteLine($"{_emitter.CDeclaration(slot.Value, slot.Key)};");
                    }
                    writer.WriteLine("ct_state = {0};");
                }
                else
                    writer.WriteLine($"{(_tryCount == 0 ? string.Empty : "volatile ")}{_emitter.DurableStateTypeName(_method)} ct_state = {{0}};");
                writer.WriteLine("(void)ct_state;");
            }
            foreach (var record in _cleanupRecords.Order(StringComparer.Ordinal))
            {
                writer.WriteLine($"ct_cleanup_record {record} = {{0}};");
                writer.WriteLine($"(void){record};");
            }
            var bodyText = OptimizeGeneratedBody(body.ToString() ?? string.Empty);
            writer.WriteBlock(bodyText.TrimEnd().Split('\n'));
        }
        writer.WriteLine();
        return writer.ToString() ?? string.Empty;
    }

    private void EmitDebugMethodEnter(ILoweringWriter writer)
    {
        if (!EmitDebugInstrumentation || _analysisOnly)
            return;
        writer.WriteLine("ct_debug_method_frame ct_debug_frame = {0};");
        writer.WriteLine("ct_debug_method_enter(&ct_debug_frame);");
        writer.WriteLine("ct_cleanup_push(&ct_cleanup_debug_frame, (void*)&ct_debug_frame, ct_debug_method_leave);");
        if (!_method.IsStatic && !_method.IsConstructor)
            writer.WriteLine("ct_debug_keep((void*)&ct_self);");
        foreach (var parameter in _method.Parameters)
        {
            var name = NameMangler.Identifier(parameter.Name);
            writer.WriteLine(parameter.Type.IsNativeBuffer
                ? $"ct_debug_keep((void*)&{name}_data); ct_debug_keep((void*)&{name}_length);"
                : $"ct_debug_keep((void*)&{name});");
        }
        var source = _method.Syntax ?? _method.Body;
        if (source is not null)
        {
            var site = _emitter.RegisterDebugSite(_method, source, "entry");
            writer.WriteLine(_emitter.DebugSourceDirective(source));
            writer.WriteLine($"ct_debug_site(UINT32_C({site}));");
            writer.WriteLine(_emitter.DebugGeneratedDirective());
        }
    }

    private string OptimizeGeneratedBody(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (_cleanupRecords.Count == 0 && _tryCount == 0)
        {
            lines.RemoveAll(line =>
            {
                var text = line.Trim();
                return text.StartsWith("ct_cleanup_record* ct_cleanup_", StringComparison.Ordinal) ||
                    text.StartsWith("(void)ct_cleanup_", StringComparison.Ordinal) ||
                    text.StartsWith("ct_cleanup_unwind_to(ct_cleanup_", StringComparison.Ordinal);
            });
        }

        var parameterNames = _method.Parameters.SelectMany(parameter => parameter.Type.IsNativeBuffer
            ? new[] { $"{NameMangler.Identifier(parameter.Name)}_data", $"{NameMangler.Identifier(parameter.Name)}_length" }
            : new[] { NameMangler.Identifier(parameter.Name) }).ToHashSet(StringComparer.Ordinal);
        if (!_method.IsStatic || _method.IsConstructor)
            parameterNames.Add("ct_self");
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var text = lines[index].Trim();
            if (!text.StartsWith("(void)", StringComparison.Ordinal) || !text.EndsWith(';'))
                continue;
            var name = text[6..^1];
            if (!parameterNames.Contains(name))
                continue;
            var uses = lines.Where((_, candidate) => candidate != index).Count(line => ContainsIdentifier(line, name));
            var requiredUses = name == "ct_self" && _method.IsConstructor ? 2 : 1;
            if (uses >= requiredUses)
                lines.RemoveAt(index);
        }
        FoldPureSingleUseTemporaries(lines);
        return string.Join('\n', lines);
    }

    private static void FoldPureSingleUseTemporaries(List<string> lines)
    {
        bool changed;
        do
        {
            changed = false;
            for (var declarationIndex = lines.Count - 1; declarationIndex >= 0; declarationIndex--)
            {
                if (!TryGetSimpleTemporary(lines[declarationIndex], out var name, out var value))
                    continue;
                var uses = Enumerable.Range(declarationIndex + 1, lines.Count - declarationIndex - 1)
                    .Where(index => ContainsIdentifier(lines[index], name)).ToArray();
                if (uses.Length != 1)
                    continue;
                var useIndex = uses[0];
                if (Enumerable.Range(declarationIndex + 1, useIndex - declarationIndex - 1)
                    .Any(index => !string.IsNullOrWhiteSpace(lines[index]) && !TryGetSimpleTemporary(lines[index], out _, out _)))
                    continue;
                lines[useIndex] = ReplaceIdentifier(lines[useIndex], name, $"({value})");
                lines.RemoveAt(declarationIndex);
                changed = true;
                break;
            }
        }
        while (changed);
    }

    private static bool TryGetSimpleTemporary(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var text = line.Trim();
        var assignment = text.IndexOf(" = ", StringComparison.Ordinal);
        if (assignment < 0 || !text.EndsWith(';'))
            return false;
        var prefix = text[..assignment];
        var separator = prefix.LastIndexOf(' ');
        if (separator < 0)
            return false;
        name = prefix[(separator + 1)..];
        if (!name.StartsWith("ct_tmp", StringComparison.Ordinal) || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            return false;
        value = text[(assignment + 3)..^1];
        if (value.StartsWith("ct_state.", StringComparison.Ordinal))
            return false;
        return value.Length != 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '+');
    }

    private static string ReplaceIdentifier(string text, string identifier, string replacement)
    {
        var result = new StringBuilder(text.Length + replacement.Length);
        var start = 0;
        while (start < text.Length)
        {
            var match = text.IndexOf(identifier, start, StringComparison.Ordinal);
            if (match < 0)
            {
                result.Append(text, start, text.Length - start);
                break;
            }
            var before = match == 0 || !(char.IsLetterOrDigit(text[match - 1]) || text[match - 1] == '_');
            var end = match + identifier.Length;
            var after = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            result.Append(text, start, match - start);
            if (before && after)
                result.Append(replacement);
            else
                result.Append(identifier);
            start = end;
        }
        return result.ToString();
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var start = 0;
        while ((start = text.IndexOf(identifier, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 || !(char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_');
            var end = start + identifier.Length;
            var after = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            if (before && after)
                return true;
            start = end;
        }
        return false;
    }

    private ILoweringWriter CreateWriter() => _analysisOnly ? NullLoweringWriter.Instance : new CWriter();

    private void RegisterDurableSlot(string name, CType type) => _durableSlots.TryAdd(name, type);
    private void RegisterCleanupRecord(string name) => _cleanupRecords.Add(name);
    private static string Durable(string name) => $"ct_state.{name}";

    private bool EmitConstructorInitializer(ILoweringWriter writer)
    {
        if (_method.ContainingType.IsObject)
            return false;
        var syntax = _method.ConstructorInitializer;
        var targetType = syntax?.Kind == ConstructorInitializerKind.This
            ? _method.ContainingType
            : _method.ContainingType.BaseType;
        if (targetType is null)
            return false;
        var argumentSyntax = syntax?.Arguments ?? [];
        var arguments = argumentSyntax.Select(LowerArgument).ToArray();
        var target = SelectOverload(targetType.Constructors, targetType.Name, arguments, argumentSyntax, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        if (target is null)
            return syntax?.Kind == ConstructorInitializerKind.This;
        CheckAccess(target, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        if (target.IsUnsafe)
            RequireUnsafe(syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        _method.ConstructorInitializerTarget = target;
        _emitter.Effects.RecordCall(_method, target, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!, requiresContract: false);
        var lowered = LowerArguments(arguments, target.Parameters, argumentSyntax);
        EmitPrelude(writer, lowered.Prelude);
        var self = syntax?.Kind == ConstructorInitializerKind.This
            ? "ct_self"
            : $"({NameMangler.Type(targetType)}*)(void*)ct_self";
        writer.WriteLine($"{CEmitter.ConstructorInitializerName(target)}({self}{(lowered.Codes.Count == 0 ? string.Empty : ", " + string.Join(", ", lowered.Codes))});");
        EmitPrelude(writer, lowered.Postlude);
        return syntax?.Kind == ConstructorInitializerKind.This;
    }

    public IrExpressionValue LowerStandalone(ExpressionSyntax expression) => LowerExpression(expression);

    public IrExpressionValue ConvertStandalone(IrExpressionValue expression, CType target, SyntaxNode syntax) => Convert(expression, target, syntax, false);

    private void RecordRuntimeFault(SyntaxNode syntax, string reason) =>
        _emitter.Effects.Record(_method, syntax, EffectKind.Throws | EffectKind.UsesRuntime, reason);

    public BoundBody GetBoundBody()
    {
        var effects = _emitter.Effects.Snapshot().GetValueOrDefault(_method, []);
        var externUses = _emitter.ExternUses.Skip(_externUseStart).ToImmutableArray();
        var semantics = _semanticEntries.ToImmutableDictionary();
        var root = BoundTreeFactory.CreateRoot(_method, semantics);
        return new BoundBody(_method, root, semantics, BoundTreeFactory.Summarize(root), effects, externUses, [.. _deferTargets]);
    }

    private void EmitConstructorPrologue(ILoweringWriter writer)
    {
        if (!_method.IsConstructor)
            return;
        var typeName = NameMangler.Type(_method.ContainingType);
        if (_method.ContainingType.Kind == DeclaredTypeKind.Struct)
        {
            writer.WriteLine($"{typeName} ct_value = ({typeName}){{0}};");
            writer.WriteLine($"{typeName}* ct_self = &ct_value;");
            writer.WriteLine("(void)ct_self;");
            if (_method.ContainingType.Type.ContainsManagedReferences)
                EmitActivateOwnedSlot(writer, _method.ContainingType.Type, "ct_value", "ct_cleanup_struct_constructor");
        }
        else
        {
            var source = _method.Syntax ?? _method.ContainingType.Syntax!;
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {_emitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
        }
    }

    private void EmitAutomaticAccessor(ILoweringWriter writer)
    {
        var field = _property!.BackingField!;
        var access = field.IsStatic ? field.CName : $"ct_self->{field.CAccessPath}";
        if (_isGetter)
        {
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_result = {access};");
                writer.WriteLine(_emitter.RetainValueStatement(field.Type, "&ct_result"));
                writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                writer.WriteLine("return ct_result;");
            }
            else
                writer.WriteLine($"return {access};");
        }
        else
        {
            var parameter = NameMangler.Identifier("value");
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_old = {access};");
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_new = {parameter};");
                writer.WriteLine(_emitter.RetainValueStatement(field.Type, "&ct_new"));
                writer.WriteLine($"{access} = ct_new;");
                writer.WriteLine(_emitter.DropValueStatement(field.Type, "&ct_old"));
            }
            else
                writer.WriteLine($"{access} = {parameter};");
        }
    }

    private void EmitInstanceFieldInitializers(ILoweringWriter writer)
    {
        if (!_method.IsConstructor)
            return;
        foreach (var field in _method.ContainingType.Fields.Where(field => !field.IsStatic && field.Initializer is not null))
        {
            var expression = Convert(LowerExpression(field.Initializer!), field.Type, field.Initializer!, false);
            EmitPrelude(writer, expression.Prelude);
            if (field.Type.ContainsManagedReferences)
                EmitInitializeOwnedSlot(writer, field.Type, $"ct_self->{field.CAccessPath}", expression.Code);
            else
                writer.WriteLine($"ct_self->{field.CAccessPath} = {expression.Code};");
            _assignedFields.Add(field);
            _fieldAssignmentCounts[field] = 1;
        }
    }
}
