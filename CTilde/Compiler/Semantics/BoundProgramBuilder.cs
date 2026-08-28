using System.Collections.Immutable;

namespace CTilde;

internal static class BoundProgramBuilder
{
    public static BoundProgram Build(CompilationModel model, CompilationTarget target, CompilationArchitecture architecture, string? sourceRoot = null, bool noRecursion = false)
    {
        var services = new AnalysisServices(model, target, architecture, sourceRoot);
        var bodies = ImmutableArray.CreateBuilder<BoundBody>();
        var analyzedMethods = new HashSet<MethodSymbol>();
        var analyzedAccessors = new HashSet<(PropertySymbol Property, bool Getter)>();
        while (AnalyzeAvailableBodies())
        {
        }

        bool AnalyzeAvailableBodies()
        {
            var changed = false;
            foreach (var type in model.UserTypes.ToArray())
            {
                if (type.Kind is DeclaredTypeKind.Enum or DeclaredTypeKind.Opaque or DeclaredTypeKind.Newtype or DeclaredTypeKind.Interface)
                    continue;
                foreach (var constructor in type.Constructors.Where(constructor => analyzedMethods.Add(constructor)))
                {
                    AnalyzeBody(services, constructor, bodies);
                    changed = true;
                }
                foreach (var method in type.Methods.Where(method => method.ExternName is null && !method.IsAbstract && !method.IsGenericDefinition).Where(analyzedMethods.Add).ToArray())
                {
                    AnalyzeBody(services, method, bodies);
                    changed = true;
                }
                foreach (var property in type.Properties)
                {
                    if (property.Getter is not null && !property.IsAbstract && analyzedAccessors.Add((property, true)))
                    {
                        var method = services.GetAccessorMethod(property, getter: true);
                        AnalyzeBody(services, method, bodies, NameMangler.Getter(property), property, getter: true);
                        changed = true;
                    }
                    if (property.Setter is not null && !property.IsAbstract && analyzedAccessors.Add((property, false)))
                    {
                        var method = services.GetAccessorMethod(property, getter: false);
                        AnalyzeBody(services, method, bodies, NameMangler.Setter(property), property, getter: false);
                        changed = true;
                    }
                }
            }
            return changed;
        }

        ConstDataEvaluator.Evaluate(model, services, bodies);
        AnalyzeModuleInitializers(model, services, bodies);
        CompileTimeEvaluator.EvaluateAssertions(model, services);
        ValidateConstructorCycles(model);
        model.Effects = EffectAnalyzer.Analyze(model, bodies);
        InterruptValidator.Validate(model, bodies, target);
        FreestandingValidator.Validate(model, bodies, target);
        RecursionAnalyzer.Validate(model, bodies, noRecursion);
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
            services.UsesExceptions,
            model.UserSyntaxTrees.SelectMany(tree => tree.Tokens).Any(token => token.Kind == SyntaxKind.AsmKeyword));
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
                     .Where(field => field.IsStatic && field.Initializer is not null && !field.IsConstInit && field.Name != "<underlying>"))
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
