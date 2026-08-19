using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed class LoweredExpression
{
    public required CType Type { get; init; }
    public required string Code { get; init; }
    public List<string> Prelude { get; init; } = [];
    public LoweredLValue? LValue { get; init; }
    public TypeSymbol? TypeReceiver { get; init; }
    public bool IsConstant { get; init; }
    public object? ConstantValue { get; init; }
    public bool IsBaseReceiver { get; init; }
    public OwnershipKind Ownership { get; init; }
    public MethodGroupBinding? MethodGroup { get; init; }
    public bool IsFunctionAddress { get; init; }
}

internal sealed record MethodGroupBinding(ImmutableArray<MethodSymbol> Candidates, LoweredExpression? Receiver, bool IsBaseReceiver);

internal enum OwnershipKind { None, Borrowed, Owned, Immortal }

internal sealed class LoweredLValue
{
    public required Func<string, string> Store { get; init; }
    public string? Address { get; init; }
    public LocalSymbol? Local { get; init; }
    public FieldSymbol? Field { get; init; }
    public PropertySymbol? Property { get; init; }
    public bool IsBaseReceiver { get; init; }
}

internal sealed class MethodLowerer
{
    private readonly CEmitter _emitter;
    private readonly CompilationModel _model;
    private readonly DiagnosticBag _diagnostics;
    private readonly MethodSymbol _method;
    private readonly string? _nameOverride;
    private readonly PropertySymbol? _property;
    private readonly bool _isGetter;
    private readonly string _temporaryPrefix;
    private readonly Stack<Dictionary<string, LocalSymbol>> _scopes = [];
    private readonly Stack<string> _cleanupBoundaries = [];
    private readonly Stack<string> _breakCleanupBoundaries = [];
    private readonly Stack<string> _continueCleanupBoundaries = [];
    private readonly HashSet<string> _cleanupRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterSymbol> _parameters;
    private readonly Dictionary<ParameterSymbol, string> _durableParameters = [];
    private readonly Dictionary<string, CType> _durableSlots = new(StringComparer.Ordinal);
    private readonly Stack<string> _breakLabels = [];
    private readonly Stack<string> _continueLabels = [];
    private readonly Stack<List<AssignmentSnapshot>> _breakAssignmentStates = [];
    private readonly Stack<List<AssignmentSnapshot>> _continueAssignmentStates = [];
    private readonly Stack<string> _catchExceptions = [];
    private readonly List<ActiveHandler> _activeExceptionFrames = [];
    private readonly Stack<FinallyContext> _finallyContexts = [];
    private readonly Stack<(int BreakDepth, int ContinueDepth)> _finallyBarriers = [];
    private readonly Dictionary<DeferStatementSyntax, LoweredExpression> _deferredCalls = [];
    private readonly HashSet<FieldSymbol> _assignedFields = [];
    private readonly Dictionary<FieldSymbol, int> _fieldAssignmentCounts = [];
    private readonly HashSet<FieldSymbol> _constantFieldsBeingEvaluated = [];
    private int _localId;
    private int _tempId;
    private int _labelId;
    private int _unsafeDepth;
    private int _repeatableLoopDepth;
    private int _tryId;
    private int _deferId;
    private int _cleanupId;
    private readonly int _tryCount;

    public MethodLowerer(CEmitter emitter, MethodSymbol method, string? nameOverride = null, PropertySymbol? property = null, bool isGetter = false, string temporaryPrefix = "")
    {
        _emitter = emitter;
        _model = emitter.Model;
        _diagnostics = emitter.Diagnostics;
        _method = method;
        _nameOverride = nameOverride;
        _property = property;
        _isGetter = isGetter;
        _temporaryPrefix = temporaryPrefix;
        _parameters = method.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        _unsafeDepth = HasModifier(method.Syntax, "unsafe") ? 1 : 0;
        _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
        _cleanupBoundaries.Push("ct_cleanup_method");
        _tryCount = CountTryStatements(method.Body) + CountDeferStatements(method.Body);
        if (_tryCount != 0)
        {
            for (var index = 0; index < method.Parameters.Length; index++)
                _durableParameters[method.Parameters[index]] = $"ct_pp_{index}";
        }
        if (_tryCount != 0 || ContainsThrow(method.Body))
            _emitter.RegisterExceptions();
    }

