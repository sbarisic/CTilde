using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace CTilde;

internal sealed partial class TypedIrBodyLowerer
{
    private FlowResult EmitStatements(ILoweringWriter writer, ImmutableArray<StatementSyntax> statements, bool allowDefer = true)
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
                if (!_analysisOnly)
                {
                    if (_emitter.EmitDebugInstrumentation)
                        writer.WriteLine($"ct_debug_site(UINT32_C({_emitter.RegisterDebugSite(_method, defer, "defer-capture")}));");
                    var deferId = _deferId;
                    _capturingDirectDefer = true;
                    var directLowered = LowerCall(call, captureForDefer: true);
                    _capturingDirectDefer = false;
                    EmitPrelude(writer, directLowered.Prelude);
                    var thunkName = _emitter.DirectDeferThunkName(_method, deferId);
                    var action = directLowered.Type.ContainsManagedReferences
                        ? $"{_emitter.CDeclaration(directLowered.Type, "ignored")} = {directLowered.Code}; {CEmitter.ValueDropName(directLowered.Type)}((void*)&ignored);"
                        : $"(void)({directLowered.Code});";
                    if (_emitter.EmitDebugInstrumentation)
                        action = $"ct_debug_site(UINT32_C({_emitter.RegisterDebugSite(_method, defer, "defer")})); {action}";
                    _directDefers.Add(new DirectDeferThunk(thunkName, action));
                    RegisterDurableSlot("ct_defer_marker", CType.Byte);
                    var record = $"ct_cleanup_defer_{deferId}";
                    RegisterCleanupRecord(record);
                    writer.WriteLine($"ct_cleanup_push(&{record}, (void*)&ct_state, {thunkName});");
                    continue;
                }
                var lowered = LowerCall(call, captureForDefer: true);
                if (lowered.Symbol is MethodSymbol deferTarget)
                    _deferTargets.Add(deferTarget);
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

    private FlowResult EmitStatement(ILoweringWriter writer, StatementSyntax statement)
    {
        // A braced block is a structural container, not an executable source site.
        // Emitting a probe before it would place statements between an if/while and
        // its body, changing the generated C control flow (and orphaning else).
        if (!_emitter.EmitDebugInformation || statement is BlockStatementSyntax)
            return EmitStatementCore(writer, statement);
        _emitter.RegisterDebugExecutable(_method, statement);
        writer.WriteLine(_emitter.DebugSourceDirective(statement));
        if (_emitter.EmitDebugInstrumentation)
        {
            foreach (var local in ActiveLocals().Where(local => local.IsAssigned).OrderBy(local => local.Id))
                writer.WriteLine($"ct_debug_keep((void*)&{local.CName});");
            writer.WriteLine($"ct_debug_site(UINT32_C({_emitter.RegisterDebugSite(_method, statement, DebugStatementKind(statement))}));");
        }
        var result = EmitStatementCore(writer, statement);
        writer.WriteLine(_emitter.DebugGeneratedDirective());
        return result;
    }

    private static string DebugStatementKind(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax => "return",
        ThrowStatementSyntax => "throw",
        TryStatementSyntax => "try",
        DeferStatementSyntax => "defer",
        IfStatementSyntax or StaticIfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForeachStatementSyntax or SwitchStatementSyntax => "condition",
        _ => "statement",
    };

