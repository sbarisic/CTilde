using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitOwnershipHelpers(CWriter writer)
    {
        var objectType = Model.Types["System.Object"];
        writer.WriteLine($"void ct_memory_retain({NameMangler.Type(objectType)}* value) {{ ct_retain((ct_object*)(void*)value); }}");
        writer.WriteLine($"void ct_memory_release({NameMangler.Type(objectType)}* value) {{ ct_release((ct_object*)(void*)value); }}");
        if (IsEspIdf && Model.Types.ContainsKey("Esp.Idf.EspError"))
            writer.WriteLine("ct_string* ct_esp_error_name(int32_t code) { const char* name = esp_err_to_name((esp_err_t)code); return ct_string_from_bytes((const uint8_t*)name, (int32_t)strlen(name), \"<esp-error>\", 0); }");

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Struct && type.Type.ContainsManagedReferences))
        {
            var valueType = type.Type;
            writer.WriteLine($"static CT_UNUSED void {ValueRetainName(valueType)}(void* storage)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)storage;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences))
                writer.WriteLine($"    {ValueRetainName(field.Type)}((void*)&value->{field.CName});");
            writer.WriteLine("}");
            writer.WriteLine($"static CT_UNUSED void {ValueDropName(valueType)}(void* storage)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)storage;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences).Reverse())
                writer.WriteLine($"    {ValueDropName(field.Type)}((void*)&value->{field.CName});");
            writer.WriteLine("}");
        }

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Class))
        {
            writer.WriteLine($"static void {ObjectDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)(void*)object;");
            writer.WriteLine("    (void)value;");
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.Type.ContainsManagedReferences).Reverse())
                writer.WriteLine($"    {ValueDropName(field.Type)}((void*)&value->{field.CName});");
            if (type.BaseType is not null)
                writer.WriteLine($"    {ObjectDropName(type.BaseType)}(object);");
            writer.WriteLine("}");
        }

        foreach (var type in OrderLayoutTypes().Where(type => type.Kind == DeclaredTypeKind.Delegate))
        {
            writer.WriteLine($"static void {DelegateDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)(void*)object;");
            writer.WriteLine("    ct_object* target = value->ct_target;");
            writer.WriteLine("    value->ct_target = NULL;");
            writer.WriteLine("    ct_release(target);");
            writer.WriteLine("}");
        }

        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var element = array.ElementType!;
            var name = NameMangler.Array(element);
            writer.WriteLine($"static void {ArrayDropName(element)}(ct_object* object)");
            writer.WriteLine("{");
            writer.WriteLine($"    {name}* value = ({name}*)(void*)object;");
            if (element.ContainsManagedReferences)
            {
                writer.WriteLine("    for (int32_t index = value->Length; index > 0; --index)");
                writer.WriteLine($"        {ValueDropName(element)}((void*)&value->Data[index - 1]);");
            }
            writer.WriteLine("    ct_dealloc(value->Data);");
            writer.WriteLine("    value->Data = NULL; value->Length = 0;");
            writer.WriteLine("}");
        }

        foreach (var type in BoxedTypes)
        {
            writer.WriteLine($"static void {BoxDropName(type)}(ct_object* object)");
            writer.WriteLine("{");
            if (type.ContainsManagedReferences)
            {
                writer.WriteLine($"    {BoxName(type)}* value = ({BoxName(type)}*)(void*)object;");
                writer.WriteLine($"    {ValueDropName(type)}((void*)&value->Value);");
            }
            else
                writer.WriteLine("    (void)object;");
            writer.WriteLine("}");
        }
        writer.WriteLine();
    }

    private void EmitObjectMetadata(CWriter writer)
    {
        var virtualMethods = VirtualMethodRoots().ToArray();
        var virtualProperties = VirtualPropertyRoots().ToArray();
        writer.WriteLine("struct ct_vtable");
        writer.WriteLine("{");
        writer.WriteLine("    ct_string* (*ToString)(ct_object*);");
        writer.WriteLine("    bool (*Equals)(ct_object*, ct_object*);");
        writer.WriteLine("    int32_t (*GetHashCode)(ct_object*);");
        foreach (var method in virtualMethods)
        {
            var parameters = string.Concat(method.Parameters.Select(parameter => $", {ParameterTypeName(parameter)}"));
            writer.WriteLine($"    {CTypeName(method.ReturnType)} (*{VirtualSlotName(method)})(ct_object*{parameters});");
        }
        foreach (var property in virtualProperties)
        {
            if (property.Getter is not null)
                writer.WriteLine($"    {CTypeName(property.Type)} (*{VirtualGetterSlotName(property)})(ct_object*);");
            if (property.Setter is not null)
                writer.WriteLine($"    void (*{VirtualSetterSlotName(property)})(ct_object*, {CTypeName(property.Type)});");
        }
        writer.WriteLine("};");
        writer.WriteLine("static ct_string* ct_object_default_to_string(ct_object* value);");
        writer.WriteLine("static bool ct_object_default_equals(ct_object* left, ct_object* right);");
        writer.WriteLine("static int32_t ct_object_default_hash(ct_object* value);");
        writer.WriteLine("static bool ct_object_value_equals(ct_object* left, ct_object* right);");
        writer.WriteLine("static uint32_t ct_object_value_hash(ct_object* value);");
        writer.WriteLine("static bool ct_type_is_assignable(const ct_type_descriptor* actual, const ct_type_descriptor* target) { for (const ct_type_descriptor* current = actual; current != NULL; current = current->Base) if (current == target) return true; return false; }");
        writer.WriteLine("static ct_object* ct_checked_cast(ct_object* value, const ct_type_descriptor* target, const char* file, int line) { if (value == NULL) return NULL; if (!ct_type_is_assignable(value->Type, target)) ct_fail(\"CTO0001\", file, line); return value; }");
        writer.WriteLine("static ct_object* ct_safe_cast(ct_object* value, const ct_type_descriptor* target) { return value != NULL && ct_type_is_assignable(value->Type, target) ? value : NULL; }");
        writer.WriteLine("static uint32_t ct_hash_bytes(const void* value, size_t size) { const uint8_t* bytes = (const uint8_t*)value; uint32_t hash = UINT32_C(2166136261); for (size_t i = 0; i < size; ++i) { hash ^= bytes[i]; hash *= UINT32_C(16777619); } return hash; }");
        writer.WriteLine("static uint32_t ct_hash_float(float value) { if (isnan(value)) return UINT32_C(0x7FC00000); if (value == 0.0f) return 0u; return ct_hash_bytes(&value, sizeof(value)); }");
        EmitDefaultVTable(writer, "ct_default_vtable", virtualMethods, virtualProperties);
        writer.WriteLine("static ct_string* ct_string_v_to_string(ct_object* value) { ct_retain(value); return (ct_string*)(void*)value; }");
        writer.WriteLine("static bool ct_string_v_equals(ct_object* left, ct_object* right) { return right != NULL && right->Type == &ct_desc_string && ct_string_equal((ct_string*)(void*)left, (ct_string*)(void*)right); }");
        writer.WriteLine("static int32_t ct_string_v_hash(ct_object* value) { ct_string* text = (ct_string*)(void*)value; return ct_i32_bits(ct_hash_bytes(text->Data, (size_t)text->Length)); }");
        EmitSpecialVTable(writer, "ct_string_vtable", "ct_string_v_to_string", "ct_string_v_equals", "ct_string_v_hash", virtualMethods, virtualProperties);
        writer.WriteLine("static ct_type_descriptor ct_desc_string = { \"string\", &" + DescriptorName(Model.Types["System.Object"]) + ", &ct_string_vtable, 1u, sizeof(ct_string), _Alignof(ct_string), false, ct_drop_string };");
        uint id = 2;
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Class).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            EmitClassVTable(writer, type, virtualMethods, virtualProperties);
            var baseDescriptor = type.BaseType is null ? "NULL" : $"&{DescriptorName(type.BaseType)}";
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)} = {{ \"{EscapeCString(type.FullName)}\", {baseDescriptor}, &ct_vtable_{NameMangler.Identifier(type.FullName)}, {id++}u, sizeof({NameMangler.Type(type)}), _Alignof({NameMangler.Type(type)}), false, {ObjectDropName(type)} }};");
        }
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            writer.WriteLine($"static ct_type_descriptor {DescriptorName(type)} = {{ \"{EscapeCString(type.FullName)}\", &{DescriptorName(Model.Types["System.Object"])}, &ct_default_vtable, {id++}u, sizeof({NameMangler.Type(type)}), _Alignof({NameMangler.Type(type)}), false, {DelegateDropName(type)} }};");
        }
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
        {
            var name = NameMangler.Array(array.ElementType!);
            writer.WriteLine($"static ct_type_descriptor {ArrayDescriptorName(array.ElementType!)} = {{ \"{EscapeCString(array.ElementType!.DisplayName)}[]\", &{DescriptorName(Model.Types["System.Object"])}, &ct_default_vtable, {id++}u, sizeof({name}), _Alignof({name}), false, {ArrayDropName(array.ElementType!)} }};");
        }
        foreach (var type in BoxedTypes)
        {
            EmitBoxMetadata(writer, type, virtualMethods, virtualProperties);
            writer.WriteLine($"static ct_type_descriptor {BoxDescriptorName(type)} = {{ \"{EscapeCString(type.DisplayName)}\", &{DescriptorName(Model.Types["System.Object"])}, &ct_vtable_box_{NameMangler.TypeCode(type)}, {id++}u, sizeof({BoxName(type)}), _Alignof({BoxName(type)}), true, {BoxDropName(type)} }};");
        }
        writer.WriteLine("static ct_string* ct_object_default_to_string(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_string_from_bytes((const uint8_t*)value->Type->Name, (int32_t)strlen(value->Type->Name), \"<runtime>\", 0); }");
        writer.WriteLine("static bool ct_object_default_equals(ct_object* left, ct_object* right) { return left == right; }");
        writer.WriteLine("static int32_t ct_object_default_hash(ct_object* value) { if (value == NULL) ct_fail(\"CTN0001\", \"<runtime>\", 0); return ct_i32_bits(value->IdentityHash); }");
        writer.WriteLine("static bool ct_object_value_equals(ct_object* left, ct_object* right) { if (left == right) return true; if (left == NULL || right == NULL) return false; return left->Type->VTable->Equals(left, right); }");
        writer.WriteLine("static uint32_t ct_object_value_hash(ct_object* value) { return value == NULL ? 0u : (uint32_t)value->Type->VTable->GetHashCode(value); }");
        var objectType = Model.Types.GetValueOrDefault("System.Object");
        var objectCType = objectType is null ? "ct_object" : NameMangler.Type(objectType);
        writer.WriteLine($"ct_string* ct_object_to_string({objectCType}* value) {{ return value == NULL ? NULL : ((ct_object*)(void*)value)->Type->VTable->ToString((ct_object*)(void*)value); }}");
        writer.WriteLine($"ct_string* ct_object_base_to_string({objectCType}* value) {{ return ct_object_default_to_string((ct_object*)(void*)value); }}");
        writer.WriteLine($"int32_t ct_object_hash({objectCType}* value) {{ return ((ct_object*)(void*)ct_require_nonnull(value, \"<runtime>\", 0))->Type->VTable->GetHashCode((ct_object*)(void*)value); }}");
        writer.WriteLine($"bool ct_object_reference_equals({objectCType}* left, {objectCType}* right) {{ return left == right; }}");
        if (_usesExceptions && Model.Types.TryGetValue("System.Exception", out var exceptionType))
        {
            var message = exceptionType.Properties.Single(property => property.Name == "Message");
            writer.WriteLine("CT_NORETURN static void ct_unhandled_exception(ct_object* exception)");
            writer.WriteLine("{");
            writer.WriteLine($"    ct_string* message = {NameMangler.Getter(message)}(({NameMangler.Type(exceptionType)}*)(void*)exception);");
            writer.WriteLine("    (void)fprintf(stderr, \"C~ unhandled exception CTE0001: %s\", exception->Type->Name);");
            writer.WriteLine("    if (message != NULL && message->Length != 0) (void)fprintf(stderr, \": %.*s\", (int)message->Length, (const char*)message->Data);");
            writer.WriteLine("    (void)fputc('\\n', stderr);");
            writer.WriteLine(IsEspIdf ? "    abort();" : "    exit(EXIT_FAILURE);");
            writer.WriteLine("}");
        }
        writer.WriteLine();
    }

    private void EmitDelegateSupport(CWriter writer)
    {
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var parameters = string.Concat(type.DelegateParameters.Select(parameter => $", {ParameterTypeName(parameter)}"));
            writer.WriteLine($"static {NameMangler.Type(type)}* {DelegateFactoryName(type)}(ct_object* target, {CTypeName(type.DelegateReturnType!)} (*invoke)(ct_object*{parameters}), const char* file, int line)");
            writer.WriteLine("{");
            writer.WriteLine($"    {NameMangler.Type(type)}* value = ({NameMangler.Type(type)}*)ct_alloc(sizeof({NameMangler.Type(type)}), file, line);");
            writer.WriteLine($"    ct_init_object(value, &{DescriptorName(type)});");
            writer.WriteLine("    value->ct_target = target;");
            writer.WriteLine("    value->ct_invoke = invoke;");
            writer.WriteLine("    ct_retain(target);");
            writer.WriteLine("    return value;");
            writer.WriteLine("}");
        }

        foreach (var ((delegateType, method, virtualDispatch), name) in _delegateThunks.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var parameters = delegateType.DelegateParameters.Select((parameter, index) => CParameterDeclaration(parameter, $"ct_arg_{index}")).ToArray();
            var signatureParameters = string.Join(", ", new[] { "ct_object* ct_target" }.Concat(parameters));
            writer.WriteLine($"static {CTypeName(delegateType.DelegateReturnType!)} {name}({signatureParameters})");
            writer.WriteLine("{");
            writer.WriteLine("    (void)ct_target;");
            var arguments = delegateType.DelegateParameters.SelectMany((parameter, index) => ParameterArgumentNames(parameter, $"ct_arg_{index}")).ToList();
            string call;
            if (method.IsStatic)
                call = $"{method.CName}({string.Join(", ", arguments)})";
            else if (virtualDispatch)
                call = $"ct_target->Type->VTable->{VirtualSlotName(method)}({string.Join(", ", new[] { "ct_target" }.Concat(arguments))})";
            else
                call = $"{method.CName}(({NameMangler.Type(method.ContainingType)}*)(void*)ct_target{(arguments.Count == 0 ? string.Empty : ", " + string.Join(", ", arguments))})";
            if (delegateType.DelegateReturnType == CType.Void)
                writer.WriteLine($"    {call};");
            else
                writer.WriteLine($"    return {call};");
            writer.WriteLine("}");
        }
        if (Model.UserTypes.Any(type => type.Kind == DeclaredTypeKind.Delegate))
            writer.WriteLine();
    }

    private void EmitSynchronousDelegateAdapters(CWriter writer)
    {
        foreach (var delegateType in _synchronousDelegateTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var parameters = delegateType.DelegateParameters
                .Select((parameter, index) => CParameterDeclaration(parameter, $"ct_arg_{index}"))
                .Append("void* ct_context")
                .ToArray();
            writer.WriteLine($"static {CTypeName(delegateType.DelegateReturnType!)} {SynchronousCallbackAdapterName(delegateType)}({string.Join(", ", parameters)})");
            writer.WriteLine("{");
            writer.WriteLine("    (void)ct_thread_require_attached();");
            writer.WriteLine($"    {NameMangler.Type(delegateType)}* ct_callback = ({NameMangler.Type(delegateType)}*)ct_require_nonnull(ct_context, \"<native-callback>\", 0);");
            writer.WriteLine("    jmp_buf ct_callback_jump;");
            writer.WriteLine("    ct_exception_frame ct_callback_frame = { &ct_callback_jump, ct_exception_top, ct_cleanup_top };");
            writer.WriteLine("    ct_exception_top = &ct_callback_frame;");
            writer.WriteLine("    if (setjmp(ct_callback_jump) != 0)");
            writer.WriteLine("    {");
            writer.WriteLine("        ct_object* ct_callback_exception = ct_current_exception;");
            writer.WriteLine("        ct_current_exception = NULL;");
            writer.WriteLine("        ct_exception_top = ct_callback_frame.Previous;");
            writer.WriteLine("        ct_release(ct_callback_exception);");
            writer.WriteLine("        ct_fail(\"CTE0003\", \"<native-callback>\", 0);");
            writer.WriteLine("    }");
            var arguments = delegateType.DelegateParameters
                .SelectMany((parameter, index) => ParameterArgumentNames(parameter, $"ct_arg_{index}"));
            var call = $"ct_callback->ct_invoke(ct_callback->ct_target{(delegateType.DelegateParameters.Length == 0 ? string.Empty : ", " + string.Join(", ", arguments))})";
            if (delegateType.DelegateReturnType == CType.Void)
            {
                writer.WriteLine($"    {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
            }
            else
            {
                writer.WriteLine($"    {CDeclaration(delegateType.DelegateReturnType!, "ct_callback_result")} = {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
                writer.WriteLine("    return ct_callback_result;");
            }
            writer.WriteLine("}");
        }
        if (_synchronousDelegateTypes.Count != 0)
            writer.WriteLine();
    }

    private void EmitFunctionPointerTrampolines(CWriter writer)
    {
        foreach (var ((type, method), name) in _functionPointerTrampolines.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var signature = type.FunctionPointer!;
            var parameters = signature.ParameterTypes.Select((parameter, index) => signature.PassingKinds[index] switch
            {
                _ when parameter.IsNativeBuffer => $"{(parameter.Kind == CTypeKind.ReadOnlyNativeBuffer ? "const " : string.Empty)}{CTypeName(parameter.ElementType!)}* ct_arg_{index}_data, size_t ct_arg_{index}_length",
                ParameterPassingKind.In => $"const {CTypeName(parameter)}* ct_arg_{index}",
                ParameterPassingKind.Ref or ParameterPassingKind.Out => $"{CTypeName(parameter)}* ct_arg_{index}",
                _ => $"{CTypeName(parameter)} ct_arg_{index}",
            }).ToArray();
            writer.WriteLine($"static {CTypeName(signature.ReturnType)} {name}({(parameters.Length == 0 ? "void" : string.Join(", ", parameters))})");
            writer.WriteLine("{");
            writer.WriteLine("    (void)ct_thread_require_attached();");
            writer.WriteLine("    jmp_buf ct_callback_jump;");
            writer.WriteLine("    ct_exception_frame ct_callback_frame = { &ct_callback_jump, ct_exception_top, ct_cleanup_top };");
            writer.WriteLine("    ct_exception_top = &ct_callback_frame;");
            writer.WriteLine("    if (setjmp(ct_callback_jump) != 0)");
            writer.WriteLine("    {");
            writer.WriteLine("        ct_exception_top = ct_callback_frame.Previous;");
            writer.WriteLine("        ct_fail(\"CTE0003\", \"<native-callback>\", 0);");
            writer.WriteLine("    }");
            var callArguments = method.Parameters.SelectMany((parameter, index) => ParameterArgumentNames(parameter, $"ct_arg_{index}"));
            var call = $"{method.CName}({string.Join(", ", callArguments)})";
            if (signature.ReturnType == CType.Void)
            {
                writer.WriteLine($"    {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
            }
            else
            {
                writer.WriteLine($"    {CTypeName(signature.ReturnType)} ct_callback_result = {call};");
                writer.WriteLine("    ct_exception_top = ct_callback_frame.Previous;");
                writer.WriteLine("    return ct_callback_result;");
            }
            writer.WriteLine("}");
        }
        if (_functionPointerTrampolines.Count != 0)
            writer.WriteLine();
    }

    private IEnumerable<MethodSymbol> VirtualMethodRoots() => Model.UserTypes
        .SelectMany(type => type.Methods)
        .Where(method => method.IsVirtual && method.OverriddenMethod is null && !method.ContainingType.IsObject)
        .OrderBy(method => method.ContainingType.FullName, StringComparer.Ordinal)
        .ThenBy(method => method.CName, StringComparer.Ordinal);

    private IEnumerable<PropertySymbol> VirtualPropertyRoots() => Model.UserTypes
        .SelectMany(type => type.Properties)
        .Where(property => property.IsVirtual && property.OverriddenProperty is null)
        .OrderBy(property => property.ContainingType.FullName, StringComparer.Ordinal)
        .ThenBy(property => property.Name, StringComparer.Ordinal);

    private void EmitDefaultVTable(CWriter writer, string name, MethodSymbol[] methods, PropertySymbol[] properties) =>
        EmitSpecialVTable(writer, name, "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash", methods, properties);

    private static void EmitSpecialVTable(CWriter writer, string name, string toString, string equals, string hash, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        writer.WriteLine($"static const ct_vtable {name} = {{");
        writer.WriteLine($"    .ToString = {toString}, .Equals = {equals}, .GetHashCode = {hash},");
        foreach (var method in methods)
            writer.WriteLine($"    .{VirtualSlotName(method)} = NULL,");
        foreach (var property in properties)
        {
            if (property.Getter is not null)
                writer.WriteLine($"    .{VirtualGetterSlotName(property)} = NULL,");
            if (property.Setter is not null)
                writer.WriteLine($"    .{VirtualSetterSlotName(property)} = NULL,");
        }
        writer.WriteLine("};");
    }

    private void EmitClassVTable(CWriter writer, TypeSymbol type, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        var objectMethods = Model.Types["System.Object"].Methods;
        var toStringRoot = objectMethods.Single(method => method.Name == "ToString" && method.Parameters.Length == 0);
        var equalsRoot = objectMethods.Single(method => method.Name == "Equals" && method.Parameters.Length == 1 && !method.IsStatic);
        var hashRoot = objectMethods.Single(method => method.Name == "GetHashCode" && method.Parameters.Length == 0);
        var toString = ResolveVirtualMethod(type, toStringRoot);
        var equals = ResolveVirtualMethod(type, equalsRoot);
        var hash = ResolveVirtualMethod(type, hashRoot);
        var toStringThunk = toString == toStringRoot ? "ct_object_default_to_string" : EmitMethodThunk(writer, toString!);
        var equalsThunk = equals == equalsRoot ? "ct_object_default_equals" : EmitMethodThunk(writer, equals!);
        var hashThunk = hash == hashRoot ? "ct_object_default_hash" : EmitMethodThunk(writer, hash!);
        var methodEntries = methods.Select(root => (Root: root, Implementation: ResolveVirtualMethod(type, root)))
            .Select(entry => (entry.Root, Name: entry.Implementation is null ? "NULL" : EmitMethodThunk(writer, entry.Implementation)))
            .ToArray();
        var propertyEntries = properties.Select(root =>
        {
            var implementation = ResolveVirtualProperty(type, root);
            return (Root: root,
                Getter: implementation?.Getter is null ? "NULL" : EmitPropertyThunk(writer, implementation, true),
                Setter: implementation?.Setter is null ? "NULL" : EmitPropertyThunk(writer, implementation, false));
        }).ToArray();
        writer.WriteLine($"static const ct_vtable ct_vtable_{NameMangler.Identifier(type.FullName)} = {{");
        writer.WriteLine($"    .ToString = {toStringThunk}, .Equals = {equalsThunk}, .GetHashCode = {hashThunk},");
        foreach (var entry in methodEntries)
            writer.WriteLine($"    .{VirtualSlotName(entry.Root)} = {entry.Name},");
        foreach (var entry in propertyEntries)
        {
            if (entry.Root.Getter is not null)
                writer.WriteLine($"    .{VirtualGetterSlotName(entry.Root)} = {entry.Getter},");
            if (entry.Root.Setter is not null)
                writer.WriteLine($"    .{VirtualSetterSlotName(entry.Root)} = {entry.Setter},");
        }
        writer.WriteLine("};");
    }

    private MethodSymbol? ResolveVirtualMethod(TypeSymbol type, MethodSymbol root)
    {
        if (!type.BaseTypesAndSelf().Contains(root.ContainingType))
            return null;
        foreach (var current in type.BaseTypesAndSelf())
        {
            var match = current.Methods.FirstOrDefault(method => VirtualRoot(method) == root);
            if (match is not null)
                return match;
        }
        return root;
    }

    private PropertySymbol? ResolveVirtualProperty(TypeSymbol type, PropertySymbol root)
    {
        if (!type.BaseTypesAndSelf().Contains(root.ContainingType))
            return null;
        foreach (var current in type.BaseTypesAndSelf())
        {
            var match = current.Properties.FirstOrDefault(property => VirtualRoot(property) == root);
            if (match is not null)
                return match;
        }
        return root;
    }

    private static MethodSymbol VirtualRoot(MethodSymbol method)
    {
        while (method.OverriddenMethod is not null)
            method = method.OverriddenMethod;
        return method;
    }

    private static PropertySymbol VirtualRoot(PropertySymbol property)
    {
        while (property.OverriddenProperty is not null)
            property = property.OverriddenProperty;
        return property;
    }

    private string EmitMethodThunk(CWriter writer, MethodSymbol method)
    {
        var name = $"ct_vthunk_{NameMangler.Identifier(method.CName)}";
        if (!_emittedThunks.Add(name))
            return name;
        var objectSlot = VirtualSlotName(method);
        var parameters = method.Parameters.Select((parameter, index) => objectSlot == "Equals"
            ? $"ct_object* a{index}"
            : CParameterDeclaration(parameter, $"a{index}")).ToArray();
        var signatureParameters = string.Join(", ", new[] { "ct_object* self" }.Concat(parameters));
        var arguments = string.Join(", ", new[] { $"({NameMangler.Type(method.ContainingType)}*)(void*)self" }.Concat(method.Parameters.Select((parameter, index) => objectSlot == "Equals"
            ? $"({CTypeName(parameter.Type)})(void*)a{index}"
            : $"a{index}")));
        writer.WriteLine($"static {CTypeName(method.ReturnType)} {name}({signatureParameters}) {{ {(method.ReturnType == CType.Void ? string.Empty : "return ")}{method.CName}({arguments}); }}");
        return name;
    }

    private string EmitPropertyThunk(CWriter writer, PropertySymbol property, bool getter)
    {
        var name = $"ct_vthunk_{(getter ? "get" : "set")}_{NameMangler.Identifier(property.ContainingType.FullName + "." + property.Name)}";
        if (!_emittedThunks.Add(name))
            return name;
        var self = $"({NameMangler.Type(property.ContainingType)}*)(void*)self";
        if (getter)
            writer.WriteLine($"static {CTypeName(property.Type)} {name}(ct_object* self) {{ return {NameMangler.Getter(property)}({self}); }}");
        else
            writer.WriteLine($"static void {name}(ct_object* self, {CTypeName(property.Type)} value) {{ {NameMangler.Setter(property)}({self}, value); }}");
        return name;
    }

    private void EmitBoxMetadata(CWriter writer, CType type, MethodSymbol[] methods, PropertySymbol[] properties)
    {
        var code = NameMangler.TypeCode(type);
        var box = BoxName(type);
        var descriptor = BoxDescriptorName(type);
        writer.WriteLine($"static ct_type_descriptor {descriptor};");
        var toString = $"ct_box_to_string_{code}";
        var equals = $"ct_box_equals_{code}";
        var hash = $"ct_box_hash_{code}";
        var structToString = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "ToString" && method.Parameters.Length == 0) : null;
        var structEquals = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "Equals" && method.Parameters.Length == 1) : null;
        var structHash = type.Kind == CTypeKind.Struct ? type.Symbol!.Methods.FirstOrDefault(method => method.IsOverride && method.Name == "GetHashCode" && method.Parameters.Length == 0) : null;
        var enumFormatter = $"ct_enum_to_string_{code}";
        if (type.Kind == CTypeKind.Enum)
        {
            var underlying = type.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type;
            writer.WriteLine($"static ct_string* {enumFormatter}({CTypeName(type)} value)");
            writer.WriteLine("{");
            foreach (var enumValue in type.Symbol.EnumValues.GroupBy(value => value.Value).Select(group => group.First()))
            {
                var escaped = EscapeCString(enumValue.Name);
                writer.WriteLine($"    if (value == ({CTypeName(type)}){FormatIntegralConstant(enumValue.Value, underlying)}) return ct_string_from_bytes((const uint8_t*)\"{escaped}\", {Encoding.UTF8.GetByteCount(enumValue.Name)}, \"<runtime>\", 0);");
            }
            var fallback = underlying.Kind switch
            {
                CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint((uint32_t)value, \"<runtime>\", 0)",
                CTypeKind.Ulong => "ct_to_string_ulong((uint64_t)value, \"<runtime>\", 0)",
                CTypeKind.Long => "ct_to_string_long((int64_t)value, \"<runtime>\", 0)",
                _ => "ct_to_string_int((int32_t)value, \"<runtime>\", 0)",
            };
            writer.WriteLine($"    return {fallback};");
            writer.WriteLine("}");
        }
        var toStringExpression = type.Kind switch
        {
            CTypeKind.Struct when structToString is not null => $"{structToString.CName}(&box->Value)",
            CTypeKind.Enum => $"{enumFormatter}(box->Value)",
            CTypeKind.Bool => "ct_to_string_bool(box->Value, \"<runtime>\", 0)",
            CTypeKind.Char => "ct_to_string_char(box->Value, \"<runtime>\", 0)",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint((uint32_t)box->Value, \"<runtime>\", 0)",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int((int32_t)box->Value, \"<runtime>\", 0)",
            CTypeKind.Long => "ct_to_string_long(box->Value, \"<runtime>\", 0)",
            CTypeKind.Ulong => "ct_to_string_ulong(box->Value, \"<runtime>\", 0)",
            CTypeKind.Nint => "ct_to_string_nint(box->Value, \"<runtime>\", 0)",
            CTypeKind.Nuint => "ct_to_string_nuint(box->Value, \"<runtime>\", 0)",
            CTypeKind.Float => "ct_to_string_float(box->Value, \"<runtime>\", 0)",
            _ => $"ct_string_from_bytes((const uint8_t*)\"{EscapeCString(type.DisplayName)}\", {Encoding.UTF8.GetByteCount(type.DisplayName)}, \"<runtime>\", 0)",
        };
        writer.WriteLine($"static ct_string* {toString}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; (void)box; return {toStringExpression}; }}");
        var comparison = type.Kind == CTypeKind.Float
            ? "left->Value == right->Value || (isnan(left->Value) && isnan(right->Value))"
            : type.Kind == CTypeKind.Struct
                ? StructEqualityExpression(type.Symbol!, "left->Value", "right->Value")
                : "left->Value == right->Value";
        if (structEquals is not null)
            writer.WriteLine($"static bool {equals}(ct_object* a, ct_object* b) {{ {box}* left = ({box}*)(void*)a; return {structEquals.CName}(&left->Value, ({NameMangler.Type(Model.Types["System.Object"])}*)(void*)b); }}");
        else
            writer.WriteLine($"static bool {equals}(ct_object* a, ct_object* b) {{ if (b == NULL || b->Type != &{descriptor}) return false; {box}* left = ({box}*)(void*)a; {box}* right = ({box}*)(void*)b; return {comparison}; }}");
        if (structHash is not null)
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return {structHash.CName}(&box->Value); }}");
        else if (type.Kind == CTypeKind.Struct)
        {
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; uint32_t result = UINT32_C(2166136261);");
            foreach (var field in type.Symbol!.Fields.Where(field => !field.IsStatic))
                writer.WriteLine($"    result = (result ^ {ValueHashExpression(field.Type, $"box->Value.{field.CName}")}) * UINT32_C(16777619);");
            writer.WriteLine("    return ct_i32_bits(result); }");
        }
        else if (type.Kind == CTypeKind.Float)
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return ct_i32_bits(ct_hash_float(box->Value)); }}");
        else
            writer.WriteLine($"static int32_t {hash}(ct_object* value) {{ {box}* box = ({box}*)(void*)value; return ct_i32_bits(ct_hash_bytes(&box->Value, sizeof(box->Value))); }}");
        EmitSpecialVTable(writer, $"ct_vtable_box_{code}", toString, equals, hash, methods, properties);
        var boxRetain = type.ContainsManagedReferences ? $" {ValueRetainName(type)}((void*)&box->Value);" : string.Empty;
        var unboxRetain = type.ContainsManagedReferences ? $" {ValueRetainName(type)}((void*)&result);" : string.Empty;
        writer.WriteLine($"static {NameMangler.Type(Model.Types["System.Object"])}* {BoxFunctionName(type)}({CTypeName(type)} value, const char* file, int line) {{ {box}* box = ({box}*)ct_alloc(sizeof({box}), file, line); ct_init_object(box, &{descriptor}); box->Value = value;{boxRetain} return ({NameMangler.Type(Model.Types["System.Object"])}*)(void*)box; }}");
        writer.WriteLine($"static {CTypeName(type)} {UnboxFunctionName(type)}({NameMangler.Type(Model.Types["System.Object"])}* value, const char* file, int line) {{ if (value == NULL) ct_fail(\"CTO0002\", file, line); ct_object* object = (ct_object*)(void*)value; if (object->Type != &{descriptor}) ct_fail(\"CTO0003\", file, line); {CTypeName(type)} result = (({box}*)(void*)value)->Value;{unboxRetain} return result; }}");
    }

    private static string StructEqualityExpression(TypeSymbol type, string left, string right)
    {
        var comparisons = type.Fields.Where(field => !field.IsStatic)
            .Select(field => ValueEqualityExpression(field.Type, $"{left}.{field.CName}", $"{right}.{field.CName}"))
            .ToArray();
        return comparisons.Length == 0 ? "true" : string.Join(" && ", comparisons.Select(value => $"({value})"));
    }

    private static string ValueEqualityExpression(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.Float => $"{left} == {right} || (isnan({left}) && isnan({right}))",
        CTypeKind.String => $"ct_string_equal({left}, {right})",
        CTypeKind.Class or CTypeKind.Array => $"ct_object_value_equals((ct_object*)(void*){left}, (ct_object*)(void*){right})",
        CTypeKind.Struct => StructEqualityExpression(type.Symbol!, left, right),
        _ => $"{left} == {right}",
    };

    private static string ValueHashExpression(CType type, string value) => type.Kind switch
    {
        CTypeKind.Float => $"ct_hash_float({value})",
        CTypeKind.String => $"({value} == NULL ? 0u : ct_hash_bytes({value}->Data, (size_t){value}->Length))",
        CTypeKind.Class or CTypeKind.Array => $"ct_object_value_hash((ct_object*)(void*){value})",
        CTypeKind.Struct => StructHashExpression(type.Symbol!, value),
        _ => $"ct_hash_bytes(&{value}, sizeof({value}))",
    };

    private static string StructHashExpression(TypeSymbol type, string value)
    {
        var result = "UINT32_C(2166136261)";
        foreach (var field in type.Fields.Where(field => !field.IsStatic))
            result = $"(({result} ^ {ValueHashExpression(field.Type, $"{value}.{field.CName}")}) * UINT32_C(16777619))";
        return result;
    }

    private void EmitMain(CWriter writer)
    {
        if (IsEspIdf)
        {
            writer.WriteLine("void app_main(void)");
            writer.WriteLine("{");
            writer.WriteLine("    (void)setvbuf(stdout, NULL, _IONBF, 0);");
            writer.WriteLine("    (void)setvbuf(stderr, NULL, _IONBF, 0);");
            writer.WriteLine("    ct_thread_state ct_primary_thread;");
            writer.WriteLine("    ct_thread_attach_primary(&ct_primary_thread);");
            writer.WriteLine("    ct_module_init();");
            writer.WriteLine("    ct_thread_publish_ready();");
            if (Model.EntryPoint is not null)
                writer.WriteLine($"    {Model.EntryPoint.CName}();");
            writer.WriteLine("    ct_thread_begin_shutdown();");
            writer.WriteLine("    ct_thread_detach();");
            writer.WriteLine("}");
            return;
        }

        writer.WriteLine("int main(void)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_keep_symbols();");
        writer.WriteLine("    ct_thread_state ct_primary_thread;");
        writer.WriteLine("    ct_thread_attach_primary(&ct_primary_thread);");
        writer.WriteLine("    ct_module_init();");
        writer.WriteLine("    ct_thread_publish_ready();");
        if (Model.EntryPoint is not null)
            writer.WriteLine($"    {Model.EntryPoint.CName}();");
        writer.WriteLine("    ct_thread_begin_shutdown();");
        writer.WriteLine("    ct_thread_detach();");
        writer.WriteLine("    return EXIT_SUCCESS;");
        writer.WriteLine("}");
    }

    private static string MarkUnusedDefinitions(string output)
    {
        var lines = output.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("static ", StringComparison.Ordinal))
                lines[index] = "static CT_UNUSED " + lines[index][7..];
        }
        return string.Join('\n', lines);
    }

    private void EmitKeepSymbols(CWriter writer)
    {
        writer.WriteLine("static void ct_keep_symbols(void)");
        writer.WriteLine("{");
        var runtime = new[]
        {
            "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_dealloc", "ct_retain", "ct_release", "ct_thread_attach", "ct_thread_detach", "ct_thread_require_attached", "ct_memory_retain", "ct_memory_release", "ct_init_object", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_cleanup_push", "ct_cleanup_unwind_to", "ct_cleanup_disarm", "ct_retain_ref_value", "ct_drop_ref_value",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_i64_bits", "ct_i64_add", "ct_i64_sub", "ct_i64_mul", "ct_i64_neg", "ct_i64_div", "ct_i64_mod",
            "ct_u64_div", "ct_u64_mod", "ct_i64_shl", "ct_i64_shr",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint", "ct_to_string_long", "ct_to_string_ulong",
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_long", "ct_write_ulong", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash", "ct_object_to_string", "ct_object_base_to_string", "ct_object_hash", "ct_object_reference_equals",
            "ct_type_is_assignable", "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_object_value_equals", "ct_object_value_hash",
        };
        foreach (var name in runtime)
            writer.WriteLine($"    (void)&{name};");
        if (_usesExceptions)
        {
            writer.WriteLine("    (void)&ct_throw;");
            writer.WriteLine("    (void)&ct_unhandled_exception;");
        }
        if (_usesHostedIo)
        {
            foreach (var name in new[] { "ct_console_read", "ct_console_read_line", "ct_host_file_open", "ct_host_file_read", "ct_host_file_write_buffer", "ct_host_file_write_string", "ct_host_file_close" })
                writer.WriteLine($"    (void)&{name};");
        }
        foreach (var name in MathFunctions.Select(function => function.RuntimeName).Where(_usedMathSymbols.Contains))
            writer.WriteLine($"    (void)&{name};");
        if (_usesNativeIntegers)
        {
            foreach (var name in new[] { "ct_ni_bits", "ct_ni_add", "ct_ni_sub", "ct_ni_mul", "ct_ni_neg", "ct_ni_div", "ct_ni_mod", "ct_nu_div", "ct_nu_mod", "ct_ni_shl", "ct_ni_shr", "ct_to_string_nint", "ct_to_string_nuint", "ct_write_nint", "ct_write_nuint" })
                writer.WriteLine($"    (void)&{name};");
        }
        if (_nativeBufferTypes.Count != 0)
        {
            writer.WriteLine("    (void)&ct_native_bounds;");
            writer.WriteLine("    (void)&ct_stack_bytes;");
        }
        writer.WriteLine("    (void)&ct_default_vtable;");
        foreach (var literal in _stringLiterals.Values.Order())
            writer.WriteLine($"    (void)&ct_sl_{literal};");
        foreach (var type in Model.UserTypes.Where(type => type.Kind is not DeclaredTypeKind.Enum and not DeclaredTypeKind.Opaque))
        {
            foreach (var constructor in type.Constructors)
                writer.WriteLine($"    (void)&{constructor.CName};");
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                writer.WriteLine($"    (void)&{method.CName};");
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                    writer.WriteLine($"    (void)&{NameMangler.Getter(property)};");
                if (property.Setter is not null)
                    writer.WriteLine($"    (void)&{NameMangler.Setter(property)};");
            }
        }
        foreach (var array in _arrayTypes.OrderBy(array => NameMangler.TypeCode(array), StringComparer.Ordinal))
            writer.WriteLine($"    (void)&ct_new_{NameMangler.Array(array.ElementType!)};");
        foreach (var type in Model.UserTypes.Where(type => type.Kind == DeclaredTypeKind.Delegate).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            writer.WriteLine($"    (void)&{DelegateFactoryName(type)};");
            writer.WriteLine($"    (void)&{DelegateDropName(type)};");
            writer.WriteLine($"    (void)&{DescriptorName(type)};");
        }
        foreach (var type in BoxedTypes)
        {
            writer.WriteLine($"    (void)&{BoxFunctionName(type)};");
            writer.WriteLine($"    (void)&{UnboxFunctionName(type)};");
            writer.WriteLine($"    (void)&{BoxDescriptorName(type)};");
            writer.WriteLine($"    (void)&ct_vtable_box_{NameMangler.TypeCode(type)};");
        }
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsStatic && field.Name != "<underlying>"))
            writer.WriteLine($"    (void)&{field.CName};");
        writer.WriteLine("}");
    }
}
