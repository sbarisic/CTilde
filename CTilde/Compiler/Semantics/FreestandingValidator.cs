using System.Collections.Immutable;

namespace CTilde;

internal static class FreestandingValidator
{
    private static readonly HashSet<string> ForbiddenExterns = new(StringComparer.Ordinal)
    {
        "ct_environment_exit",
        "ct_console_read",
        "ct_console_read_line",
        "ct_host_file_open",
        "ct_host_file_read",
        "ct_host_file_write_buffer",
        "ct_host_file_write_string",
        "ct_host_file_close",
    };

    public static void Validate(CompilationModel model, ImmutableArray<BoundBody>.Builder bodies, CompilationTarget target)
    {
        if (target != CompilationTarget.Freestanding)
            return;

        var allBodies = bodies.ToImmutable();
        var bodyByMethod = allBodies.GroupBy(body => body.Method).ToDictionary(group => group.Key, group => group.First());
        var roots = model.UserTypes.SelectMany(type => type.Methods)
            .Where(method => !method.IsNaked && (method.ExportName is not null || method.IsUsed))
            .Concat(allBodies.Where(body => body.Method.Name == "<module_init>").Select(body => body.Method))
            .Distinct()
            .ToArray();
        model.FreestandingRuntimeRequired = roots.Length != 0 || model.UserTypes.SelectMany(type => type.Fields).Any(field => field.IsUsed);

        var reachable = ReachableMethods(roots, bodyByMethod);
        model.FreestandingHeapRequired = reachable.Any(method => MayAllocate(method, bodyByMethod, []));

        if (model.FreestandingRuntimeRequired)
            Require(model, RuntimeImplementationRole.Panic);
        if (model.FreestandingHeapRequired)
        {
            Require(model, RuntimeImplementationRole.Allocate);
            Require(model, RuntimeImplementationRole.Free);
        }

        foreach (var body in allBodies.Where(body => reachable.Contains(body.Method)))
        {
            if (body.Flow.ContainsThrow || body.Flow.ContainsExceptionRegion)
                ReportUnavailable(model, body.Method, "Exceptions and exception regions are unavailable in freestanding compilations.");
            foreach (var use in body.ExternUses)
            {
                var name = use.Method.ExternName!;
                if (ForbiddenExterns.Contains(name) || name.StartsWith("ct_write_", StringComparison.Ordinal) ||
                    name.StartsWith("ct_math_", StringComparison.Ordinal) || name.StartsWith("ct_managed_", StringComparison.Ordinal))
                    model.Diagnostics.Add("CT4115", $"Native runtime API '{name}' is unavailable in freestanding compilations.", use.Syntax.Source, use.Syntax.Span);
            }
            foreach (var semantic in body.Semantics.Values)
            {
                if (semantic.Symbol is MethodSymbol { IsNaked: true } naked && naked != body.Method)
                    model.Diagnostics.Add("CT1302", "Naked methods cannot be invoked as ordinary C~ methods.", semantic.Syntax.Source, semantic.Syntax.Span);
                if (semantic.Symbol is TypeSymbol { Namespace: "System.Threading", Name: "Thread" or "Mutex" or "ThreadStart" } ||
                    semantic.Symbol is MethodSymbol { ContainingType.Namespace: "System.Threading", ContainingType.Name: "Thread" or "Mutex" })
                    model.Diagnostics.Add("CT4115", "Managed threading and synchronization are unavailable in freestanding compilations.", semantic.Syntax.Source, semantic.Syntax.Span);
                if (semantic.Symbol is TypeSymbol { FullName: "System.Math" } || semantic.Symbol is MethodSymbol { ContainingType.FullName: "System.Math" })
                    model.Diagnostics.Add("CT4115", "System.Math is unavailable in freestanding compilations.", semantic.Syntax.Source, semantic.Syntax.Span);
            }
            foreach (var effect in body.AllocationEffects.Where(effect => effect.Reason.StartsWith("conversion of '", StringComparison.Ordinal)))
                model.Diagnostics.Add("CT4115", "Runtime-formatted scalar conversion to string is unavailable in freestanding compilations.", effect.Syntax.Source, effect.Syntax.Span);
        }

        foreach (var implementation in model.RuntimeImplementations.Values)
        {
            if (!bodyByMethod.TryGetValue(implementation, out var body))
                continue;
            ValidateBootstrapBody(model, implementation, body, bodyByMethod, []);
        }
    }