    private FlowResult EmitStatementCore(ILoweringWriter writer, StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockStatementSyntax block:
                using (writer.Block())
                {
                    BeginScope(writer, block, block.Span.End);
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
                    if (lowered.Ownership == OwnershipKind.Owned && lowered.Type.Kind is CTypeKind.Opaque or CTypeKind.Pointer)
                        Report("CT1255", "An owned native resource result cannot be discarded.", expression.Expression);
                    EmitPrelude(writer, lowered.Prelude);
                    writer.WriteLine($"(void)({lowered.Code});");
                    return FlowResult.None;
                }
            case IfStatementSyntax @if:
                return EmitIf(writer, @if);
            case StaticIfStatementSyntax @if:
                return EmitStaticIf(writer, @if);
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
            case LockStatementSyntax @lock:
                return EmitLock(writer, @lock);
            case UnsafeStatementSyntax unsafeStatement:
                _unsafeDepth++;
                var unsafeFlow = EmitStatement(writer, unsafeStatement.Body);
                _unsafeDepth--;
                return unsafeFlow;
            case InlineAssemblyStatementSyntax assembly:
                EmitInlineAssembly(writer, assembly);
                return FlowResult.None;
            default:
                return FlowResult.None;
        }
    }

    private FlowResult EmitStaticIf(ILoweringWriter writer, StaticIfStatementSyntax syntax)
    {
        var condition = LowerExpression(syntax.Condition);
        if (condition.Type != CType.Bool || !condition.IsConstant || condition.ConstantValue is not bool selected)
        {
            Report(_emitter.Architecture == CompilationArchitecture.Auto ? "CT4108" : "CT2200",
                _emitter.Architecture == CompilationArchitecture.Auto
                    ? "The target architecture could not be resolved before compile-time selection."
                    : "A static if condition must be a compile-time Boolean expression.", syntax.Condition);
            return FlowResult.None;
        }
        return selected
            ? EmitStatement(writer, syntax.Then)
            : syntax.Else is null ? FlowResult.None : EmitStatement(writer, syntax.Else);
    }

    private FlowResult EmitLock(ILoweringWriter writer, LockStatementSyntax syntax)
    {
        var hiddenName = $"__ct_lock_{_lockId++}";
        var mutexType = new TypeSyntax(syntax.Source, syntax.Expression.Span, "System.Threading.Mutex");
        var local = new LocalDeclarationStatementSyntax(syntax.Source, syntax.Expression.Span, mutexType, hiddenName, syntax.Expression, false, false);
        var hidden = new NameExpressionSyntax(syntax.Source, syntax.Expression.Span, hiddenName);
        var enterTarget = new MemberAccessExpressionSyntax(syntax.Source, syntax.Expression.Span, hidden, "Enter");
        var enterCall = new CallExpressionSyntax(syntax.Source, syntax.Expression.Span, enterTarget, []);
        var enter = new ExpressionStatementSyntax(syntax.Source, syntax.Expression.Span, enterCall);
        var exitTarget = new MemberAccessExpressionSyntax(syntax.Source, syntax.Expression.Span, hidden, "Exit");
        var exitCall = new CallExpressionSyntax(syntax.Source, syntax.Expression.Span, exitTarget, []);
        var defer = new DeferStatementSyntax(syntax.Source, syntax.Expression.Span, exitCall);
        var statements = ImmutableArray.Create<StatementSyntax>(local, enter, defer).AddRange(syntax.Body.Statements);
        var block = new BlockStatementSyntax(syntax.Source, syntax.Span, statements);
        return EmitStatementCore(writer, block);
    }

    private void EmitInlineAssembly(ILoweringWriter writer, InlineAssemblyStatementSyntax syntax)
    {
        if (_unsafeDepth == 0)
            Report("CT2190", "Inline assembly requires an unsafe method or block.", syntax);

        var noAllocAttributes = syntax.Attributes.Where(attribute => attribute.Name == "NoAlloc").ToArray();
        foreach (var attribute in syntax.Attributes.Where(attribute => attribute.Name != "NoAlloc"))
            Report("CT2191", $"Attribute '{attribute.Name}' is not valid on an asm statement.", attribute);
        if (noAllocAttributes.Length > 1)
            Report("CT2191", "An asm statement cannot repeat the NoAlloc attribute.", noAllocAttributes[1]);
        if (noAllocAttributes.FirstOrDefault() is { Arguments.Length: > 0 } invalidNoAlloc)
            Report("CT1233", "NoAlloc does not accept arguments.", invalidNoAlloc);
        var trustedNoAlloc = noAllocAttributes.Length == 1 && noAllocAttributes[0].Arguments.IsEmpty;
        if (!trustedNoAlloc)
            _emitter.AllocationEffects.RecordDirect(_method, syntax, "inline assembly has no NoAlloc assertion");

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var lowered = new List<(InlineAssemblyOperandSyntax Syntax, IrExpressionValue Expression, string Constraint)>();
        for (var index = 0; index < syntax.Operands.Length; index++)
        {
            var operand = syntax.Operands[index];
            if (!aliases.Add(operand.Name))
                Report("CT2192", $"Inline assembly operand name '{operand.Name}' is already declared.", operand);

            var expression = operand.Kind switch
            {
                InlineAssemblyOperandKind.Output => LowerAssignable(operand.Variable),
                _ => LowerExpression(operand.Variable),
            };
            var symbol = (object?)expression.LValue?.Local ?? expression.LValue?.Parameter;
            if (symbol is null)
                Report("CT2193", $"Inline assembly operand '{operand.Variable.Name}' must name a local variable or parameter.", operand.Variable);
            else if (!symbols.Add(symbol))
                Report("CT2194", $"Variable '{operand.Variable.Name}' is bound more than once; use ref for a read/write operand.", operand.Variable);

            if (!IsInlineAssemblyType(expression.Type))
                Report("CT2195", $"Type '{expression.Type.DisplayName}' is not a supported inline assembly operand type.", operand.Variable);
            if (operand.Constraint is null && expression.Type == CType.Float)
                Report("CT2196", "A float inline assembly operand requires an explicit GNU constraint.", operand);

            var constraint = operand.Constraint ?? "r";
            if (constraint.Length == 0 || constraint.Contains('\0') || constraint.Contains('\r') || constraint.Contains('\n'))
                Report("CT2197", "An inline assembly constraint must be a non-empty single-line string.", operand);
            if (constraint.Contains('=') || constraint.Contains('+'))
                Report("CT2197", "Inline assembly constraints omit '=' and '+'; the operand role supplies the direction marker.", operand);

            if (operand.Kind is InlineAssemblyOperandKind.Output or InlineAssemblyOperandKind.InputOutput)
            {
                if (expression.LValue is null)
                    Report("CT2198", $"Inline assembly {Role(operand.Kind)} operand '{operand.Variable.Name}' is not assignable.", operand.Variable);
                else
                {
                    if (expression.LValue.Local is { IsConst: true } or { IsReadonly: true } ||
                        expression.LValue.Parameter?.PassingKind == ParameterPassingKind.In)
                        Report("CT2198", $"Inline assembly {Role(operand.Kind)} operand '{operand.Variable.Name}' must be mutable.", operand.Variable);
                    ValidateAssignmentTarget(expression.LValue, operand.Variable);
                    MarkAssigned(expression.LValue);
                }
            }

            var emittedConstraint = operand.Kind switch
            {
                InlineAssemblyOperandKind.Output => $"={constraint}",
                InlineAssemblyOperandKind.InputOutput => $"+{constraint}",
                _ => constraint,
            };
            lowered.Add((operand, expression, emittedConstraint));

            if (_semanticEntries.TryGetValue(operand.Variable, out var semantic))
            {
                foreach (var reference in syntax.References.Where(reference => reference.OperandIndex == index))
                    _semanticEntries[reference] = semantic with { Syntax = reference };
            }
        }

        var clobbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clobber in syntax.Clobbers)
        {
            if (clobber.Length == 0 || clobber.Contains('\0') || clobber.Contains('\r') || clobber.Contains('\n'))
                Report("CT2199", "An inline assembly clobber must be a non-empty single-line string.", syntax);
            else if (!clobbers.Add(clobber))
                Report("CT2199", $"Inline assembly clobber '{clobber}' is duplicated.", syntax);
        }

        if (_analysisOnly)
            return;

        writer.WriteLine("__asm__ volatile (");
        writer.WriteLine($"    \"{BuildInlineAssemblyTemplate(syntax)}\"");
        var outputs = lowered.Select((item, index) => (item, index))
            .Where(pair => pair.item.Syntax.Kind is InlineAssemblyOperandKind.Output or InlineAssemblyOperandKind.InputOutput)
            .Select(pair => $"[ct_asm_{pair.index}] \"{EscapeInlineAssemblyCString(pair.item.Constraint)}\" ({pair.item.Expression.Code})");
        var inputs = lowered.Select((item, index) => (item, index))
            .Where(pair => pair.item.Syntax.Kind == InlineAssemblyOperandKind.Input)
            .Select(pair => $"[ct_asm_{pair.index}] \"{EscapeInlineAssemblyCString(pair.item.Constraint)}\" ({pair.item.Expression.Code})");
        writer.WriteLine($"    : {string.Join(", ", outputs)}");
        writer.WriteLine($"    : {string.Join(", ", inputs)}");
        writer.WriteLine($"    : {string.Join(", ", syntax.Clobbers.Select(clobber => $"\"{EscapeInlineAssemblyCString(clobber)}\""))});");
    }

    private static bool IsInlineAssemblyType(CType type) => type.Kind is
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or CTypeKind.Char or
        CTypeKind.Int or CTypeKind.Uint or CTypeKind.Long or CTypeKind.Ulong or CTypeKind.Nint or CTypeKind.Nuint or
        CTypeKind.Float or CTypeKind.Enum or CTypeKind.Opaque or CTypeKind.Pointer or CTypeKind.FunctionPointer;

    private static string Role(InlineAssemblyOperandKind kind) => kind == InlineAssemblyOperandKind.Output ? "out" : "ref";

    private static string BuildInlineAssemblyTemplate(InlineAssemblyStatementSyntax syntax)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (var reference in syntax.References.OrderBy(reference => reference.Span.Start))
        {
            var relative = reference.Span.Start - syntax.BodySpan.Start;
            AppendInlineAssemblyRaw(result, syntax.Body.AsSpan(position, relative - position));
            result.Append("%[ct_asm_").Append(reference.OperandIndex).Append(']');
            position = relative + reference.Span.Length;
        }
        AppendInlineAssemblyRaw(result, syntax.Body.AsSpan(position));
        return result.ToString();
    }

    private static void AppendInlineAssemblyRaw(System.Text.StringBuilder result, ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    break;
                case '\r':
                case '\n':
                    result.Append("\\n\\t");
                    break;
                case '\\':
                    result.Append("\\\\");
                    break;
                case '"':
                    result.Append("\\\"");
                    break;
                case '%':
                    result.Append("%%");
                    break;
                default:
                    result.Append(text[index]);
                    break;
            }
        }
    }

    private static string EscapeInlineAssemblyCString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private void EmitLocal(ILoweringWriter writer, LocalDeclarationStatementSyntax syntax)
    {
        if (FindLocal(syntax.Name) is not null || _parameters.ContainsKey(syntax.Name))
            Report("CT1106", $"A local named '{syntax.Name}' is already active.", syntax);
        var tree = TreeFor(syntax);
        var type = syntax.Type.Name == "var" ? CType.Error : ResolveType(syntax.Type);
        IrExpressionValue? initializer = null;
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
        if (type.ContainsAtomic && (syntax.Initializer is not NewExpressionSyntax || initializer?.Type.IsAtomic != true))
            Report("CT1278", "Atomic<T> locals require direct construction and cannot be initialized by copying.", syntax);
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
            IsKnownNonNull = initializer?.IsKnownNonNull == true,
            KnownLength = initializer?.KnownLength,
            IsDurable = RequiresDurableStorage(syntax.Name, syntax.Span.Start),
            NativeResourceState = type.Kind is CTypeKind.Opaque or CTypeKind.Pointer
                ? initializer?.Ownership == OwnershipKind.Owned ? NativeResourceState.Owned :
                    initializer?.Ownership == OwnershipKind.Borrowed ? NativeResourceState.Borrowed : NativeResourceState.None
                : NativeResourceState.None,
        };
        if (type.Kind is CTypeKind.Opaque or CTypeKind.Pointer && initializer?.Ownership == OwnershipKind.Owned)
            ConsumeOwnedExpression(initializer, syntax.Initializer!);
        _scopes.Peek()[syntax.Name] = symbol;
        _emitter.RegisterDebugLocal(_method, symbol, syntax.Span.End, _debugScopeEnds.Peek());
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
                EmitInitializeOwnedSlot(writer, type, symbol.CName, initializer);
            else
                writer.WriteLine($"{symbol.CName} = {initializer.Code};");
        }
        writer.WriteLine($"(void){symbol.CName};");
        if (_emitter.EmitDebugInstrumentation)
            writer.WriteLine($"ct_debug_keep((void*)&{symbol.CName});");
    }

    private FlowResult EmitIf(ILoweringWriter writer, IfStatementSyntax syntax)
    {
        var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
        EmitPrelude(writer, condition.Prelude);
        if (!_emitter.EmitDebugInstrumentation && condition.IsConstant && condition.ConstantValue is bool constantCondition)
        {
            if (constantCondition)
                return EmitEmbedded(writer, syntax.Then);
            return syntax.Else is null ? FlowResult.None : EmitEmbedded(writer, syntax.Else);
        }
        var before = SnapshotAssignments();
        writer.WriteLine($"if {FormatCondition(condition.Code)}");
        ApplyNullGuard(syntax.Condition, conditionIsTrue: true);
        var thenFlow = EmitEmbedded(writer, syntax.Then);
        var thenAssignments = SnapshotAssignments();
        RestoreAssignments(before);
        ApplyNullGuard(syntax.Condition, conditionIsTrue: false);
        FlowResult elseFlow = FlowResult.None;
        AssignmentSnapshot elseAssignments;
        if (syntax.Else is not null)
        {
            writer.WriteLine("else");
            elseFlow = EmitEmbedded(writer, syntax.Else);
            elseAssignments = SnapshotAssignments();
        }
        else
            elseAssignments = SnapshotAssignments();
        var fallthroughStates = new List<AssignmentSnapshot>();
        if (thenFlow.FallsThrough)
            fallthroughStates.Add(thenAssignments);
        if (elseFlow.FallsThrough)
            fallthroughStates.Add(elseAssignments);
        RestoreAssignments(fallthroughStates.Count == 0 ? before : MergeAssignments(fallthroughStates));
        return new FlowResult(thenFlow.Exits | elseFlow.Exits);
    }

    private void ApplyNullGuard(ExpressionSyntax condition, bool conditionIsTrue)
    {
        if (condition is not BinaryExpressionSyntax { OperatorKind: SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken } binary)
            return;
        var name = binary.Left is NameExpressionSyntax leftName && binary.Right is LiteralExpressionSyntax { LiteralKind: SyntaxKind.NullKeyword }
            ? leftName
            : binary.Right is NameExpressionSyntax rightName && binary.Left is LiteralExpressionSyntax { LiteralKind: SyntaxKind.NullKeyword }
                ? rightName
                : null;
        if (name is null || FindLocal(name.Name) is not { Type.IsPointerLike: true } local)
            return;
        var inequality = binary.OperatorKind == SyntaxKind.BangEqualsToken;
        local.IsKnownNonNull = conditionIsTrue == inequality;
    }

    private FlowResult EmitEmbedded(ILoweringWriter writer, StatementSyntax statement)
    {
        if (statement is BlockStatementSyntax)
            return EmitStatement(writer, statement);
        using (writer.Block())
        {
            BeginScope(writer, statement, statement.Span.End);
            var flow = EmitStatement(writer, statement);
            EndScope(writer, flow.FallsThrough);
            return flow;
        }
    }

    private void EmitWhile(ILoweringWriter writer, WhileStatementSyntax syntax)
    {
        var cleanup = EmitCleanupBoundary(writer, "while", syntax);
        var start = NewLabel("while_test");
        var @continue = NewLabel("while_continue");
        var @break = NewLabel("while_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
        EmitPrelude(writer, condition.Prelude);
        if (condition.IsConstant && condition.ConstantValue is bool constantCondition)
        {
            if (!constantCondition)
                writer.WriteLine($"goto {@break};");
            else
                writer.WriteLine($"if (false) goto {@break};");
        }
        else
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
        if (cleanup is not null)
            writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        if (cleanup is not null)
            writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        RestoreAssignments(before);
    }

    private FlowResult EmitDo(ILoweringWriter writer, DoStatementSyntax syntax)
    {
        var cleanup = EmitCleanupBoundary(writer, "do", syntax);
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
        if (!canRepeat)
            writer.WriteLine($"if (false) goto {start};");
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
        if (cleanup is not null)
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
            if (condition.IsConstant && condition.ConstantValue is bool constantCondition)
            {
                if (constantCondition)
                    writer.WriteLine($"goto {start};");
            }
            else
                writer.WriteLine($"if {FormatCondition(condition.Code)} goto {start};");
        }
        writer.WriteLine($"goto {@break};");
        writer.WriteLine($"{@break}:;");
        if (cleanup is not null)
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

    private void EmitFor(ILoweringWriter writer, ForStatementSyntax syntax)
    {
        BeginScope(writer, syntax, syntax.Span.End);
        if (syntax.Initializer is not null)
            EmitStatement(writer, syntax.Initializer);
        var start = NewLabel("for_test");
        var @continue = NewLabel("for_continue");
        var @break = NewLabel("for_break");
        var before = SnapshotAssignments();
        var cleanup = EmitCleanupBoundary(writer, "for", syntax);
        writer.WriteLine($"{start}:;");
        if (syntax.Condition is not null)
        {
            var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
            EmitPrelude(writer, condition.Prelude);
            if (condition.IsConstant && condition.ConstantValue is bool constantCondition)
            {
                if (!constantCondition)
                    writer.WriteLine($"goto {@break};");
                else
                    writer.WriteLine($"if (false) goto {@break};");
            }
            else
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
        if (cleanup is not null)
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

    private void EmitForeach(ILoweringWriter writer, ForeachStatementSyntax syntax)
    {
        BeginScope(writer, syntax, syntax.Span.End);
        var collection = Materialize(LowerExpression(syntax.Collection), syntax.Collection);
        if (collection.Type.Kind != CTypeKind.Array)
            Report("CT2105", "foreach requires a one-dimensional array.", syntax.Collection);
        EmitPrelude(writer, collection.Prelude);
        var cleanup = EmitCleanupBoundary(writer, "foreach", syntax);
        var elementType = collection.Type.ElementType ?? CType.Error;
        var declaredType = syntax.Type.Name == "var" ? elementType : ResolveType(syntax.Type);
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
        _emitter.RegisterDebugLocal(_method, local, syntax.Body.Span.Start, syntax.Body.Span.End);
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
        if (cleanup is not null)
            writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        writer.WriteLine($"{index} = ct_i32_add({index}, 1);");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        if (cleanup is not null)
        {
            writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
            writer.WriteLine($"ct_cleanup_unwind_to({cleanup});");
        }
        EndScope(writer, fallsThrough: true);
        RestoreAssignments(before);
    }

    private FlowResult EmitSwitch(ILoweringWriter writer, SwitchStatementSyntax syntax)
    {
        var value = Materialize(LowerExpression(syntax.Expression), syntax.Expression);
        if (!value.Type.IsIntegral)
            Report("CT2107", "switch requires an integral or enum expression.", syntax.Expression);
        EmitPrelude(writer, value.Prelude);
        var @break = NewLabel("switch_break");
        var cleanup = EmitCleanupBoundary(writer, "switch", syntax);
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
                BeginScope(writer, syntax, section.Span.End);
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
        if (cleanup is not null)
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

    private bool TryConvertCaseConstant(IrExpressionValue constant, CType governingType, out string key, out string code)
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
}
