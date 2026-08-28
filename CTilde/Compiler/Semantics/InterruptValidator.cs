using System.Collections.Immutable;

namespace CTilde;

internal static class InterruptValidator
{
    public static void Validate(CompilationModel model, ImmutableArray<BoundBody>.Builder bodies, CompilationTarget target)
    {
        var roots = model.UserTypes.SelectMany(type => type.Methods)
            .Where(method => method.IsInterrupt)
            .OrderBy(method => method.Syntax?.Source.FilePath, StringComparer.Ordinal)
            .ThenBy(method => method.Syntax?.Span.Start ?? 0)
            .ToArray();
        if (roots.Length == 0 || target != CompilationTarget.EspIdf)
            return;

        var bodyByMethod = bodies.GroupBy(body => body.Method).ToDictionary(group => group.Key, group => group.First());
        var emitted = new HashSet<(string Code, string File, int Start, string Reason)>();

        foreach (var root in roots)
        {
            ValidateRequiredEffects(root);
            var closure = model.Effects.ReachableMethods([root]);
            foreach (var method in closure.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal))
            {
                method.IsInterruptCode = true;
                if (method.IsAssemblyFunction && !method.IsInterruptSafe && method.Syntax is { } assemblyFunctionSyntax)
                    Report("CT2216", $"Interrupt call path '{Path(root, method)}' reaches an assembly function without InterruptSafe.", assemblyFunctionSyntax);
                if (method.SectionName is not null && method.Syntax is { } sectionSyntax)
                    Report("CT2216", $"Interrupt call path '{Path(root, method)}' uses custom code section '{method.SectionName}'; interrupt code placement is compiler-controlled.", sectionSyntax);

                foreach (var operation in model.Effects.Operations.GetValueOrDefault(method))
                {
                    if (operation.RequiresContract)
                        Report("CT2215", $"Interrupt call path '{Path(root, method)}' contains unprovable virtual or interface dispatch: {operation.Reason}.", operation.Syntax);
                    if (operation.Target is { ExternName: not null, IsInterruptSafe: false } targetMethod)
                        Report("CT2216", $"Interrupt call path '{Path(root, method)} -> {EffectRegistry.Display(targetMethod)}' reaches an extern without InterruptSafe.", operation.Syntax);
                }

                if (model.Effects.UnknownCalls.TryGetValue(method, out var unknown))
                    Report("CT2215", $"Interrupt call path '{Path(root, method)}' contains an indirect call whose targets cannot be closed statically.", unknown);

                if (!bodyByMethod.TryGetValue(method, out var body))
                    continue;
                foreach (var assembly in Descendants(body.Root).Select(statement => statement.Syntax).OfType<InlineAssemblyStatementSyntax>())
                {
                    if (!assembly.Attributes.Any(attribute => attribute.Name == "InterruptSafe" && attribute.Arguments.IsEmpty))
                        Report("CT2216", $"Interrupt call path '{Path(root, method)}' contains inline assembly without InterruptSafe.", assembly);
                }
                foreach (var literal in body.Semantics.Keys.OfType<LiteralExpressionSyntax>().Where(literal => literal.LiteralKind == SyntaxKind.StringToken))
                    Report("CT2216", $"Interrupt call path '{Path(root, method)}' references a flash-backed string literal.", literal);
                foreach (var field in body.Semantics.Values.Select(semantic => semantic.Symbol).OfType<FieldSymbol>()
                             .Where(field => field.IsStatic).Distinct())
                    ValidateField(root, method, field);
            }
        }

        foreach (var pair in model.Effects.Operations.OrderBy(pair => NameMangler.MethodIdentity(pair.Key), StringComparer.Ordinal))
        {
            foreach (var operation in pair.Value.Where(operation => operation.Target?.IsInterrupt == true))
                Report("CT2215", $"Interrupt entry '{EffectRegistry.Display(operation.Target!)}' is native-only and cannot be called from C~ code.", operation.Syntax);
        }

