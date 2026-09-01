using System.Collections.Immutable;

namespace CTilde;

[Flags]
internal enum EffectKind
{
    None = 0,
    Allocates = 1 << 0,
    Throws = 1 << 1,
    Blocks = 1 << 2,
    UsesRuntime = 1 << 3,
    All = Allocates | Throws | Blocks | UsesRuntime,
}

[Flags]
internal enum EffectContract
{
    None = 0,
    NoAlloc = 1 << 0,
    NoThrow = 1 << 1,
    NoBlock = 1 << 2,
    NoRuntime = 1 << 3,
}

internal static class EffectFacts
{
    public static EffectKind ForbiddenEffect(EffectContract contract) => contract switch
    {
        EffectContract.NoAlloc => EffectKind.Allocates,
        EffectContract.NoThrow => EffectKind.Throws,
        EffectContract.NoBlock => EffectKind.Blocks,
        EffectContract.NoRuntime => EffectKind.UsesRuntime,
        _ => EffectKind.None,
    };

    public static IEnumerable<EffectContract> IndividualContracts(EffectContract contracts)
    {
        foreach (var contract in new[] { EffectContract.NoAlloc, EffectContract.NoThrow, EffectContract.NoBlock, EffectContract.NoRuntime })
            if ((contracts & contract) != 0)
                yield return contract;
    }

    public static bool Proves(this EffectContract contracts, EffectKind effect) => effect switch
    {
        EffectKind.Allocates => (contracts & (EffectContract.NoAlloc | EffectContract.NoThrow | EffectContract.NoRuntime)) != 0,
        EffectKind.Throws => (contracts & (EffectContract.NoThrow | EffectContract.NoRuntime)) != 0,
        EffectKind.Blocks => (contracts & EffectContract.NoBlock) != 0,
        EffectKind.UsesRuntime => (contracts & EffectContract.NoRuntime) != 0,
        _ => false,
    };

    public static string ContractName(EffectContract contract) => contract switch
    {
        EffectContract.NoAlloc => "NoAlloc",
        EffectContract.NoThrow => "NoThrow",
        EffectContract.NoBlock => "NoBlock",
        EffectContract.NoRuntime => "NoRuntime",
        _ => contract.ToString(),
    };

    public static string EffectName(EffectKind effect) => effect switch
    {
        EffectKind.Allocates => "allocates",
        EffectKind.Throws => "throws",
        EffectKind.Blocks => "blocks",
        EffectKind.UsesRuntime => "usesRuntime",
        _ => effect.ToString(),
    };
}

internal sealed record EffectOperation(
    SyntaxNode Syntax,
    EffectKind Effects,
    string Reason,
    MethodSymbol? Target = null,
    bool RequiresContract = false,
    EffectContract TrustedContracts = EffectContract.None);

internal sealed class EffectRegistry
{
    private readonly Dictionary<MethodSymbol, List<EffectOperation>> _operations = [];

    public ImmutableDictionary<MethodSymbol, ImmutableArray<EffectOperation>> Snapshot() =>
        _operations.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray());

    public void RecordAllocation(MethodSymbol method, SyntaxNode syntax, string reason) =>
        Record(method, syntax, EffectKind.Allocates | EffectKind.Throws | EffectKind.UsesRuntime, reason);

    public void Record(MethodSymbol method, SyntaxNode syntax, EffectKind effects, string reason,
        EffectContract trustedContracts = EffectContract.None) =>
        Operations(method).Add(new EffectOperation(syntax, effects, reason, TrustedContracts: trustedContracts));

    public void RecordCall(MethodSymbol method, MethodSymbol target, SyntaxNode syntax, bool requiresContract) =>
        Operations(method).Add(new EffectOperation(syntax, EffectKind.None, $"call to '{Display(target)}'", target, requiresContract));

    private List<EffectOperation> Operations(MethodSymbol method)
    {
        if (!_operations.TryGetValue(method, out var operations))
        {
            operations = [];
            _operations.Add(method, operations);
        }
        return operations;
    }

    internal static string Display(MethodSymbol method) =>
        $"{method.ContainingType.FullName}.{(method.IsOperator ? OperatorFacts.DisplayName(method.OperatorKind) : method.Name)}";
}

