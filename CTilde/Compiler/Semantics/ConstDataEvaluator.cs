using System.Collections.Immutable;

namespace CTilde;

internal static class ConstDataEvaluator
{
    public static void Evaluate(
        CompilationModel model,
        AnalysisServices services,
        ImmutableArray<BoundBody>.Builder bodies)
    {
        var bodyByMethod = bodies.GroupBy(body => body.Method).ToDictionary(group => group.Key, group => group.First());
        var values = ImmutableDictionary.CreateBuilder<FieldSymbol, ConstDataValue>();
        var initializerIndex = 0;
        foreach (var field in model.UserTypes.SelectMany(type => type.Fields)
                     .Where(field => field.IsConstInit && field.Initializer is not null)
                     .OrderBy(field => field.Syntax?.Source.FilePath, StringComparer.Ordinal)
                     .ThenBy(field => field.Syntax?.Span.Start ?? 0))
        {
            var method = new MethodSymbol
            {
                Name = "<const_init>",
                ContainingType = field.ContainingType,
                Accessibility = Accessibility.Private,
                IsStatic = true,
                Syntax = field.Syntax,
                ReturnType = CType.Void,
                Parameters = [],
                Body = null,
            };
            var analyzer = new BoundBodyBinder(services, method, temporaryPrefix: $"_ci_{initializerIndex++}");
            var value = EvaluateExpression(field.Initializer!, field.Type, analyzer, model, services, bodyByMethod, []);
            bodies.Add(analyzer.Finish());
            if (value is not null)
                values[field] = value;
        }
        model.ConstInitializers = values.ToImmutable();
    }

    private static ConstDataValue? EvaluateExpression(
        ExpressionSyntax syntax,
        CType target,
        BoundBodyBinder analyzer,
        CompilationModel model,
        AnalysisServices services,
        IReadOnlyDictionary<MethodSymbol, BoundBody> bodyByMethod,
        HashSet<MethodSymbol> active)
    {
        if (syntax is NewExpressionSyntax { ArrayLength: null } creation)
            return EvaluateConstruction(creation, target, analyzer, model, services, bodyByMethod, active);
        if (!IsAllowedConstantExpression(syntax))
        {
            Report(model, syntax, "ConstInit expressions may contain only constants, casts, arithmetic, layout queries, and nested unmanaged construction.");
            return null;
        }
        var expression = analyzer.BindExpression(syntax);
        var converted = analyzer.ConvertExpression(expression, target, syntax);
        if (!converted.IsConstant || converted.Prelude.Count != 0 || converted.Type.IsError)
        {
            Report(model, syntax, "ConstInit requires a compile-time scalar expression without runtime evaluation.");
            return null;
        }
        return new ConstDataScalar(target, converted.Code);
    }

    private static ConstDataValue? EvaluateConstruction(
        NewExpressionSyntax creation,
        CType target,
        BoundBodyBinder analyzer,
        CompilationModel model,
        AnalysisServices services,
        IReadOnlyDictionary<MethodSymbol, BoundBody> bodyByMethod,
        HashSet<MethodSymbol> active)
    {
        var expression = analyzer.BindExpression(creation);
        if (expression.Type != target || expression.Symbol is not MethodSymbol constructor || target.Kind != CTypeKind.Struct || target.Symbol is null)
        {
            Report(model, creation, "ConstInit construction requires an exact unmanaged struct constructor.");
            return null;
        }
        var type = target.Symbol;
        if (type.Syntax is TypeDeclarationSyntax { Kind: TypeDeclarationKind.Union } || type.AggregateLayout == AggregateLayoutKind.Explicit ||
            type.Fields.Any(field => !field.IsStatic && field.Initializer is not null))
        {
            Report(model, creation, "ConstInit v1 supports sequential structs without instance field initializers, unions, or overlapping explicit layout.");
            return null;
        }
        if (!active.Add(constructor))
        {
            Report(model, creation, "ConstInit constructor evaluation is recursive.");
            return null;
        }

        try
        {
            var fields = type.Fields.Where(field => !field.IsStatic && !field.IsBitView).ToArray();
            if (constructor.Syntax is null && creation.Arguments.IsEmpty)
                return new ConstDataAggregate(target, fields.Select(field => Zero(field.Type)).ToImmutableArray());
            if (constructor.ConstructorInitializer is not null || constructor.Body is null || creation.Arguments.Length != constructor.Parameters.Length ||
                creation.Arguments.Any(argument => argument.PassingKind != ParameterPassingKind.Value))
            {
                Report(model, creation, "ConstInit constructors cannot chain and require value arguments matching one body-bearing constructor.");
                return null;
            }

            var replacements = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            for (var index = 0; index < constructor.Parameters.Length; index++)
            {
                var parameter = constructor.Parameters[index];
                var argument = creation.Arguments[index].Expression;
                if (EvaluateExpression(argument, parameter.Type, analyzer, model, services, bodyByMethod, active) is null)
                    return null;
                var parameterType = parameter.Syntax?.Type ?? creation.Type;
                replacements[parameter.Name] = argument is NewExpressionSyntax
                    ? argument
                    : new CastExpressionSyntax(argument.Source, argument.Span, parameterType, argument);
            }

            var assigned = new Dictionary<FieldSymbol, ConstDataValue>(ReferenceEqualityComparer.Instance);
            foreach (var statement in constructor.Body.Statements)
            {
                if (statement is not ExpressionStatementSyntax
                    {
                        Expression: AssignmentExpressionSyntax
                        {
                            OperatorKind: SyntaxKind.EqualsToken,
                            Left: var left,
                            Right: var right,
                        }
                    } || !TryFieldName(left, out var fieldName))
                {
                    Report(model, statement, "ConstInit constructor bodies may contain only straight-line assignments to instance fields.");
                    return null;
                }
                var field = fields.FirstOrDefault(candidate => candidate.Name == fieldName);
                if (field is null || !assigned.TryAdd(field, null!))
                {
                    Report(model, left, $"ConstInit constructor field '{fieldName}' is missing, static, or assigned more than once.");
                    return null;
                }
                var rewritten = RewriteParameters(right, replacements);
                var constructorAnalyzer = new BoundBodyBinder(services, constructor, temporaryPrefix: "_ci_ctor");
                var fieldValue = EvaluateExpression(rewritten, field.Type, constructorAnalyzer, model, services, bodyByMethod, active);
                if (fieldValue is null)
                    return null;
                assigned[field] = fieldValue;
            }
            if (assigned.Count != fields.Length)
            {
                var missing = fields.First(field => !assigned.ContainsKey(field));
                Report(model, constructor.Syntax ?? creation, $"ConstInit constructor must assign field '{missing.Name}' exactly once.");
                return null;
            }
            return new ConstDataAggregate(target, fields.Select(field => assigned[field]).ToImmutableArray());
        }
        finally
        {
            active.Remove(constructor);
        }
    }

