namespace CTilde;

internal static class TargetValidator
{
    private static readonly HashSet<string> EspIdfSymbols = new(StringComparer.Ordinal)
    {
        "app_main",
        "ct_esp_delay_ms",
        "ct_esp_tick_count",
        "ct_esp_stack_high_water_mark",
        "ct_esp_restart",
        "ct_esp_free_heap_size",
        "ct_esp_minimum_free_heap_size",
        "ct_esp_timer_get_time_us",
        "ct_esp_gpio_configure_input",
        "ct_esp_gpio_configure_output",
        "ct_esp_gpio_write",
        "ct_esp_gpio_read",
        "ct_esp_ws2812_configure",
        "ct_esp_ws2812_set_pixel",
        "ct_esp_ws2812_refresh",
        "ct_esp_ws2812_clear",
        "ct_esp_error_name",
        "ct_esp_current_task",
        "ct_esp_thread_state_get",
        "ct_esp_thread_state_set",
    };

    public static void Validate(CompilationModel model, ILoweringServices emitter, CompilationTarget target)
    {
        if (!Enum.IsDefined(target))
        {
            var source = model.UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
            model.Diagnostics.Add("CT4104", $"Compilation target value '{(int)target}' is not supported.", source, new TextSpan(0, 0));
        }

        var names = new Dictionary<string, MethodSymbol>(StringComparer.Ordinal);
        foreach (var method in model.Types.Values
                     .Where(type => !type.IsGenericDefinition && !type.IsOpenConstructed)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.ExternName is null && !method.IsGenericDefinition))
        {
            if (names.TryAdd(method.CName, method))
                continue;
            var earlier = names[method.CName];
            model.Diagnostics.Add("CT4103", $"Generated C symbol '{method.CName}' is not unique.", method.Syntax!.Source, method.Syntax.Span,
                earlier.Syntax?.Source.GetLocation(earlier.Syntax.Span));
        }

        ValidateManagedOverlays(model);

        var dynamicSymbols = emitter.DynamicGeneratedSymbols.ToHashSet(StringComparer.Ordinal);
        foreach (var method in model.Types.Values.SelectMany(type => type.Methods)
                     .Where(method => method.ExternName?.StartsWith("ct_idf_", StringComparison.Ordinal) == true && !method.IsTrustedExtern))
            model.Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' uses the reserved ESP-IDF binding prefix.", method.Syntax!.Source, method.Syntax.Span);
        foreach (var method in model.Types.Values.SelectMany(type => type.Methods)
                     .Where(method => method.ExternName is not null && !method.IsTrustedExtern && dynamicSymbols.Contains(method.ExternName)))
        {
            model.Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' conflicts with a generated C symbol.", method.Syntax!.Source, method.Syntax.Span);
        }

        if (target == CompilationTarget.EspIdf)
        {
            foreach (var method in model.Types.Values.SelectMany(type => type.Methods)
                         .Where(method => method.ExternName is not null && !method.IsTrustedExtern && EspIdfSymbols.Contains(method.ExternName)))
            {
                model.Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' conflicts with an ESP-IDF target symbol.", method.Syntax!.Source, method.Syntax.Span);
            }

            if (!model.RuntimeImplementations.ContainsKey(RuntimeImplementationRole.Exit))
                foreach (var use in emitter.ExternUses.Where(use => use.Method.ExternName == "ct_environment_exit"))
                    model.Diagnostics.Add("CT4105", "System.Environment.Exit on ESP-IDF requires RuntimeImpl(Runtime.Exit); use Esp.Idf.EspSystem.Restart when a reset is intended.", use.Syntax.Source, use.Syntax.Span);
        }
        else
        {
            var userRoots = model.Effects.Operations.Keys.Where(method =>
                method.Syntax is not null && model.UserSyntaxTrees.Any(tree => ReferenceEquals(tree.Text, method.Syntax.Source)));
            var reachableFromUser = model.Effects.ReachableMethods(userRoots);
            foreach (var caller in reachableFromUser)
            {
                foreach (var operation in model.Effects.Operations.GetValueOrDefault(caller).Where(operation =>
                             operation.Target?.ContainingType.FullName == "System.Diagnostics.ProcessRuntime" &&
                             operation.Target.ExternName?.StartsWith("ct_managed_process_", StringComparison.Ordinal) == true))
                    model.Diagnostics.Add("CT6206", "System.Diagnostics.Process is available only to ESP-IDF firmware and managed modules that link Runtime ABI 21.", operation.Syntax.Source, operation.Syntax.Span);
            }
        }