internal sealed class EffectAnalysis
{
    public static readonly EffectAnalysis Empty = new(
        ImmutableDictionary<MethodSymbol, EffectKind>.Empty,
        ImmutableDictionary<MethodSymbol, ImmutableArray<EffectOperation>>.Empty,
        ImmutableDictionary<MethodSymbol, ImmutableArray<MethodSymbol>>.Empty,
        ImmutableDictionary<MethodSymbol, SyntaxNode>.Empty);

    public EffectAnalysis(
        ImmutableDictionary<MethodSymbol, EffectKind> inferredEffects,
        ImmutableDictionary<MethodSymbol, ImmutableArray<EffectOperation>> operations,
        ImmutableDictionary<MethodSymbol, ImmutableArray<MethodSymbol>> callTargets,
        ImmutableDictionary<MethodSymbol, SyntaxNode> unknownCalls)
    {
        InferredEffects = inferredEffects;
        Operations = operations;
        CallTargets = callTargets;
        UnknownCalls = unknownCalls;
    }

    public ImmutableDictionary<MethodSymbol, EffectKind> InferredEffects { get; }
    public ImmutableDictionary<MethodSymbol, ImmutableArray<EffectOperation>> Operations { get; }
    public ImmutableDictionary<MethodSymbol, ImmutableArray<MethodSymbol>> CallTargets { get; }
    public ImmutableDictionary<MethodSymbol, SyntaxNode> UnknownCalls { get; }

    public EffectKind GetEffects(MethodSymbol method) => InferredEffects.GetValueOrDefault(method);

    public EffectKind GetBootstrapEffects(MethodSymbol method) => GetBootstrapEffects(method, []);

    private EffectKind GetBootstrapEffects(MethodSymbol method, HashSet<MethodSymbol> active)
    {
        if (!active.Add(method))
            return EffectKind.None;
        var effects = EffectKind.None;
        foreach (var operation in Operations.GetValueOrDefault(method))
        {
            if (operation.Target is null)
            {
                var direct = operation.Effects;
                foreach (var effect in EffectAnalyzer.IndividualEffects(direct))
                    if (operation.TrustedContracts.Proves(effect))
                        direct &= ~effect;
                effects |= direct;
                continue;
            }
            if (operation.Target.IsNativeBoundary || operation.RequiresContract || operation.Target.IsAbstract)
            {
                if (!operation.Target.IsNoAlloc)
                    effects |= EffectKind.Allocates | EffectKind.Throws | EffectKind.UsesRuntime;
                continue;
            }
            effects |= GetBootstrapEffects(operation.Target, active);
        }
        active.Remove(method);
        return effects;
    }

    public HashSet<MethodSymbol> ReachableMethods(IEnumerable<MethodSymbol> roots)
    {
        var reachable = new HashSet<MethodSymbol>();
        var pending = new Queue<MethodSymbol>(roots);
        while (pending.TryDequeue(out var method))
        {
            if (!reachable.Add(method))
                continue;
            foreach (var target in CallTargets.GetValueOrDefault(method))
                pending.Enqueue(target);
        }
        return reachable;
    }
}

internal static class EffectAnalyzer
{
    public static EffectAnalysis Analyze(CompilationModel model, ImmutableArray<BoundBody>.Builder bodies)
    {
        var operations = ImmutableDictionary.CreateBuilder<MethodSymbol, ImmutableArray<EffectOperation>>();
        foreach (var body in bodies)
        {
            var builder = body.EffectOperations.ToBuilder();
            AddBodyEffects(body, builder);
            operations[body.Method] = builder
                .OrderBy(operation => operation.Syntax.Source.FilePath, StringComparer.Ordinal)
                .ThenBy(operation => operation.Syntax.Span.Start)
                .ThenBy(operation => operation.Reason, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        var snapshot = operations.ToImmutable();
        var inferred = snapshot.Keys.ToDictionary(method => method, _ => EffectKind.None);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in snapshot.OrderBy(pair => EffectRegistry.Display(pair.Key), StringComparer.Ordinal))
            {
                var value = pair.Value.Aggregate(EffectKind.None, (effects, operation) =>
                    effects | OperationEffects(operation, inferred, snapshot));
                if (inferred[pair.Key] == value)
                    continue;
                inferred[pair.Key] = value;
                changed = true;
            }
        }

        var (callTargets, unknownCalls) = BuildCallGraph(bodies, snapshot);
        var analysis = new EffectAnalysis(inferred.ToImmutableDictionary(), snapshot, callTargets, unknownCalls);
        ValidateContracts(model.Diagnostics, analysis);
        return analysis;
    }