    private static bool TryFieldName(ExpressionSyntax expression, out string name)
    {
        switch (expression)
        {
            case NameExpressionSyntax direct:
                name = direct.Name;
                return true;
            case MemberAccessExpressionSyntax { Receiver: ThisExpressionSyntax, Name: var member }:
                name = member;
                return true;
            default:
                name = string.Empty;
                return false;
        }
    }

    private static ExpressionSyntax RewriteParameters(ExpressionSyntax expression, IReadOnlyDictionary<string, ExpressionSyntax> replacements) => expression switch
    {
        NameExpressionSyntax name when replacements.TryGetValue(name.Name, out var replacement) => replacement,
        ParenthesizedExpressionSyntax parenthesized => parenthesized with { Expression = RewriteParameters(parenthesized.Expression, replacements) },
        UnaryExpressionSyntax unary => unary with { Operand = RewriteParameters(unary.Operand, replacements) },
        BinaryExpressionSyntax binary => binary with
        {
            Left = RewriteParameters(binary.Left, replacements),
            Right = RewriteParameters(binary.Right, replacements),
        },
        CastExpressionSyntax cast => cast with { Expression = RewriteParameters(cast.Expression, replacements) },
        NewExpressionSyntax creation => creation with
        {
            Arguments = creation.Arguments.Select(argument => argument with
            {
                Expression = RewriteParameters(argument.Expression, replacements),
            }).ToImmutableArray(),
            ArrayLength = creation.ArrayLength is null ? null : RewriteParameters(creation.ArrayLength, replacements),
        },
        _ => expression,
    };

    private static bool IsAllowedConstantExpression(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        NameExpressionSyntax => true,
        MemberAccessExpressionSyntax member => IsAllowedConstantExpression(member.Receiver),
        ParenthesizedExpressionSyntax parenthesized => IsAllowedConstantExpression(parenthesized.Expression),
        UnaryExpressionSyntax unary => IsAllowedConstantExpression(unary.Operand),
        BinaryExpressionSyntax binary => IsAllowedConstantExpression(binary.Left) && IsAllowedConstantExpression(binary.Right),
        CastExpressionSyntax cast => IsAllowedConstantExpression(cast.Expression),
        SizeOfExpressionSyntax or AlignOfExpressionSyntax or OffsetOfExpressionSyntax => true,
        NewExpressionSyntax { ArrayLength: null } => true,
        _ => false,
    };

    private static ConstDataValue Zero(CType type) => type.Kind switch
    {
        CTypeKind.Struct when type.Symbol is not null => new ConstDataAggregate(type,
            type.Symbol.Fields.Where(field => !field.IsStatic && !field.IsBitView).Select(field => Zero(field.Type)).ToImmutableArray()),
        CTypeKind.InlineArray => new ConstDataScalar(type, "{0}"),
        _ => new ConstDataScalar(type, "0"),
    };

    private static void Report(CompilationModel model, SyntaxNode syntax, string message) =>
        model.Diagnostics.Add("CT2218", message, syntax.Source, syntax.Span);
}
