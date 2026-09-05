using System.Collections.Immutable;

namespace CTilde;

internal sealed class BoundBodyBinder
{
    private readonly TypedIrBodyLowerer _pipeline;

    public BoundBodyBinder(
        AnalysisServices services,
        MethodSymbol method,
        string? nameOverride = null,
        PropertySymbol? property = null,
        bool getter = false,
        string temporaryPrefix = "")
    {
        _pipeline = new TypedIrBodyLowerer(services, method, nameOverride, property, getter, temporaryPrefix, analysisOnly: true);
    }

    public BoundBody Bind()
    {
        _ = _pipeline.EmitDefinition();
        return _pipeline.GetBoundBody();
    }

    public IrExpressionValue BindExpression(ExpressionSyntax expression) => _pipeline.LowerStandalone(expression);

    public IrExpressionValue ConvertExpression(IrExpressionValue expression, CType target, SyntaxNode syntax) =>
        _pipeline.ConvertStandalone(expression, target, syntax);

    public BoundBody Finish() => _pipeline.GetBoundBody();
}

internal sealed class TypedIrEmissionLowerer(CEmitter emitter)
{
    public TypedIrProgram Lower(TypedIrProgram program)
    {
        emitter.RegisterAccessorMethods(program.Functions);
        var functions = program.Functions.Select(LowerFunction).ToImmutableArray();
        var initializerIndex = 0;
        var initializers = program.ModuleInitializers.Select(initializer => LowerInitializer(initializer, initializerIndex++)).ToImmutableArray();
        return program with { Functions = functions, ModuleInitializers = initializers };
    }

    private IrFunction LowerFunction(IrFunction function)
    {
        var method = function.Property is null
            ? function.Method
            : emitter.GetAccessorMethod(function.Property, function.IsGetter);
        var name = function.Property is null
            ? null
            : function.IsGetter ? NameMangler.Getter(function.Property) : NameMangler.Setter(function.Property);
        string? overlayDefinitionName = null;
        var emittedName = name;
        if (method.IsOverlay)
        {
            var callable = method.IsConstructor && method.ContainingType.Kind == DeclaredTypeKind.Class
                ? CEmitter.ConstructorInitializerName(method)
                : name ?? method.CName;
            overlayDefinitionName = CEmitter.OverlayBodyName(method, callable);
            if (!method.IsConstructor || method.ContainingType.Kind != DeclaredTypeKind.Class)
                emittedName = overlayDefinitionName;
        }
        ValidateBody(function.Body, method);
        var lowerer = new TypedIrBodyLowerer(
            emitter,
            method,
            emittedName,
            function.Property,
            function.IsGetter,
            semanticHints: function.Body.Semantics,
            optimizationFacts: function.Optimization,
            overlayDefinitionName: overlayDefinitionName);
        var definition = lowerer.EmitDefinition();
        return function with { Emission = new IrFunctionEmission(definition) };
    }

    private IrStaticInitializer LowerInitializer(IrStaticInitializer initializer, int index)
    {
        var method = initializer.Body.Method;
        ValidateBody(initializer.Body, method);
        var lowerer = new TypedIrBodyLowerer(
            emitter,
            method,
            temporaryPrefix: $"_mi_{index}",
            semanticHints: initializer.Body.Semantics);
        var expression = lowerer.LowerStandalone(initializer.Field.Initializer!);
        var value = lowerer.ConvertStandalone(expression, initializer.Type, initializer.Field.Initializer!);
        if (initializer.Field.IsConst && !value.IsConstant)
            emitter.Model.Diagnostics.Add("CT2140", $"Const field '{initializer.Field.Name}' does not have a constant initializer.", initializer.Field.Initializer!.Source, initializer.Field.Initializer.Span);
        return initializer with
        {
            Emission = new IrInitializerEmission(value.Prelude.ToImmutableArray(), value.Code, value.IsConstant, value.Ownership),
        };
    }

    private static void ValidateBody(BoundBody body, MethodSymbol method)
    {
        if (!ReferenceEquals(body.Method.Syntax, method.Syntax) || body.Method.Name != method.Name)
            throw new InvalidOperationException($"Bound body for '{body.Method.Name}' does not match typed-IR emission method '{method.Name}'.");
    }
}
