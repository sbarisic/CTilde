using System.Collections.Immutable;

namespace CTilde;

internal abstract record IrInstruction(CType Type, string Text);

internal sealed record IrLabel(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrBranch(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrConditionalBranch(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrLocal(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrStore(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrCall(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrCheck(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrReturn(CType Type, string Text) : IrInstruction(Type, Text);
internal sealed record IrRaw(CType Type, string Text) : IrInstruction(Type, Text);

internal sealed record IrFunction(MethodSymbol Method, ImmutableArray<IrInstruction> Instructions)
{
    public string Render() => string.Join('\n', Instructions.Select(instruction => instruction.Text));
}

internal sealed record TypedIrProgram(ImmutableArray<IrFunction> Functions, ImmutableArray<IrInstruction> ModuleInitializer);

internal sealed class TypedIrLowerer(CompilationModel model, CEmitter emitter)
{
    public TypedIrProgram Lower()
    {
        emitter.RegisterDeclaredTypes();
        var definitions = ImmutableArray.CreateBuilder<IrFunction>();
        foreach (var type in model.UserTypes)
        {
            if (type.Kind == DeclaredTypeKind.Enum)
                continue;
            foreach (var constructor in type.Constructors)
                definitions.Add(ToIr(constructor, new MethodLowerer(emitter, constructor).EmitDefinition()));
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                definitions.Add(ToIr(method, new MethodLowerer(emitter, method).EmitDefinition()));
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                {
                    var method = emitter.GetAccessorMethod(property, true);
                    definitions.Add(ToIr(method, LowerAccessor(property, method, true)));
                }
                if (property.Setter is not null)
                {
                    var method = emitter.GetAccessorMethod(property, false);
                    definitions.Add(ToIr(method, LowerAccessor(property, method, false)));
                }
            }
        }
        ValidateConstructorCycles();
        return new TypedIrProgram(definitions.ToImmutable(), ToInstructions(LowerModuleInitializer(), CType.Void));
    }

    private void ValidateConstructorCycles()
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

    private string LowerAccessor(PropertySymbol property, MethodSymbol method, bool getter)
    {
        var name = getter ? NameMangler.Getter(property) : NameMangler.Setter(property);
        return new MethodLowerer(emitter, method, name, property, getter).EmitDefinition();
    }

    private string LowerModuleInitializer()
    {
        var writer = new CWriter();
        writer.WriteLine("static void ct_module_init(void)");
        writer.WriteLine("{");
        var initializerIndex = 0;
        foreach (var field in model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Initializer is not null && field.Name != "<underlying>"))
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
            var lowerer = new MethodLowerer(emitter, method, temporaryPrefix: $"_mi_{initializerIndex++}");
            var expression = lowerer.LowerStandalone(field.Initializer!);
            foreach (var line in expression.Prelude)
                writer.WriteLine("    " + line);
            var value = lowerer.ConvertStandalone(expression, field.Type, field.Initializer!);
            if (field.IsConst && !value.IsConstant)
                model.Diagnostics.Add("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer!.Source, field.Initializer.Span);
            foreach (var line in value.Prelude.Skip(expression.Prelude.Count))
                writer.WriteLine("    " + line);
            writer.WriteLine($"    {field.CName} = {value.Code};");
        }
        writer.WriteLine("}");
        return writer.ToString();
    }

    private static IrFunction ToIr(MethodSymbol method, string definition) => new(method, ToInstructions(definition, method.ReturnType));

    private static ImmutableArray<IrInstruction> ToInstructions(string text, CType type) => text
        .TrimEnd().Split('\n').Select(line => Classify(type, line.TrimEnd('\r'))).ToImmutableArray();

    private static IrInstruction Classify(CType type, string text)
    {
        var code = text.Trim();
        if (code.EndsWith(":;", StringComparison.Ordinal))
            return new IrLabel(type, text);
        if (code.StartsWith("if ", StringComparison.Ordinal) && code.Contains(" goto ", StringComparison.Ordinal))
            return new IrConditionalBranch(type, text);
        if (code.StartsWith("goto ", StringComparison.Ordinal))
            return new IrBranch(type, text);
        if (code.StartsWith("return", StringComparison.Ordinal))
            return new IrReturn(type, text);
        if (code.Contains("ct_require_nonnull", StringComparison.Ordinal) || code.Contains("ct_bounds", StringComparison.Ordinal))
            return new IrCheck(type, text);
        if ((code.Contains("ct_tmp", StringComparison.Ordinal) || code.Contains("ct_l_", StringComparison.Ordinal)) && code.Contains(" = ", StringComparison.Ordinal))
            return new IrLocal(type, text);
        if (code.Contains(" = ", StringComparison.Ordinal))
            return new IrStore(type, text);
        if (code.Contains('(') && code.EndsWith(';'))
            return new IrCall(type, text);
        return new IrRaw(type, text);
    }
}

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
        "ct_esp_gpio_configure_input",
        "ct_esp_gpio_configure_output",
        "ct_esp_gpio_write",
        "ct_esp_gpio_read",
        "ct_esp_ws2812_configure",
        "ct_esp_ws2812_set_pixel",
        "ct_esp_ws2812_refresh",
        "ct_esp_ws2812_clear",
    };

    public static void Validate(CompilationModel model, CEmitter emitter, CompilationTarget target)
    {
        if (!Enum.IsDefined(target))
        {
            var source = model.UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
            model.Diagnostics.Add("CT4104", $"Compilation target value '{(int)target}' is not supported.", source, new TextSpan(0, 0));
        }

        var names = new Dictionary<string, MethodSymbol>(StringComparer.Ordinal);
        foreach (var method in model.Types.Values.SelectMany(type => type.Methods).Where(method => method.ExternName is null))
        {
            if (names.TryAdd(method.CName, method))
                continue;
            var earlier = names[method.CName];
            model.Diagnostics.Add("CT4103", $"Generated C symbol '{method.CName}' is not unique.", method.Syntax!.Source, method.Syntax.Span,
                earlier.Syntax?.Source.GetLocation(earlier.Syntax.Span));
        }

        var dynamicSymbols = emitter.DynamicGeneratedSymbols.ToHashSet(StringComparer.Ordinal);
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
        foreach (var type in model.UserTypes.Where(type => type.Kind != DeclaredTypeKind.Enum))
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