        var complete = new HashSet<TypeSymbol>();
        var active = new HashSet<TypeSymbol>();
        foreach (var type in model.UserTypes.Where(type => type.Kind is not DeclaredTypeKind.Enum and not DeclaredTypeKind.Newtype and not DeclaredTypeKind.Opaque))
            VisitLayout(type);

        void VisitLayout(TypeSymbol type)
        {
            if (complete.Contains(type))
                return;
            if (!active.Add(type))
            {
                model.Diagnostics.Add("CT4100", $"Type '{type.FullName}' has a recursive value-type layout.", type.Syntax!.Source, type.Syntax.Span);
                return;
            }
            foreach (var dependency in type.Fields.Where(field => !field.IsStatic && field.Type.Kind == CTypeKind.Struct).Select(field => field.Type.Symbol!).Distinct())
                VisitLayout(dependency);
            active.Remove(type);
            complete.Add(type);
        }
    }

    private static void ValidateManagedOverlays(CompilationModel model)
    {
        var declaredOverlaySyntax = model.ProjectDeclaredTypes
            .Where(type => type.OverlayName is not null)
            .Select(type => type.Syntax!)
            .Concat(model.ProjectTypes.SelectMany(type => type.Methods.Concat(type.Constructors))
                .Where(method => method.IsOverlay).Select(method => method.Syntax!).Where(syntax => syntax is not null))
            .Concat(model.ProjectTypes.SelectMany(type => type.Properties)
                .Where(property => property.OverlayName is not null).Select(property => property.Syntax!).Where(syntax => syntax is not null))
            .Distinct()
            .ToArray();
        if (declaredOverlaySyntax.Length != 0 &&
            (model.Target != CompilationTarget.EspIdf || model.Architecture != CompilationArchitecture.Xtensa || model.ManagedModuleKind is null))
        {
            foreach (var syntax in declaredOverlaySyntax)
                model.Diagnostics.Add("CT6232", "Overlay code is supported only by ESP-IDF Xtensa managed applications and libraries.", syntax.Source, syntax.Span);
        }

        foreach (var property in model.ProjectTypes.SelectMany(type => type.Properties)
                     .Where(property => property.OverlayName is not null && property.IsAbstract))
            model.Diagnostics.Add("CT6231", "Overlay placement requires concrete property accessors.", property.Syntax!.Source, property.Syntax.Span);

        if (!model.HasOverlays)
            return;

        var roots = model.ProjectTypes.SelectMany(type => type.Methods.Concat(type.Constructors));
        var reachable = model.Effects.ReachableMethods(roots);
        foreach (var operation in reachable.SelectMany(method => model.Effects.Operations.TryGetValue(method, out var operations)
                         ? operations : [])
                     .Where(operation => operation.Target is { ContainingType.FullName: "System.Threading.Thread", Name: "Start" }))
            model.Diagnostics.Add("CT6233", "An overlay-enabled managed application or dependency closure cannot create C~ threads.", operation.Syntax.Source, operation.Syntax.Span);

        foreach (var interrupt in model.ProjectTypes.SelectMany(type => type.Methods).Where(method => method.IsInterrupt))
        {
            var interruptClosure = model.Effects.ReachableMethods([interrupt]);
            foreach (var overlay in interruptClosure.Where(method => method.IsOverlay))
            {
                var syntax = overlay.Syntax ?? interrupt.Syntax;
                if (syntax is not null)
                    model.Diagnostics.Add("CT6235", "An interrupt call closure cannot enter managed overlay code.", syntax.Source, syntax.Span);
            }
        }
    }
}
