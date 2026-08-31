using System.Collections.Immutable;

namespace CTilde;

internal readonly record struct IrValue(int Id, CType Type, OwnershipKind Ownership);

internal abstract record IrInstruction(IrValue? Output, SyntaxNode Syntax, ImmutableArray<IrValue> Inputs);
internal sealed record IrConstant(IrValue Result, SyntaxNode Syntax, object? Value) : IrInstruction(Result, Syntax, []);
internal sealed record IrLoad(IrValue Result, SyntaxNode Syntax, object? Symbol, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Syntax, Inputs);
internal sealed record IrStore(IrValue Result, SyntaxNode Syntax, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Syntax, Inputs);
internal sealed record IrUnary(IrValue Result, UnaryExpressionSyntax Expression, IrValue Operand, object? Target) : IrInstruction(Result, Expression, [Operand]);
internal sealed record IrBinary(IrValue Result, BinaryExpressionSyntax Expression, IrValue Left, IrValue Right, object? Target) : IrInstruction(Result, Expression, [Left, Right]);
internal sealed record IrStringBuild(IrValue Result, BinaryExpressionSyntax Expression, ImmutableArray<IrValue> Segments) : IrInstruction(Result, Expression, Segments);
internal sealed record IrConvert(IrValue Result, SyntaxNode Syntax, IrValue Operand) : IrInstruction(Result, Syntax, [Operand]);
internal sealed record IrCall(IrValue Result, CallExpressionSyntax Expression, object? Target, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Expression, Inputs);
internal sealed record IrAllocate(IrValue Result, NewExpressionSyntax Expression, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Expression, Inputs);
internal sealed record IrReadElement(IrValue Result, IndexExpressionSyntax Expression, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Expression, Inputs);
internal sealed record IrTypeCheck(IrValue Result, SyntaxNode Syntax, IrValue Operand) : IrInstruction(Result, Syntax, [Operand]);
internal sealed record IrCopy(IrValue Result, SyntaxNode Syntax, ImmutableArray<IrValue> Inputs) : IrInstruction(Result, Syntax, Inputs);
internal enum IrCheckKind { Null, Bounds, Division, Cast, Allocation }
internal enum IrOwnershipActionKind { AcquireOwned, Retain, Drop }
internal enum IrCleanupActionKind { EnterScope, LeaveScope, EnterExceptionRegion, LeaveExceptionRegion, RunDefer, RunFinally }
internal sealed record IrCheck(SyntaxNode Syntax, IrCheckKind Kind, ImmutableArray<IrValue> Inputs) : IrInstruction(null, Syntax, Inputs);
internal sealed record IrOwnershipAction(SyntaxNode Syntax, IrOwnershipActionKind Kind, IrValue Value) : IrInstruction(null, Syntax, [Value]);
internal sealed record IrCleanupAction(SyntaxNode Syntax, IrCleanupActionKind Kind) : IrInstruction(null, Syntax, []);
internal sealed record IrInlineAssembly(InlineAssemblyStatementSyntax Assembly, ImmutableArray<IrValue> Operands) : IrInstruction(null, Assembly, Operands);

internal abstract record IrTerminator;
internal sealed record IrFallThrough : IrTerminator;
internal sealed record IrBranchTerminator(int TargetBlock) : IrTerminator;
internal sealed record IrConditionalTerminator(IrValue? Condition, int TrueBlock, int FalseBlock) : IrTerminator;
internal sealed record IrSwitchTerminator(IrValue? Value, ImmutableArray<int> TargetBlocks, int DefaultBlock) : IrTerminator;
internal sealed record IrExceptionRegionTerminator(int ProtectedBlock, ImmutableArray<int> HandlerBlocks, int ContinuationBlock) : IrTerminator;
internal sealed record IrReturnTerminator(CType Type) : IrTerminator;
internal sealed record IrThrowTerminator : IrTerminator;