    public string EmitDefinition()
    {
        if (_method.IsConstructor && _method.ContainingType.Kind == DeclaredTypeKind.Class)
            return EmitClassConstructorDefinition();
        var body = new CWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            EmitConstructorPrologue(body);
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            if (!_method.IsStatic && !_method.IsConstructor)
                body.WriteLine("(void)ct_self;");
            foreach (var parameter in _method.Parameters)
                body.WriteLine($"(void){NameMangler.Identifier(parameter.Name)};");
            EmitInstanceFieldInitializers(body);
            if (_property is not null && _method.Body is null)
                EmitAutomaticAccessor(body);
            else if (_method.Body is not null)
            {
                var flow = EmitStatements(body, _method.Body.Statements);
                if (!_method.IsConstructor && _method.ReturnType != CType.Void && !flow.AlwaysReturns)
                    Report("CT3100", $"Not every reachable path returns a value from '{_method.Name}'.", _method.Syntax ?? _method.Body);
            }

            if (_method.IsConstructor)
            {
                ValidateConstructorAssignments();
                if (_method.ContainingType.Kind == DeclaredTypeKind.Struct && _method.ContainingType.Type.ContainsManagedReferences)
                {
                    body.WriteLine("ct_cleanup_disarm(&ct_cleanup_struct_constructor);");
                    body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                }
                body.WriteLine(_method.ContainingType.Kind == DeclaredTypeKind.Struct ? "return ct_value;" : "return ct_self;");
            }
            else if (_method.ReturnType == CType.Void && (_property is null || !_isGetter))
            {
                body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                body.WriteLine("return;");
            }
        }
        return RenderFunction(_emitter.MethodSignature(_method, _nameOverride), body);
    }

    private string EmitClassConstructorDefinition()
    {
        var writer = new CWriter();
        var typeName = NameMangler.Type(_method.ContainingType);
        var parameterNames = _method.Parameters.Select(parameter => NameMangler.Identifier(parameter.Name)).ToArray();
        writer.WriteLine(_emitter.MethodSignature(_method, _nameOverride));
        using (writer.Block())
        {
            var source = _method.Syntax ?? _method.ContainingType.Syntax!;
            writer.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            writer.WriteLine("(void)ct_cleanup_method;");
            writer.WriteLine("ct_cleanup_record ct_cleanup_constructor = {0};");
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {_emitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
            writer.WriteLine("ct_cleanup_push(&ct_cleanup_constructor, (void*)&ct_self, ct_drop_ref_value);");
            writer.WriteLine($"{CEmitter.ConstructorInitializerName(_method)}(ct_self{(parameterNames.Length == 0 ? string.Empty : ", " + string.Join(", ", parameterNames))});");
            writer.WriteLine("ct_cleanup_disarm(&ct_cleanup_constructor);");
            writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
            writer.WriteLine("return ct_self;");
        }
        writer.WriteLine();
        var initializerParameters = new[] { $"{typeName}* ct_self" }
            .Concat(_method.Parameters.Select(parameter => _emitter.CDeclaration(parameter.Type, NameMangler.Identifier(parameter.Name))));
        var body = new CWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            body.WriteLine("(void)ct_self;");
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            foreach (var parameter in _method.Parameters)
                body.WriteLine($"(void){NameMangler.Identifier(parameter.Name)};");
            var delegatesToThis = EmitConstructorInitializer(body);
            if (!delegatesToThis)
                EmitInstanceFieldInitializers(body);
            if (_method.Body is not null)
                _ = EmitStatements(body, _method.Body.Statements);
            if (!delegatesToThis)
                ValidateConstructorAssignments();
            body.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
            body.WriteLine("return;");
        }
        writer.WriteBlock(RenderFunction($"static void {CEmitter.ConstructorInitializerName(_method)}({string.Join(", ", initializerParameters)})", body).TrimEnd().Split('\n'));
        return writer.ToString();
    }

    private string RenderFunction(string signature, CWriter body)
    {
        var writer = new CWriter();
        writer.WriteLine(signature);
        using (writer.Block())
        {
            if (_durableSlots.Count != 0)
            {
                writer.WriteLine("volatile struct");
                using (writer.Block())
                {
                    foreach (var slot in _durableSlots)
                        writer.WriteLine($"{_emitter.CDeclaration(slot.Value, slot.Key)};");
                }
                writer.WriteLine("ct_state = {0};");
                writer.WriteLine("(void)ct_state;");
            }
            foreach (var record in _cleanupRecords.Order(StringComparer.Ordinal))
                writer.WriteLine($"ct_cleanup_record {record} = {{0}};");
            writer.WriteBlock(body.ToString().TrimEnd().Split('\n'));
        }
        writer.WriteLine();
        return writer.ToString();
    }

    private void RegisterDurableSlot(string name, CType type) => _durableSlots.TryAdd(name, type);
    private void RegisterCleanupRecord(string name) => _cleanupRecords.Add(name);
    private static string Durable(string name) => $"ct_state.{name}";

    private bool EmitConstructorInitializer(CWriter writer)
    {
        if (_method.ContainingType.IsObject)
            return false;
        var syntax = _method.ConstructorInitializer;
        var targetType = syntax?.Kind == ConstructorInitializerKind.This
            ? _method.ContainingType
            : _method.ContainingType.BaseType;
        if (targetType is null)
            return false;
        var argumentSyntax = syntax?.Arguments ?? [];
        var arguments = argumentSyntax.Select(LowerExpression).ToArray();
        var target = SelectOverload(targetType.Constructors, targetType.Name, arguments, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        if (target is null)
            return syntax?.Kind == ConstructorInitializerKind.This;
        CheckAccess(target, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        if (target.IsUnsafe)
            RequireUnsafe(syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
        _method.ConstructorInitializerTarget = target;
        _emitter.AllocationEffects.RecordCall(_method, target, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!, requiresContract: false);
        var lowered = LowerArguments(arguments, target.Parameters, argumentSyntax);
        EmitPrelude(writer, lowered.Prelude);
        var self = syntax?.Kind == ConstructorInitializerKind.This
            ? "ct_self"
            : $"({NameMangler.Type(targetType)}*)(void*)ct_self";
        writer.WriteLine($"{CEmitter.ConstructorInitializerName(target)}({self}{(lowered.Codes.Count == 0 ? string.Empty : ", " + string.Join(", ", lowered.Codes))});");
        return syntax?.Kind == ConstructorInitializerKind.This;
    }

    public LoweredExpression LowerStandalone(ExpressionSyntax expression) => LowerExpression(expression);

    public LoweredExpression ConvertStandalone(LoweredExpression expression, CType target, SyntaxNode syntax) => Convert(expression, target, syntax, false);

    private void EmitConstructorPrologue(CWriter writer)
    {
        if (!_method.IsConstructor)
            return;
        var typeName = NameMangler.Type(_method.ContainingType);
        if (_method.ContainingType.Kind == DeclaredTypeKind.Struct)
        {
            writer.WriteLine($"{typeName} ct_value = ({typeName}){{0}};");
            writer.WriteLine($"{typeName}* ct_self = &ct_value;");
            writer.WriteLine("(void)ct_self;");
            if (_method.ContainingType.Type.ContainsManagedReferences)
                EmitActivateOwnedSlot(writer, _method.ContainingType.Type, "ct_value", "ct_cleanup_struct_constructor");
        }
        else
        {
            var source = _method.Syntax ?? _method.ContainingType.Syntax!;
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {_emitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
        }
    }

    private void EmitAutomaticAccessor(CWriter writer)
    {
        var field = _property!.BackingField!;
        var access = field.IsStatic ? field.CName : $"ct_self->{field.CName}";
        if (_isGetter)
        {
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_result = {access};");
                writer.WriteLine(_emitter.RetainValueStatement(field.Type, "&ct_result"));
                writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
                writer.WriteLine("return ct_result;");
            }
            else
                writer.WriteLine($"return {access};");
        }
        else
        {
            var parameter = NameMangler.Identifier("value");
            if (field.Type.ContainsManagedReferences)
            {
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_old = {access};");
                writer.WriteLine($"{_emitter.CTypeName(field.Type)} ct_new = {parameter};");
                writer.WriteLine(_emitter.RetainValueStatement(field.Type, "&ct_new"));
                writer.WriteLine($"{access} = ct_new;");
                writer.WriteLine(_emitter.DropValueStatement(field.Type, "&ct_old"));
            }
            else
                writer.WriteLine($"{access} = {parameter};");
        }
    }

    private void EmitInstanceFieldInitializers(CWriter writer)
    {
        if (!_method.IsConstructor)
            return;
        foreach (var field in _method.ContainingType.Fields.Where(field => !field.IsStatic && field.Initializer is not null))
        {
            var expression = Convert(LowerExpression(field.Initializer!), field.Type, field.Initializer!, false);
            EmitPrelude(writer, expression.Prelude);
            if (field.Type.ContainsManagedReferences)
                EmitInitializeOwnedSlot(writer, field.Type, $"ct_self->{field.CName}", expression.Code);
            else
                writer.WriteLine($"ct_self->{field.CName} = {expression.Code};");
            _assignedFields.Add(field);
            _fieldAssignmentCounts[field] = 1;
        }
    }

    private FlowResult EmitStatements(CWriter writer, ImmutableArray<StatementSyntax> statements, bool allowDefer = true)
    {
        var exits = FlowExit.None;
        var reachable = true;
        for (var index = 0; index < statements.Length; index++)
        {
            var statement = statements[index];
            if (!reachable)
                Report("CT3101", "Unreachable statement.", statement);
            var before = reachable ? null : SnapshotAssignments();
            if (statement is DeferStatementSyntax defer && !_deferredCalls.ContainsKey(defer))
            {
                if (!allowDefer)
                {
                    Report("CT3111", "defer must be a direct member of a braced block.", defer);
                    continue;
                }
                if (defer.Expression is not CallExpressionSyntax call)
                {
                    Report("CT2156", "defer requires a method invocation.", defer.Expression);
                    _ = LowerExpression(defer.Expression);
                    continue;
                }
                var lowered = LowerCall(call, captureForDefer: true);
                EmitPrelude(writer, lowered.Prelude);
                _deferredCalls[defer] = lowered;
                var tailStatements = statements[(index + 1)..];
                var tailEnd = tailStatements.IsDefaultOrEmpty ? defer.Span.End : tailStatements[^1].Span.End;
                var tail = new BlockStatementSyntax(defer.Source, TextSpan.FromBounds(defer.Span.End, tailEnd), tailStatements);
                var cleanupBody = new BlockStatementSyntax(defer.Source, defer.Span, [defer]);
                var finallyClause = new FinallyClauseSyntax(defer.Source, defer.Span, cleanupBody);
                var synthetic = new TryStatementSyntax(defer.Source, TextSpan.FromBounds(defer.Span.Start, tailEnd), tail, [], finallyClause);
                var deferFlow = EmitTry(writer, synthetic);
                _deferredCalls.Remove(defer);
                if (!reachable)
                    RestoreAssignments(before!);
                else
                {
                    exits |= deferFlow.Exits & ~FlowExit.FallThrough;
                    reachable = deferFlow.FallsThrough;
                }
                break;
            }
            var flow = EmitStatement(writer, statement);
            if (!reachable)
            {
                RestoreAssignments(before!);
                continue;
            }
            exits |= flow.Exits & ~FlowExit.FallThrough;
            reachable = flow.FallsThrough;
        }
        return new FlowResult(exits | (reachable ? FlowExit.FallThrough : FlowExit.None));
    }

    private FlowResult EmitStatement(CWriter writer, StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockStatementSyntax block:
                using (writer.Block())
                {
                    BeginScope(writer);
                    var flow = EmitStatements(writer, block.Statements);
                    EndScope(writer, flow.FallsThrough);
                    return flow;
                }
            case EmptyStatementSyntax:
                writer.WriteLine(";");
                return FlowResult.None;
            case LocalDeclarationStatementSyntax local:
                EmitLocal(writer, local);
                return FlowResult.None;
            case ExpressionStatementSyntax expression:
                {
                    var lowered = LowerExpression(expression.Expression);
                    EmitPrelude(writer, lowered.Prelude);
                    writer.WriteLine($"(void)({lowered.Code});");
                    return FlowResult.None;
                }
            case IfStatementSyntax @if:
                return EmitIf(writer, @if);
            case WhileStatementSyntax @while:
                EmitWhile(writer, @while);
                return FlowResult.None;
            case DoStatementSyntax @do:
                return EmitDo(writer, @do);
            case ForStatementSyntax @for:
                EmitFor(writer, @for);
                return FlowResult.None;
            case ForeachStatementSyntax @foreach:
                EmitForeach(writer, @foreach);
                return FlowResult.None;
            case SwitchStatementSyntax @switch:
                return EmitSwitch(writer, @switch);
            case BreakStatementSyntax:
                if (_breakLabels.Count == 0)
                    Report("CT3102", "break is valid only inside a loop or switch.", statement);
                else if (_finallyBarriers.Count != 0 && _breakLabels.Count <= _finallyBarriers.Peek().BreakDepth)
                    Report("CT3110", "break cannot leave a finally block.", statement);
                else
                {
                    _breakAssignmentStates.Peek().Add(SnapshotAssignments());
                    EmitBreakOrContinue(writer, false);
                }
                return new FlowResult(FlowExit.Break);
            case ContinueStatementSyntax:
                if (_continueLabels.Count == 0)
                    Report("CT3103", "continue is valid only inside a loop.", statement);
                else if (_finallyBarriers.Count != 0 && _continueLabels.Count <= _finallyBarriers.Peek().ContinueDepth)
                    Report("CT3110", "continue cannot leave a finally block.", statement);
                else
                {
                    _continueAssignmentStates.Peek().Add(SnapshotAssignments());
                    EmitBreakOrContinue(writer, true);
                }
                return new FlowResult(FlowExit.Continue);
            case DeferStatementSyntax defer:
                if (_deferredCalls.TryGetValue(defer, out var deferred))
                {
                    if (deferred.Type.ContainsManagedReferences)
                    {
                        var ignored = NewTemp();
                        writer.WriteLine($"{_emitter.CDeclaration(deferred.Type, ignored)} = {deferred.Code};");
                        writer.WriteLine(_emitter.DropValueStatement(deferred.Type, $"&{ignored}"));
                    }
                    else
                        writer.WriteLine($"(void)({deferred.Code});");
                    return FlowResult.None;
                }
                Report("CT3111", "defer must be a direct member of a braced block.", defer);
                return FlowResult.None;
            case ReturnStatementSyntax @return:
                EmitReturn(writer, @return);
                return new FlowResult(FlowExit.Return);
            case ThrowStatementSyntax @throw:
                EmitThrow(writer, @throw);
                return new FlowResult(FlowExit.Throw);
            case TryStatementSyntax @try:
                return EmitTry(writer, @try);
            case UnsafeStatementSyntax unsafeStatement:
                _unsafeDepth++;
                var unsafeFlow = EmitStatement(writer, unsafeStatement.Body);
                _unsafeDepth--;
                return unsafeFlow;
            default:
                return FlowResult.None;
        }
    }

    private void EmitLocal(CWriter writer, LocalDeclarationStatementSyntax syntax)
    {
        if (FindLocal(syntax.Name) is not null || _parameters.ContainsKey(syntax.Name))
            Report("CT1106", $"A local named '{syntax.Name}' is already active.", syntax);
        var tree = TreeFor(syntax);
        var type = syntax.Type.Name == "var" ? CType.Error : _model.ResolveType(syntax.Type, tree);
        LoweredExpression? initializer = null;
        if (syntax.Initializer is not null)
        {
            initializer = LowerExpression(syntax.Initializer);
            if (syntax.Type.Name == "var")
            {
                if (initializer.Type.Kind is CTypeKind.Null or CTypeKind.Void)
                {
                    Report("CT2102", "var requires an initializer with a usable compile-time type.", syntax.Initializer);
                    type = CType.Error;
                }
                else
                    type = initializer.Type;
            }
            initializer = Convert(initializer, type, syntax.Initializer, false);
        }
        else if (syntax.Type.Name == "var")
            Report("CT2103", "var requires an initializer.", syntax);
        if (type.ContainsPointer)
            RequireUnsafe(syntax);
        _emitter.RegisterType(type);
        if (syntax.IsConst && initializer is not null && !initializer.IsConstant)
            Report("CT2104", "A const initializer must be a compile-time constant.", syntax.Initializer!);

        var symbol = new LocalSymbol
        {
            Name = syntax.Name,
            Type = type,
            Id = _localId++,
            Syntax = syntax,
            IsReadonly = syntax.IsReadonly,
            IsConst = syntax.IsConst,
            LoopDepthAtDeclaration = _repeatableLoopDepth,
            IsAssigned = initializer is not null,
            AssignmentCount = initializer is null ? 0 : 1,
            ConstantCode = syntax.IsConst ? initializer?.Code : null,
            ConstantValue = syntax.IsConst ? initializer?.ConstantValue : null,
            IsDurable = _tryCount != 0,
        };
        _scopes.Peek()[syntax.Name] = symbol;
        if (symbol.IsDurable)
        {
            RegisterDurableSlot(symbol.StorageName, type);
        }
        else
            writer.WriteLine($"{_emitter.CDeclaration(type, symbol.CName)} = {_emitter.DefaultValue(type)};");
        if (type.ContainsManagedReferences)
            EmitActivateOwnedSlot(writer, type, symbol.CName, $"ct_cleanup_local_{symbol.Id}");
        if (initializer is not null)
        {
            EmitPrelude(writer, initializer.Prelude);
            if (type.ContainsManagedReferences)
                EmitInitializeOwnedSlot(writer, type, symbol.CName, initializer.Code);
            else
                writer.WriteLine($"{symbol.CName} = {initializer.Code};");
        }
        writer.WriteLine($"(void){symbol.CName};");
    }

    private FlowResult EmitIf(CWriter writer, IfStatementSyntax syntax)
    {
        var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
        EmitPrelude(writer, condition.Prelude);
        var before = SnapshotAssignments();
        writer.WriteLine($"if {FormatCondition(condition.Code)}");
        var thenFlow = EmitEmbedded(writer, syntax.Then);
        var thenAssignments = SnapshotAssignments();
        RestoreAssignments(before);
        FlowResult elseFlow = FlowResult.None;
        AssignmentSnapshot elseAssignments;
        if (syntax.Else is not null)
        {
            writer.WriteLine("else");
            elseFlow = EmitEmbedded(writer, syntax.Else);
            elseAssignments = SnapshotAssignments();
        }
        else
            elseAssignments = before;
        var fallthroughStates = new List<AssignmentSnapshot>();
        if (thenFlow.FallsThrough)
            fallthroughStates.Add(thenAssignments);
        if (elseFlow.FallsThrough)
            fallthroughStates.Add(elseAssignments);
        RestoreAssignments(fallthroughStates.Count == 0 ? before : MergeAssignments(fallthroughStates));
        return new FlowResult(thenFlow.Exits | elseFlow.Exits);
    }

    private FlowResult EmitEmbedded(CWriter writer, StatementSyntax statement)
    {
        if (statement is BlockStatementSyntax)
            return EmitStatement(writer, statement);
        using (writer.Block())
        {
            BeginScope(writer);
            var flow = EmitStatement(writer, statement);
            EndScope(writer, flow.FallsThrough);
            return flow;
        }
    }

    private void EmitWhile(CWriter writer, WhileStatementSyntax syntax)
    {
        var cleanup = EmitCleanupBoundary(writer, "while");
        var start = NewLabel("while_test");
        var @continue = NewLabel("while_continue");
        var @break = NewLabel("while_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
        EmitPrelude(writer, condition.Prelude);
        writer.WriteLine($"if (!{FormatCondition(condition.Code)}) goto {@break};");
        _breakAssignmentStates.Push([]); _continueAssignmentStates.Push([]);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _breakCleanupBoundaries.Push(cleanup); _continueCleanupBoundaries.Push(cleanup);
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueCleanupBoundaries.Pop(); _breakCleanupBoundaries.Pop();
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        RestoreAssignments(before);
    }

    private FlowResult EmitDo(CWriter writer, DoStatementSyntax syntax)
    {
        var cleanup = EmitCleanupBoundary(writer, "do");
        var start = NewLabel("do_body");
        var @continue = NewLabel("do_continue");
        var @break = NewLabel("do_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        var breakStates = new List<AssignmentSnapshot>();
        var continueStates = new List<AssignmentSnapshot>();
        _breakAssignmentStates.Push(breakStates); _continueAssignmentStates.Push(continueStates);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _breakCleanupBoundaries.Push(cleanup); _continueCleanupBoundaries.Push(cleanup);
        var canRepeat = syntax.Condition is not LiteralExpressionSyntax { LiteralKind: SyntaxKind.FalseKeyword };
        if (canRepeat)
            _repeatableLoopDepth++;
        var bodyFlow = EmitEmbedded(writer, syntax.Body);
        if (canRepeat)
            _repeatableLoopDepth--;
        var bodyState = SnapshotAssignments();
        _continueCleanupBoundaries.Pop(); _breakCleanupBoundaries.Pop();
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        var conditionStates = new List<AssignmentSnapshot>(continueStates);
        if (bodyFlow.FallsThrough)
            conditionStates.Add(bodyState);
        AssignmentSnapshot? conditionExit = null;
        if (conditionStates.Count > 0)
        {
            RestoreAssignments(MergeAssignments(conditionStates));
            var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
            EmitPrelude(writer, condition.Prelude);
            conditionExit = SnapshotAssignments();
            writer.WriteLine($"if {FormatCondition(condition.Code)} goto {start};");
        }
        writer.WriteLine($"goto {@break};");
        writer.WriteLine($"{@break}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        var exits = new List<AssignmentSnapshot>(breakStates);
        if (conditionExit is not null)
            exits.Add(conditionExit);
        RestoreAssignments(exits.Count == 0 ? before : MergeAssignments(exits));
        var flowExits = bodyFlow.Exits & FlowExit.Return;
        if (exits.Count > 0)
            flowExits |= FlowExit.FallThrough;
        return new FlowResult(flowExits);
    }

    private void EmitFor(CWriter writer, ForStatementSyntax syntax)
    {
        BeginScope(writer);
        if (syntax.Initializer is not null)
            EmitStatement(writer, syntax.Initializer);
        var start = NewLabel("for_test");
        var @continue = NewLabel("for_continue");
        var @break = NewLabel("for_break");
        var before = SnapshotAssignments();
        var cleanup = EmitCleanupBoundary(writer, "for");
        writer.WriteLine($"{start}:;");
        if (syntax.Condition is not null)
        {
            var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
            EmitPrelude(writer, condition.Prelude);
            writer.WriteLine($"if (!{FormatCondition(condition.Code)}) goto {@break};");
        }
        _breakAssignmentStates.Push([]); _continueAssignmentStates.Push([]);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _breakCleanupBoundaries.Push(cleanup); _continueCleanupBoundaries.Push(cleanup);
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueCleanupBoundaries.Pop(); _breakCleanupBoundaries.Pop();
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        if (syntax.Iterator is not null)
        {
            var iterator = LowerExpression(syntax.Iterator);
            EmitPrelude(writer, iterator.Prelude);
            writer.WriteLine($"(void)({iterator.Code});");
        }
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        RestoreAssignments(before);
        EndScope(writer, fallsThrough: true);
    }

    private void EmitForeach(CWriter writer, ForeachStatementSyntax syntax)
    {
        BeginScope(writer);
        var collection = Materialize(LowerExpression(syntax.Collection), syntax.Collection);
        if (collection.Type.Kind != CTypeKind.Array)
            Report("CT2105", "foreach requires a one-dimensional array.", syntax.Collection);
        EmitPrelude(writer, collection.Prelude);
        var cleanup = EmitCleanupBoundary(writer, "foreach");
        var elementType = collection.Type.ElementType ?? CType.Error;
        var declaredType = syntax.Type.Name == "var" ? elementType : _model.ResolveType(syntax.Type, TreeFor(syntax));
        if (!TypeFacts.CanImplicitlyConvert(elementType, declaredType))
            Report("CT2106", $"Array element type '{elementType.DisplayName}' cannot convert to '{declaredType.DisplayName}'.", syntax.Type);
        var local = new LocalSymbol
        {
            Name = syntax.Name,
            Type = declaredType,
            Id = _localId++,
            Syntax = syntax,
            IsAssigned = true,
            AssignmentCount = 1,
            LoopDepthAtDeclaration = _repeatableLoopDepth + 1,
            IsDurable = _tryCount != 0,
        };
        _scopes.Peek()[syntax.Name] = local;
        var index = NewTemp();
        writer.WriteLine($"int32_t {index} = 0;");
        var start = NewLabel("foreach_test");
        var @continue = NewLabel("foreach_continue");
        var @break = NewLabel("foreach_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        writer.WriteLine($"if ({index} >= {collection.Code}->Length) goto {@break};");
        if (local.IsDurable)
        {
            RegisterDurableSlot(local.StorageName, declaredType);
        }
        else
            writer.WriteLine($"{_emitter.CDeclaration(declaredType, local.CName)} = {_emitter.DefaultValue(declaredType)};");
        if (declaredType.ContainsManagedReferences)
        {
            EmitActivateOwnedSlot(writer, declaredType, local.CName, $"ct_cleanup_local_{local.Id}");
            EmitInitializeOwnedSlot(writer, declaredType, local.CName, $"{collection.Code}->Data[{index}]");
        }
        else
            writer.WriteLine($"{local.CName} = {collection.Code}->Data[{index}];");
        _breakAssignmentStates.Push([]); _continueAssignmentStates.Push([]);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _breakCleanupBoundaries.Push(cleanup); _continueCleanupBoundaries.Push(cleanup);
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueCleanupBoundaries.Pop(); _breakCleanupBoundaries.Pop();
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        writer.WriteLine($"{index} = ct_i32_add({index}, 1);");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        EndScope(writer, fallsThrough: true);
        RestoreAssignments(before);
    }

    private FlowResult EmitSwitch(CWriter writer, SwitchStatementSyntax syntax)
    {
        var value = Materialize(LowerExpression(syntax.Expression), syntax.Expression);
        if (!value.Type.IsIntegral)
            Report("CT2107", "switch requires an integral or enum expression.", syntax.Expression);
        EmitPrelude(writer, value.Prelude);
        var @break = NewLabel("switch_break");
        var cleanup = EmitCleanupBoundary(writer, "switch");
        var before = SnapshotAssignments();
        var breakStates = new List<AssignmentSnapshot>();
        _breakAssignmentStates.Push(breakStates);
        _breakLabels.Push(@break);
        _breakCleanupBoundaries.Push(cleanup);
        var sectionFlows = new List<FlowResult>();
        var fallthroughStates = new List<AssignmentSnapshot>();
        var caseValues = new HashSet<string>(StringComparer.Ordinal);
        var hasDefault = false;
        writer.WriteLine($"switch ({value.Code})");
        using (writer.Block())
        {
            foreach (var section in syntax.Sections)
            {
                RestoreAssignments(before);
                foreach (var label in section.Labels)
                {
                    if (label.Value is null)
                    {
                        if (hasDefault)
                            Report("CT3104", "A switch can contain only one default label.", label);
                        hasDefault = true;
                        writer.WriteLine("default:;");
                    }
                    else
                    {
                        var constant = LowerExpression(label.Value);
                        if (!constant.IsConstant || constant.Prelude.Count != 0 || !TryConvertCaseConstant(constant, value.Type, out var key, out var code))
                        {
                            Report("CT2108", "A case label must be an integral constant.", label.Value);
                            code = "0";
                        }
                        else if (!caseValues.Add(key))
                            Report("CT3109", "A switch cannot contain duplicate case values after conversion.", label.Value);
                        writer.WriteLine($"case {code}:;");
                    }
                }
                BeginScope(writer);
                var flow = EmitStatements(writer, section.Statements, allowDefer: false);
                var sectionState = SnapshotAssignments();
                EndScope(writer, flow.FallsThrough);
                sectionFlows.Add(flow);
                if (flow.FallsThrough)
                    fallthroughStates.Add(sectionState);
                if (flow.FallsThrough)
                    Report("CT3105", "A switch section must end with break, continue, or return.", section);
            }
        }
        _breakCleanupBoundaries.Pop();
        _breakLabels.Pop();
        _breakAssignmentStates.Pop();
        writer.WriteLine($"{@break}:;");
        writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        var switchExitStates = new List<AssignmentSnapshot>(breakStates);
        switchExitStates.AddRange(fallthroughStates);
        if (!hasDefault)
            switchExitStates.Add(before);
        RestoreAssignments(switchExitStates.Count == 0 ? before : MergeAssignments(switchExitStates));

        var exits = sectionFlows.Aggregate(FlowExit.None, (current, flow) => current | (flow.Exits & (FlowExit.Return | FlowExit.Continue)));
        if (!hasDefault || breakStates.Count > 0 || fallthroughStates.Count > 0)
            exits |= FlowExit.FallThrough;
        return new FlowResult(exits);
    }

    private bool TryConvertCaseConstant(LoweredExpression constant, CType governingType, out string key, out string code)
    {
        key = string.Empty;
        code = "0";
        if (!constant.Type.IsIntegral || constant.Type.Kind == CTypeKind.Enum && constant.Type != governingType)
            return false;
        var target = governingType.Kind == CTypeKind.Enum
            ? governingType.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type
            : governingType;
        if (!target.IsIntegral || !TryGetIntegralValue(constant.ConstantValue, out var value) || !FitsIntegralType(value, target))
            return false;
        key = value.ToString(CultureInfo.InvariantCulture);
        var literal = target.Kind switch
        {
            CTypeKind.Uint => $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})",
            CTypeKind.Long => FormatInt64((long)value),
            CTypeKind.Ulong => FormatUInt64((ulong)value),
            _ => value == int.MinValue ? "INT32_MIN" : value.ToString(CultureInfo.InvariantCulture),
        };
        code = governingType.Kind == CTypeKind.Enum ? $"({_emitter.CTypeName(governingType)})({literal})" : $"({_emitter.CTypeName(target)})({literal})";
        return true;
    }

    private static bool TryGetIntegralValue(object? constant, out BigInteger value)
    {
        switch (constant)
        {
            case BigInteger item: value = item; return true;
            case byte item: value = item; return true;
            case sbyte item: value = item; return true;
            case short item: value = item; return true;
            case ushort item: value = item; return true;
            case int item: value = item; return true;
            case uint item: value = item; return true;
            case long item: value = item; return true;
            case ulong item: value = item; return true;
            default: value = default; return false;
        }
    }

    private static bool FitsIntegralType(BigInteger value, CType type) => type.Kind switch
    {
        CTypeKind.Byte or CTypeKind.Char => value >= byte.MinValue && value <= byte.MaxValue,
        CTypeKind.Sbyte => value >= sbyte.MinValue && value <= sbyte.MaxValue,
        CTypeKind.Short => value >= short.MinValue && value <= short.MaxValue,
        CTypeKind.Ushort => value >= ushort.MinValue && value <= ushort.MaxValue,
        CTypeKind.Int => value >= int.MinValue && value <= int.MaxValue,
        CTypeKind.Uint => value >= uint.MinValue && value <= uint.MaxValue,
        CTypeKind.Long => value >= long.MinValue && value <= long.MaxValue,
        CTypeKind.Ulong => value >= ulong.MinValue && value <= ulong.MaxValue,
        _ => false,
    };

    private void EmitReturn(CWriter writer, ReturnStatementSyntax syntax)
    {
        if (_finallyBarriers.Count != 0)
        {
            Report("CT3110", "return cannot leave a finally block.", syntax);
            return;
        }
        if (_method.IsConstructor)
        {
            Report("CT3106", "A constructor cannot contain a return statement in draft 0.7.", syntax);
            return;
        }
        if (_method.ReturnType == CType.Void)
        {
            if (syntax.Expression is not null)
                Report("CT2109", "A void method cannot return a value.", syntax.Expression);
            EmitReturnTransfer(writer, null);
            return;
        }
        if (syntax.Expression is null)
        {
            Report("CT2110", $"Method '{_method.Name}' must return '{_method.ReturnType.DisplayName}'.", syntax);
            writer.WriteLine($"return {_emitter.DefaultValue(_method.ReturnType)};");
            return;
        }
        var expression = Convert(LowerExpression(syntax.Expression), _method.ReturnType, syntax.Expression, false);
        EmitPrelude(writer, expression.Prelude);
        EmitReturnTransfer(writer, expression.Code);
    }

    private void EmitThrow(CWriter writer, ThrowStatementSyntax syntax)
    {
        string exceptionCode;
        if (syntax.Expression is null)
        {
            if (_catchExceptions.Count == 0)
            {
                Report("CT2154", "A rethrow statement is valid only inside a catch clause.", syntax);
                return;
            }
            exceptionCode = _catchExceptions.Peek();
        }
        else
        {
            var expression = LowerExpression(syntax.Expression);
            var exceptionType = _model.Types["System.Exception"].Type;
            if (expression.Type.Kind != CTypeKind.Null &&
                (expression.Type.Kind != CTypeKind.Class || expression.Type.Symbol is null ||
                 expression.Type.Symbol != exceptionType.Symbol && !expression.Type.Symbol.DerivesFrom(exceptionType.Symbol!)))
                Report("CT2151", "A thrown value must derive from System.Exception.", syntax.Expression);
            expression = Convert(expression, exceptionType, syntax.Expression, false);
            EmitPrelude(writer, expression.Prelude);
            exceptionCode = expression.Code;
        }
        writer.WriteLine($"ct_throw((ct_object*)(void*){exceptionCode}, {_emitter.SourceArgument(syntax)});");
    }

    private FlowResult EmitTry(CWriter writer, TryStatementSyntax syntax)
    {
        var id = _tryId++;
        var before = SnapshotAssignments();
        return syntax.Finally is null
            ? EmitTryCatchCore(writer, syntax, id, before)
            : EmitTryFinally(writer, syntax, id, before);
    }

    private FlowResult EmitTryFinally(CWriter writer, TryStatementSyntax syntax, int id, AssignmentSnapshot before)
    {
        var frame = $"ct_eh_{id}_finally";
        var cleanup = NewLabel("finally");
        var after = NewLabel("after_finally");
        var context = new FinallyContext(
            id, cleanup, _activeExceptionFrames.Count, _breakLabels.Count, _continueLabels.Count,
            _breakLabels.Count == 0 ? null : _breakLabels.Peek(),
            _continueLabels.Count == 0 ? null : _continueLabels.Peek());
        _finallyContexts.Push(context);

        var exceptionStateType = _model.Types["System.Object"].Type;
        EmitActivateOwnedSlot(writer, exceptionStateType, Durable($"ct_ex_{id}"), $"ct_cleanup_exception_{id}");
        if (!_method.IsConstructor && _method.ReturnType.ContainsManagedReferences)
            EmitActivateOwnedSlot(writer, _method.ReturnType, Durable($"ct_er_{id}"), $"ct_cleanup_return_{id}");
        writer.WriteLine($"{frame}->Previous = ct_exception_top;");
        writer.WriteLine($"{frame}->CleanupBoundary = ct_cleanup_top;");
        writer.WriteLine($"ct_exception_top = {frame};");
        _activeExceptionFrames.Add(new ActiveHandler(frame, _breakLabels.Count, _continueLabels.Count));
        writer.WriteLine($"if (setjmp(*{frame}->Target) == 0)");
        FlowResult protectedFlow;
        using (writer.Block())
        {
            protectedFlow = syntax.Catches.Length == 0
                ? EmitStatement(writer, syntax.Body)
                : EmitTryCatchCore(writer, syntax, id, before);
            if (protectedFlow.FallsThrough)
            {
                writer.WriteLine($"ct_exception_top = {frame}->Previous;");
                writer.WriteLine($"{Durable($"ct_ep_{id}")} = 0;");
                writer.WriteLine($"goto {cleanup};");
            }
        }
        writer.WriteLine("else");
        using (writer.Block())
        {
            writer.WriteLine($"{CEmitter.ValueDropName(exceptionStateType)}((void*)(uintptr_t)&{Durable($"ct_ex_{id}")});");
            writer.WriteLine($"{Durable($"ct_ex_{id}")} = ({_emitter.CTypeName(exceptionStateType)})(void*)ct_current_exception;");
            writer.WriteLine("ct_current_exception = NULL;");
            writer.WriteLine($"ct_exception_top = {frame}->Previous;");
            writer.WriteLine($"{Durable($"ct_ep_{id}")} = 4;");
            writer.WriteLine($"goto {cleanup};");
        }
        _activeExceptionFrames.RemoveAt(_activeExceptionFrames.Count - 1);
        _finallyContexts.Pop();

        writer.WriteLine($"{cleanup}:;");
        var protectedAssignments = SnapshotAssignments();
        RestoreAssignments(before);
        _finallyBarriers.Push((_breakLabels.Count, _continueLabels.Count));
        var finallyFlow = EmitStatement(writer, syntax.Finally!.Body);
        _finallyBarriers.Pop();
        var finallyAssignments = SnapshotAssignments();
        ValidateFinallyReadonlyAssignments(protectedAssignments, before, finallyAssignments, syntax.Finally!);
        if (finallyFlow.FallsThrough)
            RestoreAssignments(ApplyFinallyAssignments(protectedAssignments, before, finallyAssignments));

        if (finallyFlow.FallsThrough)
        {
            writer.WriteLine($"if ({Durable($"ct_ep_{id}")} == 4) ct_throw((ct_object*)(void*){Durable($"ct_ex_{id}")}, {_emitter.SourceArgument(syntax)});");
            if (!_method.IsConstructor && _method.ReturnType != CType.Void)
            {
                writer.WriteLine($"if ({Durable($"ct_ep_{id}")} == 1)");
                using (writer.Block())
                    EmitReturnTransfer(writer, Durable($"ct_er_{id}"));
            }
            else
            {
                writer.WriteLine($"if ({Durable($"ct_ep_{id}")} == 1)");
                using (writer.Block())
                    EmitReturnTransfer(writer, null);
            }
            if (context.BreakTarget is not null)
            {
                writer.WriteLine($"if ({Durable($"ct_ep_{id}")} == 2)");
                using (writer.Block())
                    EmitResumedBranch(writer, false, context.BreakTarget);
            }
            if (context.ContinueTarget is not null)
            {
                writer.WriteLine($"if ({Durable($"ct_ep_{id}")} == 3)");
                using (writer.Block())
                    EmitResumedBranch(writer, true, context.ContinueTarget);
            }
            writer.WriteLine($"goto {after};");
            writer.WriteLine($"{after}:;");
            if (!protectedFlow.FallsThrough)
                writer.WriteLine(_method.ReturnType == CType.Void || _method.IsConstructor
                    ? "return;"
                    : $"return {_emitter.DefaultValue(_method.ReturnType)};");
        }

        if (!finallyFlow.FallsThrough)
            return finallyFlow;
        var exits = (protectedFlow.Exits | finallyFlow.Exits) & ~FlowExit.FallThrough;
        if (protectedFlow.FallsThrough)
            exits |= FlowExit.FallThrough;
        return new FlowResult(exits);
    }

    private FlowResult EmitTryCatchCore(CWriter writer, TryStatementSyntax syntax, int id, AssignmentSnapshot before)
    {
        if (syntax.Catches.Length == 0)
            return EmitStatement(writer, syntax.Body);

        var catches = BindCatches(syntax);
        var frame = $"ct_eh_{id}_catch";
        var done = NewLabel("after_catch");
        writer.WriteLine($"{frame}->Previous = ct_exception_top;");
        writer.WriteLine($"{frame}->CleanupBoundary = ct_cleanup_top;");
        writer.WriteLine($"ct_exception_top = {frame};");
        _activeExceptionFrames.Add(new ActiveHandler(frame, _breakLabels.Count, _continueLabels.Count));
        writer.WriteLine($"if (setjmp(*{frame}->Target) == 0)");
        FlowResult tryFlow;
        var fallthroughStates = new List<AssignmentSnapshot>();
        using (writer.Block())
        {
            tryFlow = EmitStatement(writer, syntax.Body);
            if (tryFlow.FallsThrough)
            {
                fallthroughStates.Add(SnapshotAssignments());
                writer.WriteLine($"ct_exception_top = {frame}->Previous;");
                writer.WriteLine($"goto {done};");
            }
        }
        _activeExceptionFrames.RemoveAt(_activeExceptionFrames.Count - 1);
        writer.WriteLine("else");
        var catchExits = FlowExit.None;
        using (writer.Block())
        {
            writer.WriteLine($"ct_object* ct_caught_{id} = ct_current_exception;");
            writer.WriteLine("ct_current_exception = NULL;");
            writer.WriteLine($"(void)ct_caught_{id};");
            var caughtRecord = $"ct_cleanup_caught_{id}";
            RegisterCleanupRecord(caughtRecord);
            writer.WriteLine($"ct_cleanup_push(&{caughtRecord}, (void*)&ct_caught_{id}, ct_drop_ref_value);");
            writer.WriteLine($"ct_exception_top = {frame}->Previous;");
            foreach (var boundCatch in catches)
            {
                var condition = boundCatch.Type is null
                    ? null
                    : $"ct_type_is_assignable(ct_caught_{id}->Type, {_emitter.DescriptorExpression(boundCatch.Type)})";
                if (condition is not null)
                    writer.WriteLine($"if ({condition})");
                using (writer.Block())
                {
                    RestoreAssignments(before);
                    BeginScope(writer);
                    DeclareCatchLocal(writer, boundCatch, $"ct_caught_{id}");
                    _catchExceptions.Push($"ct_caught_{id}");
                    var catchFlow = EmitStatements(writer, boundCatch.Syntax.Body.Statements);
                    _catchExceptions.Pop();
                    EndScope(writer, catchFlow.FallsThrough);
                    catchExits |= catchFlow.Exits;
                    if (catchFlow.FallsThrough)
                    {
                        fallthroughStates.Add(SnapshotAssignments());
                        writer.WriteLine($"ct_cleanup_unwind_to({frame}->CleanupBoundary);");
                        writer.WriteLine($"goto {done};");
                    }
                }
            }
            if (!catches.Any(boundCatch => boundCatch.Type is null))
                writer.WriteLine($"ct_throw(ct_caught_{id}, {_emitter.SourceArgument(syntax)});");
        }
        if (fallthroughStates.Count != 0)
            writer.WriteLine($"{done}:;");
        if (fallthroughStates.Count != 0)
            RestoreAssignments(MergeAssignments(fallthroughStates));
        else
            RestoreAssignments(before);
        var hasCatchAll = catches.Any(boundCatch => boundCatch.Type is null);
        var exits = tryFlow.Exits | catchExits | (hasCatchAll ? FlowExit.None : FlowExit.Throw);
        if (fallthroughStates.Count != 0)
            exits |= FlowExit.FallThrough;
        return new FlowResult(exits);
    }

    private ImmutableArray<BoundCatch> BindCatches(TryStatementSyntax syntax)
    {
        var result = ImmutableArray.CreateBuilder<BoundCatch>();
        var priorTypes = new List<CType>();
        var sawCatchAll = false;
        var exceptionType = _model.Types["System.Exception"].Type;
        foreach (var catchClause in syntax.Catches)
        {
            CType? type = null;
            if (catchClause.Type is null)
            {
                if (sawCatchAll || catchClause != syntax.Catches[^1])
                    Report("CT2153", "A catch-all clause must be the last catch clause.", catchClause);
                sawCatchAll = true;
            }
            else
            {
                type = _model.ResolveType(catchClause.Type, TreeFor(catchClause));
                if (type.Kind != CTypeKind.Class || type.Symbol is null ||
                    type.Symbol != exceptionType.Symbol && !type.Symbol.DerivesFrom(exceptionType.Symbol!))
                    Report("CT2152", "A catch type must derive from System.Exception.", catchClause.Type);
                if (sawCatchAll || priorTypes.Any(prior => type.Symbol is not null && prior.Symbol is not null &&
                    (type.Symbol == prior.Symbol || type.Symbol.DerivesFrom(prior.Symbol))))
                    Report("CT2153", "This catch clause is unreachable because an earlier clause handles its type.", catchClause);
                priorTypes.Add(type);
                _emitter.RegisterType(type);
            }
            result.Add(new BoundCatch(catchClause, type));
        }
        return result.ToImmutable();
    }

    private void DeclareCatchLocal(CWriter writer, BoundCatch boundCatch, string exceptionCode)
    {
        if (boundCatch.Syntax.Name is null || boundCatch.Type is null)
            return;
        if (FindLocal(boundCatch.Syntax.Name) is not null || _parameters.ContainsKey(boundCatch.Syntax.Name))
            Report("CT1106", $"A local named '{boundCatch.Syntax.Name}' is already active.", boundCatch.Syntax);
        var symbol = new LocalSymbol
        {
            Name = boundCatch.Syntax.Name,
            Type = boundCatch.Type,
            Id = _localId++,
            Syntax = boundCatch.Syntax,
            IsAssigned = true,
            AssignmentCount = 1,
            IsDurable = true,
        };
        _scopes.Peek()[symbol.Name] = symbol;
        RegisterDurableSlot(symbol.StorageName, symbol.Type);
        EmitActivateOwnedSlot(writer, symbol.Type, symbol.CName, $"ct_cleanup_local_{symbol.Id}");
        EmitInitializeOwnedSlot(writer, symbol.Type, symbol.CName, $"({_emitter.CTypeName(symbol.Type)})(void*){exceptionCode}");
    }

    private void EmitReturnTransfer(CWriter writer, string? value)
    {
        if (_finallyContexts.Count != 0)
        {
            var context = _finallyContexts.Peek();
            if (value is not null)
            {
                var pending = Durable($"ct_er_{context.TryId}");
                if (_method.ReturnType.ContainsManagedReferences)
                {
                    var raw = NewTemp();
                    writer.WriteLine($"{_emitter.CDeclaration(_method.ReturnType, raw)} = {value};");
                    writer.WriteLine(_emitter.RetainValueStatement(_method.ReturnType, $"&{raw}"));
                    writer.WriteLine($"{CEmitter.ValueDropName(_method.ReturnType)}((void*)(uintptr_t)&{pending});");
                    writer.WriteLine($"{pending} = {raw};");
                }
                else
                    writer.WriteLine($"{pending} = {value};");
            }
            writer.WriteLine($"ct_cleanup_unwind_to(ct_eh_{context.TryId}_finally->CleanupBoundary);");
            EmitPopHandlersTo(writer, context.HandlerDepth);
            writer.WriteLine($"{Durable($"ct_ep_{context.TryId}")} = 1;");
            writer.WriteLine($"goto {context.CleanupLabel};");
            return;
        }
        string? finalValue = value;
        if (value is not null && _method.ReturnType.ContainsManagedReferences)
        {
            finalValue = NewTemp();
            writer.WriteLine($"{_emitter.CDeclaration(_method.ReturnType, finalValue)} = {value};");
            writer.WriteLine(_emitter.RetainValueStatement(_method.ReturnType, $"&{finalValue}"));
        }
        writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
        EmitPopHandlersTo(writer, 0);
        writer.WriteLine(finalValue is null ? "return;" : $"return {finalValue};");
    }

    private void EmitBreakOrContinue(CWriter writer, bool isContinue)
    {
        var depth = isContinue ? _continueLabels.Count : _breakLabels.Count;
        var context = _finallyContexts.FirstOrDefault(item => depth <= (isContinue ? item.ContinueDepth : item.BreakDepth));
        if (context is not null)
        {
            writer.WriteLine($"ct_cleanup_unwind_to(ct_eh_{context.TryId}_finally->CleanupBoundary);");
            EmitPopHandlersTo(writer, context.HandlerDepth);
            writer.WriteLine($"{Durable($"ct_ep_{context.TryId}")} = {(isContinue ? 3 : 2)};");
            writer.WriteLine($"goto {context.CleanupLabel};");
            return;
        }
        writer.WriteLine($"ct_cleanup_unwind_to({(isContinue ? _continueCleanupBoundaries.Peek() : _breakCleanupBoundaries.Peek())});");
        EmitPopCrossedHandlers(writer, isContinue, depth);
        writer.WriteLine($"goto {(isContinue ? _continueLabels.Peek() : _breakLabels.Peek())};");
    }

    private void EmitResumedBranch(CWriter writer, bool isContinue, string target)
    {
        var context = _finallyContexts.FirstOrDefault(item =>
            (isContinue ? item.ContinueTarget : item.BreakTarget) == target);
        if (context is not null)
        {
            writer.WriteLine($"ct_cleanup_unwind_to(ct_eh_{context.TryId}_finally->CleanupBoundary);");
            EmitPopHandlersTo(writer, context.HandlerDepth);
            writer.WriteLine($"{Durable($"ct_ep_{context.TryId}")} = {(isContinue ? 3 : 2)};");
            writer.WriteLine($"goto {context.CleanupLabel};");
        }
        else
        {
            var boundaries = isContinue ? _continueCleanupBoundaries : _breakCleanupBoundaries;
            writer.WriteLine($"ct_cleanup_unwind_to({boundaries.Peek()});");
            EmitPopHandlersTo(writer, 0);
            writer.WriteLine($"goto {target};");
        }
    }

    private void EmitPopHandlersTo(CWriter writer, int depth)
    {
        for (var index = _activeExceptionFrames.Count - 1; index >= depth; index--)
            writer.WriteLine($"ct_exception_top = {_activeExceptionFrames[index].Name}->Previous;");
    }

    private void EmitPopCrossedHandlers(CWriter writer, bool isContinue, int depth)
    {
        for (var index = _activeExceptionFrames.Count - 1; index >= 0; index--)
        {
            var handler = _activeExceptionFrames[index];
            if (depth > (isContinue ? handler.ContinueDepth : handler.BreakDepth))
                break;
            writer.WriteLine($"ct_exception_top = {handler.Name}->Previous;");
        }
    }

    private void ValidateConstructorAssignments()
    {
        if (!_method.IsConstructor || _method.Syntax is null)
            return;
        var required = _method.ContainingType.Fields.Where(field => !field.IsStatic && (_method.ContainingType.Kind == DeclaredTypeKind.Struct || field.IsReadonly));
        foreach (var field in required.Where(field => !_assignedFields.Contains(field) && field.Initializer is null))
            Report("CT3107", $"Constructor must assign field '{field.Name}'.", _method.Syntax ?? _method.ContainingType.Syntax!);
    }

    private void EmitPrelude(CWriter writer, IEnumerable<string> prelude)
    {
        foreach (var line in prelude)
            writer.WriteLine(line);
    }

    private void EmitActivateOwnedSlot(CWriter writer, CType type, string slot, string record)
    {
        RegisterCleanupRecord(record);
        writer.WriteLine($"ct_cleanup_push(&{record}, (void*)(uintptr_t)&{slot}, {CEmitter.ValueDropName(type)});");
    }

    private void EmitInitializeOwnedSlot(CWriter writer, CType type, string slot, string value)
    {
        var temporary = NewTemp();
        writer.WriteLine($"{_emitter.CDeclaration(type, temporary)} = {value};");
        writer.WriteLine(_emitter.RetainValueStatement(type, $"&{temporary}"));
        writer.WriteLine($"{slot} = {temporary};");
    }

    private void AddStrongStore(List<string> prelude, LoweredExpression target, string value)
    {
        var type = target.Type;
        var next = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(type, next)} = {value};");
        if (target.LValue!.Property is not null)
        {
            prelude.Add(target.LValue.Store(next) + ";");
            return;
        }
        prelude.Add(_emitter.RetainValueStatement(type, $"&{next}"));
        var old = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(type, old)} = {target.Code};");
        prelude.Add(target.LValue.Store(next) + ";");
        prelude.Add(_emitter.DropValueStatement(type, $"&{old}"));
    }

    private LoweredExpression OwnResult(CType type, string code, IEnumerable<string> sourcePrelude, bool borrowed = false)
    {
        if (!type.ContainsManagedReferences)
            return new LoweredExpression { Type = type, Code = code, Prelude = [.. sourcePrelude] };
        if (_method.Name == "<module_init>")
            return new LoweredExpression { Type = type, Code = code, Prelude = [.. sourcePrelude], Ownership = borrowed ? OwnershipKind.Borrowed : OwnershipKind.Owned };
        var prelude = new List<string>(sourcePrelude);
        var raw = NewTemp();
        var slotName = $"ct_owned_{_tempId++}";
        var slot = Durable(slotName);
        var record = $"ct_cleanup_{slotName}";
        RegisterDurableSlot(slotName, type);
        RegisterCleanupRecord(record);
        prelude.Add($"{_emitter.CDeclaration(type, raw)} = {code};");
        if (borrowed)
            prelude.Add(_emitter.RetainValueStatement(type, $"&{raw}"));
        prelude.Add($"if ({record}.Active) {CEmitter.ValueDropName(type)}((void*)(uintptr_t)&{slot}); else ct_cleanup_push(&{record}, (void*)(uintptr_t)&{slot}, {CEmitter.ValueDropName(type)});");
        prelude.Add($"{slot} = {raw};");
        return new LoweredExpression { Type = type, Code = slot, Prelude = prelude, Ownership = OwnershipKind.Owned };
    }

    private void AddCapturedSlot(List<string> prelude, CType type, string slotName, string value)
    {
        RegisterDurableSlot(slotName, type);
        var slot = Durable(slotName);
        if (!type.ContainsManagedReferences)
        {
            prelude.Add($"{slot} = {value};");
            return;
        }
        var record = $"ct_cleanup_{slotName}";
        var raw = NewTemp();
        RegisterCleanupRecord(record);
        prelude.Add($"{_emitter.CDeclaration(type, raw)} = {value};");
        prelude.Add(_emitter.RetainValueStatement(type, $"&{raw}"));
        prelude.Add($"if ({record}.Active) {CEmitter.ValueDropName(type)}((void*)(uintptr_t)&{slot}); else ct_cleanup_push(&{record}, (void*)(uintptr_t)&{slot}, {CEmitter.ValueDropName(type)});");
        prelude.Add($"{slot} = {raw};");
    }

    private void BeginScope(CWriter writer)
    {
        var boundary = EmitCleanupBoundary(writer, "scope");
        _cleanupBoundaries.Push(boundary);
        _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
    }

    private string EmitCleanupBoundary(CWriter writer, string kind)
    {
        var boundary = $"ct_cleanup_{kind}_{_cleanupId++}";
        writer.WriteLine($"ct_cleanup_record* {boundary} = ct_cleanup_top;");
        writer.WriteLine($"(void){boundary};");
        return boundary;
    }

    private void EndScope(CWriter writer, bool fallsThrough)
    {
        var boundary = _cleanupBoundaries.Pop();
        if (fallsThrough)
            writer.WriteLine($"ct_cleanup_unwind_to({boundary});");
        _scopes.Pop();
    }
    private LocalSymbol? FindLocal(string name) => _scopes.Select(scope => scope.GetValueOrDefault(name)).FirstOrDefault(local => local is not null);
    private IEnumerable<LocalSymbol> ActiveLocals() => _scopes.SelectMany(scope => scope.Values).Distinct();
    private AssignmentSnapshot SnapshotAssignments() => new(
        ActiveLocals().ToDictionary(local => local, local => (local.IsAssigned, local.AssignmentCount)),
        [.. _assignedFields],
        new Dictionary<FieldSymbol, int>(_fieldAssignmentCounts));

    private void RestoreAssignments(AssignmentSnapshot snapshot)
    {
        foreach (var pair in snapshot.Locals)
        {
            pair.Key.IsAssigned = pair.Value.IsAssigned;
            pair.Key.AssignmentCount = pair.Value.AssignmentCount;
        }
        _assignedFields.Clear();
        _assignedFields.UnionWith(snapshot.Fields);
        _fieldAssignmentCounts.Clear();
        foreach (var pair in snapshot.FieldCounts)
            _fieldAssignmentCounts[pair.Key] = pair.Value;
    }

    private static AssignmentSnapshot MergeAssignments(AssignmentSnapshot before, AssignmentSnapshot thenState, AssignmentSnapshot elseState)
    {
        var locals = before.Locals.ToDictionary(
            pair => pair.Key,
            pair => (
                thenState.Locals.GetValueOrDefault(pair.Key).IsAssigned && elseState.Locals.GetValueOrDefault(pair.Key).IsAssigned,
                Math.Max(thenState.Locals.GetValueOrDefault(pair.Key).AssignmentCount, elseState.Locals.GetValueOrDefault(pair.Key).AssignmentCount)));
        var fields = new HashSet<FieldSymbol>(thenState.Fields);
        fields.IntersectWith(elseState.Fields);
        var fieldCounts = thenState.FieldCounts.Keys.Concat(elseState.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => Math.Max(thenState.FieldCounts.GetValueOrDefault(field), elseState.FieldCounts.GetValueOrDefault(field)));
        return new AssignmentSnapshot(locals, fields, fieldCounts);
    }

    private void EmitExceptionFrameStorage(CWriter writer)
    {
        if (_tryCount == 0)
            return;
        for (var index = 0; index < _tryCount; index++)
        {
            writer.WriteLine($"jmp_buf ct_ej_{index}_catch;");
            writer.WriteLine($"jmp_buf ct_ej_{index}_finally;");
            writer.WriteLine($"ct_exception_frame ct_ehs_{index}_catch = {{ &ct_ej_{index}_catch, NULL, NULL }};");
            writer.WriteLine($"ct_exception_frame ct_ehs_{index}_finally = {{ &ct_ej_{index}_finally, NULL, NULL }};");
            writer.WriteLine($"ct_exception_frame* ct_eh_{index}_catch = &ct_ehs_{index}_catch;");
            writer.WriteLine($"ct_exception_frame* ct_eh_{index}_finally = &ct_ehs_{index}_finally;");
            RegisterDurableSlot($"ct_ep_{index}", CType.Int);
            RegisterDurableSlot($"ct_ex_{index}", _model.Types["System.Object"].Type);
            writer.WriteLine($"(void)ct_eh_{index}_catch;");
            writer.WriteLine($"(void)ct_eh_{index}_finally;");
            if (!_method.IsConstructor && _method.ReturnType != CType.Void)
            {
                RegisterDurableSlot($"ct_er_{index}", _method.ReturnType);
            }
        }
    }

    private void EmitDurableParameterStorage(CWriter writer)
    {
        if (_durableParameters.Count == 0)
            return;
        foreach (var parameter in _method.Parameters)
        {
            var storage = _durableParameters[parameter];
            var parameterName = NameMangler.Identifier(parameter.Name);
            RegisterDurableSlot(storage, parameter.Type);
            writer.WriteLine($"{Durable(storage)} = {parameterName};");
        }
    }

    private static int CountTryStatements(BlockStatementSyntax? body) => body is null ? 0 : CountTry(body);

    private static int CountDeferStatements(BlockStatementSyntax? body) => body is null ? 0 : CountDefer(body);

    private static int CountDefer(StatementSyntax statement) => statement switch
    {
        DeferStatementSyntax => 1,
        TryStatementSyntax @try => CountDefer(@try.Body) + @try.Catches.Sum(catchClause => CountDefer(catchClause.Body)) + (@try.Finally is null ? 0 : CountDefer(@try.Finally.Body)),
        BlockStatementSyntax block => block.Statements.Sum(CountDefer),
        IfStatementSyntax @if => CountDefer(@if.Then) + (@if.Else is null ? 0 : CountDefer(@if.Else)),
        WhileStatementSyntax @while => CountDefer(@while.Body),
        DoStatementSyntax @do => CountDefer(@do.Body),
        ForStatementSyntax @for => CountDefer(@for.Body) + (@for.Initializer is null ? 0 : CountDefer(@for.Initializer)),
        ForeachStatementSyntax @foreach => CountDefer(@foreach.Body),
        SwitchStatementSyntax @switch => @switch.Sections.Sum(section => section.Statements.Sum(CountDefer)),
        UnsafeStatementSyntax unsafeStatement => CountDefer(unsafeStatement.Body),
        _ => 0,
    };

    private static int CountTry(StatementSyntax statement) => statement switch
    {
        TryStatementSyntax @try => 1 + CountTry(@try.Body) + @try.Catches.Sum(catchClause => CountTry(catchClause.Body)) + (@try.Finally is null ? 0 : CountTry(@try.Finally.Body)),
        BlockStatementSyntax block => block.Statements.Sum(CountTry),
        IfStatementSyntax @if => CountTry(@if.Then) + (@if.Else is null ? 0 : CountTry(@if.Else)),
        WhileStatementSyntax @while => CountTry(@while.Body),
        DoStatementSyntax @do => CountTry(@do.Body),
        ForStatementSyntax @for => CountTry(@for.Body) + (@for.Initializer is null ? 0 : CountTry(@for.Initializer)),
        ForeachStatementSyntax @foreach => CountTry(@foreach.Body),
        SwitchStatementSyntax @switch => @switch.Sections.Sum(section => section.Statements.Sum(CountTry)),
        UnsafeStatementSyntax unsafeStatement => CountTry(unsafeStatement.Body),
        _ => 0,
    };

    private static bool ContainsThrow(BlockStatementSyntax? body) => body is not null && ContainsThrow((StatementSyntax)body);

    private static bool ContainsThrow(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax => true,
        TryStatementSyntax @try => ContainsThrow(@try.Body) || @try.Catches.Any(catchClause => ContainsThrow(catchClause.Body)) || @try.Finally is not null && ContainsThrow(@try.Finally.Body),
        BlockStatementSyntax block => block.Statements.Any(ContainsThrow),
        IfStatementSyntax @if => ContainsThrow(@if.Then) || @if.Else is not null && ContainsThrow(@if.Else),
        WhileStatementSyntax @while => ContainsThrow(@while.Body),
        DoStatementSyntax @do => ContainsThrow(@do.Body),
        ForStatementSyntax @for => ContainsThrow(@for.Body) || @for.Initializer is not null && ContainsThrow(@for.Initializer),
        ForeachStatementSyntax @foreach => ContainsThrow(@foreach.Body),
        SwitchStatementSyntax @switch => @switch.Sections.Any(section => section.Statements.Any(ContainsThrow)),
        UnsafeStatementSyntax unsafeStatement => ContainsThrow(unsafeStatement.Body),
        _ => false,
    };

    private static AssignmentSnapshot MergeAssignments(IReadOnlyList<AssignmentSnapshot> states)
    {
        if (states.Count == 0)
            throw new ArgumentException("At least one assignment state is required.", nameof(states));
        if (states.Count == 1)
            return states[0];
        var first = states[0];
        var locals = first.Locals.ToDictionary(
            pair => pair.Key,
            pair => (
                states.All(state => state.Locals.GetValueOrDefault(pair.Key).IsAssigned),
                states.Max(state => state.Locals.GetValueOrDefault(pair.Key).AssignmentCount)));
        var fields = new HashSet<FieldSymbol>(first.Fields);
        foreach (var state in states.Skip(1))
            fields.IntersectWith(state.Fields);
        var fieldCounts = states.SelectMany(state => state.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => states.Max(state => state.FieldCounts.GetValueOrDefault(field)));
        return new AssignmentSnapshot(locals, fields, fieldCounts);
    }

    private static AssignmentSnapshot ApplyFinallyAssignments(AssignmentSnapshot protectedState, AssignmentSnapshot before, AssignmentSnapshot finallyState)
    {
        var locals = protectedState.Locals.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var beforeValue = before.Locals.GetValueOrDefault(pair.Key);
                var finallyValue = finallyState.Locals.GetValueOrDefault(pair.Key);
                var addedAssignments = Math.Max(0, finallyValue.AssignmentCount - beforeValue.AssignmentCount);
                return (pair.Value.IsAssigned || finallyValue.IsAssigned, pair.Value.AssignmentCount + addedAssignments);
            });
        var fields = new HashSet<FieldSymbol>(protectedState.Fields);
        fields.UnionWith(finallyState.Fields);
        var fieldCounts = protectedState.FieldCounts.Keys.Concat(finallyState.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => protectedState.FieldCounts.GetValueOrDefault(field) +
                Math.Max(0, finallyState.FieldCounts.GetValueOrDefault(field) - before.FieldCounts.GetValueOrDefault(field)));
        return new AssignmentSnapshot(locals, fields, fieldCounts);
    }

    private void ValidateFinallyReadonlyAssignments(AssignmentSnapshot protectedState, AssignmentSnapshot before, AssignmentSnapshot finallyState, FinallyClauseSyntax syntax)
    {
        foreach (var local in protectedState.Locals.Keys.Where(local => local.IsReadonly))
        {
            if (protectedState.Locals.GetValueOrDefault(local).AssignmentCount > before.Locals.GetValueOrDefault(local).AssignmentCount &&
                finallyState.Locals.GetValueOrDefault(local).AssignmentCount > before.Locals.GetValueOrDefault(local).AssignmentCount)
                Report("CT3130", $"Readonly local '{local.Name}' can be assigned only once.", syntax);
        }
        foreach (var field in protectedState.FieldCounts.Keys.Concat(finallyState.FieldCounts.Keys).Distinct().Where(field => field.IsReadonly))
        {
            if (protectedState.FieldCounts.GetValueOrDefault(field) > before.FieldCounts.GetValueOrDefault(field) &&
                finallyState.FieldCounts.GetValueOrDefault(field) > before.FieldCounts.GetValueOrDefault(field))
                Report("CT3131", $"Readonly field '{field.Name}' can be assigned only once.", syntax);
        }
    }
    private string NewTemp() => $"ct_tmp{_temporaryPrefix}_{_tempId++}";
    private string NewLabel(string prefix) => $"ct_{prefix}_{_labelId++}";
    private SyntaxTree TreeFor(SyntaxNode syntax) => _model.SyntaxTrees.First(tree => ReferenceEquals(tree.Text, syntax.Source));
    private void Report(string code, string message, SyntaxNode syntax) => _diagnostics.Add(code, message, syntax.Source, syntax.Span);
    private static bool HasModifier(SyntaxNode? syntax, string modifier) => syntax switch
    {
        MemberDeclarationSyntax member => member.Modifiers.Contains(modifier, StringComparer.Ordinal),
        _ => false,
    };

    [Flags]
    private enum FlowExit
    {
        None = 0,
        FallThrough = 1,
        Return = 2,
        Break = 4,
        Continue = 8,
        Throw = 16,
    }

    private readonly record struct FlowResult(FlowExit Exits)
    {
        public static FlowResult None => new(FlowExit.FallThrough);
        public bool FallsThrough => (Exits & FlowExit.FallThrough) != 0;
        public bool AlwaysReturns => !FallsThrough && (Exits & (FlowExit.Return | FlowExit.Throw)) != 0;
    }

    private sealed record AssignmentSnapshot(
        Dictionary<LocalSymbol, (bool IsAssigned, int AssignmentCount)> Locals,
        HashSet<FieldSymbol> Fields,
        Dictionary<FieldSymbol, int> FieldCounts);

    private sealed record ActiveHandler(string Name, int BreakDepth, int ContinueDepth);
    private sealed record FinallyContext(int TryId, string CleanupLabel, int HandlerDepth, int BreakDepth, int ContinueDepth, string? BreakTarget, string? ContinueTarget);
    private sealed record BoundCatch(CatchClauseSyntax Syntax, CType? Type);

    private LoweredExpression LowerExpression(ExpressionSyntax syntax)
    {
        return syntax switch
        {
            LiteralExpressionSyntax literal => LowerLiteral(literal),
            NameExpressionSyntax name => LowerName(name, false),
            ThisExpressionSyntax @this => LowerThis(@this),
            BaseExpressionSyntax @base => LowerBase(@base),
            ParenthesizedExpressionSyntax parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionSyntax unary => LowerUnary(unary),
            BinaryExpressionSyntax binary => LowerBinary(binary),
            AssignmentExpressionSyntax assignment => LowerAssignment(assignment),
            MemberAccessExpressionSyntax member => LowerMember(member, false),
            CallExpressionSyntax call => LowerCall(call),
            IndexExpressionSyntax index => LowerIndex(index, false),
            NewExpressionSyntax @new => LowerNew(@new),
            CastExpressionSyntax cast => LowerCast(cast),
            TypeTestExpressionSyntax typeTest => LowerTypeTest(typeTest),
            SafeCastExpressionSyntax safeCast => LowerSafeCast(safeCast),
            _ => ErrorExpression(),
        };
    }

    private LoweredExpression LowerLiteral(LiteralExpressionSyntax syntax)
    {
        if (syntax.LiteralKind == SyntaxKind.TrueKeyword || syntax.LiteralKind == SyntaxKind.FalseKeyword)
            return Constant(CType.Bool, (bool)syntax.Value!, (bool)syntax.Value! ? "true" : "false");
        if (syntax.LiteralKind == SyntaxKind.NullKeyword)
            return Constant(CType.Null, null, "NULL");
        if (syntax.LiteralKind == SyntaxKind.StringToken)
            return new LoweredExpression { Type = CType.String, Code = _emitter.RegisterString((string)syntax.Value!), IsConstant = true, ConstantValue = syntax.Value, Ownership = OwnershipKind.Immortal };
        if (syntax.LiteralKind == SyntaxKind.CharacterToken)
            return Constant(CType.Char, syntax.Value, ((byte)syntax.Value!).ToString(CultureInfo.InvariantCulture));
        if (syntax.Value is NumericLiteralValue numeric)
        {
            if (numeric.FloatingPoint is float value)
                return Constant(CType.Float, value, FormatFloat(value));
            if (numeric.Suffix == IntegerLiteralSuffix.None && numeric.Integer <= int.MaxValue)
                return Constant(CType.Int, (int)numeric.Integer, FormatInt32((int)numeric.Integer));
            if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Unsigned && numeric.Integer <= uint.MaxValue)
                return Constant(CType.Uint, (uint)numeric.Integer, $"UINT32_C({numeric.Integer.ToString(CultureInfo.InvariantCulture)})");
            if (numeric.Suffix is IntegerLiteralSuffix.None or IntegerLiteralSuffix.Long && numeric.Integer <= long.MaxValue)
                return Constant(CType.Long, (long)numeric.Integer, FormatInt64((long)numeric.Integer));
            if (numeric.Integer <= ulong.MaxValue)
                return Constant(CType.Ulong, (ulong)numeric.Integer, FormatUInt64((ulong)numeric.Integer));
            Report("CT2112", "Integer literal does not fit the type selected by its suffix.", syntax);
            return Constant(CType.Int, 0, "0");
        }
        return ErrorExpression();
    }

    private LoweredExpression LowerName(NameExpressionSyntax syntax, bool forWrite)
    {
        var local = FindLocal(syntax.Name);
        if (local is not null)
        {
            if (local.Type.ContainsPointer)
                RequireUnsafe(syntax);
            if (!forWrite && !local.IsAssigned)
                Report("CT3108", $"Local '{syntax.Name}' is read before it is assigned.", syntax);
            return new LoweredExpression
            {
                Type = local.Type,
                Code = local.ConstantCode ?? local.CName,
                LValue = new LoweredLValue { Store = value => $"{local.CName} = {value}", Address = $"&{local.CName}", Local = local },
                IsConstant = local.IsConst,
                ConstantValue = local.ConstantValue,
            };
        }
        if (_parameters.TryGetValue(syntax.Name, out var parameter))
        {
            if (parameter.Type.ContainsPointer)
                RequireUnsafe(syntax);
            var name = _durableParameters.TryGetValue(parameter, out var storage)
                ? Durable(storage)
                : NameMangler.Identifier(parameter.Name);
            return new LoweredExpression
            {
                Type = parameter.Type,
                Code = name,
                LValue = new LoweredLValue { Store = value => $"{name} = {value}", Address = $"&{name}" },
            };
        }
        var field = Hierarchy(_method.ContainingType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name);
        if (field is not null)
            return LowerField(field, null, syntax, forWrite);
        var property = Hierarchy(_method.ContainingType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name);
        if (property is not null)
            return LowerProperty(property, null, syntax, forWrite);
        var methods = Hierarchy(_method.ContainingType).SelectMany(type => type.Methods)
            .Where(candidate => candidate.Name == syntax.Name && (!_method.IsStatic || candidate.IsStatic))
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
        if (!forWrite && methods.Length != 0)
            return new LoweredExpression { Type = CType.Error, Code = string.Empty, MethodGroup = new MethodGroupBinding(methods, null, false) };
        var type = _model.ResolveNamedType(syntax.Name, TreeFor(syntax));
        if (type is not null)
            return new LoweredExpression { Type = CType.Error, Code = string.Empty, TypeReceiver = type };
        Report("CT1107", $"Name '{syntax.Name}' does not exist in the current context.", syntax);
        return ErrorExpression();
    }

    private LoweredExpression LowerThis(ThisExpressionSyntax syntax)
    {
        if (_method.IsStatic)
        {
            Report("CT2113", "this is not available in a static method.", syntax);
            return ErrorExpression();
        }
        if (_method.ContainingType.Kind == DeclaredTypeKind.Struct)
            return new LoweredExpression
            {
                Type = _method.ContainingType.Type,
                Code = "(*ct_self)",
                LValue = new LoweredLValue { Store = value => $"*ct_self = {value}", Address = "ct_self" },
            };
        return new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
    }

    private LoweredExpression LowerBase(BaseExpressionSyntax syntax)
    {
        if (_method.IsStatic || _method.ContainingType.Kind != DeclaredTypeKind.Class || _method.ContainingType.BaseType is null)
        {
            Report("CT2150", "base is available only in an instance member of a derived class.", syntax);
            return ErrorExpression();
        }
        var baseType = _method.ContainingType.BaseType;
        return new LoweredExpression
        {
            Type = baseType.Type,
            Code = $"({NameMangler.Type(baseType)}*)(void*)ct_self",
            IsBaseReceiver = true,
        };
    }

    private LoweredExpression LowerMember(MemberAccessExpressionSyntax syntax, bool forWrite)
    {
        var staticType = TryResolveTypeExpression(syntax.Receiver);
        if (staticType is not null)
        {
            if (staticType.Kind == DeclaredTypeKind.Enum)
            {
                var enumValue = staticType.EnumValues.FirstOrDefault(value => value.Name == syntax.Name);
                if (enumValue is not null)
                    return Constant(staticType.Type, enumValue.Value, NameMangler.Identifier(staticType.FullName + "." + enumValue.Name));
            }
            var field = Hierarchy(staticType).SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic);
            if (field is not null)
                return LowerField(field, null, syntax, forWrite);
            var property = Hierarchy(staticType).SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic);
            if (property is not null)
                return LowerProperty(property, null, syntax, forWrite);
            var methods = Hierarchy(staticType).SelectMany(type => type.Methods)
                .Where(candidate => candidate.Name == syntax.Name && candidate.IsStatic)
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
            if (!forWrite && methods.Length != 0)
                return new LoweredExpression { Type = CType.Error, Code = string.Empty, MethodGroup = new MethodGroupBinding(methods, null, false) };
            Report("CT1108", $"Type '{staticType.FullName}' has no static member named '{syntax.Name}'.", syntax);
            return ErrorExpression();
        }

        var receiver = LowerExpression(syntax.Receiver);
        if (receiver.Type.Kind == CTypeKind.String && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        if (receiver.Type.Kind == CTypeKind.Array && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        var type = receiver.Type.Symbol;
        if (type is null && (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array || receiver.Type.IsValueType))
            type = _model.Types.GetValueOrDefault("System.Object");
        if (type is null)
        {
            Report("CT2114", $"Type '{receiver.Type.DisplayName}' has no members.", syntax);
            return ErrorExpression(receiver.Prelude);
        }
        var instanceField = Hierarchy(type).SelectMany(candidateType => candidateType.Fields).FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic);
        if (instanceField is not null)
            return LowerField(instanceField, receiver, syntax, forWrite);
        var instanceProperty = Hierarchy(type).SelectMany(candidateType => candidateType.Properties).FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic);
        if (instanceProperty is not null)
            return LowerProperty(instanceProperty, receiver, syntax, forWrite);
        var instanceMethods = Hierarchy(type).SelectMany(candidateType => candidateType.Methods)
            .Where(candidate => candidate.Name == syntax.Name && !candidate.IsStatic)
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
        if (!forWrite && instanceMethods.Length != 0)
            return new LoweredExpression { Type = CType.Error, Code = string.Empty, Prelude = receiver.Prelude, MethodGroup = new MethodGroupBinding(instanceMethods, receiver, receiver.IsBaseReceiver) };
        Report("CT1109", $"Type '{type.FullName}' has no instance member named '{syntax.Name}'.", syntax);
        return ErrorExpression(receiver.Prelude);
    }

    private LoweredExpression LowerField(FieldSymbol field, LoweredExpression? receiver, SyntaxNode syntax, bool forWrite)
    {
        if (field.Type.ContainsPointer)
            RequireUnsafe(syntax);
        CheckAccess(field, syntax);
        if (!forWrite && field.IsConst && field.Initializer is not null)
        {
            if (!_constantFieldsBeingEvaluated.Add(field))
            {
                Report("CT2144", $"Const field '{field.Name}' has a circular initializer.", field.Initializer);
                return ErrorExpression();
            }
            var constant = Convert(LowerExpression(field.Initializer), field.Type, field.Initializer, false);
            _constantFieldsBeingEvaluated.Remove(field);
            if (!constant.IsConstant)
                Report("CT2140", $"Const field '{field.Name}' does not have a constant initializer.", field.Initializer);
            else
                return constant;
        }
        string code;
        var prelude = new List<string>();
        if (field.IsStatic)
            code = field.CName;
        else
        {
            if (_method.IsStatic && receiver is null)
            {
                Report("CT2115", $"Instance field '{field.Name}' requires an object.", syntax);
                return ErrorExpression();
            }
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new LoweredExpression { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new LoweredLValue { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
            var loweredReceiver = MaterializeReceiver(receiver, syntax);
            prelude.AddRange(loweredReceiver.Prelude);
            code = $"(({NameMangler.Type(field.ContainingType)}*)(void*){loweredReceiver.Code})->{field.CName}";
        }
        return new LoweredExpression
        {
            Type = field.Type,
            Code = code,
            Prelude = prelude,
            IsConstant = field.IsConst,
            LValue = new LoweredLValue { Store = value => $"{code} = {value}", Address = $"&({code})", Field = field },
        };
    }

    private LoweredExpression LowerProperty(PropertySymbol property, LoweredExpression? receiver, SyntaxNode syntax, bool forWrite)
    {
        if (property.Type.ContainsPointer)
            RequireUnsafe(syntax);
        CheckAccess(property, syntax);
        if (property.Syntax is PropertyDeclarationSyntax propertySyntax && propertySyntax.Modifiers.Contains("unsafe", StringComparer.Ordinal))
            RequireUnsafe(syntax);
        CheckAccessibility(forWrite ? property.SetterAccessibility : property.GetterAccessibility, property, syntax);
        var prelude = new List<string>();
        string receiverArgument = string.Empty;
        var baseReceiver = receiver?.IsBaseReceiver == true;
        if (!property.IsStatic)
        {
            if (_method.IsStatic && receiver is null)
            {
                Report("CT2116", $"Instance property '{property.Name}' requires an object.", syntax);
                return ErrorExpression();
            }
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new LoweredExpression { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new LoweredLValue { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
            var loweredReceiver = MaterializeReceiver(receiver, syntax);
            prelude.AddRange(loweredReceiver.Prelude);
            receiverArgument = loweredReceiver.Code;
        }
        if (!forWrite && property.Getter is null)
            Report("CT2117", $"Property '{property.Name}' has no getter.", syntax);
        var selectedAccessor = forWrite
            ? property.Setter is null ? null : _emitter.GetAccessorMethod(property, getter: false)
            : property.Getter is null ? null : _emitter.GetAccessorMethod(property, getter: true);
        if (selectedAccessor is not null)
            _emitter.AllocationEffects.RecordCall(_method, selectedAccessor, syntax, property.IsVirtual && !baseReceiver);
        var typedReceiver = property.IsStatic ? string.Empty : $"({NameMangler.Type(property.ContainingType)}*)(void*){receiverArgument}";
        var objectReceiver = property.IsStatic ? string.Empty : $"((ct_object*)(void*){receiverArgument})";
        var getterCode = property.Getter is null
            ? _emitter.DefaultValue(property.Type)
            : property.IsVirtual && !baseReceiver
                ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualGetterSlotName(property)}({objectReceiver})"
                : $"{NameMangler.Getter(property)}({typedReceiver})";
        var result = new LoweredExpression
        {
            Type = property.Type,
            Code = getterCode,
            Prelude = prelude,
            LValue = property.Setter is null ? null : new LoweredLValue
            {
                Store = value => property.IsVirtual && !baseReceiver
                    ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualSetterSlotName(property)}({objectReceiver}, {value})"
                    : $"{NameMangler.Setter(property)}({(property.IsStatic ? string.Empty : typedReceiver + ", ")}{value})",
                Field = property.BackingField,
                Property = property,
                IsBaseReceiver = baseReceiver,
            },
        };
        return !forWrite && property.Type.ContainsManagedReferences
            ? OwnResult(property.Type, getterCode, prelude)
            : result;
    }

    private LoweredExpression LowerIndex(IndexExpressionSyntax syntax, bool forWrite)
    {
        var receiver = Materialize(LowerExpression(syntax.Receiver), syntax.Receiver);
        var index = Materialize(Convert(LowerExpression(syntax.Index), CType.Int, syntax.Index, false), syntax.Index);
        var prelude = new List<string>(receiver.Prelude);
        prelude.AddRange(index.Prelude);
        if (receiver.Type.Kind == CTypeKind.Array)
        {
            prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            var code = $"{receiver.Code}->Data[{index.Code}]";
            return new LoweredExpression
            {
                Type = receiver.Type.ElementType!,
                Code = code,
                Prelude = prelude,
                LValue = new LoweredLValue { Store = value => $"{code} = {value}", Address = $"&({code})" },
            };
        }
        if (receiver.Type.Kind == CTypeKind.String)
        {
            prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(syntax)});");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {_emitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = CType.Char, Code = $"{receiver.Code}->Data[{index.Code}]", Prelude = prelude };
        }
        if (receiver.Type.Kind == CTypeKind.Pointer)
        {
            RequireUnsafe(syntax);
            var code = $"{receiver.Code}[{index.Code}]";
            return new LoweredExpression { Type = receiver.Type.ElementType!, Code = code, Prelude = prelude, LValue = new LoweredLValue { Store = value => $"{code} = {value}", Address = $"&({code})" } };
        }
        Report("CT2118", $"Type '{receiver.Type.DisplayName}' cannot be indexed.", syntax.Receiver);
        return ErrorExpression(prelude);
    }

    private LoweredExpression LowerNew(NewExpressionSyntax syntax)
    {
        var type = _model.ResolveType(syntax.Type, TreeFor(syntax));
        if (type.ContainsPointer)
            RequireUnsafe(syntax);
        if (syntax.ArrayLength is not null)
        {
            if (type.Kind != CTypeKind.Array)
                return ErrorExpression();
            _emitter.RegisterType(type);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "array construction");
            var length = Materialize(Convert(LowerExpression(syntax.ArrayLength), CType.Int, syntax.ArrayLength, false), syntax.ArrayLength);
            var code = $"ct_new_{NameMangler.Array(type.ElementType!)}({length.Code}, {_emitter.SourceArgument(syntax)})";
            return OwnResult(type, code, length.Prelude);
        }
        if (type.Kind is not CTypeKind.Class and not CTypeKind.Struct)
        {
            Report("CT2119", $"new cannot construct '{type.DisplayName}'.", syntax);
            return ErrorExpression();
        }
        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        var constructor = SelectOverload(type.Symbol!.Constructors, type.Symbol.Name, arguments, syntax);
        if (constructor is null)
            return ErrorExpression(arguments.SelectMany(argument => argument.Prelude));
        CheckAccess(constructor, syntax);
        if (constructor.IsUnsafe)
            RequireUnsafe(syntax);
        _emitter.AllocationEffects.RecordCall(_method, constructor, syntax, requiresContract: false);
        if (type.Kind == CTypeKind.Class)
            _emitter.AllocationEffects.RecordDirect(_method, syntax, $"construction of class '{type.DisplayName}'");
        var lowered = LowerArguments(arguments, constructor.Parameters, syntax.Arguments);
        var construction = $"{constructor.CName}({string.Join(", ", lowered.Codes)})";
        return type.ContainsManagedReferences ? OwnResult(type, construction, lowered.Prelude) : new LoweredExpression { Type = type, Code = construction, Prelude = lowered.Prelude };
    }

    private TypeSymbol? TryResolveTypeExpression(ExpressionSyntax expression)
    {
        var parts = new Stack<string>();
        var current = expression;
        while (current is MemberAccessExpressionSyntax member)
        {
            parts.Push(member.Name);
            current = member.Receiver;
        }
        if (current is not NameExpressionSyntax name)
            return null;
        parts.Push(name.Name);
        var qualified = string.Join('.', parts);
        return _model.ResolveNamedType(qualified, TreeFor(expression));
    }

    private LoweredExpression LowerCall(CallExpressionSyntax syntax, bool captureForDefer = false)
    {
        var possibleDelegate = syntax.Target switch
        {
            NameExpressionSyntax delegateName when IsCallablePointer(FindLocal(delegateName.Name)?.Type) ||
                                                   IsCallablePointer(_parameters.GetValueOrDefault(delegateName.Name)?.Type) ||
                                                   Hierarchy(_method.ContainingType).SelectMany(type => type.Fields).Any(field => field.Name == delegateName.Name && IsCallablePointer(field.Type)) ||
                                                   Hierarchy(_method.ContainingType).SelectMany(type => type.Properties).Any(property => property.Name == delegateName.Name && IsCallablePointer(property.Type))
                => LowerName(delegateName, false),
            MemberAccessExpressionSyntax member => TryLowerDelegateMember(member),
            _ => null,
        };
        if (possibleDelegate?.Type.Kind == CTypeKind.Delegate)
            return LowerDelegateInvocation(syntax, possibleDelegate);
        if (possibleDelegate?.Type.Kind == CTypeKind.FunctionPointer)
            return LowerFunctionPointerInvocation(syntax, possibleDelegate);

        TypeSymbol? containingType = null;
        LoweredExpression? receiver = null;
        string methodName;
        bool requireStatic;
        if (syntax.Target is NameExpressionSyntax name)
        {
            containingType = _method.ContainingType;
            methodName = name.Name;
            requireStatic = _method.IsStatic;
        }
        else if (syntax.Target is MemberAccessExpressionSyntax member)
        {
            var staticType = TryResolveTypeExpression(member.Receiver);
            if (staticType is not null)
            {
                containingType = staticType;
                methodName = member.Name;
                requireStatic = true;
            }
            else
            {
                receiver = LowerExpression(member.Receiver);
                if (member.Name == "ToString" && SupportsBuiltInToString(receiver.Type))
                    return LowerBuiltInToString(syntax, member, receiver, captureForDefer);
                containingType = receiver.Type.Symbol;
                if (containingType is null && (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array || receiver.Type.IsValueType))
                    containingType = _model.Types.GetValueOrDefault("System.Object");
                methodName = member.Name;
                requireStatic = false;
            }
        }
        else
        {
            Report("CT2120", "Only methods, delegates, and function pointers can be called in draft 0.7.", syntax.Target);
            return ErrorExpression();
        }

        if (containingType is null)
        {
            Report("CT2121", "The call receiver does not declare methods.", syntax.Target);
            return ErrorExpression(receiver?.Prelude);
        }

        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        var candidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => method.Name == methodName && method.IsStatic == requireStatic)
            .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (!requireStatic && receiver is not null && receiver.Type.IsValueType)
            candidates = containingType.Methods.Where(method => method.Name == methodName && !method.IsStatic)
                .Concat(_model.Types["System.Object"].Methods.Where(method => method.Name == methodName && !method.IsStatic))
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First())
                .ToArray();
        if (syntax.Target is NameExpressionSyntax && !_method.IsStatic)
        {
            var allCandidates = Hierarchy(containingType).SelectMany(type => type.Methods).Where(method => method.Name == methodName)
                .GroupBy(MethodSignatureKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (allCandidates.Length > 0)
                candidates = allCandidates;
        }
        var selected = SelectOverload(candidates, methodName, arguments, syntax);
        if (selected is null)
            return ErrorExpression((receiver?.Prelude ?? []).Concat(arguments.SelectMany(argument => argument.Prelude)));
        if (selected.ReturnType.ContainsPointer || selected.Parameters.Any(parameter => parameter.Type.ContainsPointer))
            RequireUnsafe(syntax);
        if (selected.IsUnsafe)
            RequireUnsafe(syntax);
        CheckAccess(selected, syntax);
        _emitter.RegisterExternUse(selected, syntax);
        _emitter.AllocationEffects.RecordCall(_method, selected, syntax, selected.IsVirtual && receiver?.IsBaseReceiver != true);

        var prelude = new List<string>();
        string? receiverCode = null;
        if (!selected.IsStatic)
        {
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new LoweredExpression { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new LoweredLValue { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
            if ((selected.ContainingType.IsObject || selected.IsVirtual && receiver.Type.IsValueType) && receiver.Type != _model.Types["System.Object"].Type)
                receiver = Convert(receiver, _model.Types["System.Object"].Type, syntax.Target, false);
            if (captureForDefer)
            {
                prelude.AddRange(receiver.Prelude);
                var slot = $"ct_df_{_deferId}_receiver";
                AddCapturedSlot(prelude, receiver.Type, slot, receiver.Code);
                receiverCode = receiver.Type.Kind == CTypeKind.Struct
                    ? $"({_emitter.CTypeName(receiver.Type)}*)(void*)&{Durable(slot)}"
                    : $"({_emitter.CTypeName(receiver.Type)})ct_require_nonnull({Durable(slot)}, {_emitter.SourceArgument(syntax.Target)})";
            }
            else
            {
                var loweredReceiver = MaterializeReceiver(receiver, syntax.Target);
                prelude.AddRange(loweredReceiver.Prelude);
                receiverCode = loweredReceiver.Code;
            }
        }
        var loweredArguments = captureForDefer
            ? CaptureDeferredArguments(arguments, selected.Parameters, syntax.Arguments)
            : LowerArguments(arguments, selected.Parameters, syntax.Arguments);
        prelude.AddRange(loweredArguments.Prelude);

        var callArguments = new List<string>();
        if (receiverCode is not null)
            callArguments.Add(receiverCode);
        callArguments.AddRange(loweredArguments.Codes);
        string call;
        if (selected.IsVirtual && receiverCode is not null && receiver?.IsBaseReceiver != true)
        {
            var objectReceiver = $"((ct_object*)(void*){receiverCode})";
            callArguments[0] = objectReceiver;
            if (CEmitter.VirtualSlotName(selected) == "Equals" && callArguments.Count == 2)
                callArguments[1] = $"(ct_object*)(void*){callArguments[1]}";
            call = $"{objectReceiver}->Type->VTable->{CEmitter.VirtualSlotName(selected)}({string.Join(", ", callArguments)})";
        }
        else
        {
            if (receiverCode is not null)
                callArguments[0] = $"({NameMangler.Type(selected.ContainingType)}*)(void*){receiverCode}";
            call = $"{selected.CName}({string.Join(", ", callArguments)})";
        }
        if (captureForDefer)
            _deferId++;
        if (captureForDefer)
            return new LoweredExpression { Type = selected.ReturnType, Code = call, Prelude = prelude, Ownership = selected.ReturnType.ContainsManagedReferences ? OwnershipKind.Owned : OwnershipKind.None };
        return selected.ReturnType.ContainsManagedReferences
            ? OwnResult(selected.ReturnType, call, prelude, selected.ReturnsBorrowed)
            : new LoweredExpression { Type = selected.ReturnType, Code = call, Prelude = prelude };
    }

    private static bool IsCallablePointer(CType? type) => type?.Kind is CTypeKind.Delegate or CTypeKind.FunctionPointer;

    private LoweredExpression? TryLowerDelegateMember(MemberAccessExpressionSyntax syntax)
    {
        var staticType = TryResolveTypeExpression(syntax.Receiver);
        if (staticType is not null)
        {
            var field = Hierarchy(staticType).SelectMany(type => type.Fields)
                .FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic && IsCallablePointer(candidate.Type));
            if (field is not null)
                return LowerField(field, null, syntax, false);
            var property = Hierarchy(staticType).SelectMany(type => type.Properties)
                .FirstOrDefault(candidate => candidate.Name == syntax.Name && candidate.IsStatic && IsCallablePointer(candidate.Type));
            return property is null ? null : LowerProperty(property, null, syntax, false);
        }

        var receiver = LowerExpression(syntax.Receiver);
        var type = receiver.Type.Symbol;
        if (type is null)
            return null;
        var instanceField = Hierarchy(type).SelectMany(candidate => candidate.Fields)
            .FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic && IsCallablePointer(candidate.Type));
        if (instanceField is not null)
            return LowerField(instanceField, receiver, syntax, false);
        var instanceProperty = Hierarchy(type).SelectMany(candidate => candidate.Properties)
            .FirstOrDefault(candidate => candidate.Name == syntax.Name && !candidate.IsStatic && IsCallablePointer(candidate.Type));
        return instanceProperty is null ? null : LowerProperty(instanceProperty, receiver, syntax, false);
    }

    private LoweredExpression LowerDelegateInvocation(CallExpressionSyntax syntax, LoweredExpression target)
    {
        var delegateType = target.Type.Symbol!;
        var parameters = delegateType.DelegateParameters;
        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        if (arguments.Length != parameters.Length)
        {
            Report("CT2160", $"Delegate '{delegateType.FullName}' expects {parameters.Length} argument(s).", syntax);
            return ErrorExpression(target.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }
        target = Materialize(target, syntax.Target);
        var loweredArguments = LowerArguments(arguments, parameters, syntax.Arguments);
        var prelude = new List<string>(target.Prelude);
        prelude.AddRange(loweredArguments.Prelude);
        prelude.Add($"(void)ct_require_nonnull({target.Code}, {_emitter.SourceArgument(syntax.Target)});");
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"indirect invocation of delegate '{delegateType.FullName}'");
        var callArguments = new[] { $"{target.Code}->ct_target" }.Concat(loweredArguments.Codes);
        var call = $"{target.Code}->ct_invoke({string.Join(", ", callArguments)})";
        var returnType = delegateType.DelegateReturnType!;
        return returnType.ContainsManagedReferences
            ? OwnResult(returnType, call, prelude)
            : new LoweredExpression { Type = returnType, Code = call, Prelude = prelude };
    }

    private LoweredExpression LowerFunctionPointerInvocation(CallExpressionSyntax syntax, LoweredExpression target)
    {
        RequireUnsafe(syntax);
        var signature = target.Type.FunctionPointer!;
        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        if (arguments.Length != signature.ParameterTypes.Length || arguments.Where((argument, index) => index < signature.ParameterTypes.Length && argument.Type != signature.ParameterTypes[index]).Any())
        {
            Report("CT2164", "Function-pointer invocation requires exact argument types.", syntax);
            return ErrorExpression(target.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }
        target = Materialize(target, syntax.Target);
        var prelude = new List<string>(target.Prelude);
        var codes = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = Materialize(arguments[index], syntax.Arguments[index]);
            prelude.AddRange(argument.Prelude);
            codes.Add(argument.Code);
        }
        prelude.Add($"(void)ct_require_nonnull((void*){target.Code}, {_emitter.SourceArgument(syntax.Target)});");
        _emitter.AllocationEffects.RecordDirect(_method, syntax, "unmanaged function-pointer invocation");
        return new LoweredExpression { Type = signature.ReturnType, Code = $"{target.Code}({string.Join(", ", codes)})", Prelude = prelude };
    }

    private static bool SupportsBuiltInToString(CType type) => type.Kind is
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or
        CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Float or CTypeKind.String;

    private LoweredExpression LowerBuiltInToString(CallExpressionSyntax syntax, MemberAccessExpressionSyntax member, LoweredExpression receiver, bool captureForDefer = false)
    {
        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        if (arguments.Length != 0)
        {
            Report("CT2122", "No overload of 'ToString' accepts the supplied argument types.", syntax);
            return ErrorExpression(receiver.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }

        if (captureForDefer)
        {
            var prelude = new List<string>(receiver.Prelude);
            var slot = $"ct_df_{_deferId}_receiver";
            AddCapturedSlot(prelude, receiver.Type, slot, receiver.Code);
            receiver = new LoweredExpression { Type = receiver.Type, Code = Durable(slot), Prelude = prelude };
            _deferId++;
        }
        else
            receiver = Materialize(receiver, member.Receiver);
        if (receiver.Type.Kind == CTypeKind.String)
        {
            if (captureForDefer)
                return new LoweredExpression { Type = CType.String, Code = $"ct_string_v_to_string((ct_object*)(void*)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)}))", Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned };
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {_emitter.SourceArgument(member)});");
            return OwnResult(CType.String, "ct_string_v_to_string((ct_object*)(void*)" + receiver.Code + ")", receiver.Prelude);
        }

        var function = receiver.Type.Kind switch
        {
            CTypeKind.Bool => "ct_to_string_bool",
            CTypeKind.Char => "ct_to_string_char",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int",
            CTypeKind.Long => "ct_to_string_long",
            CTypeKind.Ulong => "ct_to_string_ulong",
            CTypeKind.Float => "ct_to_string_float",
            _ => throw new InvalidOperationException($"Unsupported ToString receiver '{receiver.Type.DisplayName}'."),
        };
        var argument = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort => $"(uint32_t){receiver.Code}",
            CTypeKind.Sbyte or CTypeKind.Short => $"(int32_t){receiver.Code}",
            _ => receiver.Code,
        };
        var code = $"{function}({argument}, {_emitter.SourceArgument(member)})";
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"conversion of '{receiver.Type.DisplayName}' to string");
        return captureForDefer
            ? new LoweredExpression { Type = CType.String, Code = code, Prelude = receiver.Prelude, Ownership = OwnershipKind.Owned }
            : OwnResult(CType.String, code, receiver.Prelude);
    }

    private MethodSymbol? SelectOverload(IEnumerable<MethodSymbol> candidates, string name, IReadOnlyList<LoweredExpression> arguments, SyntaxNode syntax)
    {
        var matches = candidates
            .Where(candidate => candidate.Parameters.Length == arguments.Count)
            .Where(candidate => candidate.Parameters
                .Select((parameter, index) => CanConvertExpression(arguments[index], parameter.Type))
                .All(valid => valid))
            .ToArray();
        if (matches.Length == 0)
        {
            Report("CT2122", $"No overload of '{name}' accepts the supplied argument types.", syntax);
            return null;
        }
        var winners = matches.Where(candidate => matches.All(other =>
            ReferenceEquals(candidate, other) || IsBetterCandidate(candidate, other, arguments))).ToArray();
        if (winners.Length != 1)
        {
            Report("CT2123", $"Call to '{name}' is ambiguous.", syntax);
            return null;
        }
        return winners[0];
    }

    private static bool IsBetterCandidate(MethodSymbol candidate, MethodSymbol other, IReadOnlyList<LoweredExpression> arguments)
    {
        var better = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var comparison = CompareConversion(arguments[index].Type, candidate.Parameters[index].Type, other.Parameters[index].Type);
            if (comparison > 0)
                return false;
            better |= comparison < 0;
        }
        return better;
    }

    private static int CompareConversion(CType source, CType leftTarget, CType rightTarget)
    {
        if (leftTarget == rightTarget)
            return 0;
        if (source == leftTarget)
            return -1;
        if (source == rightTarget)
            return 1;
        var leftToRight = TypeFacts.CanImplicitlyConvert(leftTarget, rightTarget);
        var rightToLeft = TypeFacts.CanImplicitlyConvert(rightTarget, leftTarget);
        if (leftToRight != rightToLeft)
            return leftToRight ? -1 : 1;
        if (source.IsIntegral && leftTarget.IsIntegral && rightTarget.IsIntegral)
        {
            var leftSigned = leftTarget.Kind is CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int;
            var rightSigned = rightTarget.Kind is CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int;
            if (leftSigned != rightSigned)
                return leftSigned ? -1 : 1;
        }
        return 0;
    }

    private static bool CanConvertExpression(LoweredExpression expression, CType target) =>
        expression.MethodGroup is { } group
            ? target.Kind == CTypeKind.Delegate && FindDelegateMethod(group, target.Symbol!) is not null
            : TypeFacts.CanImplicitlyConvert(expression.Type, target);

    private static MethodSymbol? FindDelegateMethod(MethodGroupBinding group, TypeSymbol delegateType)
    {
        var matches = group.Candidates.Where(candidate =>
            candidate.ReturnType == delegateType.DelegateReturnType &&
            candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(delegateType.DelegateParameters.Select(parameter => parameter.Type))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private (List<string> Prelude, List<string> Codes) LowerArguments(IReadOnlyList<LoweredExpression> arguments, ImmutableArray<ParameterSymbol> parameters, ImmutableArray<ExpressionSyntax> syntax)
    {
        var prelude = new List<string>();
        var codes = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var converted = Convert(arguments[index], parameters[index].Type, syntax[index], false);
            prelude.AddRange(converted.Prelude);
            if (converted.Type.Kind == CTypeKind.Void)
            {
                codes.Add(converted.Code);
                continue;
            }
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(converted.Type, temp)} = {converted.Code};");
            if (parameters[index].IsRetained)
                prelude.Add($"ct_retain((ct_object*)(void*){temp});");
            codes.Add(temp);
        }
        return (prelude, codes);
    }

    private (List<string> Prelude, List<string> Codes) CaptureDeferredArguments(IReadOnlyList<LoweredExpression> arguments, ImmutableArray<ParameterSymbol> parameters, ImmutableArray<ExpressionSyntax> syntax)
    {
        var prelude = new List<string>();
        var codes = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var converted = Convert(arguments[index], parameters[index].Type, syntax[index], false);
            prelude.AddRange(converted.Prelude);
            var slot = $"ct_df_{_deferId}_arg_{index}";
            AddCapturedSlot(prelude, converted.Type, slot, converted.Code);
            codes.Add(parameters[index].IsRetained
                ? $"(ct_retain((ct_object*)(void*){Durable(slot)}), {Durable(slot)})"
                : Durable(slot));
        }
        return (prelude, codes);
    }

    private LoweredExpression LowerCast(CastExpressionSyntax syntax)
    {
        var target = _model.ResolveType(syntax.Type, TreeFor(syntax));
        var expression = LowerExpression(syntax.Expression);
        if (target.ContainsPointer || expression.Type.ContainsPointer)
            RequireUnsafe(syntax);
        return Convert(expression, target, syntax, true);
    }

    private LoweredExpression LowerTypeTest(TypeTestExpressionSyntax syntax)
    {
        var target = _model.ResolveType(syntax.Type, TreeFor(syntax));
        if (target.Kind is CTypeKind.Void or CTypeKind.Null or CTypeKind.Error)
        {
            Report("CT2147", $"Type '{target.DisplayName}' is not valid in an is expression.", syntax.Type);
            return ErrorExpression();
        }
        if (target.ContainsPointer)
            RequireUnsafe(syntax);
        _emitter.RegisterType(target);
        if (!target.IsReference)
            _emitter.RegisterBox(target);
        var objectType = _model.Types["System.Object"].Type;
        var value = Materialize(Convert(LowerExpression(syntax.Expression), objectType, syntax.Expression, false), syntax.Expression);
        var code = $"({value.Code} != NULL && ct_type_is_assignable(((ct_object*)(void*){value.Code})->Type, {_emitter.DescriptorExpression(target)}))";
        return new LoweredExpression { Type = CType.Bool, Code = code, Prelude = value.Prelude };
    }

    private LoweredExpression LowerSafeCast(SafeCastExpressionSyntax syntax)
    {
        var target = _model.ResolveType(syntax.Type, TreeFor(syntax));
        if (!target.IsReference)
        {
            Report("CT2147", "The as operator requires a reference target type.", syntax.Type);
            return ErrorExpression();
        }
        var source = LowerExpression(syntax.Expression);
        if (!source.Type.IsReference && source.Type.Kind != CTypeKind.Null && !source.Type.IsError)
        {
            Report("CT2147", "The as operator requires a reference source expression.", syntax.Expression);
            return ErrorExpression(source.Prelude);
        }
        _emitter.RegisterType(target);
        var objectType = _model.Types["System.Object"].Type;
        var value = Materialize(Convert(source, objectType, syntax.Expression, false), syntax.Expression);
        var code = $"({_emitter.CTypeName(target)})(void*)ct_safe_cast((ct_object*)(void*){value.Code}, {_emitter.DescriptorExpression(target)})";
        return new LoweredExpression { Type = target, Code = code, Prelude = value.Prelude };
    }

    private LoweredExpression LowerUnary(UnaryExpressionSyntax syntax)
    {
        if (syntax.OperatorKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
            return LowerIncrement(syntax);
        if (syntax.OperatorKind == SyntaxKind.AmpersandToken)
        {
            RequireUnsafe(syntax);
            var methodGroup = LowerExpression(syntax.Operand);
            if (methodGroup.MethodGroup is not null)
                return new LoweredExpression { Type = CType.Error, Code = string.Empty, Prelude = methodGroup.Prelude, MethodGroup = methodGroup.MethodGroup, IsFunctionAddress = true };
            var operand = LowerAssignable(syntax.Operand);
            if (operand.LValue?.Address is null)
            {
                Report("CT2124", "The address-of operator requires an addressable value.", syntax.Operand);
                return ErrorExpression(operand.Prelude);
            }
            return new LoweredExpression { Type = new CType(CTypeKind.Pointer, ElementType: operand.Type), Code = operand.LValue.Address, Prelude = operand.Prelude };
        }
        if (syntax.OperatorKind == SyntaxKind.StarToken)
        {
            RequireUnsafe(syntax);
            var pointer = Materialize(LowerExpression(syntax.Operand), syntax.Operand);
            if (pointer.Type.Kind != CTypeKind.Pointer)
            {
                Report("CT2125", "The dereference operator requires a pointer.", syntax.Operand);
                return ErrorExpression(pointer.Prelude);
            }
            var dereferenceCode = $"*({pointer.Code})";
            return new LoweredExpression
            {
                Type = pointer.Type.ElementType!,
                Code = dereferenceCode,
                Prelude = pointer.Prelude,
                LValue = new LoweredLValue { Store = value => $"{dereferenceCode} = {value}", Address = pointer.Code },
            };
        }

        if (syntax.OperatorKind == SyntaxKind.MinusToken && syntax.Operand is LiteralExpressionSyntax
            {
                LiteralKind: SyntaxKind.NumberToken,
                Value: NumericLiteralValue { FloatingPoint: null } numeric,
            })
        {
            if (numeric.Suffix == IntegerLiteralSuffix.None && numeric.Integer == (BigInteger)int.MaxValue + 1)
                return Constant(CType.Int, int.MinValue, "INT32_MIN");
            if (numeric.Suffix == IntegerLiteralSuffix.Long && numeric.Integer == (BigInteger)long.MaxValue + 1)
                return Constant(CType.Long, long.MinValue, "INT64_MIN");
        }

        var operandExpression = LowerExpression(syntax.Operand);
        if (syntax.OperatorKind == SyntaxKind.TildeToken && !operandExpression.Type.IsIntegral && !operandExpression.Type.IsError)
        {
            Report("CT2148", "The bitwise complement operator requires an integral operand.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        if (operandExpression.IsConstant && TryFoldUnary(syntax, operandExpression, out var foldedUnary))
            return foldedUnary;
        if (syntax.OperatorKind == SyntaxKind.BangToken)
        {
            var operand = RequireBoolean(operandExpression, syntax.Operand);
            return new LoweredExpression { Type = CType.Bool, Code = $"!({operand.Code})", Prelude = operand.Prelude, IsConstant = operand.IsConstant };
        }
        if (!operandExpression.Type.IsNumeric && !operandExpression.Type.IsIntegral)
        {
            Report("CT2126", $"Unary operator cannot be applied to '{operandExpression.Type.DisplayName}'.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        var promoted = operandExpression.Type.Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char ? CType.Int : operandExpression.Type;
        if (syntax.OperatorKind == SyntaxKind.MinusToken && promoted.Kind is CTypeKind.Uint or CTypeKind.Ulong)
        {
            Report("CT2145", "Unary minus requires a signed numeric operand.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        var operandValue = Convert(operandExpression, promoted, syntax.Operand, false);
        string code = syntax.OperatorKind switch
        {
            SyntaxKind.PlusToken => operandValue.Code,
            SyntaxKind.MinusToken when promoted == CType.Int => $"ct_i32_neg({operandValue.Code})",
            SyntaxKind.MinusToken when promoted == CType.Long => $"ct_i64_neg({operandValue.Code})",
            SyntaxKind.MinusToken => $"-({operandValue.Code})",
            SyntaxKind.TildeToken => $"~({operandValue.Code})",
            _ => operandValue.Code,
        };
        return new LoweredExpression { Type = promoted, Code = code, Prelude = operandValue.Prelude, IsConstant = operandValue.IsConstant };
    }

    private LoweredExpression LowerIncrement(UnaryExpressionSyntax syntax)
    {
        var target = LowerAssignable(syntax.Operand);
        if (target.LValue is null || !target.Type.IsNumeric)
        {
            Report("CT2127", "Increment and decrement require an assignable numeric operand.", syntax.Operand);
            return ErrorExpression(target.Prelude);
        }
        ValidateAssignmentTarget(target.LValue, syntax);
        if (target.LValue.Property is { Getter: not null } property)
        {
            var getter = _emitter.GetAccessorMethod(property, getter: true);
            _emitter.AllocationEffects.RecordCall(_method, getter, syntax.Operand, property.IsVirtual && !target.LValue.IsBaseReceiver);
        }
        var prelude = new List<string>(target.Prelude);
        var old = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, old)} = {target.Code};");
        var one = target.Type == CType.Float ? "1.0f" : "1";
        var nextCode = NumericOperation(syntax.OperatorKind == SyntaxKind.PlusPlusToken ? SyntaxKind.PlusToken : SyntaxKind.MinusToken, target.Type, old, one, syntax);
        var next = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, next)} = {nextCode};");
        prelude.Add(target.LValue.Store(next) + ";");
        MarkAssigned(target.LValue);
        return new LoweredExpression { Type = target.Type, Code = syntax.IsPostfix ? old : next, Prelude = prelude };
    }

    private LoweredExpression LowerBinary(BinaryExpressionSyntax syntax)
    {
        if (syntax.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
            return LowerShortCircuit(syntax);
        var left = LowerExpression(syntax.Left);
        var right = LowerExpression(syntax.Right);
        if (syntax.OperatorKind == SyntaxKind.PercentToken &&
            (!left.Type.IsIntegral || !right.Type.IsIntegral) &&
            !left.Type.IsError && !right.Type.IsError)
        {
            Report("CT2149", "The remainder operator requires integral operands.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        if (left.IsConstant && right.IsConstant && TryFoldBinary(syntax, left, right, out var foldedBinary))
            return foldedBinary;

        if (syntax.OperatorKind == SyntaxKind.PlusToken &&
            (left.Type == CType.String && right.Type.Kind is CTypeKind.String or CTypeKind.Null || right.Type == CType.String && left.Type.Kind is CTypeKind.String or CTypeKind.Null))
        {
            left = Convert(left, CType.String, syntax.Left, false);
            right = Convert(right, CType.String, syntax.Right, false);
            left = Materialize(left, syntax.Left);
            right = Materialize(right, syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "nonconstant string concatenation");
            return OwnResult(CType.String, $"ct_string_concat({left.Code}, {right.Code}, {_emitter.SourceArgument(syntax)})", prelude);
        }

        if (syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            return LowerEquality(syntax, left, right);

        if (syntax.OperatorKind is SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken)
        {
            if (!(left.Type.IsNumeric && right.Type.IsNumeric) && !(left.Type.Kind == CTypeKind.Enum && left.Type == right.Type))
                Report("CT2128", "Ordered comparison requires numeric operands or the same enum type.", syntax);
            var common = left.Type.Kind == CTypeKind.Enum ? left.Type : TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError && !left.Type.IsError && !right.Type.IsError)
            {
                Report("CT2128", "Ordered comparison has no valid common numeric type.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            return new LoweredExpression { Type = CType.Bool, Code = $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})", Prelude = prelude };
        }

        if (syntax.OperatorKind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.HatToken or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
        {
            if (!left.Type.IsIntegral || !right.Type.IsIntegral)
                Report("CT2129", "Bitwise and shift operators require integral operands.", syntax);
            if (left.Type.Kind == CTypeKind.Enum)
            {
                var enumType = left.Type;
                if (syntax.OperatorKind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.HatToken && right.Type != enumType)
                    Report("CT2143", "Binary enum bitwise operands must have the same enum type.", syntax);
                var underlying = enumType.Symbol!.Fields.Single(field => field.Name == "<underlying>").Type;
                left = Materialize(Convert(left, underlying, syntax.Left, true), syntax.Left);
                right = Materialize(Convert(right, syntax.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken ? CType.Int : underlying, syntax.Right, true), syntax.Right);
                var enumPrelude = new List<string>(left.Prelude); enumPrelude.AddRange(right.Prelude);
                var enumCode = syntax.OperatorKind switch
                {
                    SyntaxKind.LessLessToken when underlying == CType.Int => $"ct_i32_shl({left.Code}, {right.Code})",
                    SyntaxKind.GreaterGreaterToken when underlying == CType.Int => $"ct_i32_shr({left.Code}, {right.Code})",
                    _ => $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})",
                };
                return new LoweredExpression { Type = enumType, Code = $"({_emitter.CTypeName(enumType)})({enumCode})", Prelude = enumPrelude };
            }
            var shifting = syntax.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
            var common = shifting
                ? left.Type.Kind is CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char ? CType.Int : left.Type
                : TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError && !left.Type.IsError && !right.Type.IsError)
            {
                Report("CT2129", "Bitwise operands have no valid common integral type.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, shifting ? CType.Int : common, syntax.Right, shifting), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            var code = syntax.OperatorKind switch
            {
                SyntaxKind.LessLessToken when common == CType.Int => $"ct_i32_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Int => $"ct_i32_shr({left.Code}, {right.Code})",
                SyntaxKind.LessLessToken when common == CType.Long => $"ct_i64_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Long => $"ct_i64_shr({left.Code}, {right.Code})",
                SyntaxKind.LessLessToken when common == CType.Ulong => $"({left.Code} << ((uint32_t){right.Code} & 63u))",
                SyntaxKind.GreaterGreaterToken when common == CType.Ulong => $"({left.Code} >> ((uint32_t){right.Code} & 63u))",
                SyntaxKind.LessLessToken => $"({left.Code} << ((uint32_t){right.Code} & 31u))",
                SyntaxKind.GreaterGreaterToken => $"({left.Code} >> ((uint32_t){right.Code} & 31u))",
                _ => $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})",
            };
            return new LoweredExpression { Type = common, Code = code, Prelude = prelude };
        }

        if (left.Type.Kind == CTypeKind.Pointer && right.Type.IsIntegral && syntax.OperatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
        {
            RequireUnsafe(syntax);
            left = Materialize(left, syntax.Left);
            right = Materialize(Convert(right, CType.Int, syntax.Right, false), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            return new LoweredExpression { Type = left.Type, Code = $"({left.Code} {OperatorText(syntax.OperatorKind)} {right.Code})", Prelude = prelude };
        }

        if (!left.Type.IsNumeric || !right.Type.IsNumeric)
        {
            Report("CT2130", "Arithmetic operators require numeric operands.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        var resultType = TypeFacts.PromoteNumeric(left.Type, right.Type);
        if (resultType.IsError)
        {
            Report("CT2130", "Arithmetic operands have no valid common numeric type.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        left = Materialize(Convert(left, resultType, syntax.Left, false), syntax.Left);
        right = Materialize(Convert(right, resultType, syntax.Right, false), syntax.Right);
        var arithmeticPrelude = new List<string>(left.Prelude); arithmeticPrelude.AddRange(right.Prelude);
        return new LoweredExpression
        {
            Type = resultType,
            Code = NumericOperation(syntax.OperatorKind, resultType, left.Code, right.Code, syntax),
            Prelude = arithmeticPrelude,
            IsConstant = left.IsConstant && right.IsConstant,
        };
    }

    private LoweredExpression LowerShortCircuit(BinaryExpressionSyntax syntax)
    {
        var rawLeft = RequireBoolean(LowerExpression(syntax.Left), syntax.Left);
        var right = RequireBoolean(LowerExpression(syntax.Right), syntax.Right);
        if (rawLeft.IsConstant && right.IsConstant && rawLeft.ConstantValue is bool && right.ConstantValue is bool && TryFoldBinary(syntax, rawLeft, right, out var folded))
            return folded;
        var left = Materialize(rawLeft, syntax.Left);
        var prelude = new List<string>(left.Prelude);
        var result = NewTemp();
        prelude.Add($"bool {result} = {left.Code};");
        var condition = syntax.OperatorKind == SyntaxKind.AmpersandAmpersandToken ? result : $"!{result}";
        prelude.Add($"if ({condition}) {{");
        prelude.AddRange(right.Prelude.Select(line => "    " + line));
        prelude.Add($"    {result} = {right.Code};");
        prelude.Add("}");
        return new LoweredExpression { Type = CType.Bool, Code = result, Prelude = prelude };
    }

    private LoweredExpression LowerEquality(BinaryExpressionSyntax syntax, LoweredExpression left, LoweredExpression right)
    {
        string code;
        CType common;
        if (left.Type == CType.String && right.Type.Kind is CTypeKind.String or CTypeKind.Null || right.Type == CType.String && left.Type.Kind is CTypeKind.String or CTypeKind.Null)
        {
            left = Convert(left, CType.String, syntax.Left, false);
            right = Convert(right, CType.String, syntax.Right, false);
            left = Materialize(left, syntax.Left); right = Materialize(right, syntax.Right);
            code = $"ct_string_equal({left.Code}, {right.Code})";
        }
        else if (left.Type.IsNumeric && right.Type.IsNumeric)
        {
            common = TypeFacts.PromoteNumeric(left.Type, right.Type);
            if (common.IsError)
            {
                Report("CT2131", $"Types '{left.Type.DisplayName}' and '{right.Type.DisplayName}' cannot be compared for equality.", syntax);
                return ErrorExpression(left.Prelude.Concat(right.Prelude));
            }
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            code = $"({left.Code} == {right.Code})";
        }
        else if (left.Type == right.Type || left.Type.Kind == CTypeKind.Null && right.Type.IsPointerLike || right.Type.Kind == CTypeKind.Null && left.Type.IsPointerLike)
        {
            common = left.Type.Kind == CTypeKind.Null ? right.Type : left.Type;
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, common, syntax.Right, false), syntax.Right);
            code = $"({left.Code} == {right.Code})";
        }
        else
        {
            Report("CT2131", $"Types '{left.Type.DisplayName}' and '{right.Type.DisplayName}' cannot be compared for equality.", syntax);
            return ErrorExpression(left.Prelude.Concat(right.Prelude));
        }
        if (syntax.OperatorKind == SyntaxKind.BangEqualsToken)
            code = $"!({code})";
        var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
        return new LoweredExpression { Type = CType.Bool, Code = code, Prelude = prelude };
    }

    private LoweredExpression LowerAssignment(AssignmentExpressionSyntax syntax)
    {
        var target = LowerAssignable(syntax.Left);
        if (target.LValue is null)
        {
            Report("CT2132", "The left side of an assignment must be assignable.", syntax.Left);
            return ErrorExpression(target.Prelude);
        }
        ValidateAssignmentTarget(target.LValue, syntax);
        var prelude = new List<string>(target.Prelude);
        if (syntax.OperatorKind == SyntaxKind.EqualsToken)
        {
            var value = Convert(LowerExpression(syntax.Right), target.Type, syntax.Right, false);
            prelude.AddRange(value.Prelude);
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(target.Type, temp)} = {value.Code};");
            if (target.Type.ContainsManagedReferences)
                AddStrongStore(prelude, target, temp);
            else
                prelude.Add(target.LValue.Store(temp) + ";");
            MarkAssigned(target.LValue);
            return new LoweredExpression { Type = target.Type, Code = temp, Prelude = prelude };
        }

        if (!target.Type.IsNumeric)
            Report("CT2133", "Compound assignment requires a numeric target in draft 0.7.", syntax.Left);
        var old = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, old)} = {target.Code};");
        var rawRight = LowerExpression(syntax.Right);
        if (syntax.OperatorKind == SyntaxKind.PercentEqualsToken &&
            (!target.Type.IsIntegral || !rawRight.Type.IsIntegral) &&
            !target.Type.IsError && !rawRight.Type.IsError)
        {
            Report("CT2149", "The remainder operator requires integral operands.", syntax);
            return ErrorExpression(prelude.Concat(rawRight.Prelude));
        }

        if (target.LValue.Property is { Getter: not null } property)
        {
            var getter = _emitter.GetAccessorMethod(property, getter: true);
            _emitter.AllocationEffects.RecordCall(_method, getter, syntax.Left, property.IsVirtual && !target.LValue.IsBaseReceiver);
        }
        var operationType = TypeFacts.PromoteNumeric(target.Type, rawRight.Type);
        if (operationType.IsError)
        {
            Report("CT2133", "Compound-assignment operands have no valid common numeric type.", syntax);
            return ErrorExpression(prelude.Concat(rawRight.Prelude));
        }
        var right = Convert(rawRight, operationType, syntax.Right, true);
        prelude.AddRange(right.Prelude);
        var rightTemp = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(operationType, rightTemp)} = {right.Code};");
        var operation = syntax.OperatorKind switch
        {
            SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
            SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
            SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
            SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
            SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
            _ => SyntaxKind.PlusToken,
        };
        var operationResult = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(operationType, operationResult)} = {NumericOperation(operation, operationType, $"({_emitter.CCastType(operationType)})({old})", rightTemp, syntax)};");
        var result = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(target.Type, result)} = ({_emitter.CCastType(target.Type)})({operationResult});");
        prelude.Add(target.LValue.Store(result) + ";");
        MarkAssigned(target.LValue);
        return new LoweredExpression { Type = target.Type, Code = result, Prelude = prelude };
    }

    private LoweredExpression LowerAssignable(ExpressionSyntax syntax) => syntax switch
    {
        NameExpressionSyntax name => LowerName(name, true),
        MemberAccessExpressionSyntax member => LowerMember(member, true),
        IndexExpressionSyntax index => LowerIndex(index, true),
        UnaryExpressionSyntax { OperatorKind: SyntaxKind.StarToken } unary => LowerUnary(unary),
        _ => LowerExpression(syntax),
    };

    private void ValidateAssignmentTarget(LoweredLValue lvalue, SyntaxNode syntax)
    {
        if (lvalue.Local is { IsConst: true })
            Report("CT2134", $"Const local '{lvalue.Local.Name}' cannot be assigned.", syntax);
        if (lvalue.Local is { IsReadonly: true } readonlyLocal &&
            (readonlyLocal.AssignmentCount > 0 || _repeatableLoopDepth > readonlyLocal.LoopDepthAtDeclaration))
            Report("CT3130", $"Readonly local '{lvalue.Local.Name}' can be assigned only once.", syntax);
        if (lvalue.Field is { IsConst: true })
            Report("CT2135", $"Const field '{lvalue.Field.Name}' cannot be assigned.", syntax);
        if (lvalue.Field is { IsReadonly: true } field && (!_method.IsConstructor || field.ContainingType != _method.ContainingType))
            Report("CT2136", $"Readonly field '{field.Name}' can be assigned only by its constructor.", syntax);
        else if (lvalue.Field is { IsReadonly: true } readonlyField &&
                 (_fieldAssignmentCounts.GetValueOrDefault(readonlyField) > 0 || _repeatableLoopDepth > 0))
            Report("CT3131", $"Readonly field '{readonlyField.Name}' can be assigned only once.", syntax);
    }

    private void MarkAssigned(LoweredLValue lvalue)
    {
        if (lvalue.Local is not null)
        {
            lvalue.Local.IsAssigned = true;
            lvalue.Local.AssignmentCount++;
        }
        if (lvalue.Field is not null)
        {
            _assignedFields.Add(lvalue.Field);
            _fieldAssignmentCounts[lvalue.Field] = _fieldAssignmentCounts.GetValueOrDefault(lvalue.Field) + 1;
        }
    }

    private string NumericOperation(SyntaxKind operation, CType type, string left, string right, SyntaxNode syntax)
    {
        if (type == CType.Int)
        {
            return operation switch
            {
                SyntaxKind.PlusToken => $"ct_i32_add({left}, {right})",
                SyntaxKind.MinusToken => $"ct_i32_sub({left}, {right})",
                SyntaxKind.StarToken => $"ct_i32_mul({left}, {right})",
                SyntaxKind.SlashToken => $"ct_i32_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_i32_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Uint)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_u32_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_u32_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Long)
        {
            return operation switch
            {
                SyntaxKind.PlusToken => $"ct_i64_add({left}, {right})",
                SyntaxKind.MinusToken => $"ct_i64_sub({left}, {right})",
                SyntaxKind.StarToken => $"ct_i64_mul({left}, {right})",
                SyntaxKind.SlashToken => $"ct_i64_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_i64_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Ulong)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_u64_div({left}, {right}, {_emitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_u64_mod({left}, {right}, {_emitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        return $"({left} {OperatorText(operation)} {right})";
    }

    private LoweredExpression Convert(LoweredExpression expression, CType target, SyntaxNode syntax, bool explicitConversion)
    {
        if (expression.IsFunctionAddress)
            return ConvertFunctionAddress(expression, target, syntax);
        if (expression.MethodGroup is not null)
            return ConvertMethodGroup(expression, target, syntax);
        if (expression.Type == target || expression.Type.IsError || target.IsError)
            return new LoweredExpression
            {
                Type = target,
                Code = expression.Code,
                Prelude = expression.Prelude,
                LValue = expression.LValue,
                IsConstant = expression.IsConstant,
                ConstantValue = expression.ConstantValue,
                Ownership = expression.Ownership,
            };
        var sourceType = expression.Type;
        var valid = explicitConversion ? TypeFacts.CanExplicitlyConvert(sourceType, target) : TypeFacts.CanImplicitlyConvert(sourceType, target);
        if (!valid)
        {
            Report("CT2137", $"Cannot {(explicitConversion ? "cast" : "implicitly convert")} '{expression.Type.DisplayName}' to '{target.DisplayName}'.", syntax);
            return new LoweredExpression { Type = target, Code = _emitter.DefaultValue(target), Prelude = expression.Prelude };
        }
        if (expression.IsConstant && TryConvertConstant(expression, target, out var constant))
            return constant;
        var objectType = _model.Types.GetValueOrDefault("System.Object")?.Type;
        if (objectType is not null && target == objectType && !sourceType.IsReference && sourceType.Kind is not CTypeKind.Null)
        {
            if (sourceType.ContainsPointer)
                RequireUnsafe(syntax);
            _emitter.RegisterBox(sourceType);
            _emitter.AllocationEffects.RecordDirect(_method, syntax, $"boxing of '{sourceType.DisplayName}'");
            var boxCode = $"{CEmitter.BoxFunctionName(sourceType)}({expression.Code}, {_emitter.SourceArgument(syntax)})";
            return OwnResult(target, boxCode, expression.Prelude);
        }
        if (objectType is not null && sourceType == objectType && target != objectType && target.Kind is not CTypeKind.Class and not CTypeKind.String and not CTypeKind.Array)
        {
            if (target.ContainsPointer)
                RequireUnsafe(syntax);
            _emitter.RegisterBox(target);
            var unboxCode = $"{CEmitter.UnboxFunctionName(target)}({expression.Code}, {_emitter.SourceArgument(syntax)})";
            return target.ContainsManagedReferences ? OwnResult(target, unboxCode, expression.Prelude) : new LoweredExpression { Type = target, Code = unboxCode, Prelude = expression.Prelude };
        }
        if (explicitConversion && sourceType.IsReference && target.IsReference && sourceType != target &&
            !(sourceType.Kind == CTypeKind.Class && target.Kind == CTypeKind.Class && sourceType.Symbol?.DerivesFrom(target.Symbol!) == true))
        {
            _emitter.RegisterType(target);
            var castCode = $"({_emitter.CTypeName(target)})(void*)ct_checked_cast((ct_object*)(void*){expression.Code}, {_emitter.DescriptorExpression(target)}, {_emitter.SourceArgument(syntax)})";
            return new LoweredExpression { Type = target, Code = castCode, Prelude = expression.Prelude };
        }
        var code = sourceType.Kind == CTypeKind.Null
            ? $"({_emitter.CCastType(target)})NULL"
            : sourceType.IsPointerLike || target.IsPointerLike
                ? $"({_emitter.CCastType(target)})(void*)({expression.Code})"
                : $"({_emitter.CCastType(target)})({expression.Code})";
        return new LoweredExpression { Type = target, Code = code, Prelude = expression.Prelude, IsConstant = expression.IsConstant, ConstantValue = expression.ConstantValue, Ownership = expression.Ownership };
    }

    private LoweredExpression ConvertFunctionAddress(LoweredExpression expression, CType target, SyntaxNode syntax)
    {
        RequireUnsafe(syntax);
        if (target.Kind != CTypeKind.FunctionPointer)
        {
            Report("CT2163", $"A method address can convert only to a compatible unmanaged function pointer, not '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var signature = target.FunctionPointer!;
        var matches = expression.MethodGroup!.Candidates.Where(candidate =>
            candidate.IsStatic && candidate.ReturnType == signature.ReturnType &&
            candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(signature.ParameterTypes)).ToArray();
        if (matches.Length != 1)
        {
            Report("CT2163", "Method address is not uniquely compatible with the unmanaged function-pointer signature.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var selected = matches[0];
        CheckAccess(selected, syntax);
        if (selected.ExternName is not null)
        {
            _emitter.RegisterExternUse(selected, syntax);
            return new LoweredExpression { Type = target, Code = $"&{selected.CName}", Prelude = expression.Prelude };
        }
        var trampoline = _emitter.RegisterFunctionPointerTrampoline(target, selected);
        return new LoweredExpression { Type = target, Code = $"&{trampoline}", Prelude = expression.Prelude };
    }

    private LoweredExpression ConvertMethodGroup(LoweredExpression expression, CType target, SyntaxNode syntax)
    {
        if (target.Kind != CTypeKind.Delegate)
        {
            Report("CT2158", $"A method group can convert only to a compatible named delegate, not '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        var group = expression.MethodGroup!;
        var selected = FindDelegateMethod(group, target.Symbol!);
        if (selected is null)
        {
            Report("CT2159", $"Method group is not uniquely compatible with delegate '{target.DisplayName}'.", syntax);
            return ErrorExpression(expression.Prelude);
        }
        CheckAccess(selected, syntax);
        if (selected.IsUnsafe)
            RequireUnsafe(syntax);
        LoweredExpression? receiver = group.Receiver;
        if (!selected.IsStatic && receiver is null)
        {
            if (_method.IsStatic)
            {
                Report("CT2115", $"Instance method '{selected.Name}' requires an object.", syntax);
                return ErrorExpression(expression.Prelude);
            }
            receiver = new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
        }
        var prelude = new List<string>(expression.Prelude);
        var targetCode = "NULL";
        if (receiver is not null)
        {
            if (receiver.Type.Kind != CTypeKind.Class)
            {
                Report("CT2161", "Instance delegates require a managed class receiver.", syntax);
                return ErrorExpression(prelude);
            }
            receiver = Materialize(receiver, syntax);
            prelude = new List<string>(receiver.Prelude);
            targetCode = $"(ct_object*)(void*){receiver.Code}";
        }
        var virtualDispatch = !selected.IsStatic && selected.IsVirtual && !group.IsBaseReceiver;
        var thunk = _emitter.RegisterDelegateThunk(target.Symbol!, selected, virtualDispatch);
        _emitter.AllocationEffects.RecordDirect(_method, syntax, $"creation of delegate '{target.DisplayName}'");
        var creation = $"{CEmitter.DelegateFactoryName(target.Symbol!)}({targetCode}, &{thunk}, {_emitter.SourceArgument(syntax)})";
        return OwnResult(target, creation, prelude);
    }

    private LoweredExpression RequireBoolean(LoweredExpression expression, SyntaxNode syntax)
    {
        if (expression.Type != CType.Bool && !expression.Type.IsError)
            Report("CT2138", $"Condition requires bool, not '{expression.Type.DisplayName}'.", syntax);
        return expression.Type == CType.Bool ? expression : new LoweredExpression { Type = CType.Bool, Code = "false", Prelude = expression.Prelude };
    }

    private LoweredExpression Materialize(LoweredExpression expression, SyntaxNode syntax)
    {
        if (expression.Type.Kind is CTypeKind.Void or CTypeKind.Error || expression.TypeReceiver is not null)
            return expression;
        var prelude = new List<string>(expression.Prelude);
        var temp = NewTemp();
        prelude.Add($"{_emitter.CDeclaration(expression.Type, temp)} = {expression.Code};");
        return new LoweredExpression
        {
            Type = expression.Type,
            Code = temp,
            Prelude = prelude,
            IsConstant = expression.IsConstant,
            ConstantValue = expression.ConstantValue,
        };
    }

    private LoweredExpression MaterializeReceiver(LoweredExpression receiver, SyntaxNode syntax)
    {
        var prelude = new List<string>(receiver.Prelude);
        if (receiver.Type.Kind == CTypeKind.Class)
        {
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(receiver.Type, temp)} = {receiver.Code};");
            prelude.Add($"(void)ct_require_nonnull({temp}, {_emitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = receiver.Type, Code = temp, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
        }
        if (receiver.Type.Kind == CTypeKind.Struct)
        {
            if (receiver.LValue?.Address is string address)
                return new LoweredExpression { Type = receiver.Type, Code = address, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
            var temp = NewTemp();
            prelude.Add($"{_emitter.CDeclaration(receiver.Type, temp)} = {receiver.Code};");
            return new LoweredExpression { Type = receiver.Type, Code = $"&{temp}", Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
        }
        return receiver;
    }

    private void RequireUnsafe(SyntaxNode syntax)
    {
        if (_unsafeDepth == 0)
            Report("CT2139", "Pointer operations require an unsafe method or block.", syntax);
    }

    private void CheckAccess(MemberSymbol member, SyntaxNode syntax)
    {
        CheckAccessibility(member.Accessibility, member, syntax);
    }

    private void CheckAccessibility(Accessibility accessibility, MemberSymbol member, SyntaxNode syntax)
    {
        if (accessibility == Accessibility.Private && member.ContainingType != _method.ContainingType)
            Report("CT1110", $"Member '{member.Name}' is private.", syntax);
        if (accessibility == Accessibility.Protected && member.ContainingType != _method.ContainingType && !_method.ContainingType.DerivesFrom(member.ContainingType))
            Report("CT1113", $"Member '{member.Name}' is protected.", syntax);
    }

    private static IEnumerable<TypeSymbol> Hierarchy(TypeSymbol type) => type.BaseTypesAndSelf();
    private static string MethodSignatureKey(MethodSymbol method) => $"{method.Name}:{string.Join(',', method.Parameters.Select(parameter => NameMangler.TypeCode(parameter.Type)))}:{method.IsStatic}";

    private bool TryFoldUnary(UnaryExpressionSyntax syntax, LoweredExpression operand, out LoweredExpression result)
    {
        result = ErrorExpression();
        try
        {
            switch (syntax.OperatorKind)
            {
                case SyntaxKind.PlusToken:
                    result = operand;
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Int:
                    var signed = unchecked(-(int)operand.ConstantValue!);
                    result = Constant(CType.Int, signed, FormatInt32(signed));
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Long:
                    var signed64 = unchecked(-(long)operand.ConstantValue!);
                    result = Constant(CType.Long, signed64, FormatInt64(signed64));
                    return true;
                case SyntaxKind.MinusToken when operand.Type == CType.Float:
                    var floating = -(float)operand.ConstantValue!;
                    result = Constant(CType.Float, floating, FormatFloat(floating));
                    return true;
                case SyntaxKind.BangToken when operand.Type == CType.Bool:
                    var boolean = !(bool)operand.ConstantValue!;
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Int:
                    var complemented = ~(int)operand.ConstantValue!;
                    result = Constant(CType.Int, complemented, FormatInt32(complemented));
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Uint:
                    var unsigned = ~(uint)operand.ConstantValue!;
                    result = Constant(CType.Uint, unsigned, $"UINT32_C({unsigned.ToString(CultureInfo.InvariantCulture)})");
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Long:
                    var complemented64 = ~(long)operand.ConstantValue!;
                    result = Constant(CType.Long, complemented64, FormatInt64(complemented64));
                    return true;
                case SyntaxKind.TildeToken when operand.Type == CType.Ulong:
                    var unsigned64 = ~(ulong)operand.ConstantValue!;
                    result = Constant(CType.Ulong, unsigned64, FormatUInt64(unsigned64));
                    return true;
            }
        }
        catch (InvalidCastException)
        {
            return false;
        }
        return false;
    }

    private bool TryFoldBinary(BinaryExpressionSyntax syntax, LoweredExpression left, LoweredExpression right, out LoweredExpression result)
    {
        result = ErrorExpression();
        if (syntax.OperatorKind == SyntaxKind.PlusToken && left.Type == CType.String && right.Type == CType.String)
        {
            var text = (string?)left.ConstantValue + (string?)right.ConstantValue;
            result = Constant(CType.String, text, _emitter.RegisterString(text));
            return true;
        }
        if (left.Type == CType.Bool && right.Type == CType.Bool)
        {
            var l = (bool)left.ConstantValue!;
            var r = (bool)right.ConstantValue!;
            var value = syntax.OperatorKind switch
            {
                SyntaxKind.AmpersandAmpersandToken => l && r,
                SyntaxKind.PipePipeToken => l || r,
                SyntaxKind.EqualsEqualsToken => l == r,
                SyntaxKind.BangEqualsToken => l != r,
                _ => false,
            };
            if (syntax.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken or SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            {
                result = Constant(CType.Bool, value, value ? "true" : "false");
                return true;
            }
        }
        if (!left.Type.IsNumeric || !right.Type.IsNumeric)
            return false;
        var common = TypeFacts.PromoteNumeric(left.Type, right.Type);
        if (!TryConvertConstant(left, common, out left) || !TryConvertConstant(right, common, out right))
            return false;
        var comparison = syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken;
        try
        {
            if (common == CType.Float)
            {
                var l = (float)left.ConstantValue!; var r = (float)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = CompareFloat(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (syntax.OperatorKind is not (SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken))
                    return false;
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => l + r,
                    SyntaxKind.MinusToken => l - r,
                    SyntaxKind.StarToken => l * r,
                    SyntaxKind.SlashToken => l / r,
                    _ => float.NaN,
                };
                result = Constant(CType.Float, value, FormatFloat(value));
                return true;
            }
            else if (common == CType.Ulong)
            {
                var l = (ulong)left.ConstantValue!; var r = (ulong)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Ulong, 0ul, "UINT64_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l / r,
                    SyntaxKind.PercentToken => l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => l << ((int)r & 63),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 63),
                    _ => ulong.MaxValue,
                };
                result = Constant(CType.Ulong, value, FormatUInt64(value));
                return true;
            }
            else if (common == CType.Long)
            {
                var l = (long)left.ConstantValue!; var r = (long)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Long, 0L, "INT64_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l == long.MinValue && r == -1 ? long.MinValue : l / r,
                    SyntaxKind.PercentToken => l == long.MinValue && r == -1 ? 0 : l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => unchecked(l << ((int)r & 63)),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 63),
                    _ => long.MinValue,
                };
                result = Constant(CType.Long, value, FormatInt64(value));
                return true;
            }
            else if (common == CType.Uint)
            {
                var l = (uint)left.ConstantValue!; var r = (uint)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Uint, 0u, "UINT32_C(0)");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l / r,
                    SyntaxKind.PercentToken => l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => l << ((int)r & 31),
                    SyntaxKind.GreaterGreaterToken => l >> ((int)r & 31),
                    _ => uint.MaxValue,
                };
                result = Constant(CType.Uint, value, $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})");
                return true;
            }
            else
            {
                var l = (int)left.ConstantValue!; var r = (int)right.ConstantValue!;
                if (comparison)
                {
                    var boolean = Compare(syntax.OperatorKind, l, r);
                    result = Constant(CType.Bool, boolean, boolean ? "true" : "false");
                    return true;
                }
                if (r == 0 && syntax.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                {
                    Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
                    result = Constant(CType.Int, 0, "0");
                    return true;
                }
                var value = syntax.OperatorKind switch
                {
                    SyntaxKind.PlusToken => unchecked(l + r),
                    SyntaxKind.MinusToken => unchecked(l - r),
                    SyntaxKind.StarToken => unchecked(l * r),
                    SyntaxKind.SlashToken => l == int.MinValue && r == -1 ? int.MinValue : l / r,
                    SyntaxKind.PercentToken => l == int.MinValue && r == -1 ? 0 : l % r,
                    SyntaxKind.AmpersandToken => l & r,
                    SyntaxKind.PipeToken => l | r,
                    SyntaxKind.HatToken => l ^ r,
                    SyntaxKind.LessLessToken => unchecked(l << (r & 31)),
                    SyntaxKind.GreaterGreaterToken => l >> (r & 31),
                    _ => int.MinValue,
                };
                result = Constant(CType.Int, value, FormatInt32(value));
                return true;
            }
        }
        catch (DivideByZeroException)
        {
            Report("CT2142", "Division by zero is not a valid constant expression.", syntax);
            result = common.Kind switch
            {
                CTypeKind.Uint => Constant(common, 0u, "UINT32_C(0)"),
                CTypeKind.Long => Constant(common, 0L, "INT64_C(0)"),
                CTypeKind.Ulong => Constant(common, 0ul, "UINT64_C(0)"),
                _ => Constant(common, 0, "0"),
            };
            return true;
        }
    }

    private static bool Compare<T>(SyntaxKind operation, T left, T right) where T : IComparable<T>
    {
        var comparison = left.CompareTo(right);
        return operation switch
        {
            SyntaxKind.EqualsEqualsToken => comparison == 0,
            SyntaxKind.BangEqualsToken => comparison != 0,
            SyntaxKind.LessToken => comparison < 0,
            SyntaxKind.LessEqualsToken => comparison <= 0,
            SyntaxKind.GreaterToken => comparison > 0,
            SyntaxKind.GreaterEqualsToken => comparison >= 0,
            _ => false,
        };
    }

    private bool TryConvertConstant(LoweredExpression expression, CType target, out LoweredExpression result)
    {
        result = expression;
        if (!expression.IsConstant)
            return false;
        if (expression.Type == target)
            return true;
        if (expression.Type.Kind == CTypeKind.Null && target.IsPointerLike)
        {
            result = Constant(target, null, $"({_emitter.CCastType(target)})NULL");
            return true;
        }
        if (!expression.Type.IsNumeric || !target.IsNumeric)
            return false;
        try
        {
            if (target == CType.Float)
            {
                var value = System.Convert.ToSingle(expression.ConstantValue, CultureInfo.InvariantCulture);
                result = Constant(target, value, FormatFloat(value));
                return true;
            }
            if (target == CType.Uint)
            {
                var value = expression.ConstantValue switch
                {
                    uint unsigned => unsigned,
                    int signed => unchecked((uint)signed),
                    float floating => unchecked((uint)floating),
                    _ => unchecked((uint)System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})");
                return true;
            }
            if (target == CType.Long)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unchecked((long)unsigned),
                    float floating => unchecked((long)floating),
                    _ => unchecked(System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, FormatInt64(value));
                return true;
            }
            if (target == CType.Ulong)
            {
                var value = expression.ConstantValue switch
                {
                    ulong unsigned => unsigned,
                    long signed => unchecked((ulong)signed),
                    int signed => unchecked((ulong)signed),
                    float floating => unchecked((ulong)floating),
                    _ => unchecked(System.Convert.ToUInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
                };
                result = Constant(target, value, FormatUInt64(value));
                return true;
            }
            var signedValue = expression.ConstantValue switch
            {
                int signed => signed,
                uint unsigned => unchecked((int)unsigned),
                long signed => unchecked((int)signed),
                ulong unsigned => unchecked((int)unsigned),
                float floating => unchecked((int)floating),
                _ => unchecked((int)System.Convert.ToInt64(expression.ConstantValue, CultureInfo.InvariantCulture)),
            };
            if (target == CType.Int)
                result = Constant(target, signedValue, FormatInt32(signedValue));
            else
            {
                var narrowed = target.Kind switch
                {
                    CTypeKind.Byte or CTypeKind.Char => unchecked((byte)signedValue),
                    CTypeKind.Sbyte => unchecked((sbyte)signedValue),
                    CTypeKind.Short => unchecked((short)signedValue),
                    CTypeKind.Ushort => unchecked((ushort)signedValue),
                    _ => signedValue,
                };
                result = Constant(target, narrowed, $"({_emitter.CTypeName(target)}){FormatInt32(signedValue)}");
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string OperatorText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.PercentToken => "%",
        SyntaxKind.AmpersandToken => "&",
        SyntaxKind.PipeToken => "|",
        SyntaxKind.HatToken => "^",
        SyntaxKind.LessToken => "<",
        SyntaxKind.LessEqualsToken => "<=",
        SyntaxKind.GreaterToken => ">",
        SyntaxKind.GreaterEqualsToken => ">=",
        SyntaxKind.EqualsEqualsToken => "==",
        SyntaxKind.BangEqualsToken => "!=",
        SyntaxKind.LessLessToken => "<<",
        SyntaxKind.GreaterGreaterToken => ">>",
        _ => "+",
    };

    private static string FormatInt32(int value) => value == int.MinValue ? "INT32_MIN" : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatInt64(long value) => value switch
    {
        long.MinValue => "INT64_MIN",
        < 0 => $"(-INT64_C({(-value).ToString(CultureInfo.InvariantCulture)}))",
        _ => $"INT64_C({value.ToString(CultureInfo.InvariantCulture)})",
    };

    private static string FormatUInt64(ulong value) => $"UINT64_C({value.ToString(CultureInfo.InvariantCulture)})";

    private static string FormatFloat(float value)
    {
        if (float.IsNaN(value))
            return "NAN";
        if (float.IsPositiveInfinity(value))
            return "INFINITY";
        if (float.IsNegativeInfinity(value))
            return "(-INFINITY)";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
            text += ".0";
        return text + "f";
    }

    private static bool CompareFloat(SyntaxKind operation, float left, float right) => operation switch
    {
        SyntaxKind.EqualsEqualsToken => left == right,
        SyntaxKind.BangEqualsToken => left != right,
        SyntaxKind.LessToken => left < right,
        SyntaxKind.LessEqualsToken => left <= right,
        SyntaxKind.GreaterToken => left > right,
        SyntaxKind.GreaterEqualsToken => left >= right,
        _ => false,
    };

    private static string FormatCondition(string code) => code.StartsWith('(') && code.EndsWith(')') ? code : $"({code})";

    private static LoweredExpression Constant(CType type, object? value, string code) => new() { Type = type, Code = code, IsConstant = true, ConstantValue = value };
    private static LoweredExpression ErrorExpression(IEnumerable<string>? prelude = null) => new() { Type = CType.Error, Code = "0", Prelude = prelude?.ToList() ?? [] };
}