        void ValidateRequiredEffects(MethodSymbol root)
        {
            foreach (var effect in new[] { EffectKind.UsesRuntime, EffectKind.Blocks })
            {
                foreach (var operation in model.Effects.Operations.GetValueOrDefault(root).Where(operation =>
                             (EffectAnalyzer.OperationEffects(operation, model.Effects.InferredEffects, model.Effects.Operations) & effect) != 0))
                {
                    var witness = EffectAnalyzer.Explain(operation, effect, model.Effects, []);
                    Report("CT2215", $"Interrupt member '{EffectRegistry.Display(root)}' violates its {(effect == EffectKind.Blocks ? "NoBlock" : "NoRuntime")} profile: {witness}.", operation.Syntax);
                }
            }
        }

        void ValidateField(MethodSymbol root, MethodSymbol method, FieldSymbol field)
        {
            var path = $"{Path(root, method)} -> field '{field.ContainingType.FullName}.{field.Name}'";
            var syntax = field.Syntax ?? method.Syntax ?? root.Syntax!;
            if (field.IsConst)
            {
                if (field.Type.ContainsManagedReferences)
                    Report("CT2216", $"Interrupt call path '{path}' references managed or flash-backed constant data.", syntax);
                return;
            }
            if (field.IsRegister || field.LinkerSymbolName is not null)
                return;
            if (field.IsConstInit && model.ConstInitializers.ContainsKey(field))
                return;
            if (field.ExternName is not null)
            {
                if (!field.IsInterruptSafe)
                    Report("CT2216", $"Interrupt call path '{path}' reaches extern data without InterruptSafe.", syntax);
                return;
            }
            if (field.Type.ContainsManagedReferences)
            {
                Report("CT2216", $"Interrupt call path '{path}' references managed static storage.", syntax);
                return;
            }
            if (field.SectionName is not null)
                Report("CT2216", $"Interrupt call path '{path}' uses custom data section '{field.SectionName}'; interrupt data placement is compiler-controlled.", syntax);
            if (field.Initializer is not null && !IsConstantInitializer(field))
                Report("CT2216", $"Interrupt call path '{path}' requires a non-constant static initializer.", field.Initializer);
            field.IsInterruptData = true;
        }

        bool IsConstantInitializer(FieldSymbol field)
        {
            if (field.Initializer is LiteralExpressionSyntax { LiteralKind: SyntaxKind.NullKeyword })
                return true;
            foreach (var body in bodies)
                if (body.Semantics.TryGetValue(field.Initializer!, out var semantic))
                    return semantic.ConstantValue is not null;
            return false;
        }

        string Path(MethodSymbol root, MethodSymbol targetMethod)
        {
            if (ReferenceEquals(root, targetMethod))
                return EffectRegistry.Display(root);
            var previous = new Dictionary<MethodSymbol, MethodSymbol?> { [root] = null };
            var pending = new Queue<MethodSymbol>();
            pending.Enqueue(root);
            while (pending.TryDequeue(out var current))
            {
                foreach (var next in model.Effects.CallTargets.GetValueOrDefault(current))
                {
                    if (!previous.TryAdd(next, current))
                        continue;
                    if (ReferenceEquals(next, targetMethod))
                    {
                        var path = new List<MethodSymbol>();
                        for (MethodSymbol? item = next; item is not null; item = previous[item])
                            path.Add(item);
                        path.Reverse();
                        return string.Join(" -> ", path.Select(EffectRegistry.Display));
                    }
                    pending.Enqueue(next);
                }
            }
            return $"{EffectRegistry.Display(root)} -> {EffectRegistry.Display(targetMethod)}";
        }

        void Report(string code, string message, SyntaxNode syntax)
        {
            var key = (code, syntax.Source.FilePath, syntax.Span.Start, message);
            if (emitted.Add(key))
                model.Diagnostics.Add(code, message, syntax.Source, syntax.Span);
        }
    }

    private static IEnumerable<BoundStatement> Descendants(BoundStatement statement)
    {
        yield return statement;
        foreach (var child in statement.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
