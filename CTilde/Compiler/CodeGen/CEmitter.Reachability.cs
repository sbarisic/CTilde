using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class CEmitter
{
    private ImmutableHashSet<TypeSymbol> _reachableTypes = ImmutableHashSet<TypeSymbol>.Empty;

    private IEnumerable<TypeSymbol> EmittedTypes => Model.UserTypes.Where(_reachableTypes.Contains);

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

        foreach (var function in program.Functions)
        {
            AddMethod(function.Method);
            if (function.Property is not null)
            {
                AddType(function.Property.ContainingType);
                AddCType(function.Property.Type);
            }
            AddSemantics(function.Body.Semantics.Values);
        }
        foreach (var initializer in program.ModuleInitializers)
        {
            AddType(initializer.Field.ContainingType);
            AddCType(initializer.Type);
            AddSemantics(initializer.Body.Semantics.Values);
        }
        AddType(Model.Types.GetValueOrDefault("System.Object"));
        if (Model.Types.ContainsKey("System.IO.IOException"))
        {
            AddType(Model.Types.GetValueOrDefault("System.IO.IOException"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileMode"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileAccess"));
            AddType(Model.Types.GetValueOrDefault("System.IO.FileHandle"));
        }

        while (pending.Count != 0)
        {
            var type = pending.Dequeue();
            AddType(type.BaseType);
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
                AddCType(field.Type);
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
