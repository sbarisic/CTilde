using System.Collections.Immutable;

namespace CTilde;

internal static class ManagedOverlayPlacementInferer
{
    public static void Apply(CompilationModel model, TypedIrProgram program)
    {
        if (model.Target != CompilationTarget.EspIdf ||
            model.Architecture != CompilationArchitecture.Xtensa ||
            model.ManagedModuleKind is null)
            return;

        var methods = program.Functions.Select(function => function.Method)
            .ToImmutableHashSet();
        foreach (var method in methods.Where(method => method.IsInferredOverlay))
        {
            method.OverlayName = null;
            method.IsInferredOverlay = false;
            method.RequiresOverlayEntry = true;
            method.OverlayPlacementReason = null;
        }
        foreach (var property in program.Functions.Where(function => function.Property?.IsInferredOverlay == true)
                     .Select(function => function.Property!).Distinct())
        {
            property.OverlayName = null;
            property.IsInferredOverlay = false;
        }

        var explicitOverlays = methods
            .Where(method => method.OverlayName is not null)
            .ToImmutableHashSet();
        foreach (var method in explicitOverlays)
        {
            method.RequiresOverlayEntry = true;
            method.OverlayPlacementReason = "explicit overlay";
        }
        if (explicitOverlays.Count == 0)
            return;

        var calls = methods.ToDictionary(method => method, method =>
            model.Effects.CallTargets.GetValueOrDefault(method, [])
                .Where(methods.Contains).Distinct().ToImmutableArray());
        var callers = methods.ToDictionary(method => method, _ => new HashSet<MethodSymbol>());
        foreach (var (caller, targets) in calls)
            foreach (var target in targets)
                callers[target].Add(caller);

        var publicProjectTypes = model.ProjectTypes
            .Where(type => type.Accessibility == Accessibility.Public).ToHashSet();
        bool CanInfer(MethodSymbol method) =>
            method.Syntax is not null &&
            !method.IsExplicitlyResident &&
            !model.UnmanagedAddressTargets.Contains(method) &&
            !model.DelegateTargets.Contains(method) &&
            !method.IsEntryPoint &&
            !method.IsConstructor &&
            !method.IsNativeBoundary &&
            method.ExportName is null &&
            method.RuntimeImplementation is null &&
            method.SectionName is null &&
            !method.IsUsed &&
            !method.IsNaked &&
            !method.IsInterrupt &&
            !method.IsVirtual &&
            !method.IsOverride &&
            !method.IsAbstract &&
            method.ImplementedInterfaceMethods.Count == 0 &&
            !(publicProjectTypes.Contains(method.ContainingType) && method.Accessibility == Accessibility.Public);

        var owners = methods.ToDictionary(method => method, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var seed in explicitOverlays.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal))
        {
            var overlay = seed.OverlayName!;
            var pending = new Queue<MethodSymbol>();
            foreach (var target in calls[seed]) pending.Enqueue(target);
            var visited = new HashSet<MethodSymbol>();
            while (pending.TryDequeue(out var method))
            {
                if (!visited.Add(method)) continue;
                if (explicitOverlays.Contains(method))
                {
                    if (method.OverlayName == overlay)
                        foreach (var target in calls[method]) pending.Enqueue(target);
                    continue;
                }
                if (!CanInfer(method)) continue;
                owners[method].Add(overlay);
                foreach (var target in calls[method]) pending.Enqueue(target);
            }
        }

        var resident = new HashSet<MethodSymbol>();
        var residentPending = new Queue<MethodSymbol>(methods.Where(method =>
            !explicitOverlays.Contains(method) && (!CanInfer(method) || callers[method].Count == 0))
            .OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal));
        while (residentPending.TryDequeue(out var method))
        {
            if (!resident.Add(method)) continue;
            foreach (var target in calls[method])
            {
                if (explicitOverlays.Contains(target)) continue;
                residentPending.Enqueue(target);
            }
        }

        foreach (var method in methods.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal))
        {
            if (explicitOverlays.Contains(method))
                continue;
            if (!CanInfer(method))
            {
                method.OverlayPlacementReason ??= ResidentReason(method, publicProjectTypes);
                continue;
            }
            if (resident.Contains(method))
            {
                method.OverlayPlacementReason = "resident caller or root";
                continue;
            }
            if (owners[method].Count != 1)
            {
                method.OverlayPlacementReason = owners[method].Count == 0
                    ? "not reachable exclusively from an overlay"
                    : "shared by multiple overlays";
                continue;
            }
            method.OverlayName = owners[method].Single();
            method.IsInferredOverlay = true;
            method.RequiresOverlayEntry = false;
            method.OverlayPlacementReason = "inferred exclusive helper";
        }

        foreach (var method in explicitOverlays.OrderBy(NameMangler.MethodIdentity, StringComparer.Ordinal))
        {
            var externallyVisible = model.DelegateTargets.Contains(method) || method.IsConstructor || method.IsEntryPoint || method.IsNativeBoundary || method.ExportName is not null ||
                method.RuntimeImplementation is not null || method.SectionName is not null || method.IsUsed ||
                method.IsNaked || method.IsInterrupt || method.IsVirtual || method.IsOverride || method.IsAbstract ||
                method.ImplementedInterfaceMethods.Count != 0 ||
                publicProjectTypes.Contains(method.ContainingType) && method.Accessibility == Accessibility.Public;
            var hasOutsideCaller = callers[method].Any(caller => caller.OverlayName != method.OverlayName);
            method.RequiresOverlayEntry = externallyVisible || hasOutsideCaller;
            method.OverlayPlacementReason = method.RequiresOverlayEntry
                ? externallyVisible ? "explicit overlay with externally visible entry" : "explicit overlay with resident or cross-overlay caller"
                : "explicit overlay with local direct calls";
        }

        foreach (var group in program.Functions.Where(function => function.Property is not null)
                     .GroupBy(function => function.Property!))
        {
            var property = group.Key;
            if (property.OverlayName is not null || property.IsExplicitlyResident)
                continue;
            var accessors = group.Select(function => function.Method).Distinct().ToArray();
            var inferred = accessors.Select(method => method.OverlayName).Distinct(StringComparer.Ordinal).ToArray();
            if (accessors.All(method => method.IsInferredOverlay) && inferred is [{ } overlay])
            {
                property.OverlayName = overlay;
                property.IsInferredOverlay = true;
            }
        }

        string ResidentReason(MethodSymbol method, HashSet<TypeSymbol> publicProjectTypes)
        {
            if (model.UnmanagedAddressTargets.Contains(method)) return "unmanaged address target";
            if (model.DelegateTargets.Contains(method)) return "delegate target";
            if (method.IsExplicitlyResident) return "explicit resident";
            if (method.IsEntryPoint) return "entry point";
            if (method.IsConstructor) return "class or value construction entry";
            if (method.IsNativeBoundary) return "native boundary";
            if (method.ExportName is not null) return "native export";
            if (method.RuntimeImplementation is not null) return "runtime implementation";
            if (method.SectionName is not null) return "explicit native section";
            if (method.IsUsed) return "address retained";
            if (method.IsNaked || method.IsInterrupt) return "special machine-code entry";
            if (method.IsVirtual || method.IsOverride || method.IsAbstract || method.ImplementedInterfaceMethods.Count != 0)
                return "dynamic dispatch target";
            if (publicProjectTypes.Contains(method.ContainingType) && method.Accessibility == Accessibility.Public)
                return "public managed-module API";
            return "not eligible for overlay inference";
        }
    }
}
