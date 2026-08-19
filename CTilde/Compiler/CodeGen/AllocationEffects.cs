using System.Collections.Immutable;

namespace CTilde;

internal sealed record AllocationOperation(
    SyntaxNode Syntax,
    string Reason,
    MethodSymbol? Target = null,
    bool RequiresContract = false);

internal sealed class AllocationEffectRegistry
{
    private readonly Dictionary<MethodSymbol, List<AllocationOperation>> _operations = [];

    public ImmutableDictionary<MethodSymbol, ImmutableArray<AllocationOperation>> Snapshot() =>
        _operations.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray());

    public void RecordDirect(MethodSymbol method, SyntaxNode syntax, string reason) =>
        Operations(method).Add(new AllocationOperation(syntax, reason));

    public void RecordCall(MethodSymbol method, MethodSymbol target, SyntaxNode syntax, bool requiresContract) =>
        Operations(method).Add(new AllocationOperation(syntax, $"call to '{Display(target)}'", target, requiresContract));

    public void Validate(DiagnosticBag diagnostics)
    {
        var mayAllocate = _operations.Keys.ToDictionary(method => method, method =>
            _operations[method].Any(operation => IsImmediateAllocation(operation)));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var pair in _operations)
            {
                if (mayAllocate[pair.Key])
                    continue;
                if (!pair.Value.Any(operation => OperationMayAllocate(operation, mayAllocate)))
                    continue;
                mayAllocate[pair.Key] = true;
                changed = true;
            }
        }

        foreach (var pair in _operations.Where(pair => pair.Key.IsNoAlloc))
        {
            foreach (var operation in pair.Value.Where(operation => OperationMayAllocate(operation, mayAllocate)))
            {
                var witness = Explain(operation, mayAllocate, []);
                diagnostics.Add("CT2155", $"NoAlloc member '{Display(pair.Key)}' may allocate: {witness}.", operation.Syntax.Source, operation.Syntax.Span);
            }
        }
    }

    private List<AllocationOperation> Operations(MethodSymbol method)
    {
        if (!_operations.TryGetValue(method, out var operations))
        {
            operations = [];
            _operations.Add(method, operations);
        }
        return operations;
    }

    private static bool IsImmediateAllocation(AllocationOperation operation) =>
        operation.Target is null ||
        operation.Target.ExternName is not null && !operation.Target.IsNoAlloc ||
        operation.RequiresContract && !operation.Target.IsNoAlloc;

    private static bool OperationMayAllocate(AllocationOperation operation, IReadOnlyDictionary<MethodSymbol, bool> mayAllocate)
    {
        if (IsImmediateAllocation(operation))
            return true;
        if (operation.Target is null || operation.Target.IsNoAlloc && operation.RequiresContract)
            return false;
        return mayAllocate.GetValueOrDefault(operation.Target);
    }

    private string Explain(AllocationOperation operation, IReadOnlyDictionary<MethodSymbol, bool> mayAllocate, HashSet<MethodSymbol> visited)
    {
        if (operation.Target is null)
            return operation.Reason;
        if (operation.Target.ExternName is not null && !operation.Target.IsNoAlloc)
            return $"extern call to '{Display(operation.Target)}' has no NoAlloc contract";
        if (operation.RequiresContract && !operation.Target.IsNoAlloc)
            return $"virtual call to '{Display(operation.Target)}' has no NoAlloc contract";
        if (!visited.Add(operation.Target) || !_operations.TryGetValue(operation.Target, out var nested))
            return operation.Reason;
        var cause = nested.FirstOrDefault(candidate => OperationMayAllocate(candidate, mayAllocate));
        return cause is null ? operation.Reason : $"{operation.Reason} -> {Explain(cause, mayAllocate, visited)}";
    }

    private static string Display(MethodSymbol method) => $"{method.ContainingType.FullName}.{method.Name}";
}
