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

            foreach (var use in emitter.ExternUses.Where(use => use.Method.ExternName == "ct_environment_exit"))
                model.Diagnostics.Add("CT4105", "System.Environment.Exit is not available for the ESP-IDF target; use Esp.Idf.EspSystem.Restart when a reset is intended.", use.Syntax.Source, use.Syntax.Span);
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
}