internal sealed record IrBasicBlock(int Id, ImmutableArray<IrInstruction> Instructions, IrTerminator Terminator);
internal sealed record IrFunctionEmission(string Definition);
internal sealed record IrOptimizationFacts(
    ImmutableHashSet<SyntaxNode> CleanupBoundaries,
    ImmutableDictionary<SyntaxNode, object?> Constants,
    ImmutableHashSet<SyntaxNode> KnownNonNullExpressions,
    ImmutableHashSet<SyntaxNode> OwnedMoveCandidates,
    ImmutableHashSet<SyntaxNode> SimdFusionExpressions)
{
    public static IrOptimizationFacts Empty { get; } = new(
        ImmutableHashSet.Create<SyntaxNode>(ReferenceEqualityComparer.Instance),
        ImmutableDictionary.Create<SyntaxNode, object?>(ReferenceEqualityComparer.Instance),
        ImmutableHashSet.Create<SyntaxNode>(ReferenceEqualityComparer.Instance),
        ImmutableHashSet.Create<SyntaxNode>(ReferenceEqualityComparer.Instance),
        ImmutableHashSet.Create<SyntaxNode>(ReferenceEqualityComparer.Instance));
}
internal sealed record IrInitializerEmission(
    ImmutableArray<string> Prelude,
    string Code,
    bool IsConstant,
    OwnershipKind Ownership);
internal sealed record IrFunction(
    MethodSymbol Method,
    BoundBody Body,
    PropertySymbol? Property,
    bool IsGetter,
    ImmutableArray<IrBasicBlock> Blocks,
    IrOptimizationFacts? Optimization = null,
    IrFunctionEmission? Emission = null);
internal sealed record IrStaticInitializer(
    FieldSymbol Field,
    BoundBody Body,
    CType Type,
    BoundSemanticEntry? Value,
    IrInitializerEmission? Emission = null);
internal sealed record TypedIrProgram(
    ImmutableArray<IrFunction> Functions,
    ImmutableArray<IrStaticInitializer> ModuleInitializers);

internal sealed class TypedIrOptimizer(BoundProgram program)
{
    public TypedIrProgram Optimize(TypedIrProgram ir)
    {
        var functionsByMethod = ir.Functions.ToDictionary(function => function.Method);
        var functionsByProperty = ir.Functions.Where(function => function.Property is not null)
            .GroupBy(function => (PropertySymbol)function.Property!)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray());
        var reachable = new HashSet<MethodSymbol>();
        var surfacedTypes = new HashSet<TypeSymbol>();
        var pending = new Queue<MethodSymbol>();

        void Add(MethodSymbol? method)
        {
            if (method is not null && functionsByMethod.ContainsKey(method) && reachable.Add(method))
                pending.Enqueue(method);
        }

        void AddTypeSurface(TypeSymbol? type)
        {
            if (type is null || !surfacedTypes.Add(type))
                return;
            AddTypeSurface(type.BaseType);
            foreach (var function in ir.Functions.Where(function => function.Method.ContainingType == type &&
                (function.Method.IsVirtual || function.Method.IsOverride || function.Method.ImplementedInterfaceMethods.Count != 0 ||
                    (function.Property?.ImplementedInterfaceProperties.Count ?? 0) != 0)))
                Add(function.Method);
        }

        Add(program.Model.EntryPoint);
        foreach (var implementation in program.Model.RuntimeImplementations.Values)
            Add(implementation);
        foreach (var function in ir.Functions.Where(function => function.Method.ExportName is not null ||
            (function.Method.IsVirtual || function.Method.IsOverride) &&
            function.Method.ContainingType.FullName is not ("System.StringSegment" or "System.Text.StringBuilder")))
            Add(function.Method);
        foreach (var function in ir.Functions.Where(function => function.Method.IsUsed && !function.Method.IsGenericDefinition))
            Add(function.Method);
        foreach (var function in ir.Functions.Where(function => function.Method.ImplementedInterfaceMethods.Count != 0 ||
            (function.Property?.ImplementedInterfaceProperties.Count ?? 0) != 0))
            Add(function.Method);
        foreach (var function in ir.Functions.Where(function => function.Method.IsOperator))
            Add(function.Method);
        if (program.Model.Types.TryGetValue("System.Exception", out var exceptionType))
        {
            foreach (var function in ir.Functions.Where(function => function.Property?.ContainingType == exceptionType && function.Property.Name == "Message"))
                Add(function.Method);
        }
        if (program.Model.Types.TryGetValue("System.IO.IOException", out var ioExceptionType))
        {
            foreach (var constructor in ioExceptionType.Constructors)
                Add(constructor);
        }
        foreach (var initializer in program.Bodies.Where(body => body.Method.Name == "<module_init>"))
            AddDependencies(initializer);

