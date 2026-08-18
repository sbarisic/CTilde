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
}

internal sealed class LoweredLValue
{
    public required Func<string, string> Store { get; init; }
    public string? Address { get; init; }
    public LocalSymbol? Local { get; init; }
    public FieldSymbol? Field { get; init; }
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
    private readonly Dictionary<string, ParameterSymbol> _parameters;
    private readonly Dictionary<ParameterSymbol, string> _heapParameters = [];
    private readonly Stack<string> _breakLabels = [];
    private readonly Stack<string> _continueLabels = [];
    private readonly Stack<List<AssignmentSnapshot>> _breakAssignmentStates = [];
    private readonly Stack<List<AssignmentSnapshot>> _continueAssignmentStates = [];
    private readonly Stack<string> _catchExceptions = [];
    private readonly List<ActiveHandler> _activeExceptionFrames = [];
    private readonly Stack<FinallyContext> _finallyContexts = [];
    private readonly Stack<(int BreakDepth, int ContinueDepth)> _finallyBarriers = [];
    private readonly HashSet<FieldSymbol> _assignedFields = [];
    private readonly Dictionary<FieldSymbol, int> _fieldAssignmentCounts = [];
    private readonly HashSet<FieldSymbol> _constantFieldsBeingEvaluated = [];
    private int _localId;
    private int _tempId;
    private int _labelId;
    private int _unsafeDepth;
    private int _repeatableLoopDepth;
    private int _tryId;
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
        _tryCount = CountTryStatements(method.Body);
        if (_tryCount != 0)
        {
            for (var index = 0; index < method.Parameters.Length; index++)
                _heapParameters[method.Parameters[index]] = $"ct_pp_{index}";
        }
        if (_tryCount != 0 || ContainsThrow(method.Body))
            _emitter.RegisterExceptions();
    }

    public string EmitDefinition()
    {
        if (_method.IsConstructor && _method.ContainingType.Kind == DeclaredTypeKind.Class)
            return EmitClassConstructorDefinition();
        var writer = new CWriter();
        writer.WriteLine(_emitter.MethodSignature(_method, _nameOverride));
        using (writer.Block())
        {
            EmitConstructorPrologue(writer);
            EmitExceptionFrameStorage(writer);
            EmitHeapParameterStorage(writer);
            if (!_method.IsStatic && !_method.IsConstructor)
                writer.WriteLine("(void)ct_self;");
            foreach (var parameter in _method.Parameters)
                writer.WriteLine($"(void){NameMangler.Identifier(parameter.Name)};");
            EmitInstanceFieldInitializers(writer);
            if (_property is not null && _method.Body is null)
                EmitAutomaticAccessor(writer);
            else if (_method.Body is not null)
            {
                var flow = EmitStatements(writer, _method.Body.Statements);
                if (!_method.IsConstructor && _method.ReturnType != CType.Void && !flow.AlwaysReturns)
                    Report("CT3100", $"Not every reachable path returns a value from '{_method.Name}'.", _method.Syntax ?? _method.Body);
            }

            if (_method.IsConstructor)
            {
                ValidateConstructorAssignments();
                writer.WriteLine(_method.ContainingType.Kind == DeclaredTypeKind.Struct ? "return ct_value;" : "return ct_self;");
            }
            else if (_method.ReturnType == CType.Void && (_property is null || !_isGetter))
            {
                writer.WriteLine("return;");
            }
        }
        return writer.ToString();
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
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
            writer.WriteLine($"{CEmitter.ConstructorInitializerName(_method)}(ct_self{(parameterNames.Length == 0 ? string.Empty : ", " + string.Join(", ", parameterNames))});");
            writer.WriteLine("return ct_self;");
        }
        writer.WriteLine();
        var initializerParameters = new[] { $"{typeName}* ct_self" }
            .Concat(_method.Parameters.Select(parameter => $"{_emitter.CTypeName(parameter.Type)} {NameMangler.Identifier(parameter.Name)}"));
        writer.WriteLine($"static void {CEmitter.ConstructorInitializerName(_method)}({string.Join(", ", initializerParameters)})");
        using (writer.Block())
        {
            writer.WriteLine("(void)ct_self;");
            EmitExceptionFrameStorage(writer);
            EmitHeapParameterStorage(writer);
            foreach (var parameter in _method.Parameters)
                writer.WriteLine($"(void){NameMangler.Identifier(parameter.Name)};");
            var delegatesToThis = EmitConstructorInitializer(writer);
            if (!delegatesToThis)
                EmitInstanceFieldInitializers(writer);
            if (_method.Body is not null)
                _ = EmitStatements(writer, _method.Body.Statements);
            if (!delegatesToThis)
                ValidateConstructorAssignments();
            writer.WriteLine("return;");
        }
        return writer.ToString();
    }

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
        _method.ConstructorInitializerTarget = target;
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
        }
        else
        {
            var source = _method.Syntax ?? _method.ContainingType.Syntax!;
            writer.WriteLine($"{typeName}* ct_self = ({typeName}*)ct_alloc(sizeof({typeName}), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"ct_init_object(ct_self, &{CEmitter.DescriptorName(_method.ContainingType)});");
        }
    }

    private void EmitAutomaticAccessor(CWriter writer)
    {
        var field = _property!.BackingField!;
        var access = field.IsStatic ? field.CName : $"ct_self->{field.CName}";
        if (_isGetter)
            writer.WriteLine($"return {access};");
        else
            writer.WriteLine($"{access} = {NameMangler.Identifier("value")};");
    }

    private void EmitInstanceFieldInitializers(CWriter writer)
    {
        if (!_method.IsConstructor)
            return;
        foreach (var field in _method.ContainingType.Fields.Where(field => !field.IsStatic && field.Initializer is not null))
        {
            var expression = Convert(LowerExpression(field.Initializer!), field.Type, field.Initializer!, false);
            EmitPrelude(writer, expression.Prelude);
            writer.WriteLine($"ct_self->{field.CName} = {expression.Code};");
            _assignedFields.Add(field);
            _fieldAssignmentCounts[field] = 1;
        }
    }

    private FlowResult EmitStatements(CWriter writer, ImmutableArray<StatementSyntax> statements)
    {
        var exits = FlowExit.None;
        var reachable = true;
        foreach (var statement in statements)
        {
            if (!reachable)
                Report("CT3101", "Unreachable statement.", statement);
            var before = reachable ? null : SnapshotAssignments();
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
                    PushScope();
                    var flow = EmitStatements(writer, block.Statements);
                    PopScope();
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
            IsHeapBacked = _tryCount != 0,
        };
        _scopes.Peek()[syntax.Name] = symbol;
        if (initializer is not null)
            EmitPrelude(writer, initializer.Prelude);
        var qualifier = syntax.IsConst && !symbol.IsHeapBacked ? "const " : string.Empty;
        if (symbol.IsHeapBacked)
        {
            writer.WriteLine($"{_emitter.CTypeName(type)}* {symbol.StorageName} = ({_emitter.CTypeName(type)}*)ct_alloc(sizeof({_emitter.CTypeName(type)}), {CEmitter.SourceArgument(syntax)});");
            if (initializer is not null)
                writer.WriteLine($"{symbol.CName} = {initializer.Code};");
        }
        else
            writer.WriteLine($"{qualifier}{_emitter.CTypeName(type)} {symbol.CName}{(initializer is null ? string.Empty : " = " + initializer.Code)};");
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
            PushScope();
            var flow = EmitStatement(writer, statement);
            PopScope();
            return flow;
        }
    }

    private void EmitWhile(CWriter writer, WhileStatementSyntax syntax)
    {
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
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        RestoreAssignments(before);
    }

    private FlowResult EmitDo(CWriter writer, DoStatementSyntax syntax)
    {
        var start = NewLabel("do_body");
        var @continue = NewLabel("do_continue");
        var @break = NewLabel("do_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        var breakStates = new List<AssignmentSnapshot>();
        var continueStates = new List<AssignmentSnapshot>();
        _breakAssignmentStates.Push(breakStates); _continueAssignmentStates.Push(continueStates);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        var canRepeat = syntax.Condition is not LiteralExpressionSyntax { LiteralKind: SyntaxKind.FalseKeyword };
        if (canRepeat)
            _repeatableLoopDepth++;
        var bodyFlow = EmitEmbedded(writer, syntax.Body);
        if (canRepeat)
            _repeatableLoopDepth--;
        var bodyState = SnapshotAssignments();
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
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
        PushScope();
        if (syntax.Initializer is not null)
            EmitStatement(writer, syntax.Initializer);
        var start = NewLabel("for_test");
        var @continue = NewLabel("for_continue");
        var @break = NewLabel("for_break");
        var before = SnapshotAssignments();
        writer.WriteLine($"{start}:;");
        if (syntax.Condition is not null)
        {
            var condition = RequireBoolean(LowerExpression(syntax.Condition), syntax.Condition);
            EmitPrelude(writer, condition.Prelude);
            writer.WriteLine($"if (!{FormatCondition(condition.Code)}) goto {@break};");
        }
        _breakAssignmentStates.Push([]); _continueAssignmentStates.Push([]);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        if (syntax.Iterator is not null)
        {
            var iterator = LowerExpression(syntax.Iterator);
            EmitPrelude(writer, iterator.Prelude);
            writer.WriteLine($"(void)({iterator.Code});");
        }
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        RestoreAssignments(before);
        PopScope();
    }

    private void EmitForeach(CWriter writer, ForeachStatementSyntax syntax)
    {
        PushScope();
        var collection = Materialize(LowerExpression(syntax.Collection), syntax.Collection);
        if (collection.Type.Kind != CTypeKind.Array)
            Report("CT2105", "foreach requires a one-dimensional array.", syntax.Collection);
        EmitPrelude(writer, collection.Prelude);
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
        writer.WriteLine($"{_emitter.CTypeName(declaredType)} {local.CName} = {collection.Code}->Data[{index}];");
        _breakAssignmentStates.Push([]); _continueAssignmentStates.Push([]);
        _breakLabels.Push(@break); _continueLabels.Push(@continue);
        _repeatableLoopDepth++;
        EmitEmbedded(writer, syntax.Body);
        _repeatableLoopDepth--;
        _continueLabels.Pop(); _breakLabels.Pop();
        _continueAssignmentStates.Pop(); _breakAssignmentStates.Pop();
        writer.WriteLine($"goto {@continue};");
        writer.WriteLine($"{@continue}:;");
        writer.WriteLine($"{index} = ct_i32_add({index}, 1);");
        writer.WriteLine($"goto {start};");
        writer.WriteLine($"{@break}:;");
        PopScope();
        RestoreAssignments(before);
    }

    private FlowResult EmitSwitch(CWriter writer, SwitchStatementSyntax syntax)
    {
        var value = Materialize(LowerExpression(syntax.Expression), syntax.Expression);
        if (!value.Type.IsIntegral)
            Report("CT2107", "switch requires an integral or enum expression.", syntax.Expression);
        EmitPrelude(writer, value.Prelude);
        var @break = NewLabel("switch_break");
        var before = SnapshotAssignments();
        var breakStates = new List<AssignmentSnapshot>();
        _breakAssignmentStates.Push(breakStates);
        _breakLabels.Push(@break);
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
                PushScope();
                var flow = EmitStatements(writer, section.Statements);
                var sectionState = SnapshotAssignments();
                PopScope();
                sectionFlows.Add(flow);
                if (flow.FallsThrough)
                    fallthroughStates.Add(sectionState);
                if (flow.FallsThrough)
                    Report("CT3105", "A switch section must end with break, continue, or return.", section);
            }
        }
        _breakLabels.Pop();
        _breakAssignmentStates.Pop();
        writer.WriteLine($"{@break}:;");
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
        var literal = target == CType.Uint
            ? $"UINT32_C({value.ToString(CultureInfo.InvariantCulture)})"
            : value == int.MinValue ? "INT32_MIN" : value.ToString(CultureInfo.InvariantCulture);
        code = governingType.Kind == CTypeKind.Enum ? $"({_emitter.CTypeName(governingType)})({literal})" : $"({_emitter.CTypeName(target)})({literal})";
        return true;
    }

    private static bool TryGetIntegralValue(object? constant, out BigInteger value)
    {
        switch (constant)
        {
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
            Report("CT3106", "A constructor cannot contain a return statement in draft 0.5.", syntax);
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
        writer.WriteLine($"ct_throw((ct_object*)(void*){exceptionCode}, {CEmitter.SourceArgument(syntax)});");
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

        writer.WriteLine($"{frame}->Previous = ct_exception_top;");
        writer.WriteLine($"ct_exception_top = {frame};");
        _activeExceptionFrames.Add(new ActiveHandler(frame, _breakLabels.Count, _continueLabels.Count));
        writer.WriteLine($"if (setjmp({frame}->Target) == 0)");
        FlowResult protectedFlow;
        using (writer.Block())
        {
            protectedFlow = syntax.Catches.Length == 0
                ? EmitStatement(writer, syntax.Body)
                : EmitTryCatchCore(writer, syntax, id, before);
            if (protectedFlow.FallsThrough)
            {
                writer.WriteLine($"ct_exception_top = {frame}->Previous;");
                writer.WriteLine($"*ct_ep_{id} = 0;");
                writer.WriteLine($"goto {cleanup};");
            }
        }
        writer.WriteLine("else");
        using (writer.Block())
        {
            writer.WriteLine($"*ct_ex_{id} = {frame}->Exception;");
            writer.WriteLine($"ct_exception_top = {frame}->Previous;");
            writer.WriteLine($"*ct_ep_{id} = 4;");
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
            writer.WriteLine($"if (*ct_ep_{id} == 4) ct_throw(*ct_ex_{id}, {CEmitter.SourceArgument(syntax)});");
            if (!_method.IsConstructor && _method.ReturnType != CType.Void)
            {
                writer.WriteLine($"if (*ct_ep_{id} == 1)");
                using (writer.Block())
                    EmitReturnTransfer(writer, $"*ct_er_{id}");
            }
            else
            {
                writer.WriteLine($"if (*ct_ep_{id} == 1)");
                using (writer.Block())
                    EmitReturnTransfer(writer, null);
            }
            if (context.BreakTarget is not null)
            {
                writer.WriteLine($"if (*ct_ep_{id} == 2)");
                using (writer.Block())
                    EmitResumedBranch(writer, false, context.BreakTarget);
            }
            if (context.ContinueTarget is not null)
            {
                writer.WriteLine($"if (*ct_ep_{id} == 3)");
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
        writer.WriteLine($"ct_exception_top = {frame};");
        _activeExceptionFrames.Add(new ActiveHandler(frame, _breakLabels.Count, _continueLabels.Count));
        writer.WriteLine($"if (setjmp({frame}->Target) == 0)");
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
            writer.WriteLine($"ct_object* ct_caught_{id} = {frame}->Exception;");
            writer.WriteLine($"(void)ct_caught_{id};");
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
                    PushScope();
                    DeclareCatchLocal(writer, boundCatch, $"ct_caught_{id}");
                    _catchExceptions.Push($"ct_caught_{id}");
                    var catchFlow = EmitStatements(writer, boundCatch.Syntax.Body.Statements);
                    _catchExceptions.Pop();
                    PopScope();
                    catchExits |= catchFlow.Exits;
                    if (catchFlow.FallsThrough)
                    {
                        fallthroughStates.Add(SnapshotAssignments());
                        writer.WriteLine($"goto {done};");
                    }
                }
            }
            if (!catches.Any(boundCatch => boundCatch.Type is null))
                writer.WriteLine($"ct_throw(ct_caught_{id}, {CEmitter.SourceArgument(syntax)});");
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
            IsHeapBacked = true,
        };
        _scopes.Peek()[symbol.Name] = symbol;
        writer.WriteLine($"{_emitter.CTypeName(symbol.Type)}* {symbol.StorageName} = ({_emitter.CTypeName(symbol.Type)}*)ct_alloc(sizeof({_emitter.CTypeName(symbol.Type)}), {CEmitter.SourceArgument(boundCatch.Syntax)});");
        writer.WriteLine($"{symbol.CName} = ({_emitter.CTypeName(symbol.Type)})(void*){exceptionCode};");
    }

    private void EmitReturnTransfer(CWriter writer, string? value)
    {
        if (_finallyContexts.Count != 0)
        {
            var context = _finallyContexts.Peek();
            EmitPopHandlersTo(writer, context.HandlerDepth);
            if (value is not null)
                writer.WriteLine($"*ct_er_{context.TryId} = {value};");
            writer.WriteLine($"*ct_ep_{context.TryId} = 1;");
            writer.WriteLine($"goto {context.CleanupLabel};");
            return;
        }
        EmitPopHandlersTo(writer, 0);
        writer.WriteLine(value is null ? "return;" : $"return {value};");
    }

    private void EmitBreakOrContinue(CWriter writer, bool isContinue)
    {
        var depth = isContinue ? _continueLabels.Count : _breakLabels.Count;
        var context = _finallyContexts.FirstOrDefault(item => depth <= (isContinue ? item.ContinueDepth : item.BreakDepth));
        if (context is not null)
        {
            EmitPopHandlersTo(writer, context.HandlerDepth);
            writer.WriteLine($"*ct_ep_{context.TryId} = {(isContinue ? 3 : 2)};");
            writer.WriteLine($"goto {context.CleanupLabel};");
            return;
        }
        EmitPopCrossedHandlers(writer, isContinue, depth);
        writer.WriteLine($"goto {(isContinue ? _continueLabels.Peek() : _breakLabels.Peek())};");
    }

    private void EmitResumedBranch(CWriter writer, bool isContinue, string target)
    {
        var context = _finallyContexts.FirstOrDefault(item =>
            (isContinue ? item.ContinueTarget : item.BreakTarget) == target);
        if (context is not null)
        {
            EmitPopHandlersTo(writer, context.HandlerDepth);
            writer.WriteLine($"*ct_ep_{context.TryId} = {(isContinue ? 3 : 2)};");
            writer.WriteLine($"goto {context.CleanupLabel};");
        }
        else
        {
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

    private void PushScope() => _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
    private void PopScope() => _scopes.Pop();
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
        var source = _method.Syntax ?? _method.ContainingType.Syntax!;
        for (var index = 0; index < _tryCount; index++)
        {
            writer.WriteLine($"ct_exception_frame* ct_eh_{index}_catch = (ct_exception_frame*)ct_alloc(sizeof(ct_exception_frame), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"ct_exception_frame* ct_eh_{index}_finally = (ct_exception_frame*)ct_alloc(sizeof(ct_exception_frame), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"int32_t* ct_ep_{index} = (int32_t*)ct_alloc(sizeof(int32_t), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"ct_object** ct_ex_{index} = (ct_object**)ct_alloc(sizeof(ct_object*), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"(void)ct_eh_{index}_catch;");
            writer.WriteLine($"(void)ct_eh_{index}_finally;");
            writer.WriteLine($"(void)ct_ep_{index};");
            writer.WriteLine($"(void)ct_ex_{index};");
            if (!_method.IsConstructor && _method.ReturnType != CType.Void)
            {
                writer.WriteLine($"{_emitter.CTypeName(_method.ReturnType)}* ct_er_{index} = ({_emitter.CTypeName(_method.ReturnType)}*)ct_alloc(sizeof({_emitter.CTypeName(_method.ReturnType)}), {CEmitter.SourceArgument(source)});");
                writer.WriteLine($"(void)ct_er_{index};");
            }
        }
    }

    private void EmitHeapParameterStorage(CWriter writer)
    {
        if (_heapParameters.Count == 0)
            return;
        var source = _method.Syntax ?? _method.ContainingType.Syntax!;
        foreach (var parameter in _method.Parameters)
        {
            var storage = _heapParameters[parameter];
            var typeName = _emitter.CTypeName(parameter.Type);
            var parameterName = NameMangler.Identifier(parameter.Name);
            writer.WriteLine($"{typeName}* {storage} = ({typeName}*)ct_alloc(sizeof({typeName}), {CEmitter.SourceArgument(source)});");
            writer.WriteLine($"*{storage} = {parameterName};");
        }
    }

    private static int CountTryStatements(BlockStatementSyntax? body) => body is null ? 0 : CountTry(body);

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
            return Constant(CType.String, syntax.Value, _emitter.RegisterString((string)syntax.Value!));
        if (syntax.LiteralKind == SyntaxKind.CharacterToken)
            return Constant(CType.Char, syntax.Value, ((byte)syntax.Value!).ToString(CultureInfo.InvariantCulture));
        if (syntax.Value is NumericLiteralValue numeric)
        {
            if (numeric.FloatingPoint is float value)
                return Constant(CType.Float, value, FormatFloat(value));
            if (numeric.IsUnsigned)
            {
                if (numeric.Integer > uint.MaxValue)
                    Report("CT2111", "Unsigned integer literal does not fit uint.", syntax);
                var bounded = (uint)BigInteger.Min(numeric.Integer, uint.MaxValue);
                return Constant(CType.Uint, bounded, $"UINT32_C({bounded.ToString(CultureInfo.InvariantCulture)})");
            }
            if (numeric.Integer <= int.MaxValue)
            {
                var bounded = (int)numeric.Integer;
                return Constant(CType.Int, bounded, FormatInt32(bounded));
            }
            if (numeric.Integer <= uint.MaxValue)
            {
                var bounded = (uint)numeric.Integer;
                return Constant(CType.Uint, bounded, $"UINT32_C({bounded.ToString(CultureInfo.InvariantCulture)})");
            }
            Report("CT2112", "Integer literal does not fit any draft 0.5 integer type.", syntax);
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
            var name = _heapParameters.TryGetValue(parameter, out var storage)
                ? $"(*{storage})"
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
            Report("CT1108", $"Type '{staticType.FullName}' has no static member named '{syntax.Name}'.", syntax);
            return ErrorExpression();
        }

        var receiver = LowerExpression(syntax.Receiver);
        if (receiver.Type.Kind == CTypeKind.String && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {CEmitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = CType.Int, Code = $"{receiver.Code}->Length", Prelude = receiver.Prelude };
        }
        if (receiver.Type.Kind == CTypeKind.Array && syntax.Name == "Length")
        {
            receiver = Materialize(receiver, syntax.Receiver);
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {CEmitter.SourceArgument(syntax)});");
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
        var typedReceiver = property.IsStatic ? string.Empty : $"({NameMangler.Type(property.ContainingType)}*)(void*){receiverArgument}";
        var objectReceiver = property.IsStatic ? string.Empty : $"((ct_object*)(void*){receiverArgument})";
        var getterCode = property.Getter is null
            ? _emitter.DefaultValue(property.Type)
            : property.IsVirtual && !baseReceiver
                ? $"{objectReceiver}->Type->VTable->{CEmitter.VirtualGetterSlotName(property)}({objectReceiver})"
                : $"{NameMangler.Getter(property)}({typedReceiver})";
        return new LoweredExpression
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
            },
        };
    }

    private LoweredExpression LowerIndex(IndexExpressionSyntax syntax, bool forWrite)
    {
        var receiver = Materialize(LowerExpression(syntax.Receiver), syntax.Receiver);
        var index = Materialize(Convert(LowerExpression(syntax.Index), CType.Int, syntax.Index, false), syntax.Index);
        var prelude = new List<string>(receiver.Prelude);
        prelude.AddRange(index.Prelude);
        if (receiver.Type.Kind == CTypeKind.Array)
        {
            prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {CEmitter.SourceArgument(syntax)});");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {CEmitter.SourceArgument(syntax)});");
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
            prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {CEmitter.SourceArgument(syntax)});");
            prelude.Add($"ct_bounds({index.Code}, {receiver.Code}->Length, {CEmitter.SourceArgument(syntax)});");
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
            var length = Materialize(Convert(LowerExpression(syntax.ArrayLength), CType.Int, syntax.ArrayLength, false), syntax.ArrayLength);
            var code = $"ct_new_{NameMangler.Array(type.ElementType!)}({length.Code}, {CEmitter.SourceArgument(syntax)})";
            return new LoweredExpression { Type = type, Code = code, Prelude = length.Prelude };
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
        var lowered = LowerArguments(arguments, constructor.Parameters, syntax.Arguments);
        return new LoweredExpression { Type = type, Code = $"{constructor.CName}({string.Join(", ", lowered.Codes)})", Prelude = lowered.Prelude };
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

    private LoweredExpression LowerCall(CallExpressionSyntax syntax)
    {
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
                    return LowerBuiltInToString(syntax, member, receiver);
                containingType = receiver.Type.Symbol;
                if (containingType is null && (receiver.Type.Kind is CTypeKind.String or CTypeKind.Array || receiver.Type.IsValueType))
                    containingType = _model.Types.GetValueOrDefault("System.Object");
                methodName = member.Name;
                requireStatic = false;
            }
        }
        else
        {
            Report("CT2120", "Only methods can be called in draft 0.5.", syntax.Target);
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
        CheckAccess(selected, syntax);

        var prelude = new List<string>();
        string? receiverCode = null;
        if (!selected.IsStatic)
        {
            receiver ??= _method.ContainingType.Kind == DeclaredTypeKind.Struct
                ? new LoweredExpression { Type = _method.ContainingType.Type, Code = "(*ct_self)", LValue = new LoweredLValue { Store = value => $"*ct_self = {value}", Address = "ct_self" } }
                : new LoweredExpression { Type = _method.ContainingType.Type, Code = "ct_self" };
            if ((selected.ContainingType.IsObject || selected.IsVirtual && receiver.Type.IsValueType) && receiver.Type != _model.Types["System.Object"].Type)
                receiver = Convert(receiver, _model.Types["System.Object"].Type, syntax.Target, false);
            var loweredReceiver = MaterializeReceiver(receiver, syntax.Target);
            prelude.AddRange(loweredReceiver.Prelude);
            receiverCode = loweredReceiver.Code;
        }
        var loweredArguments = LowerArguments(arguments, selected.Parameters, syntax.Arguments);
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
        return new LoweredExpression { Type = selected.ReturnType, Code = call, Prelude = prelude };
    }

    private static bool SupportsBuiltInToString(CType type) => type.Kind is
        CTypeKind.Bool or CTypeKind.Byte or CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Ushort or
        CTypeKind.Char or CTypeKind.Int or CTypeKind.Uint or CTypeKind.Float or CTypeKind.String;

    private LoweredExpression LowerBuiltInToString(CallExpressionSyntax syntax, MemberAccessExpressionSyntax member, LoweredExpression receiver)
    {
        var arguments = syntax.Arguments.Select(LowerExpression).ToArray();
        if (arguments.Length != 0)
        {
            Report("CT2122", "No overload of 'ToString' accepts the supplied argument types.", syntax);
            return ErrorExpression(receiver.Prelude.Concat(arguments.SelectMany(argument => argument.Prelude)));
        }

        receiver = Materialize(receiver, member.Receiver);
        if (receiver.Type.Kind == CTypeKind.String)
        {
            receiver.Prelude.Add($"(void)ct_require_nonnull({receiver.Code}, {CEmitter.SourceArgument(member)});");
            return new LoweredExpression { Type = CType.String, Code = receiver.Code, Prelude = receiver.Prelude };
        }

        var function = receiver.Type.Kind switch
        {
            CTypeKind.Bool => "ct_to_string_bool",
            CTypeKind.Char => "ct_to_string_char",
            CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint => "ct_to_string_uint",
            CTypeKind.Sbyte or CTypeKind.Short or CTypeKind.Int => "ct_to_string_int",
            CTypeKind.Float => "ct_to_string_float",
            _ => throw new InvalidOperationException($"Unsupported ToString receiver '{receiver.Type.DisplayName}'."),
        };
        var argument = receiver.Type.Kind switch
        {
            CTypeKind.Byte or CTypeKind.Ushort => $"(uint32_t){receiver.Code}",
            CTypeKind.Sbyte or CTypeKind.Short => $"(int32_t){receiver.Code}",
            _ => receiver.Code,
        };
        var code = $"{function}({argument}, {CEmitter.SourceArgument(member)})";
        return new LoweredExpression { Type = CType.String, Code = code, Prelude = receiver.Prelude };
    }

    private MethodSymbol? SelectOverload(IEnumerable<MethodSymbol> candidates, string name, IReadOnlyList<LoweredExpression> arguments, SyntaxNode syntax)
    {
        var matches = candidates
            .Where(candidate => candidate.Parameters.Length == arguments.Count)
            .Where(candidate => candidate.Parameters
                .Select((parameter, index) => TypeFacts.CanImplicitlyConvert(arguments[index].Type, parameter.Type))
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
            prelude.Add($"{_emitter.CTypeName(converted.Type)} {temp} = {converted.Code};");
            codes.Add(temp);
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
        if (syntax.OperatorKind == SyntaxKind.MinusToken && promoted == CType.Uint)
        {
            Report("CT2145", "Unary minus requires a signed numeric operand.", syntax);
            return ErrorExpression(operandExpression.Prelude);
        }
        var operandValue = Convert(operandExpression, promoted, syntax.Operand, false);
        string code = syntax.OperatorKind switch
        {
            SyntaxKind.PlusToken => operandValue.Code,
            SyntaxKind.MinusToken when promoted == CType.Int => $"ct_i32_neg({operandValue.Code})",
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
        var prelude = new List<string>(target.Prelude);
        var old = NewTemp();
        prelude.Add($"{_emitter.CTypeName(target.Type)} {old} = {target.Code};");
        var one = target.Type == CType.Float ? "1.0f" : "1";
        var nextCode = NumericOperation(syntax.OperatorKind == SyntaxKind.PlusPlusToken ? SyntaxKind.PlusToken : SyntaxKind.MinusToken, target.Type, old, one, syntax);
        var next = NewTemp();
        prelude.Add($"{_emitter.CTypeName(target.Type)} {next} = {nextCode};");
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
            return new LoweredExpression { Type = CType.String, Code = $"ct_string_concat({left.Code}, {right.Code}, {CEmitter.SourceArgument(syntax)})", Prelude = prelude };
        }

        if (syntax.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            return LowerEquality(syntax, left, right);

        if (syntax.OperatorKind is SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken)
        {
            if (!(left.Type.IsNumeric && right.Type.IsNumeric) && !(left.Type.Kind == CTypeKind.Enum && left.Type == right.Type))
                Report("CT2128", "Ordered comparison requires numeric operands or the same enum type.", syntax);
            var common = left.Type.Kind == CTypeKind.Enum ? left.Type : TypeFacts.PromoteNumeric(left.Type, right.Type);
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
            var common = left.Type.Kind == CTypeKind.Uint || right.Type.Kind == CTypeKind.Uint ? CType.Uint : CType.Int;
            left = Materialize(Convert(left, common, syntax.Left, false), syntax.Left);
            right = Materialize(Convert(right, CType.Int, syntax.Right, false), syntax.Right);
            var prelude = new List<string>(left.Prelude); prelude.AddRange(right.Prelude);
            var code = syntax.OperatorKind switch
            {
                SyntaxKind.LessLessToken when common == CType.Int => $"ct_i32_shl({left.Code}, {right.Code})",
                SyntaxKind.GreaterGreaterToken when common == CType.Int => $"ct_i32_shr({left.Code}, {right.Code})",
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
            prelude.Add($"{_emitter.CTypeName(target.Type)} {temp} = {value.Code};");
            prelude.Add(target.LValue.Store(temp) + ";");
            MarkAssigned(target.LValue);
            return new LoweredExpression { Type = target.Type, Code = temp, Prelude = prelude };
        }

        if (!target.Type.IsNumeric)
            Report("CT2133", "Compound assignment requires a numeric target in draft 0.5.", syntax.Left);
        var old = NewTemp();
        prelude.Add($"{_emitter.CTypeName(target.Type)} {old} = {target.Code};");
        var rawRight = LowerExpression(syntax.Right);
        if (syntax.OperatorKind == SyntaxKind.PercentEqualsToken &&
            (!target.Type.IsIntegral || !rawRight.Type.IsIntegral) &&
            !target.Type.IsError && !rawRight.Type.IsError)
        {
            Report("CT2149", "The remainder operator requires integral operands.", syntax);
            return ErrorExpression(prelude.Concat(rawRight.Prelude));
        }
        var operationType = TypeFacts.PromoteNumeric(target.Type, rawRight.Type);
        var right = Convert(rawRight, operationType, syntax.Right, true);
        prelude.AddRange(right.Prelude);
        var rightTemp = NewTemp();
        prelude.Add($"{_emitter.CTypeName(operationType)} {rightTemp} = {right.Code};");
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
        prelude.Add($"{_emitter.CTypeName(operationType)} {operationResult} = {NumericOperation(operation, operationType, $"({_emitter.CTypeName(operationType)})({old})", rightTemp, syntax)};");
        var result = NewTemp();
        prelude.Add($"{_emitter.CTypeName(target.Type)} {result} = ({_emitter.CTypeName(target.Type)})({operationResult});");
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
                SyntaxKind.SlashToken => $"ct_i32_div({left}, {right}, {CEmitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_i32_mod({left}, {right}, {CEmitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        if (type == CType.Uint)
        {
            return operation switch
            {
                SyntaxKind.SlashToken => $"ct_u32_div({left}, {right}, {CEmitter.SourceArgument(syntax)})",
                SyntaxKind.PercentToken => $"ct_u32_mod({left}, {right}, {CEmitter.SourceArgument(syntax)})",
                _ => $"({left} {OperatorText(operation)} {right})",
            };
        }
        return $"({left} {OperatorText(operation)} {right})";
    }

    private LoweredExpression Convert(LoweredExpression expression, CType target, SyntaxNode syntax, bool explicitConversion)
    {
        if (expression.Type == target || expression.Type.IsError || target.IsError)
            return new LoweredExpression
            {
                Type = target,
                Code = expression.Code,
                Prelude = expression.Prelude,
                LValue = expression.LValue,
                IsConstant = expression.IsConstant,
                ConstantValue = expression.ConstantValue,
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
            var boxCode = $"{CEmitter.BoxFunctionName(sourceType)}({expression.Code}, {CEmitter.SourceArgument(syntax)})";
            return new LoweredExpression { Type = target, Code = boxCode, Prelude = expression.Prelude };
        }
        if (objectType is not null && sourceType == objectType && target != objectType && target.Kind is not CTypeKind.Class and not CTypeKind.String and not CTypeKind.Array)
        {
            if (target.ContainsPointer)
                RequireUnsafe(syntax);
            _emitter.RegisterBox(target);
            var unboxCode = $"{CEmitter.UnboxFunctionName(target)}({expression.Code}, {CEmitter.SourceArgument(syntax)})";
            return new LoweredExpression { Type = target, Code = unboxCode, Prelude = expression.Prelude };
        }
        if (explicitConversion && sourceType.IsReference && target.IsReference && sourceType != target &&
            !(sourceType.Kind == CTypeKind.Class && target.Kind == CTypeKind.Class && sourceType.Symbol?.DerivesFrom(target.Symbol!) == true))
        {
            _emitter.RegisterType(target);
            var castCode = $"({_emitter.CTypeName(target)})(void*)ct_checked_cast((ct_object*)(void*){expression.Code}, {_emitter.DescriptorExpression(target)}, {CEmitter.SourceArgument(syntax)})";
            return new LoweredExpression { Type = target, Code = castCode, Prelude = expression.Prelude };
        }
        var code = sourceType.Kind == CTypeKind.Null
            ? $"({_emitter.CTypeName(target)})NULL"
            : sourceType.IsPointerLike || target.IsPointerLike
                ? $"({_emitter.CTypeName(target)})(void*)({expression.Code})"
                : $"({_emitter.CTypeName(target)})({expression.Code})";
        return new LoweredExpression { Type = target, Code = code, Prelude = expression.Prelude, IsConstant = expression.IsConstant, ConstantValue = expression.ConstantValue };
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
        prelude.Add($"{_emitter.CTypeName(expression.Type)} {temp} = {expression.Code};");
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
            prelude.Add($"{_emitter.CTypeName(receiver.Type)} {temp} = {receiver.Code};");
            prelude.Add($"(void)ct_require_nonnull({temp}, {CEmitter.SourceArgument(syntax)});");
            return new LoweredExpression { Type = receiver.Type, Code = temp, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
        }
        if (receiver.Type.Kind == CTypeKind.Struct)
        {
            if (receiver.LValue?.Address is string address)
                return new LoweredExpression { Type = receiver.Type, Code = address, Prelude = prelude, IsBaseReceiver = receiver.IsBaseReceiver };
            var temp = NewTemp();
            prelude.Add($"{_emitter.CTypeName(receiver.Type)} {temp} = {receiver.Code};");
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
            result = Constant(common, common == CType.Uint ? 0u : 0, "0");
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
            result = Constant(target, null, $"({_emitter.CTypeName(target)})NULL");
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
            var signedValue = expression.ConstantValue switch
            {
                int signed => signed,
                uint unsigned => unchecked((int)unsigned),
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
