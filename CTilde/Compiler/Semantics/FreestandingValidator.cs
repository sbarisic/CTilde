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
        model.FreestandingRuntimeRequired = roots.Length != 0 || model.UserTypes.SelectMany(type => type.Fields).Any(field => field.IsUsed && !field.IsConstInit);

        var reachable = model.Effects.ReachableMethods(roots);
        model.FreestandingHeapRequired = reachable.Any(method => (model.Effects.GetEffects(method) & EffectKind.Allocates) != 0);

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
            foreach (var effect in body.EffectOperations.Where(effect => effect.Reason.StartsWith("conversion of '", StringComparison.Ordinal)))
                model.Diagnostics.Add("CT4115", "Runtime-formatted scalar conversion to string is unavailable in freestanding compilations.", effect.Syntax.Source, effect.Syntax.Span);
        }

        foreach (var implementation in model.RuntimeImplementations.Values)
        {
            if (!bodyByMethod.TryGetValue(implementation, out var body))
                continue;
            ValidateBootstrapBody(model, implementation);
        }
    }

    private static bool ValidateBootstrapBody(CompilationModel model, MethodSymbol method)
    {
        var invalid = (model.Effects.GetBootstrapEffects(method) &
            (EffectKind.Allocates | EffectKind.Throws | EffectKind.UsesRuntime)) != 0;
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