        while (pending.Count != 0)
        {
            var method = pending.Dequeue();
            if (functionsByMethod.TryGetValue(method, out var function))
            {
                foreach (var call in function.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>())
                    Add(call.Target as MethodSymbol);
                foreach (var unary in function.Blocks.SelectMany(block => block.Instructions).OfType<IrUnary>())
                    Add(unary.Target as MethodSymbol);
                foreach (var binary in function.Blocks.SelectMany(block => block.Instructions).OfType<IrBinary>())
                    Add(binary.Target as MethodSymbol);
                AddDependencies(function.Body);
            }
            Add(method.ConstructorInitializerTarget);
        }

        return ir with
        {
            Functions = ir.Functions.Where(function => reachable.Contains(function.Method))
                .Select(function => function with { Optimization = Analyze(function) })
                .ToImmutableArray(),
        };

        void AddDependencies(BoundBody body)
        {
            foreach (var deferred in body.DeferredCalls)
                Add(deferred);
            foreach (var semantic in body.Semantics.Values)
            {
                AddTypeSurface(semantic.Type.Symbol);
                switch (semantic.Symbol)
                {
                    case TypeSymbol type:
                        AddTypeSurface(type);
                        break;
                    case MethodSymbol method:
                        Add(method);
                        break;
                    case PropertySymbol property when functionsByProperty.TryGetValue(property, out var accessors):
                        foreach (var accessor in accessors)
                            Add(accessor.Method);
                        break;
                    case MethodGroupBinding group:
                        foreach (var candidate in group.Candidates)
                            Add(candidate);
                        break;
                }
            }
        }

        IrOptimizationFacts Analyze(IrFunction function)
        {
            var cleanupBoundaries = ImmutableHashSet.CreateBuilder<SyntaxNode>(ReferenceEqualityComparer.Instance);
            var constants = ImmutableDictionary.CreateBuilder<SyntaxNode, object?>(ReferenceEqualityComparer.Instance);
            var knownNonNull = ImmutableHashSet.CreateBuilder<SyntaxNode>(ReferenceEqualityComparer.Instance);
            var ownedMoves = ImmutableHashSet.CreateBuilder<SyntaxNode>(ReferenceEqualityComparer.Instance);
            var simdFusionExpressions = ImmutableHashSet.CreateBuilder<SyntaxNode>(ReferenceEqualityComparer.Instance);

            var uses = new Dictionary<int, List<int>>();
            foreach (var block in function.Blocks)
                foreach (var input in block.Instructions.SelectMany(instruction => instruction.Inputs))
                {
                    if (!uses.TryGetValue(input.Id, out var blocks))
                        uses[input.Id] = blocks = [];
                    blocks.Add(block.Id);
                }

            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction.Output is { } output && instruction.Syntax is ExpressionSyntax expression)
                    {
                        var semantic = function.Body.Semantics.GetValueOrDefault(expression);
                        if (semantic?.ConstantValue is not null || expression is LiteralExpressionSyntax { LiteralKind: SyntaxKind.NullKeyword })
                            constants[expression] = semantic?.ConstantValue;
                        if (expression is ThisExpressionSyntax or BaseExpressionSyntax or NewExpressionSyntax or
                            LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken })
                            knownNonNull.Add(expression);
                        if (output.Ownership == OwnershipKind.Owned && output.Type.ContainsManagedReferences)
                            ownedMoves.Add(expression);

