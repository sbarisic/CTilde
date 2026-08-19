using System.Collections.Immutable;

namespace CTilde;

internal static class BoundProgramBuilder
{
    public static BoundProgram Build(CompilationModel model, CompilationTarget target)
    {
        var services = new AnalysisServices(model, target);
        var bodies = ImmutableArray.CreateBuilder<BoundBody>();
        foreach (var type in model.UserTypes)
        {
            if (type.Kind == DeclaredTypeKind.Enum)
                continue;
            foreach (var constructor in type.Constructors)
                AnalyzeBody(services, constructor, bodies);
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                AnalyzeBody(services, method, bodies);
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                {
                    var method = services.GetAccessorMethod(property, getter: true);
                    AnalyzeBody(services, method, bodies, NameMangler.Getter(property), property, getter: true);
                }
                if (property.Setter is not null)
                {
                    var method = services.GetAccessorMethod(property, getter: false);
                    AnalyzeBody(services, method, bodies, NameMangler.Setter(property), property, getter: false);
                }
            }
        }

        AnalyzeModuleInitializers(model, services, bodies);
        ValidateConstructorCycles(model);
        services.AllocationEffects.Validate(model.Diagnostics);
        TargetValidator.Validate(model, services, target);

        var semanticMap = ImmutableDictionary.CreateBuilder<SyntaxNode, BoundSemanticEntry>();
        foreach (var entry in bodies.SelectMany(body => body.Semantics))
            semanticMap[entry.Key] = entry.Value;
        return new BoundProgram(
            model,
            bodies.ToImmutable(),
            semanticMap.ToImmutable(),
            services.ExternUses.ToImmutableArray(),
            services.DynamicGeneratedSymbols.ToImmutableHashSet(StringComparer.Ordinal),
            services.UsesExceptions);
    }

    private static void AnalyzeBody(
        AnalysisServices services,
        MethodSymbol method,
        ImmutableArray<BoundBody>.Builder bodies,
        string? nameOverride = null,
        PropertySymbol? property = null,
        bool getter = false)
    {
        bodies.Add(new BoundBodyBinder(services, method, nameOverride, property, getter).Bind());
    }

    private static void AnalyzeModuleInitializers(
        CompilationModel model,
        AnalysisServices services,
        ImmutableArray<BoundBody>.Builder bodies)
    {
        var initializerIndex = 0;
        foreach (var field in model.UserTypes.SelectMany(type => type.Fields)
                     .Where(field => field.IsStatic && field.Initializer is not null && field.Name != "<underlying>"))
        {
            var method = new MethodSymbol
            {
                Name = "<module_init>",
                ContainingType = field.ContainingType,
                Accessibility = Accessibility.Private,
                IsStatic = true,
                Syntax = field.Syntax,
                ReturnType = CType.Void,
                Parameters = [],
                Body = null,
            };
            var analyzer = new BoundBodyBinder(services, method, temporaryPrefix: $"_mi_{initializerIndex++}");
            var expression = analyzer.BindExpression(field.Initializer!);
            var value = analyzer.ConvertExpression(expression, field.Type, field.Initializer!);
            if (field.IsConst && !value.IsConstant)
                model.Diagnostics.Add("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer!.Source, field.Initializer.Span);
            bodies.Add(analyzer.Finish());
        }
    }

    private static void ValidateConstructorCycles(CompilationModel model)
    {
        foreach (var type in model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class))
        {
            foreach (var constructor in type.Constructors)
            {
                var active = new HashSet<MethodSymbol>();
                for (var current = constructor; current is not null && current.ContainingType == type; current = current.ConstructorInitializerTarget)
                {
                    if (active.Add(current))
                        continue;
                    var syntax = constructor.ConstructorInitializer ?? constructor.Syntax ?? type.Syntax!;
                    model.Diagnostics.Add("CT1232", $"Constructor chain for '{type.FullName}' contains a cycle.", syntax.Source, syntax.Span);
                    break;
                }
            }
        }
    }
}
