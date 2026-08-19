namespace CTilde;

internal sealed class BoundBodyBinder
{
    private readonly BodyPipeline _pipeline;

    public BoundBodyBinder(
        AnalysisServices services,
        MethodSymbol method,
        string? nameOverride = null,
        PropertySymbol? property = null,
        bool getter = false,
        string temporaryPrefix = "")
    {
        _pipeline = new BodyPipeline(services, method, nameOverride, property, getter, temporaryPrefix, analysisOnly: true);
    }

    public BoundBody Bind()
    {
        _ = _pipeline.EmitDefinition();
        return _pipeline.GetBoundBody();
    }

    public LoweredExpression BindExpression(ExpressionSyntax expression) => _pipeline.LowerStandalone(expression);

    public LoweredExpression ConvertExpression(LoweredExpression expression, CType target, SyntaxNode syntax) =>
        _pipeline.ConvertStandalone(expression, target, syntax);

    public BoundBody Finish() => _pipeline.GetBoundBody();
}

internal sealed class CBodyLowerer
{
    private readonly BodyPipeline _pipeline;

    public CBodyLowerer(
        CEmitter emitter,
        BoundBody boundBody,
        MethodSymbol method,
        string? nameOverride = null,
        PropertySymbol? property = null,
        bool getter = false,
        string temporaryPrefix = "")
    {
        if (!ReferenceEquals(boundBody.Method.Syntax, method.Syntax) || boundBody.Method.Name != method.Name)
            throw new InvalidOperationException($"Bound body for '{boundBody.Method.Name}' does not match emission method '{method.Name}'.");
        _pipeline = new BodyPipeline(emitter, method, nameOverride, property, getter, temporaryPrefix, semanticHints: boundBody.Semantics);
    }

    public string LowerDefinition() => _pipeline.EmitDefinition();

    public LoweredExpression LowerExpression(ExpressionSyntax expression) => _pipeline.LowerStandalone(expression);

    public LoweredExpression ConvertExpression(LoweredExpression expression, CType target, SyntaxNode syntax) =>
        _pipeline.ConvertStandalone(expression, target, syntax);
}
