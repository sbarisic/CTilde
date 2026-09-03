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
        ValidateBody(function.Body, method);
        var lowerer = new TypedIrBodyLowerer(
            emitter,
            method,
            name,
            function.Property,
            function.IsGetter,
            semanticHints: function.Body.Semantics,
            optimizationFacts: function.Optimization);
        var definition = lowerer.EmitDefinition();
        if (method.IsOverlay)
            definition = PlaceOverlayBody(method, name ?? method.CName, definition);
        return function with { Emission = new IrFunctionEmission(definition) };
    }

    private static string PlaceOverlayBody(MethodSymbol method, string callableName, string definition)
    {
        var bodyCallable = method.IsConstructor && method.ContainingType.Kind == DeclaredTypeKind.Class
            ? CEmitter.ConstructorInitializerName(method)
            : callableName;
        var bodyName = CEmitter.OverlayBodyName(method, bodyCallable);
        if (method.IsConstructor && method.ContainingType.Kind == DeclaredTypeKind.Class)
        {
            var signature = "static void " + bodyCallable + "(";
            var signatureStart = definition.IndexOf(signature, StringComparison.Ordinal);
            if (signatureStart < 0)
                throw new InvalidOperationException($"Class constructor initializer '{bodyCallable}' was not emitted.");
            var callableStart = signatureStart + "static void ".Length;
            definition = definition[..callableStart] + bodyName + definition[(callableStart + bodyCallable.Length)..];
        }
        else
            definition = definition.Replace(bodyCallable + "(", bodyName + "(", StringComparison.Ordinal);
        var lines = definition.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(bodyName + "(", StringComparison.Ordinal))
                continue;
            var storage = lines[index].IndexOf("static ", StringComparison.Ordinal);
            if (storage >= 0)
            {
                lines[index] = lines[index].Insert(storage + "static ".Length,
                    $"CT_OVERLAY_BODY(\"{method.OverlayName}\") ");
                break;
            }
        }
        return string.Join('\n', lines);
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
