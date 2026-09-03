using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class CEmitter
{
    private ImmutableHashSet<TypeSymbol> _reachableTypes = ImmutableHashSet<TypeSymbol>.Empty;

    private IEnumerable<TypeSymbol> EmittedTypes => Model.UserTypes.Where(type => _reachableTypes.Contains(type) && !IsOpenGenericType(type));

    private static bool IsOpenGenericType(TypeSymbol type) => type.TypeArguments.Any(ContainsOpenTypeParameter);

    private static bool ContainsOpenTypeParameter(CType type)
    {
        if (type.Kind == CTypeKind.TypeParameter)
            return true;
        if (type.ElementType is not null && ContainsOpenTypeParameter(type.ElementType))
            return true;
        if (type.Symbol is not null && type.Symbol.TypeArguments.Any(ContainsOpenTypeParameter))
            return true;
        if (type.FunctionPointer is not null &&
            (ContainsOpenTypeParameter(type.FunctionPointer.ReturnType) || type.FunctionPointer.ParameterTypes.Any(ContainsOpenTypeParameter)))
            return true;
        return false;
    }

    private void ComputeReachableTypes(TypedIrProgram program)
    {
        var reachable = new HashSet<TypeSymbol>();
        var pending = new Queue<TypeSymbol>();

        void AddType(TypeSymbol? type)
        {
            if (type is not null && reachable.Add(type))
                pending.Enqueue(type);
        }

        void AddCType(CType? type)
        {
            if (type is null)
                return;
            AddType(type.Symbol);
            AddCType(type.ElementType);
            if (type.FunctionPointer is not null)
            {
                AddCType(type.FunctionPointer.ReturnType);
                foreach (var parameter in type.FunctionPointer.ParameterTypes)
                    AddCType(parameter);
            }
        }

        void AddMethod(MethodSymbol method)
        {
            AddType(method.ContainingType);
            AddCType(method.ReturnType);
            foreach (var parameter in method.Parameters)
                AddCType(parameter.Type);
        }

        var functions = IsFreestanding && !Model.FreestandingRuntimeRequired
            ? program.Functions.Where(function => function.Method.IsNaked)
            : program.Functions;
        foreach (var function in functions)
        {
            AddMethod(function.Method);
            foreach (var use in function.Body.ExternUses)
                AddMethod(use.Method);
            if (function.Property is not null)
            {
                AddType(function.Property.ContainingType);
                AddCType(function.Property.Type);
            }
            AddSemantics(function.Body.Semantics.Values);
        }
        if (Model.ManagedModuleKind == ManagedModuleKind.Library)
            foreach (var type in Model.ProjectTypes.Where(type => type.Accessibility == Accessibility.Public))
                AddType(type);
        foreach (var initializer in program.ModuleInitializers)
        {
            AddType(initializer.Field.ContainingType);
            AddCType(initializer.Type);
            AddSemantics(initializer.Body.Semantics.Values);
        }
        foreach (var field in Model.UserTypes.SelectMany(type => type.Fields).Where(field => field.IsUsed || field.ExternName is not null))
        {
            AddType(field.ContainingType);
            AddCType(field.Type);
        }
        if (!IsFreestanding || Model.FreestandingRuntimeRequired)
            AddType(Model.Types.GetValueOrDefault("System.Object"));
        foreach (var type in Model.StaticAssertionLayoutTypes)
            AddType(type);
        foreach (var name in RuntimeFaultTypeNames)
            AddType(Model.Types.GetValueOrDefault(name));
        if (_externUses.Any(use => use.Method.ExternName == "ct_encoding_get_string"))
            AddType(Model.Types.GetValueOrDefault("System.DecoderFallbackException"));
        if (program.Functions.Any(function => function.Body.ExternUses.Any(use =>
                use.Method.ExternName is "ct_random_argument_out_of_range" or "ct_string_argument_out_of_range")))
            AddType(Model.Types.GetValueOrDefault("System.ArgumentOutOfRangeException"));
        if (Model.Types.ContainsKey("System.IO.IOException"))
        {
            AddType(Model.Types.GetValueOrDefault("System.IO.IOException"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileMode"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileAccess"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileHandle"));
            // Hosted-I/O support retains extern declarations as a deterministic ABI
            // surface. Keep their aggregate signature types available whenever that
            // support is emitted, even if the metadata call itself is unreachable.
            if (_usesHostedIo)
                AddType(Model.Types.GetValueOrDefault("System.IO.FileMetadata"));
            if (_usesHostedIo && UsesEspRuntimeIo)
            {
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeResult"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeTransferResult"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeFileMode"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeFileAccess"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeSeekOrigin"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeFileTimestamp"));
                AddType(Model.Types.GetValueOrDefault("System.Runtime.RuntimeFileMetadata"));
                _usesNativeUtf8 = true;
                _usesNativeIntegers = true;
            }
        }

        // Typed-IR lowering may register an array shape while lowering an
        // otherwise pruned standard-library member. If the array declaration
        // is retained, its value-type element must also be in the C type
        // closure so the flexible-array member never names an undefined type.
        foreach (var array in _arrayTypes)
            AddCType(array.ElementType);

        while (pending.Count != 0)
        {
            var type = pending.Dequeue();
            AddType(type.BaseType);
            foreach (var contract in type.Interfaces)
                AddType(contract);
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
                AddCType(field.Type);
            // Ordinary images retain every native extern prototype declared on a
            // reachable type. Keep the complete signatures of those prototypes in
            // the type closure as well; otherwise a sibling overload can name an
            // enum or aggregate that is referenced by generated C but never defined.
            foreach (var method in type.Methods.Where(ShouldEmitMethodPrototype))
                AddMethod(method);
            if (type.Kind == DeclaredTypeKind.Delegate)
            {
                AddCType(type.DelegateReturnType);
                foreach (var parameter in type.DelegateParameters)
                    AddCType(parameter.Type);
            }
        }
        _reachableTypes = reachable.ToImmutableHashSet();

        void AddSemantics(IEnumerable<BoundSemanticEntry> entries)
        {
            foreach (var semantic in entries)
            {
                AddCType(semantic.Type);
                switch (semantic.Symbol)
                {
                    case TypeSymbol type:
                        AddType(type);
                        break;
                    case FieldSymbol field:
                        AddType(field.ContainingType);
                        AddCType(field.Type);
                        break;
                    case PropertySymbol property:
                        AddType(property.ContainingType);
                        AddCType(property.Type);
                        break;
                    case MethodSymbol method:
                        AddMethod(method);
                        break;
                    case MethodGroupBinding group:
                        foreach (var method in group.Candidates)
                            AddMethod(method);
                        break;
                }
            }
        }
    }
}
