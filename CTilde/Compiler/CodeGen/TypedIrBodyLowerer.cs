using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed class IrExpressionValue
{
    public required CType Type { get; init; }
    public required string Code { get; init; }
    public List<string> Prelude { get; init; } = [];
    public IrValueStorage? LValue { get; init; }
    public TypeSymbol? TypeReceiver { get; init; }
    public bool IsConstant { get; init; }
    public object? ConstantValue { get; init; }
    public bool IsBaseReceiver { get; init; }
    public OwnershipKind Ownership { get; init; }
    public MethodGroupBinding? MethodGroup { get; init; }
    public bool IsFunctionAddress { get; init; }
    public object? Symbol { get; init; }
}

internal sealed record MethodGroupBinding(ImmutableArray<MethodSymbol> Candidates, IrExpressionValue? Receiver, bool IsBaseReceiver);

internal enum OwnershipKind { None, Borrowed, Owned, Immortal }

internal sealed class IrValueStorage
{
    public required Func<string, string> Store { get; init; }
    public string? Address { get; init; }
    public LocalSymbol? Local { get; init; }
    public FieldSymbol? Field { get; init; }
    public PropertySymbol? Property { get; init; }
    public ParameterSymbol? Parameter { get; init; }
    public bool IsBaseReceiver { get; init; }
}

internal sealed partial class TypedIrBodyLowerer
{
    private readonly ILoweringServices _emitter;
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
    private readonly Dictionary<DeferStatementSyntax, IrExpressionValue> _deferredCalls = [];
    private readonly List<DirectDeferThunk> _directDefers = [];
    private readonly HashSet<MethodSymbol> _deferTargets = [];
    private readonly HashSet<FieldSymbol> _assignedFields = [];
    private readonly Dictionary<FieldSymbol, int> _fieldAssignmentCounts = [];
    private readonly HashSet<ParameterSymbol> _assignedOutParameters = [];
    private readonly HashSet<FieldSymbol> _constantFieldsBeingEvaluated = [];
    private readonly Dictionary<SyntaxNode, BoundSemanticEntry> _semanticEntries = [];
    private int _localId;
    private int _tempId;
    private int _labelId;
    private int _unsafeDepth;
    private int _repeatableLoopDepth;
    private int _tryId;
    private int _deferId;
    private int _cleanupId;
    private readonly int _tryCount;
    private readonly int _externUseStart;
    private readonly bool _analysisOnly;
    private readonly ImmutableDictionary<SyntaxNode, BoundSemanticEntry>? _semanticHints;
    private bool _capturingDirectDefer;

    public TypedIrBodyLowerer(ILoweringServices emitter, MethodSymbol method, string? nameOverride = null, PropertySymbol? property = null, bool isGetter = false, string temporaryPrefix = "", bool analysisOnly = false, ImmutableDictionary<SyntaxNode, BoundSemanticEntry>? semanticHints = null)
    {
        _emitter = emitter;
        _model = emitter.Model;
        _diagnostics = emitter.Diagnostics;
        _method = method;
        _nameOverride = nameOverride;
        _property = property;
        _isGetter = isGetter;
        _temporaryPrefix = temporaryPrefix;
        _analysisOnly = analysisOnly;
        _semanticHints = semanticHints;
        _parameters = method.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        _unsafeDepth = HasModifier(method.Syntax, "unsafe") ? 1 : 0;
        _scopes.Push(new Dictionary<string, LocalSymbol>(StringComparer.Ordinal));
        _cleanupBoundaries.Push("ct_cleanup_method");
        _externUseStart = _emitter.ExternUses.Count();
        _tryCount = CountTryStatements(method.Body) + (_analysisOnly ? CountDeferStatements(method.Body) : 0);
        if (_tryCount != 0)
        {
            for (var index = 0; index < method.Parameters.Length; index++)
                if (method.Parameters[index].PassingKind == ParameterPassingKind.Value && RequiresDurableStorage(method.Parameters[index].Name, -1))
                    _durableParameters[method.Parameters[index]] = $"ct_pp_{index}";
        }
        if (_tryCount != 0 || ContainsThrow(method.Body))
            _emitter.RegisterExceptions();
    }