    private static HashSet<MethodSymbol> ReachableMethods(IEnumerable<MethodSymbol> roots, IReadOnlyDictionary<MethodSymbol, BoundBody> bodies)
    {
        var reachable = new HashSet<MethodSymbol>();
        var pending = new Queue<MethodSymbol>(roots);
        while (pending.TryDequeue(out var method))
        {
            if (!reachable.Add(method) || !bodies.TryGetValue(method, out var body))
                continue;
            foreach (var target in body.AllocationEffects.Select(effect => effect.Target).Where(target => target is not null).Cast<MethodSymbol>())
                pending.Enqueue(target);
            foreach (var semantic in body.Semantics.Values)
                if (semantic.Symbol is MethodSymbol target)
                    pending.Enqueue(target);
        }
        return reachable;
    }

    private static bool MayAllocate(MethodSymbol method, IReadOnlyDictionary<MethodSymbol, BoundBody> bodies, HashSet<MethodSymbol> active)
    {
        if (!active.Add(method) || !bodies.TryGetValue(method, out var body))
            return false;
        try
        {
            foreach (var operation in body.AllocationEffects)
            {
                if (operation.Target is null || operation.Target.ExternName is not null && !operation.Target.IsNoAlloc ||
                    operation.RequiresContract && !operation.Target.IsNoAlloc || MayAllocate(operation.Target, bodies, active))
                    return true;
            }
            return false;
        }
        finally
        {
            active.Remove(method);
        }
    }

    private static bool ValidateBootstrapBody(CompilationModel model, MethodSymbol method, BoundBody body,
        IReadOnlyDictionary<MethodSymbol, BoundBody> bodies, HashSet<MethodSymbol> active)
    {
        if (!active.Add(method))
            return true;
        var invalid = body.Flow.ContainsThrow || body.Flow.ContainsExceptionRegion || body.Flow.ContainsDefer ||
            body.Semantics.Values.Any(semantic => semantic.Type.ContainsManagedReferences ||
                semantic.Symbol is FieldSymbol { IsStatic: true, Initializer: not null });
        foreach (var operation in body.AllocationEffects)
        {
            if (operation.Target is null || operation.Target.ExternName is not null && !operation.Target.IsNoAlloc ||
                operation.RequiresContract && !operation.Target.IsNoAlloc)
                invalid = true;
            else if (operation.Target is { ExternName: null } target && bodies.TryGetValue(target, out var targetBody) &&
                     !ValidateBootstrapBody(model, target, targetBody, bodies, active))
                invalid = true;
        }
        active.Remove(method);
        if (invalid)
            model.Diagnostics.Add("CT2211", $"Runtime implementation '{method.ContainingType.FullName}.{method.Name}' is not bootstrap-safe.",
                method.Syntax!.Source, method.Syntax.Span);
        return !invalid;
    }

    private static void Require(CompilationModel model, RuntimeImplementationRole role)
    {
        if (model.RuntimeImplementations.ContainsKey(role))
            return;
        var source = model.UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        model.Diagnostics.Add("CT4114", $"Freestanding compilation requires one RuntimeImpl(Runtime.{role}) method.", source, new TextSpan(0, 0));
    }

    private static void ReportUnavailable(CompilationModel model, MethodSymbol method, string message)
    {
        var syntax = method.Syntax ?? method.ContainingType.Syntax!;
        model.Diagnostics.Add("CT4115", message, syntax.Source, syntax.Span);
    }
}
