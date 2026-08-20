namespace CTilde;

internal sealed partial class CompilationModel
{
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
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_long", "ct_write_ulong", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_math_sqrt", "ct_math_abs", "ct_math_tan", "ct_math_min", "ct_math_max", "ct_math_sin", "ct_math_cos", "ct_math_floor", "ct_math_ceiling",
            "ct_console_read", "ct_console_read_line", "ct_host_file_open", "ct_host_file_read", "ct_host_file_write_buffer", "ct_host_file_write_string", "ct_host_file_close",
            "ct_host_io_throw", "ct_host_utf8_valid", "ct_host_file", "ct_host_file_require", "ct_host_write_all",
            "ct_to_string_nint", "ct_to_string_nuint", "ct_write_nint", "ct_write_nuint", "ct_native_bounds", "ct_stack_bytes",
            "ct_module_init", "ct_string", "ct_object", "ct_type_descriptor", "ct_vtable",
            "ct_init_object", "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash",
            "ct_object_to_string", "ct_object_base_to_string", "ct_object_hash", "ct_object_reference_equals", "ct_type_is_assignable",
            "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_object_value_equals",
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
            foreach (var field in type.Fields.Where(field => field.IsStatic && field.Name != "<underlying>"))
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
        var nativeTypeNames = Types.Values.Where(type => type.Kind == DeclaredTypeKind.Opaque && type.NativeTypeName is not null)
            .Select(type => type.NativeTypeName!).ToHashSet(StringComparer.Ordinal);
        foreach (var method in externs.Where(method => !method.IsTrustedExtern))
        {
            if (runtimeSymbols.Contains(method.ExternName!) || generatedSymbols.Contains(method.ExternName!) || nativeTypeNames.Contains(method.ExternName!) || IsExceptionLoweringName(method.ExternName!) || IsOwnershipLoweringName(method.ExternName!))
                Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' conflicts with a compiler-owned or generated C symbol.", method.Syntax!.Source, method.Syntax.Span);
        }

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

        var exports = Types.Values.SelectMany(type => type.Methods).Where(method => method.ExportName is not null).ToArray();
        var nativeSymbols = externs.Select(method => method.ExternName!).ToHashSet(StringComparer.Ordinal);
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
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or CTypeKind.Float or
        CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.EspError or CTypeKind.Pointer or CTypeKind.NativeBuffer or CTypeKind.ReadOnlyNativeBuffer or CTypeKind.NativeUtf8String => true,
        CTypeKind.Struct => !type.ContainsManagedReferences && type.Symbol!.Fields.Where(field => !field.IsStatic).All(field => IsExportParameterType(field.Type)),
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