    public string EmitDefinition()
    {
        if (_method.IsConstructor && _method.ContainingType.Kind == DeclaredTypeKind.Class)
            return EmitClassConstructorDefinition();
        var body = CreateWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            EmitConstructorPrologue(body);
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            if (!_method.IsStatic && !_method.IsConstructor)
                body.WriteLine("(void)ct_self;");
            foreach (var parameter in _method.Parameters)
            {
                var name = NameMangler.Identifier(parameter.Name);
                if (parameter.Type.IsNativeBuffer)
                {
                    body.WriteLine($"(void){name}_data;");
                    body.WriteLine($"(void){name}_length;");
                }
                else
                    body.WriteLine($"(void){name};");
            }
            EmitInstanceFieldInitializers(body);
            if (_property is not null && _method.Body is null)
                EmitAutomaticAccessor(body);
            else if (_method.Body is not null)
            {
                var flow = EmitStatements(body, _method.Body.Statements);
                if (!_method.IsConstructor && _method.ReturnType != CType.Void && !flow.AlwaysReturns)
                    Report("CT3100", $"Not every reachable path returns a value from '{(_method.IsOperator ? OperatorFacts.DisplayName(_method.OperatorKind) : _method.Name)}'.", _method.Syntax ?? _method.Body);
                if (flow.FallsThrough)
                {
                    ValidateOutParameters(_method.Body);
                    ValidateNativeResourceObligations();
                }
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
        return _analysisOnly ? string.Empty : RenderFunction(_emitter.MethodSignature(_method, _nameOverride), body);
    }

    private string EmitClassConstructorDefinition()
    {
        var writer = CreateWriter();
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
            .Concat(_method.Parameters.Select(parameter => _emitter.CParameterDeclaration(parameter, NameMangler.Identifier(parameter.Name))));
        var body = CreateWriter();
        {
            body.WriteLine("ct_cleanup_record* ct_cleanup_method = ct_cleanup_top;");
            body.WriteLine("(void)ct_cleanup_method;");
            body.WriteLine("(void)ct_self;");
            EmitExceptionFrameStorage(body);
            EmitDurableParameterStorage(body);
            foreach (var parameter in _method.Parameters)
            {
                var name = NameMangler.Identifier(parameter.Name);
                if (parameter.Type.IsNativeBuffer)
                {
                    body.WriteLine($"(void){name}_data;");
                    body.WriteLine($"(void){name}_length;");
                }
                else
                    body.WriteLine($"(void){name};");
            }
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
        return writer.ToString() ?? string.Empty;
    }

    private string RenderFunction(string signature, ILoweringWriter body)
    {
        var writer = CreateWriter();
        if (_directDefers.Count != 0)
            _emitter.RegisterDirectDeferState(_method, _durableSlots, _directDefers);
        writer.WriteLine(signature);
        using (writer.Block())
        {
            if (_durableSlots.Count != 0)
            {
                if (_directDefers.Count == 0)
                {
                    writer.WriteLine("volatile struct");
                    using (writer.Block())
                    {
                        foreach (var slot in _durableSlots)
                            writer.WriteLine($"{_emitter.CDeclaration(slot.Value, slot.Key)};");
                    }
                    writer.WriteLine("ct_state = {0};");
                }
                else
                    writer.WriteLine($"{(_tryCount == 0 ? string.Empty : "volatile ")}{_emitter.DurableStateTypeName(_method)} ct_state = {{0}};");
                writer.WriteLine("(void)ct_state;");
            }
            foreach (var record in _cleanupRecords.Order(StringComparer.Ordinal))
                writer.WriteLine($"ct_cleanup_record {record} = {{0}};");
            var bodyText = OptimizeGeneratedBody(body.ToString() ?? string.Empty);
            writer.WriteBlock(bodyText.TrimEnd().Split('\n'));
        }
        writer.WriteLine();
        return writer.ToString() ?? string.Empty;
    }

    private string OptimizeGeneratedBody(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (_cleanupRecords.Count == 0 && _tryCount == 0)
        {
            lines.RemoveAll(line =>
            {
                var text = line.Trim();
                return text.StartsWith("ct_cleanup_record* ct_cleanup_", StringComparison.Ordinal) ||
                    text.StartsWith("(void)ct_cleanup_", StringComparison.Ordinal) ||
                    text.StartsWith("ct_cleanup_unwind_to(ct_cleanup_", StringComparison.Ordinal);
            });
        }

        var parameterNames = _method.Parameters.SelectMany(parameter => parameter.Type.IsNativeBuffer
            ? new[] { $"{NameMangler.Identifier(parameter.Name)}_data", $"{NameMangler.Identifier(parameter.Name)}_length" }
            : new[] { NameMangler.Identifier(parameter.Name) }).ToHashSet(StringComparer.Ordinal);
        if (!_method.IsStatic || _method.IsConstructor)
            parameterNames.Add("ct_self");
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var text = lines[index].Trim();
            if (!text.StartsWith("(void)", StringComparison.Ordinal) || !text.EndsWith(';'))
                continue;
            var name = text[6..^1];
            if (!parameterNames.Contains(name))
                continue;
            var uses = lines.Where((_, candidate) => candidate != index).Count(line => ContainsIdentifier(line, name));
            var requiredUses = name == "ct_self" && _method.IsConstructor ? 2 : 1;
            if (uses >= requiredUses)
                lines.RemoveAt(index);
        }
        FoldPureSingleUseTemporaries(lines);
        return string.Join('\n', lines);
    }

    private static void FoldPureSingleUseTemporaries(List<string> lines)
    {
        bool changed;
        do
        {
            changed = false;
            for (var declarationIndex = lines.Count - 1; declarationIndex >= 0; declarationIndex--)
            {
                if (!TryGetSimpleTemporary(lines[declarationIndex], out var name, out var value))
                    continue;
                var uses = Enumerable.Range(declarationIndex + 1, lines.Count - declarationIndex - 1)
                    .Where(index => ContainsIdentifier(lines[index], name)).ToArray();
                if (uses.Length != 1)
                    continue;
                var useIndex = uses[0];
                if (Enumerable.Range(declarationIndex + 1, useIndex - declarationIndex - 1)
                    .Any(index => !string.IsNullOrWhiteSpace(lines[index]) && !TryGetSimpleTemporary(lines[index], out _, out _)))
                    continue;
                lines[useIndex] = ReplaceIdentifier(lines[useIndex], name, $"({value})");
                lines.RemoveAt(declarationIndex);
                changed = true;
                break;
            }
        }
        while (changed);
    }

    private static bool TryGetSimpleTemporary(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var text = line.Trim();
        var assignment = text.IndexOf(" = ", StringComparison.Ordinal);
        if (assignment < 0 || !text.EndsWith(';'))
            return false;
        var prefix = text[..assignment];
        var separator = prefix.LastIndexOf(' ');
        if (separator < 0)
            return false;
        name = prefix[(separator + 1)..];
        if (!name.StartsWith("ct_tmp", StringComparison.Ordinal) || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            return false;
        value = text[(assignment + 3)..^1];
        if (value.StartsWith("ct_state.", StringComparison.Ordinal))
            return false;
        return value.Length != 0 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '+');
    }

    private static string ReplaceIdentifier(string text, string identifier, string replacement)
    {
        var result = new StringBuilder(text.Length + replacement.Length);
        var start = 0;
        while (start < text.Length)
        {
            var match = text.IndexOf(identifier, start, StringComparison.Ordinal);
            if (match < 0)
            {
                result.Append(text, start, text.Length - start);
                break;
            }
            var before = match == 0 || !(char.IsLetterOrDigit(text[match - 1]) || text[match - 1] == '_');
            var end = match + identifier.Length;
            var after = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            result.Append(text, start, match - start);
            if (before && after)
                result.Append(replacement);
            else
                result.Append(identifier);
            start = end;
        }
        return result.ToString();
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var start = 0;
        while ((start = text.IndexOf(identifier, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 || !(char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_');
            var end = start + identifier.Length;
            var after = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            if (before && after)
                return true;
            start = end;
        }
        return false;
    }

    private ILoweringWriter CreateWriter() => _analysisOnly ? NullLoweringWriter.Instance : new CWriter();

    private void RegisterDurableSlot(string name, CType type) => _durableSlots.TryAdd(name, type);
    private void RegisterCleanupRecord(string name) => _cleanupRecords.Add(name);
    private static string Durable(string name) => $"ct_state.{name}";

    private bool EmitConstructorInitializer(ILoweringWriter writer)
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
        var arguments = argumentSyntax.Select(LowerArgument).ToArray();
        var target = SelectOverload(targetType.Constructors, targetType.Name, arguments, argumentSyntax, syntax ?? _method.Syntax ?? _method.ContainingType.Syntax!);
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
        EmitPrelude(writer, lowered.Postlude);
        return syntax?.Kind == ConstructorInitializerKind.This;
    }

    public IrExpressionValue LowerStandalone(ExpressionSyntax expression) => LowerExpression(expression);

    public IrExpressionValue ConvertStandalone(IrExpressionValue expression, CType target, SyntaxNode syntax) => Convert(expression, target, syntax, false);

    public BoundBody GetBoundBody()
    {
        var effects = _emitter.AllocationEffects.Snapshot().GetValueOrDefault(_method, []);
        var externUses = _emitter.ExternUses.Skip(_externUseStart).ToImmutableArray();
        var semantics = _semanticEntries.ToImmutableDictionary();
        var root = BoundTreeFactory.CreateRoot(_method, semantics);
        return new BoundBody(_method, root, semantics, BoundTreeFactory.Summarize(root), effects, externUses, [.. _deferTargets]);
    }

    private void EmitConstructorPrologue(ILoweringWriter writer)
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

    private void EmitAutomaticAccessor(ILoweringWriter writer)
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

    private void EmitInstanceFieldInitializers(ILoweringWriter writer)
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
}
