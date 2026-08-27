using System.Collections.Immutable;

namespace CTilde;

internal static class RecursionAnalyzer
{
    public static void Validate(CompilationModel model, ImmutableArray<BoundBody>.Builder bodies, bool projectWide)
    {
        var bodyByMethod = bodies.ToDictionary(body => body.Method);
        var edges = model.Effects.CallTargets.ToDictionary(pair => pair.Key,
            pair => new SortedSet<MethodSymbol>(pair.Value, MethodComparer.Instance));
        var unknown = model.Effects.UnknownCalls;

        var roots = new SortedSet<MethodSymbol>(MethodComparer.Instance);
        foreach (var method in bodyByMethod.Keys.Where(method => method.IsNoRecursion))
            roots.Add(method);
        if (projectWide)
            foreach (var method in bodyByMethod.Keys.Where(method => method.IsEntryPoint || method.ExportName is not null || method.IsUsed || method.TaskStackSize is not null))
                roots.Add(method);

        foreach (var root in roots)
        {
            var reachable = Reachable(root, edges);
            var unresolved = reachable.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal).FirstOrDefault(unknown.ContainsKey);
            if (unresolved is not null)
            {
                var syntax = unknown[unresolved];
                model.Diagnostics.Add("CT2206", $"NoRecursion cannot prove a closed call target set in '{Display(unresolved)}'.", syntax.Source, syntax.Span);
            }

            var cycle = FindCycle(root, edges, reachable);
            if (cycle.IsDefaultOrEmpty)
                continue;
            var rootSyntax = root.Syntax ?? root.ContainingType.Syntax!;
            model.Diagnostics.Add("CT2206", $"NoRecursion call graph contains a cycle: {string.Join(" -> ", cycle.Select(Display))}.", rootSyntax.Source, rootSyntax.Span);
        }
    }

    private static HashSet<MethodSymbol> Reachable(MethodSymbol root, IReadOnlyDictionary<MethodSymbol, SortedSet<MethodSymbol>> edges)
    {
        var visited = new HashSet<MethodSymbol>();
        var pending = new Stack<MethodSymbol>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var method = pending.Pop();
            if (!visited.Add(method) || !edges.TryGetValue(method, out var targets))
                continue;
            foreach (var target in targets.Reverse())
                pending.Push(target);
        }
        return visited;
    }

    private static ImmutableArray<MethodSymbol> FindCycle(MethodSymbol root, IReadOnlyDictionary<MethodSymbol, SortedSet<MethodSymbol>> edges, HashSet<MethodSymbol> allowed)
    {
        var active = new Dictionary<MethodSymbol, int>();
        var complete = new HashSet<MethodSymbol>();
        var path = new List<MethodSymbol>();
        ImmutableArray<MethodSymbol> result = [];

        bool Visit(MethodSymbol method)
        {
            active[method] = path.Count;
            path.Add(method);
            if (edges.TryGetValue(method, out var targets))
            {
                foreach (var target in targets.Where(allowed.Contains))
                {
                    if (active.TryGetValue(target, out var index))
                    {
                        result = [.. path.Skip(index), target];
                        return true;
                    }
                    if (!complete.Contains(target) && Visit(target))
                        return true;
                }
            }
            path.RemoveAt(path.Count - 1);
            active.Remove(method);
            complete.Add(method);
            return false;
        }

        Visit(root);
        return result;
    }

    private static string Display(MethodSymbol method) => $"{method.ContainingType.FullName}.{method.Name}";

    private static CType? SymbolType(object? symbol) => symbol switch
    {
        LocalSymbol local => local.Type,
        ParameterSymbol parameter => parameter.Type,
        FieldSymbol field => field.Type,
        PropertySymbol property => property.Type,
        _ => null,
    };

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
    {
        yield return root;
        foreach (var child in root.ChildNodesAndTokens().Where(child => child.IsNode).Select(child => child.Node!))
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private static bool Overrides(MethodSymbol method, MethodSymbol target)
    {
        for (var current = method.OverriddenMethod; current is not null; current = current.OverriddenMethod)
            if (ReferenceEquals(current, target))
                return true;
        return false;
    }

    private sealed class MethodComparer : IComparer<MethodSymbol>
    {
        public static MethodComparer Instance { get; } = new();
        public int Compare(MethodSymbol? x, MethodSymbol? y) => ReferenceEquals(x, y) ? 0 : string.Compare(NameMangler.MethodIdentity(x!), NameMangler.MethodIdentity(y!), StringComparison.Ordinal);
    }
}