    private static (ImmutableDictionary<MethodSymbol, ImmutableArray<MethodSymbol>> Targets,
        ImmutableDictionary<MethodSymbol, SyntaxNode> Unknown) BuildCallGraph(
        ImmutableArray<BoundBody>.Builder bodies,
        ImmutableDictionary<MethodSymbol, ImmutableArray<EffectOperation>> operations)
    {
        var methods = bodies.Select(body => body.Method).ToHashSet();
        var targets = ImmutableDictionary.CreateBuilder<MethodSymbol, ImmutableArray<MethodSymbol>>();
        var unknown = ImmutableDictionary.CreateBuilder<MethodSymbol, SyntaxNode>();
        foreach (var method in methods.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal))
        {
            var edges = new HashSet<MethodSymbol>();
            foreach (var operation in operations.GetValueOrDefault(method))
            {
                if (operation.Target is not { IsNativeBoundary: false } target)
                {
                    if (operation.Target is null && operation.Reason is var reason &&
                        (reason.StartsWith("indirect invocation", StringComparison.Ordinal) || reason.StartsWith("unmanaged function-pointer", StringComparison.Ordinal)))
                        unknown.TryAdd(method, operation.Syntax);
                    continue;
                }
                if (operation.RequiresContract)
                {
                    var possible = methods.Where(candidate => !candidate.IsAbstract &&
                        (ReferenceEquals(candidate, target) || Overrides(candidate, target) || candidate.ImplementedInterfaceMethods.Contains(target)));
                    var found = false;
                    foreach (var candidate in possible)
                    {
                        edges.Add(candidate);
                        found = true;
                    }
                    if (!found)
                        unknown.TryAdd(method, operation.Syntax);
                }
                else if (methods.Contains(target))
                    edges.Add(target);
            }
            targets[method] = edges.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal).ToImmutableArray();
        }
        return (targets.ToImmutable(), unknown.ToImmutable());

        static bool Overrides(MethodSymbol method, MethodSymbol target)
        {
            for (var current = method.OverriddenMethod; current is not null; current = current.OverriddenMethod)
                if (ReferenceEquals(current, target))
                    return true;
            return false;
        }
    }

    private static void AddBodyEffects(BoundBody body, ImmutableArray<EffectOperation>.Builder operations)
    {
        var syntax = body.Method.Syntax ?? body.Method.ContainingType.Syntax!;
        foreach (var statement in DescendantsAndSelf(body.Root))
        {
            if (statement.Kind == BoundStatementKind.Throw)
                operations.Add(new EffectOperation(statement.Syntax, EffectKind.Throws | EffectKind.UsesRuntime, "explicit throw"));
            else if (statement.Kind == BoundStatementKind.Try)
                operations.Add(new EffectOperation(statement.Syntax, EffectKind.Throws | EffectKind.UsesRuntime, "exception region"));
            else if (statement.Kind == BoundStatementKind.Defer)
                operations.Add(new EffectOperation(statement.Syntax, EffectKind.UsesRuntime, "defer cleanup"));
        }

        if (!body.Method.IsConstructor && body.Method.ReturnType.ContainsManagedReferences ||
            body.Method.Parameters.Any(parameter => parameter.Type.ContainsManagedReferences &&
                !(body.Method.RuntimeImplementation is not null && parameter.Type.IsNativeUtf8String)))
            operations.Add(new EffectOperation(syntax, EffectKind.UsesRuntime, "managed method signature"));

        foreach (var semantic in body.Semantics.Values
                     .Where(semantic => semantic.Type.ContainsManagedReferences &&
                         !(body.Method.RuntimeImplementation is not null && semantic.Type.IsNativeUtf8String && semantic.Symbol is ParameterSymbol) &&
                         semantic.Symbol is LocalSymbol or ParameterSymbol or FieldSymbol)
                     .GroupBy(semantic => semantic.Syntax)
                     .Select(group => group.First()))
            operations.Add(new EffectOperation(semantic.Syntax, EffectKind.UsesRuntime, "managed value or ARC operation"));

        foreach (var semantic in body.Semantics.Values.Where(semantic =>
                     semantic.Symbol is FieldSymbol { IsStatic: true, Initializer: not null } field && field.Type.ContainsManagedReferences))
            operations.Add(new EffectOperation(semantic.Syntax, EffectKind.UsesRuntime, "initialized managed static field"));

        static IEnumerable<BoundStatement> DescendantsAndSelf(BoundStatement statement)
        {
            yield return statement;
            foreach (var child in statement.Children)
                foreach (var descendant in DescendantsAndSelf(child))
                    yield return descendant;
        }
    }

    internal static EffectKind OperationEffects(
        EffectOperation operation,
        IReadOnlyDictionary<MethodSymbol, EffectKind> inferred,
        IReadOnlyDictionary<MethodSymbol, ImmutableArray<EffectOperation>> operations)
    {
        EffectKind effects;
        EffectContract trusted;
        if (operation.Target is null)
        {
            effects = operation.Effects;
            trusted = operation.TrustedContracts;
        }
        else
        {
            var target = operation.Target;
            trusted = target.DeclaredEffects | operation.TrustedContracts;
            effects = target.IsNativeBoundary || target.IsAbstract || operation.RequiresContract || !operations.ContainsKey(target)
                ? EffectKind.All
                : inferred.GetValueOrDefault(target);
        }

        foreach (var effect in IndividualEffects(effects))
            if (trusted.Proves(effect))
                effects &= ~effect;
        return effects;
    }

    private static void ValidateContracts(DiagnosticBag diagnostics, EffectAnalysis analysis)
    {
        var emitted = new HashSet<(EffectContract Contract, string File, int Start, string Witness)>();
        foreach (var pair in analysis.Operations
                     .Where(pair => pair.Key.DeclaredEffects != EffectContract.None)
                     .OrderBy(pair => pair.Key.Syntax?.Source.FilePath, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Syntax?.Span.Start ?? 0))
        {
            foreach (var contract in EffectFacts.IndividualContracts(pair.Key.DeclaredEffects))
            {
                var forbidden = EffectFacts.ForbiddenEffect(contract);
                foreach (var operation in pair.Value.Where(operation =>
                             (OperationEffects(operation, analysis.InferredEffects, analysis.Operations) & forbidden) != 0))
                {
                    var witness = Explain(operation, forbidden, analysis, []);
                    var key = (contract, operation.Syntax.Source.FilePath, operation.Syntax.Span.Start, witness);
                    if (!emitted.Add(key))
                        continue;
                    var code = contract switch
                    {
                        EffectContract.NoAlloc => "CT2155",
                        EffectContract.NoThrow => "CT2212",
                        EffectContract.NoBlock => "CT2213",
                        EffectContract.NoRuntime => "CT2214",
                        _ => throw new InvalidOperationException(),
                    };
                    diagnostics.Add(code,
                        $"{EffectFacts.ContractName(contract)} member '{EffectRegistry.Display(pair.Key)}' violates its contract: {witness}.",
                        operation.Syntax.Source, operation.Syntax.Span);
                }
            }
        }
    }

    internal static string Explain(EffectOperation operation, EffectKind effect, EffectAnalysis analysis, HashSet<MethodSymbol> visited)
    {
        if (operation.Target is null)
            return operation.Reason;
        var target = operation.Target;
        if (target.DeclaredEffects.Proves(effect))
            return operation.Reason;
        if (target.ExternName is not null)
            return $"extern call to '{EffectRegistry.Display(target)}' has no {RequiredContract(effect)} contract";
        if (target.IsNativeImport)
            return $"native-import call to '{EffectRegistry.Display(target)}' has no {RequiredContract(effect)} contract";
        if (operation.RequiresContract || target.IsAbstract)
            return $"virtual call to '{EffectRegistry.Display(target)}' has no {RequiredContract(effect)} contract";
        if (!visited.Add(target))
            return operation.Reason;
        var cause = analysis.Operations.GetValueOrDefault(target).FirstOrDefault(candidate =>
            (OperationEffects(candidate, analysis.InferredEffects, analysis.Operations) & effect) != 0);
        return cause is null ? operation.Reason : $"{operation.Reason} -> {Explain(cause, effect, analysis, visited)}";
    }

    private static string RequiredContract(EffectKind effect) => effect switch
    {
        EffectKind.Allocates => "NoAlloc",
        EffectKind.Throws => "NoThrow",
        EffectKind.Blocks => "NoBlock",
        EffectKind.UsesRuntime => "NoRuntime",
        _ => "effect",
    };

    internal static IEnumerable<EffectKind> IndividualEffects(EffectKind effects)
    {
        foreach (var effect in new[] { EffectKind.Allocates, EffectKind.Throws, EffectKind.Blocks, EffectKind.UsesRuntime })
            if ((effects & effect) != 0)
                yield return effect;
    }
}