                        var kernel = instruction switch
                        {
                            IrCall call => call.Target as MethodSymbol,
                            IrUnary unary => unary.Target as MethodSymbol,
                            IrBinary binary => binary.Target as MethodSymbol,
                            _ => null,
                        };
                        if (kernel is not null && SimdOperation.IsPureFusionKernel(kernel) &&
                            SimdOperation.IsFusionValue(output.Type) &&
                            uses.TryGetValue(output.Id, out var consumers) && consumers.Count == 1 && consumers[0] == block.Id)
                            simdFusionExpressions.Add(expression);
                    }
                }
            }

            MarkCleanupBoundaries(function.Body.Root);
            return new IrOptimizationFacts(cleanupBoundaries.ToImmutable(), constants.ToImmutable(), knownNonNull.ToImmutable(),
                ownedMoves.ToImmutable(), simdFusionExpressions.ToImmutable());

            bool MarkCleanupBoundaries(BoundStatement statement)
            {
                var ownCleanup = statement.Kind is BoundStatementKind.Defer or BoundStatementKind.Try or BoundStatementKind.Catch or BoundStatementKind.Finally ||
                    statement.Syntax is LockStatementSyntax ||
                    statement.Expressions.SelectMany(ExpressionsAndSelf).Any(expression =>
                        expression.Ownership == OwnershipKind.Owned && expression.Type.ContainsManagedReferences);
                if (statement.Syntax is LocalDeclarationStatementSyntax local)
                {
                    var type = local.Type.Name == "var"
                        ? statement.Expressions.FirstOrDefault()?.Type ?? CType.Error
                        : program.Model.ResolveType(local.Type, program.Model.SyntaxTrees.First(tree => ReferenceEquals(tree.Text, local.Source)), function.Method.TypeSubstitutions);
                    ownCleanup |= type.ContainsManagedReferences;
                }
                if (statement.Syntax is ForeachStatementSyntax @foreach)
                {
                    var collectionType = statement.Expressions.FirstOrDefault()?.Type ?? CType.Error;
                    var elementType = collectionType.ElementType ?? CType.Error;
                    var declaredType = @foreach.Type.Name == "var"
                        ? elementType
                        : program.Model.ResolveType(@foreach.Type, program.Model.SyntaxTrees.First(tree => ReferenceEquals(tree.Text, @foreach.Source)), function.Method.TypeSubstitutions);
                    ownCleanup |= declaredType.ContainsManagedReferences;
                }
                var descendantCleanup = false;
                foreach (var child in statement.Children)
                    descendantCleanup |= MarkCleanupBoundaries(child);
                var requiresCleanup = ownCleanup || descendantCleanup;
                if (requiresCleanup)
                    cleanupBoundaries.Add(statement.Syntax);
                return requiresCleanup;
            }

            static IEnumerable<BoundExpression> ExpressionsAndSelf(BoundExpression expression)
            {
                yield return expression;
                foreach (var child in expression.Children)
                    foreach (var descendant in ExpressionsAndSelf(child))
                        yield return descendant;
            }
        }
    }
}

internal sealed class TypedIrLowerer(BoundProgram program)
{
    private CompilationModel Model => program.Model;

