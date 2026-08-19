using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed partial class BodyPipeline
{
    private void EmitReturn(ILoweringWriter writer, ReturnStatementSyntax syntax)
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
        ValidateOutParameters(syntax);
        if (_method.ReturnType == CType.Void)
        {
            if (syntax.Expression is not null)
                Report("CT2109", "A void method cannot return a value.", syntax.Expression);
            ValidateNativeResourceObligations();
            EmitReturnTransfer(writer, null);
            return;
        }
        if (syntax.Expression is null)
        {
            Report("CT2110", $"Method '{(_method.IsOperator ? OperatorFacts.DisplayName(_method.OperatorKind) : _method.Name)}' must return '{_method.ReturnType.DisplayName}'.", syntax);
            writer.WriteLine($"return {_emitter.DefaultValue(_method.ReturnType)};");
            return;
        }
        var expression = Convert(LowerExpression(syntax.Expression), _method.ReturnType, syntax.Expression, false);
        if (_method.ReturnType.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
        {
            if (_method.ReturnsOwned && expression.Ownership != OwnershipKind.Owned)
                Report("CT1256", "An owned native resource return requires an owned value.", syntax.Expression);
            if (!_method.ReturnsOwned && expression.Ownership == OwnershipKind.Owned)
                Report("CT1257", "An owned native resource cannot be returned from a method without ReturnsOwned.", syntax.Expression);
            if (_method.ReturnsOwned)
                ConsumeOwnedExpression(expression, syntax.Expression);
        }
        ValidateNativeResourceObligations();
        EmitPrelude(writer, expression.Prelude);
        EmitReturnTransfer(writer, expression.Code);
    }

    private void EmitThrow(ILoweringWriter writer, ThrowStatementSyntax syntax)
    {
        ValidateNativeResourceObligations();
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

    private FlowResult EmitTry(ILoweringWriter writer, TryStatementSyntax syntax)
    {
        var id = _tryId++;
        var before = SnapshotAssignments();
        return syntax.Finally is null
            ? EmitTryCatchCore(writer, syntax, id, before)
            : EmitTryFinally(writer, syntax, id, before);
    }

    private FlowResult EmitTryFinally(ILoweringWriter writer, TryStatementSyntax syntax, int id, AssignmentSnapshot before)
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

    private FlowResult EmitTryCatchCore(ILoweringWriter writer, TryStatementSyntax syntax, int id, AssignmentSnapshot before)
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

    private void DeclareCatchLocal(ILoweringWriter writer, BoundCatch boundCatch, string exceptionCode)
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

    private void EmitReturnTransfer(ILoweringWriter writer, string? value)
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
        if (value is not null)
        {
            finalValue = NewTemp();
            writer.WriteLine($"{_emitter.CDeclaration(_method.ReturnType, finalValue)} = {value};");
            if (_method.ReturnType.ContainsManagedReferences)
                writer.WriteLine(_emitter.RetainValueStatement(_method.ReturnType, $"&{finalValue}"));
        }
        writer.WriteLine("ct_cleanup_unwind_to(ct_cleanup_method);");
        EmitPopHandlersTo(writer, 0);
        writer.WriteLine(finalValue is null ? "return;" : $"return {finalValue};");
    }

    private void EmitBreakOrContinue(ILoweringWriter writer, bool isContinue)
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

    private void EmitResumedBranch(ILoweringWriter writer, bool isContinue, string target)
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

    private void EmitPopHandlersTo(ILoweringWriter writer, int depth)
    {
        for (var index = _activeExceptionFrames.Count - 1; index >= depth; index--)
            writer.WriteLine($"ct_exception_top = {_activeExceptionFrames[index].Name}->Previous;");
    }

    private void EmitPopCrossedHandlers(ILoweringWriter writer, bool isContinue, int depth)
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

    private void EmitPrelude(ILoweringWriter writer, IEnumerable<string> prelude)
    {
        foreach (var line in prelude)
            writer.WriteLine(line);
    }

    private void EmitActivateOwnedSlot(ILoweringWriter writer, CType type, string slot, string record)
    {
        RegisterCleanupRecord(record);
        writer.WriteLine($"ct_cleanup_push(&{record}, (void*)(uintptr_t)&{slot}, {CEmitter.ValueDropName(type)});");
    }

    private void EmitInitializeOwnedSlot(ILoweringWriter writer, CType type, string slot, string value)
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

    private LoweredExpression OwnResult(CType type, string code, IEnumerable<string> sourcePrelude, bool borrowed = false, object? symbol = null)
    {
        if (!type.ContainsManagedReferences)
            return new LoweredExpression { Type = type, Code = code, Prelude = [.. sourcePrelude], Symbol = symbol };
        if (_method.Name == "<module_init>")
            return new LoweredExpression { Type = type, Code = code, Prelude = [.. sourcePrelude], Ownership = borrowed ? OwnershipKind.Borrowed : OwnershipKind.Owned, Symbol = symbol };
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
        return new LoweredExpression { Type = type, Code = slot, Prelude = prelude, Ownership = OwnershipKind.Owned, Symbol = symbol };
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

    private void BeginScope(ILoweringWriter writer)
    {
        var boundary = EmitCleanupBoundary(writer, "scope");
        _cleanupBoundaries.Push(boundary);
        _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
    }

    private string EmitCleanupBoundary(ILoweringWriter writer, string kind)
    {
        var boundary = $"ct_cleanup_{kind}_{_cleanupId++}";
        writer.WriteLine($"ct_cleanup_record* {boundary} = ct_cleanup_top;");
        writer.WriteLine($"(void){boundary};");
        return boundary;
    }

    private void EndScope(ILoweringWriter writer, bool fallsThrough)
    {
        var boundary = _cleanupBoundaries.Pop();
        if (fallsThrough)
            writer.WriteLine($"ct_cleanup_unwind_to({boundary});");
        var scope = _scopes.Pop();
        if (fallsThrough)
            foreach (var local in scope.Values.Where(local => local.NativeResourceState == NativeResourceState.Owned))
                Report("CT1258", $"Owned native resource '{local.Name}' must be returned, consumed, retained, or scheduled with defer.", local.Syntax);
    }
    private LocalSymbol? FindLocal(string name) => _scopes.Select(scope => scope.GetValueOrDefault(name)).FirstOrDefault(local => local is not null);
    private IEnumerable<LocalSymbol> ActiveLocals() => _scopes.SelectMany(scope => scope.Values).Distinct();
    private void ValidateNativeResourceObligations()
    {
        foreach (var local in ActiveLocals().Where(local => local.NativeResourceState == NativeResourceState.Owned))
            Report("CT1258", $"Owned native resource '{local.Name}' must be returned, consumed, retained, or scheduled with defer.", local.Syntax);
    }
    private AssignmentSnapshot SnapshotAssignments() => new(
        ActiveLocals().ToDictionary(local => local, local => (local.IsAssigned, local.AssignmentCount, local.NativeResourceState)),
        [.. _assignedFields],
        new Dictionary<FieldSymbol, int>(_fieldAssignmentCounts),
        [.. _assignedOutParameters]);

    private void RestoreAssignments(AssignmentSnapshot snapshot)
    {
        foreach (var pair in snapshot.Locals)
        {
            pair.Key.IsAssigned = pair.Value.IsAssigned;
            pair.Key.AssignmentCount = pair.Value.AssignmentCount;
            pair.Key.NativeResourceState = pair.Value.NativeResourceState;
        }
        _assignedFields.Clear();
        _assignedFields.UnionWith(snapshot.Fields);
        _fieldAssignmentCounts.Clear();
        foreach (var pair in snapshot.FieldCounts)
            _fieldAssignmentCounts[pair.Key] = pair.Value;
        _assignedOutParameters.Clear();
        _assignedOutParameters.UnionWith(snapshot.OutParameters);
    }

    private static AssignmentSnapshot MergeAssignments(AssignmentSnapshot before, AssignmentSnapshot thenState, AssignmentSnapshot elseState)
    {
        var locals = before.Locals.ToDictionary(
            pair => pair.Key,
            pair => (
                thenState.Locals.GetValueOrDefault(pair.Key).IsAssigned && elseState.Locals.GetValueOrDefault(pair.Key).IsAssigned,
                Math.Max(thenState.Locals.GetValueOrDefault(pair.Key).AssignmentCount, elseState.Locals.GetValueOrDefault(pair.Key).AssignmentCount),
                MergeNativeResourceState(thenState.Locals.GetValueOrDefault(pair.Key).NativeResourceState, elseState.Locals.GetValueOrDefault(pair.Key).NativeResourceState)));
        var fields = new HashSet<FieldSymbol>(thenState.Fields);
        fields.IntersectWith(elseState.Fields);
        var fieldCounts = thenState.FieldCounts.Keys.Concat(elseState.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => Math.Max(thenState.FieldCounts.GetValueOrDefault(field), elseState.FieldCounts.GetValueOrDefault(field)));
        var outParameters = new HashSet<ParameterSymbol>(thenState.OutParameters);
        outParameters.IntersectWith(elseState.OutParameters);
        return new AssignmentSnapshot(locals, fields, fieldCounts, outParameters);
    }

    private void EmitExceptionFrameStorage(ILoweringWriter writer)
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

    private void EmitDurableParameterStorage(ILoweringWriter writer)
    {
        if (_durableParameters.Count == 0)
            return;
        foreach (var parameter in _method.Parameters.Where(_durableParameters.ContainsKey))
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

    private bool RequiresDurableStorage(string name, int declarationStart)
    {
        if (_tryCount == 0 || _method.Body is null)
            return false;
        foreach (var @try in DescendantNodes(_method.Body).OfType<TryStatementSyntax>())
        {
            if (declarationStart >= @try.Body.Span.Start)
                continue;
            var enclosingLoop = DescendantNodes(_method.Body).OfType<StatementSyntax>()
                .Where(statement => statement is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForeachStatementSyntax)
                .FirstOrDefault(loop => loop.Span.Start <= @try.Span.Start && loop.Span.End >= @try.Span.End);
            if (enclosingLoop is not null && IsModified(enclosingLoop, name) && ContainsName(enclosingLoop, name))
                return true;
            if (!IsModified(@try, name))
                continue;
            var usedAfterProtectedBody = @try.Catches.Any(catchClause => ContainsName(catchClause.Body, name)) ||
                @try.Finally is not null && ContainsName(@try.Finally.Body, name) ||
                DescendantNodes(_method.Body).OfType<NameExpressionSyntax>()
                    .Any(reference => reference.Name == name && reference.Span.Start >= @try.Span.End);
            if (usedAfterProtectedBody)
                return true;
        }
        return false;
    }

    private static bool IsModified(SyntaxNode root, string name) => DescendantNodes(root).Any(node => node switch
    {
        AssignmentExpressionSyntax assignment when ContainsName(assignment.Left, name) => true,
        UnaryExpressionSyntax { OperatorKind: SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken } unary when ContainsName(unary.Operand, name) => true,
        ArgumentSyntax { PassingKind: ParameterPassingKind.Ref or ParameterPassingKind.Out } argument when ContainsName(argument.Expression, name) => true,
        _ => false,
    });

    private static bool ContainsName(SyntaxNode root, string name) =>
        DescendantNodes(root).OfType<NameExpressionSyntax>().Any(reference => reference.Name == name);

    private static IEnumerable<SyntaxNode> DescendantNodes(SyntaxNode root)
    {
        yield return root;
        foreach (var child in root.ChildNodesAndTokens().Where(child => child.IsNode).Select(child => child.Node!))
            foreach (var descendant in DescendantNodes(child))
                yield return descendant;
    }

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
                states.Max(state => state.Locals.GetValueOrDefault(pair.Key).AssignmentCount),
                states.Select(state => state.Locals.GetValueOrDefault(pair.Key).NativeResourceState).Aggregate(MergeNativeResourceState)));
        var fields = new HashSet<FieldSymbol>(first.Fields);
        foreach (var state in states.Skip(1))
            fields.IntersectWith(state.Fields);
        var fieldCounts = states.SelectMany(state => state.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => states.Max(state => state.FieldCounts.GetValueOrDefault(field)));
        var outParameters = new HashSet<ParameterSymbol>(first.OutParameters);
        foreach (var state in states.Skip(1))
            outParameters.IntersectWith(state.OutParameters);
        return new AssignmentSnapshot(locals, fields, fieldCounts, outParameters);
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
                return (pair.Value.IsAssigned || finallyValue.IsAssigned, pair.Value.AssignmentCount + addedAssignments,
                    finallyValue.NativeResourceState == beforeValue.NativeResourceState ? pair.Value.NativeResourceState : finallyValue.NativeResourceState);
            });
        var fields = new HashSet<FieldSymbol>(protectedState.Fields);
        fields.UnionWith(finallyState.Fields);
        var fieldCounts = protectedState.FieldCounts.Keys.Concat(finallyState.FieldCounts.Keys).Distinct().ToDictionary(
            field => field,
            field => protectedState.FieldCounts.GetValueOrDefault(field) +
                Math.Max(0, finallyState.FieldCounts.GetValueOrDefault(field) - before.FieldCounts.GetValueOrDefault(field)));
        var outParameters = new HashSet<ParameterSymbol>(protectedState.OutParameters);
        outParameters.UnionWith(finallyState.OutParameters);
        return new AssignmentSnapshot(locals, fields, fieldCounts, outParameters);
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
        Dictionary<LocalSymbol, (bool IsAssigned, int AssignmentCount, NativeResourceState NativeResourceState)> Locals,
        HashSet<FieldSymbol> Fields,
        Dictionary<FieldSymbol, int> FieldCounts,
        HashSet<ParameterSymbol> OutParameters);

    private sealed record ActiveHandler(string Name, int BreakDepth, int ContinueDepth);
    private sealed record FinallyContext(int TryId, string CleanupLabel, int HandlerDepth, int BreakDepth, int ContinueDepth, string? BreakTarget, string? ContinueTarget);
    private sealed record BoundCatch(CatchClauseSyntax Syntax, CType? Type);

    private static NativeResourceState MergeNativeResourceState(NativeResourceState left, NativeResourceState right) =>
        left == right ? left : NativeResourceState.Moved;
}
