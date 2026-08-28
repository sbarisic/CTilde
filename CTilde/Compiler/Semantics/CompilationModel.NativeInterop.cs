namespace CTilde;

internal sealed partial class CompilationModel
{
    private void ValidateNativeSections()
    {
        var declarations = Types.Values.Where(type => type.Syntax is not null)
            .SelectMany(type => type.Methods.Where(method => method.SectionName is not null)
                .Select(method => (Name: method.SectionName!, Kind: NativeSectionKind.Code, Syntax: method.Syntax!))
                .Concat(type.Fields.Where(field => field.SectionName is not null)
                    .Select(field => (Name: field.SectionName!, Kind: field.IsConstInit ? NativeSectionKind.ReadOnlyData : NativeSectionKind.Data, Syntax: field.Syntax!))))
            .GroupBy(item => item.Syntax)
            .Select(group => group.First())
            .OrderBy(item => item.Syntax.Source.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Syntax.Span.Start)
            .ToArray();
        var sections = new Dictionary<string, (NativeSectionKind Kind, SyntaxNode Syntax)>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (!sections.TryGetValue(declaration.Name, out var previous))
            {
                sections.Add(declaration.Name, (declaration.Kind, declaration.Syntax));
                continue;
            }
            if (previous.Kind != declaration.Kind)
                Diagnostics.Add("CT4107", $"Native section '{declaration.Name}' cannot mix code, writable data, and read-only data definitions.", declaration.Syntax.Source, declaration.Syntax.Span,
                    previous.Syntax.Source.GetLocation(previous.Syntax.Span));
        }
    }

    private void ValidateExternalSymbols()
    {
        var runtimeSymbols = new HashSet<string>(StringComparer.Ordinal)
        {
            "main", "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_dealloc", "ct_retain", "ct_release", "ct_memory_retain", "ct_memory_release", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_i64_bits", "ct_i64_add", "ct_i64_sub", "ct_i64_mul", "ct_i64_neg", "ct_i64_div", "ct_i64_mod",
            "ct_u64_div", "ct_u64_mod", "ct_i64_shl", "ct_i64_shr",
            "ct_ni_bits", "ct_ni_add", "ct_ni_sub", "ct_ni_mul", "ct_ni_neg", "ct_ni_div", "ct_ni_mod", "ct_nu_div", "ct_nu_mod", "ct_ni_shl", "ct_ni_shr",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint", "ct_to_string_long", "ct_to_string_ulong",
            "ct_to_string_float", "ct_to_string_double", "ct_to_string_bool", "ct_to_string_char", "ct_to_string_rune", "ct_validate_rune", "ct_utf8_encode_rune", "ct_write_string", "ct_write_char", "ct_write_rune",
            "ct_write_int", "ct_write_uint", "ct_write_long", "ct_write_ulong", "ct_write_float", "ct_write_double", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_math_sqrt", "ct_math_abs", "ct_math_tan", "ct_math_min", "ct_math_max", "ct_math_sin", "ct_math_cos", "ct_math_floor", "ct_math_ceiling",
            "ct_console_read", "ct_console_read_line", "ct_host_file_open", "ct_host_file_read", "ct_host_file_write_buffer", "ct_host_file_write_string", "ct_host_file_close",
            "ct_host_io_throw", "ct_host_utf8_valid", "ct_host_file", "ct_host_file_require", "ct_host_write_all",
            "ct_to_string_nint", "ct_to_string_nuint", "ct_write_nint", "ct_write_nuint", "ct_native_bounds", "ct_stack_bytes",
            "ct_module_init", "ct_string", "ct_object", "ct_type_descriptor", "ct_vtable",
            "ct_init_object", "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash",
            "ct_object_to_string", "ct_object_base_to_string", "ct_object_hash", "ct_object_reference_equals", "ct_type_is_assignable",
            "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_hash_double", "ct_object_value_equals",
            "ct_object_value_hash", "ct_default_vtable", "ct_string_vtable", "ct_desc_string",
            "ct_string_v_to_string", "ct_string_v_equals", "ct_string_v_hash", "NAN", "INFINITY",
            "ct_cleanup_record", "ct_cleanup_top", "ct_cleanup_push", "ct_cleanup_unwind_to", "ct_cleanup_disarm",
            "ct_release_head", "ct_release_draining", "ct_retain_ref_value", "ct_drop_ref_value", "ct_drop_string",
            "ct_memory_live_allocations", "ct_memory_live_objects", "ct_memory_total_allocations",
            "ct_exception_frame", "ct_exception_top", "ct_current_exception", "ct_throw", "ct_unhandled_exception", "setjmp", "longjmp", "CT_NORETURN",
            "ct_native_utf8_string", "ct_native_utf8_borrow", "ct_native_utf8_null", "ct_retain_value_nu8", "ct_drop_value_nu8",
            "ct_atomic_u32", "ct_atomic_load_relaxed", "ct_atomic_load_acquire", "ct_atomic_store_relaxed", "ct_atomic_store_release",
            "ct_atomic_compare_exchange_relaxed", "ct_atomic_compare_exchange_release", "ct_atomic_fetch_add_relaxed", "ct_atomic_fetch_sub_release", "ct_atomic_acquire_fence",
            "ct_thread_state", "ct_thread_current_state", "ct_thread_current", "ct_thread_set_current", "ct_thread_require_attached", "ct_thread_attach_primary",
            "ct_thread_publish_ready", "ct_thread_begin_shutdown", "ct_thread_state_deleted", "ct_thread_attach", "ct_thread_detach", "ct_runtime_phase", "ct_attached_thread_count",
            "ct_runtime_initialize", "ct_runtime_shutdown", "ct_runtime_test_fail_allocation_after",
            "ct_debug_control", "ct_debug_site", "ct_debug_keep", "ct_debug_method_enter", "ct_debug_method_leave", "ct_debug_throw_hook", "ct_debug_fatal_hook",
            "ct_debug_wait_for_client", "ct_debug_startup_probe", "ct_debug_live_head", "ct_debug_live_count", "ct_debug_allocation_count", "ct_debug_final_release_count",
        };
        var generatedSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Types.Values)
        {
            generatedSymbols.Add(NameMangler.Type(type));
            generatedSymbols.Add(CEmitter.DescriptorName(type));
            if (type.Kind == DeclaredTypeKind.Class)
            {
                generatedSymbols.Add(CEmitter.VTableName(type));
                generatedSymbols.Add(CEmitter.ObjectDropName(type));
            }
            else if (type.Kind == DeclaredTypeKind.Delegate)
            {
                generatedSymbols.Add(CEmitter.DelegateFactoryName(type));
                generatedSymbols.Add(CEmitter.DelegateDropName(type));
            }
            foreach (var field in type.Fields.Where(field => field.IsStatic && field.Name != "<underlying>" && field.ExternName is null && field.LinkerSymbolName is null && !field.IsRegister))
                generatedSymbols.Add(field.CName);
            foreach (var value in type.EnumValues)
                generatedSymbols.Add(NameMangler.Identifier(type.FullName + "." + value.Name));
            foreach (var constructor in type.Constructors)
            {
                generatedSymbols.Add(NameMangler.Method(constructor));
                generatedSymbols.Add(CEmitter.ConstructorInitializerName(constructor));
            }
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
            {
                generatedSymbols.Add(NameMangler.Method(method));
                if (method.IsVirtual && !method.ContainingType.IsObject)
                    generatedSymbols.Add(CEmitter.VirtualMethodThunkName(method));
            }
            foreach (var parameter in type.Methods.SelectMany(method => method.Parameters).Where(parameter => parameter.IsSynchronousCallback && parameter.Type.Symbol is not null))
                generatedSymbols.Add(NameMangler.Artifact("ct_k_", $"callback-adapter:{NameMangler.TypeIdentity(parameter.Type.Symbol!)}"));
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                {
                    generatedSymbols.Add(NameMangler.Getter(property));
                    if (property.IsVirtual)
                        generatedSymbols.Add(CEmitter.VirtualPropertyThunkName(property, true));
                }
                if (property.Setter is not null)
                {
                    generatedSymbols.Add(NameMangler.Setter(property));
                    if (property.IsVirtual)
                        generatedSymbols.Add(CEmitter.VirtualPropertyThunkName(property, false));
                }
            }
        }

        var externs = Types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ExternName is not null)
            .OrderBy(method => method.ExternName, StringComparer.Ordinal)
            .ThenBy(method => method.ContainingType.FullName, StringComparer.Ordinal)
            .ToArray();
        var externFields = Types.Values.SelectMany(type => type.Fields).Where(field => field.ExternName is not null)
            .OrderBy(field => field.ExternName, StringComparer.Ordinal).ThenBy(field => field.ContainingType.FullName, StringComparer.Ordinal).ToArray();
        var linkerFields = Types.Values.SelectMany(type => type.Fields).Where(field => field.LinkerSymbolName is not null)
            .OrderBy(field => field.LinkerSymbolName, StringComparer.Ordinal).ThenBy(field => field.ContainingType.FullName, StringComparer.Ordinal).ToArray();
        var nativeTypeNames = Types.Values.Where(type => type.Kind == DeclaredTypeKind.Opaque && type.NativeTypeName is not null)
            .Select(type => type.NativeTypeName!).ToHashSet(StringComparer.Ordinal);
        foreach (var method in externs.Where(method => !method.IsTrustedExtern))
        {
            if (runtimeSymbols.Contains(method.ExternName!) || generatedSymbols.Contains(method.ExternName!) || nativeTypeNames.Contains(method.ExternName!) || IsExceptionLoweringName(method.ExternName!) || IsOwnershipLoweringName(method.ExternName!))
                Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' conflicts with a compiler-owned or generated C symbol.", method.Syntax!.Source, method.Syntax.Span);
        }
        foreach (var field in externFields)
            if (runtimeSymbols.Contains(field.ExternName!) || generatedSymbols.Contains(field.ExternName!) || nativeTypeNames.Contains(field.ExternName!))
                Diagnostics.Add("CT4101", $"External data symbol '{field.ExternName}' conflicts with a compiler-owned or generated C symbol.", field.Syntax!.Source, field.Syntax.Span);
        foreach (var field in linkerFields)
            if (runtimeSymbols.Contains(field.LinkerSymbolName!) || generatedSymbols.Contains(field.LinkerSymbolName!) || nativeTypeNames.Contains(field.LinkerSymbolName!))
                Diagnostics.Add("CT4101", $"Linker symbol '{field.LinkerSymbolName}' conflicts with a compiler-owned or generated C symbol.", field.Syntax!.Source, field.Syntax.Span);

        foreach (var group in externs.GroupBy(method => method.ExternName!, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var method in group.Skip(1))
            {
                if (HaveSameAbiSignature(first, method))
                    continue;
                Diagnostics.Add("CT4102", $"External symbol '{group.Key}' has incompatible ABI signatures.", method.Syntax!.Source, method.Syntax.Span,
                    first.Syntax?.Source.GetLocation(first.Syntax.Span));
            }
        }
        foreach (var group in externFields.GroupBy(field => field.ExternName!, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var field in group.Skip(1))
                if (field.Type != first.Type || field.IsReadonly != first.IsReadonly || field.IsNativeVolatile != first.IsNativeVolatile)
                    Diagnostics.Add("CT4102", $"External data symbol '{group.Key}' has incompatible ABI declarations.", field.Syntax!.Source, field.Syntax.Span,
                        first.Syntax!.Source.GetLocation(first.Syntax.Span));
        }
        foreach (var group in linkerFields.GroupBy(field => field.LinkerSymbolName!, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var field in group.Skip(1))
                if (field.Type != first.Type)
                    Diagnostics.Add("CT4102", $"Linker symbol '{group.Key}' has incompatible address declarations.", field.Syntax!.Source, field.Syntax.Span,
                        first.Syntax!.Source.GetLocation(first.Syntax.Span));
        }
        var functionSymbols = externs.Select(method => method.ExternName!).ToHashSet(StringComparer.Ordinal);
        foreach (var field in externFields.Where(field => functionSymbols.Contains(field.ExternName!)))
            Diagnostics.Add("CT4102", $"External symbol '{field.ExternName}' cannot be declared as both function and data.", field.Syntax!.Source, field.Syntax.Span);
        foreach (var field in linkerFields.Where(field => functionSymbols.Contains(field.LinkerSymbolName!) || externFields.Any(external => external.ExternName == field.LinkerSymbolName)))
            Diagnostics.Add("CT4102", $"Native symbol '{field.LinkerSymbolName}' cannot be declared with incompatible linker, function, or data contracts.", field.Syntax!.Source, field.Syntax.Span);

        var exports = Types.Values.SelectMany(type => type.Methods).Where(method => method.ExportName is not null).ToArray();
        var nativeSymbols = externs.Select(method => method.ExternName!).Concat(externFields.Select(field => field.ExternName!)).Concat(linkerFields.Select(field => field.LinkerSymbolName!)).ToHashSet(StringComparer.Ordinal);
        foreach (var method in exports)
        {
            if (runtimeSymbols.Contains(method.ExportName!) || generatedSymbols.Contains(method.ExportName!) || nativeSymbols.Contains(method.ExportName!) || nativeTypeNames.Contains(method.ExportName!) || IsExceptionLoweringName(method.ExportName!) || IsOwnershipLoweringName(method.ExportName!))
                Diagnostics.Add("CT4101", $"Exported symbol '{method.ExportName}' conflicts with a native, compiler-owned, or generated C symbol.", method.Syntax!.Source, method.Syntax.Span);
            if (!IsExportReturnType(method.ReturnType) || method.Parameters.Any(parameter => !IsExportParameterType(parameter.Type)))
                Diagnostics.Add("CT1267", $"Export '{method.ExportName}' contains a type that is not safe in the native C ABI.", method.Syntax!.Source, method.Syntax.Span);
        }
        foreach (var group in exports.GroupBy(method => method.ExportName!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var duplicate in group.Skip(1))
                Diagnostics.Add("CT4101", $"Exported symbol '{group.Key}' is declared more than once.", duplicate.Syntax!.Source, duplicate.Syntax.Span, group.First().Syntax!.Source.GetLocation(group.First().Syntax!.Span));
    }

    private static bool IsExportReturnType(CType type) => type.Kind != CTypeKind.NativeUtf8String && IsExportParameterType(type);

    private static bool IsExportParameterType(CType type) => type.Kind switch
    {
        CTypeKind.Void or CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or CTypeKind.Double or
        CTypeKind.Enum or CTypeKind.Newtype or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer or CTypeKind.NativeUtf8String => true,
        CTypeKind.Struct => !type.ContainsManagedReferences && type.Symbol!.Fields.Where(field => !field.IsStatic).All(field => IsExportParameterType(field.Type)),
        CTypeKind.InlineArray => type.InlineArrayLength > 0 && IsExportParameterType(type.ElementType!),
        _ => false,
    };

    private static bool IsExceptionLoweringName(string name) =>
        name.StartsWith("ct_eh_", StringComparison.Ordinal) ||
        name.StartsWith("ct_ep_", StringComparison.Ordinal) ||
        name.StartsWith("ct_ex_", StringComparison.Ordinal) ||
        name.StartsWith("ct_er_", StringComparison.Ordinal) ||
        name.StartsWith("ct_lp_", StringComparison.Ordinal) ||
        name.StartsWith("ct_pp_", StringComparison.Ordinal) ||
        name.StartsWith("ct_finally_", StringComparison.Ordinal) ||
        name.StartsWith("ct_after_finally_", StringComparison.Ordinal) ||
        name.StartsWith("ct_after_catch_", StringComparison.Ordinal);

    private static bool IsOwnershipLoweringName(string name) =>
        name.StartsWith("ct_drop_object_", StringComparison.Ordinal) ||
        name.StartsWith("ct_drop_array_", StringComparison.Ordinal) ||
        name.StartsWith("ct_drop_box_", StringComparison.Ordinal) ||
        name.StartsWith("ct_retain_value_", StringComparison.Ordinal) ||
        name.StartsWith("ct_drop_value_", StringComparison.Ordinal) ||
        name.StartsWith("ct_cleanup_", StringComparison.Ordinal);

    private static bool HaveSameAbiSignature(MethodSymbol left, MethodSymbol right) =>
        left.ReturnType == right.ReturnType &&
        left.ReturnsBorrowed == right.ReturnsBorrowed &&
        left.ReturnsOwned == right.ReturnsOwned &&
        left.ReturnsNullable == right.ReturnsNullable &&
        left.Parameters.Select(parameter => (parameter.Type, parameter.PassingKind, parameter.IsRetained, parameter.NativeOwnership, parameter.IsNullable, parameter.IsSynchronousCallback))
            .SequenceEqual(right.Parameters.Select(parameter => (parameter.Type, parameter.PassingKind, parameter.IsRetained, parameter.NativeOwnership, parameter.IsNullable, parameter.IsSynchronousCallback)));
}