    public TypedIrProgram Lower()
    {
        var definitions = ImmutableArray.CreateBuilder<IrFunction>();
        foreach (var type in Model.UserTypes)
        {
            if (type.Kind is DeclaredTypeKind.Enum or DeclaredTypeKind.Opaque or DeclaredTypeKind.Newtype or DeclaredTypeKind.Interface)
                continue;
            foreach (var constructor in type.Constructors)
                AddFunction(FindBoundBody(constructor));
            foreach (var method in type.Methods.Where(method => !method.IsNativeBoundary && !method.IsAbstract && !method.IsGenericDefinition))
                AddFunction(FindBoundBody(method));
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null && !property.IsAbstract)
                    AddFunction(FindBoundBody(property, getter: true), property, isGetter: true);
                if (property.Setter is not null && !property.IsAbstract)
                    AddFunction(FindBoundBody(property, getter: false), property, isGetter: false);
            }
        }
        var moduleInitializers = Model.UserTypes.SelectMany(type => type.Fields)
            .Where(field => field.IsStatic && field.Initializer is not null && !field.IsConstInit && field.Name != "<underlying>")
            .Select(field => new IrStaticInitializer(
                field,
                FindInitializerBoundBody(field.Syntax!),
                field.Type,
                program.SemanticMap.GetValueOrDefault(field.Initializer!)))
            .ToImmutableArray();
        return new TypedIrProgram(definitions.ToImmutable(), moduleInitializers);

        void AddFunction(BoundBody body, PropertySymbol? property = null, bool isGetter = false) =>
            definitions.Add(BuildIr(body, property, isGetter));
    }

    private IrFunction BuildIr(BoundBody body, PropertySymbol? property, bool isGetter)
    {
        var method = body.Method;

        var statements = BoundStatements(body.Root).ToArray();
        var blockIds = new Dictionary<BoundStatement, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < statements.Length; index++)
            blockIds.Add(statements[index], index);
        var blocks = ImmutableArray.CreateBuilder<IrBasicBlock>();
        var values = new Dictionary<BoundExpression, IrValue>(ReferenceEqualityComparer.Instance);
        var nextValue = 0;
        for (var statementIndex = 0; statementIndex < statements.Length; statementIndex++)
        {
            var statement = statements[statementIndex];
            var instructions = ImmutableArray.CreateBuilder<IrInstruction>();
            if (statement.CreatesLexicalScope)
                instructions.Add(new IrCleanupAction(statement.Syntax, IrCleanupActionKind.EnterScope));
            if (statement.Kind == BoundStatementKind.Try)
                instructions.Add(new IrCleanupAction(statement.Syntax, IrCleanupActionKind.EnterExceptionRegion));
            if (statement.Kind == BoundStatementKind.Defer)
                instructions.Add(new IrCleanupAction(statement.Syntax, IrCleanupActionKind.RunDefer));
            if (statement.Kind == BoundStatementKind.Finally)
                instructions.Add(new IrCleanupAction(statement.Syntax, IrCleanupActionKind.RunFinally));

            foreach (var expression in statement.Expressions.SelectMany(BoundExpressions))
            {
                if (values.ContainsKey(expression))
                    continue;
                var result = new IrValue(nextValue++, expression.Type, expression.Ownership);
                var inputs = expression.Children.Where(values.ContainsKey).Select(child => values[child]).ToImmutableArray();
                instructions.Add(expression.Syntax switch
                {
                    LiteralExpressionSyntax or SizeOfExpressionSyntax or AlignOfExpressionSyntax or OffsetOfExpressionSyntax => new IrConstant(result, expression.Syntax, expression.ConstantValue),
                    AssignmentExpressionSyntax => new IrStore(result, expression.Syntax, inputs),
                    UnaryExpressionSyntax unary when inputs.Length != 0 => new IrUnary(result, unary, inputs[0], expression.Symbol),
                    BinaryExpressionSyntax binary when expression.Type == CType.String && binary.OperatorKind == SyntaxKind.PlusToken => new IrStringBuild(result, binary, inputs),
                    BinaryExpressionSyntax binary when inputs.Length >= 2 => new IrBinary(result, binary, inputs[0], inputs[1], expression.Symbol),
                    CastExpressionSyntax or ParenthesizedExpressionSyntax when inputs.Length != 0 => new IrConvert(result, expression.Syntax, inputs[0]),
                    CallExpressionSyntax call => new IrCall(result, call, expression.Symbol, inputs),
                    NewExpressionSyntax allocation => new IrAllocate(result, allocation, inputs),
                    IndexExpressionSyntax index => new IrReadElement(result, index, inputs),
                    TypeTestExpressionSyntax or SafeCastExpressionSyntax when inputs.Length != 0 => new IrTypeCheck(result, expression.Syntax, inputs[0]),
                    NameExpressionSyntax or ThisExpressionSyntax or BaseExpressionSyntax or MemberAccessExpressionSyntax => new IrLoad(result, expression.Syntax, expression.Symbol, inputs),
                    _ => new IrCopy(result, expression.Syntax, inputs),
                });
                AddChecks(instructions, expression, inputs);
                if (expression.Ownership == OwnershipKind.Owned && expression.Type.ContainsManagedReferences)
                    instructions.Add(new IrOwnershipAction(expression.Syntax, IrOwnershipActionKind.AcquireOwned, result));
                values[expression] = result;
            }
            if (statement.CreatesLexicalScope)
                instructions.Add(new IrCleanupAction(statement.Syntax, IrCleanupActionKind.LeaveScope));
            if (statement.Syntax is InlineAssemblyStatementSyntax assembly)
            {
                var operands = statement.Expressions.SelectMany(BoundExpressions)
                    .Select(expression => values.GetValueOrDefault(expression))
                    .Where(value => value.Type is not null)
                    .ToImmutableArray();
                instructions.Add(new IrInlineAssembly(assembly, operands));
            }
            blocks.Add(new IrBasicBlock(statementIndex, instructions.ToImmutable(), Terminator(statement, statementIndex)));
        }
        return new IrFunction(method, body, property, isGetter, blocks.ToImmutable());

        IrTerminator Terminator(BoundStatement statement, int index)
        {
            var next = index + 1 < statements.Length ? index + 1 : -1;
            var targets = statement.Children.Select(child => blockIds[child]).ToImmutableArray();
            var condition = statement.Expressions.SelectMany(BoundExpressions).Select(expression => values.GetValueOrDefault(expression)).LastOrDefault();
            return statement.Kind switch
            {
                BoundStatementKind.Return => new IrReturnTerminator(method.ReturnType),
                BoundStatementKind.Throw => new IrThrowTerminator(),
                BoundStatementKind.Break or BoundStatementKind.Continue => new IrBranchTerminator(-1),
                BoundStatementKind.If or BoundStatementKind.While or BoundStatementKind.Do or BoundStatementKind.For or BoundStatementKind.Foreach =>
                    new IrConditionalTerminator(condition.Id == 0 && condition.Type is null ? null : condition, targets.FirstOrDefault(-1), targets.Skip(1).FirstOrDefault(next)),
                BoundStatementKind.Switch => new IrSwitchTerminator(condition.Id == 0 && condition.Type is null ? null : condition, targets, next),
                BoundStatementKind.Try => new IrExceptionRegionTerminator(targets.FirstOrDefault(-1), targets.Skip(1).ToImmutableArray(), next),
                _ when targets.Length != 0 => new IrBranchTerminator(targets[0]),
                _ when next >= 0 => new IrBranchTerminator(next),
                _ => new IrFallThrough(),
            };
        }
    }

    private BoundBody FindBoundBody(MethodSymbol method) =>
        program.Bodies.FirstOrDefault(candidate => ReferenceEquals(candidate.Method, method)) ??
        program.Bodies.FirstOrDefault(candidate => candidate.Method.Name == method.Name &&
            ReferenceEquals(candidate.Method.Syntax, method.Syntax) && candidate.Method.IsStatic == method.IsStatic) ??
        throw new InvalidOperationException($"No bound body exists for '{method.ContainingType.FullName}.{method.Name}'.");

    private BoundBody FindBoundBody(PropertySymbol property, bool getter)
    {
        var methodName = getter ? $"get_{property.Name}" : $"set_{property.Name}";
        return program.Bodies.FirstOrDefault(candidate =>
            candidate.Method.Name == methodName && candidate.Method.ContainingType == property.ContainingType) ??
            throw new InvalidOperationException($"No bound body exists for '{property.ContainingType.FullName}.{methodName}'.");
    }

    private BoundBody FindInitializerBoundBody(SyntaxNode syntax) =>
        program.Bodies.FirstOrDefault(candidate => candidate.Method.Name == "<module_init>" && ReferenceEquals(candidate.Method.Syntax, syntax)) ??
        throw new InvalidOperationException("No bound body exists for '<module_init>'.");

    private static IEnumerable<BoundExpression> BoundExpressions(BoundStatement statement)
    {
        foreach (var expression in statement.Expressions)
            foreach (var descendant in BoundExpressions(expression))
                yield return descendant;
        foreach (var child in statement.Children)
            foreach (var expression in BoundExpressions(child))
                yield return expression;
    }

    private static IEnumerable<BoundExpression> BoundExpressions(BoundExpression expression)
    {
        foreach (var child in expression.Children)
            foreach (var descendant in BoundExpressions(child))
                yield return descendant;
        yield return expression;
    }

    private static IEnumerable<BoundStatement> BoundStatements(BoundStatement statement)
    {
        yield return statement;
        foreach (var child in statement.Children)
            foreach (var descendant in BoundStatements(child))
                yield return descendant;
    }

    private static void AddChecks(ImmutableArray<IrInstruction>.Builder instructions, BoundExpression expression, ImmutableArray<IrValue> inputs)
    {
        var kind = expression.Syntax switch
        {
            IndexExpressionSyntax => IrCheckKind.Bounds,
            MemberAccessExpressionSyntax or CallExpressionSyntax when expression.Children.FirstOrDefault()?.Type.IsReference == true => IrCheckKind.Null,
            BinaryExpressionSyntax binary when binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken => IrCheckKind.Division,
            CastExpressionSyntax or SafeCastExpressionSyntax => IrCheckKind.Cast,
            NewExpressionSyntax => IrCheckKind.Allocation,
            _ => (IrCheckKind?)null,
        };
        if (kind is not null)
            instructions.Add(new IrCheck(expression.Syntax, kind.Value, inputs));
    }
}
