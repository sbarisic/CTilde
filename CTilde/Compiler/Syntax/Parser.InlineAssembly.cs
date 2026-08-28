using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class Parser
{
    private AssemblyFunctionBodySyntax ParseAssemblyFunctionBody()
    {
        var start = Current.Span.Start;
        ParseInlineAssemblyParts(out var operands, out var clobbers, out var body, out var close);
        var bodyText = body.Value as string ?? body.Text;
        return new AssemblyFunctionBodySyntax(
            _source,
            TextSpan.FromBounds(start, close.Span.End),
            operands,
            clobbers,
            body.Span,
            bodyText,
            FindInlineAssemblyReferences(bodyText, body.Span.Start, operands));
    }

    private InlineAssemblyStatementSyntax ParseInlineAssembly(ImmutableArray<AttributeSyntax> attributes)
    {
        var start = attributes.IsDefaultOrEmpty ? Current.Span.Start : attributes[0].Span.Start;
        Match(SyntaxKind.AsmKeyword);
        ParseInlineAssemblyParts(out var operands, out var clobbers, out var body, out var close);
        var bodyText = body.Value as string ?? body.Text;
        return new InlineAssemblyStatementSyntax(
            _source,
            TextSpan.FromBounds(start, close.Span.End),
            attributes,
            operands,
            clobbers,
            body.Span,
            bodyText,
            FindInlineAssemblyReferences(bodyText, body.Span.Start, operands));
    }

    private void ParseInlineAssemblyParts(
        out ImmutableArray<InlineAssemblyOperandSyntax> operandsResult,
        out ImmutableArray<string> clobbersResult,
        out SyntaxToken body,
        out SyntaxToken close)
    {
        var operands = ImmutableArray.CreateBuilder<InlineAssemblyOperandSyntax>();
        var clobbers = ImmutableArray.CreateBuilder<string>();
        if (Current.Kind == SyntaxKind.OpenParenToken)
        {
            NextToken();
            while (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
            {
                if (Current.Kind == SyntaxKind.ClobberKeyword)
                    ParseInlineAssemblyClobbers(clobbers);
                else
                    operands.Add(ParseInlineAssemblyOperand());
                if (Current.Kind != SyntaxKind.CommaToken)
                    break;
                NextToken();
            }
            Match(SyntaxKind.CloseParenToken);
        }
        Match(SyntaxKind.OpenBraceToken);
        body = Match(SyntaxKind.AsmTextToken);
        close = Match(SyntaxKind.CloseBraceToken);
        operandsResult = operands.ToImmutable();
        clobbersResult = clobbers.ToImmutable();
    }

    private void ParseInlineAssemblyClobbers(ImmutableArray<string>.Builder clobbers)
    {
        NextToken();
        Match(SyntaxKind.OpenParenToken);
        while (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
        {
            var clobber = Match(SyntaxKind.StringToken);
            clobbers.Add(clobber.Value as string ?? string.Empty);
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        Match(SyntaxKind.CloseParenToken);
    }

    private InlineAssemblyOperandSyntax ParseInlineAssemblyOperand()
    {
        var start = Current.Span.Start;
        var kind = Current.Kind switch
        {
            SyntaxKind.InKeyword => InlineAssemblyOperandKind.Input,
            SyntaxKind.OutKeyword => InlineAssemblyOperandKind.Output,
            SyntaxKind.RefKeyword => InlineAssemblyOperandKind.InputOutput,
            _ => InlineAssemblyOperandKind.Input,
        };
        if (Current.Kind is SyntaxKind.InKeyword or SyntaxKind.OutKeyword or SyntaxKind.RefKeyword)
            NextToken();
        else
            Report("CT0110", "Expected an asm operand role ('in', 'out', or 'ref') or clobber.", Current);
        string? constraint = null;
        if (Current.Kind == SyntaxKind.OpenParenToken)
        {
            NextToken();
            var constraintToken = Match(SyntaxKind.StringToken);
            constraint = constraintToken.Value as string ?? string.Empty;
            Match(SyntaxKind.CloseParenToken);
        }
        var variable = Match(SyntaxKind.IdentifierToken);
        var variableName = variable.Value as string ?? variable.Text.TrimStart('@');
        var name = variableName;
        var end = variable.Span.End;
        if (Current.Kind == SyntaxKind.AsKeyword)
        {
            NextToken();
            var alias = Match(SyntaxKind.IdentifierToken);
            name = alias.Value as string ?? alias.Text.TrimStart('@');
            end = alias.Span.End;
        }
        var expression = new NameExpressionSyntax(_source, variable.Span, variableName);
        return new InlineAssemblyOperandSyntax(_source, TextSpan.FromBounds(start, end), kind, constraint, expression, name);
    }

    private static ImmutableArray<InlineAssemblyReferenceSyntax> FindInlineAssemblyReferences(
        string body,
        int bodyStart,
        IEnumerable<InlineAssemblyOperandSyntax> operandSequence)
    {
        var references = ImmutableArray.CreateBuilder<InlineAssemblyReferenceSyntax>();
        var operands = operandSequence.ToArray();
        var aliases = operands.Select((operand, index) => (operand.Name, Index: index))
            .OrderByDescending(item => item.Name.Length).ToArray();
        char quote = '\0';
        var escaped = false;
        for (var position = 0; position < body.Length; position++)
        {
            var current = body[position];
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (current == '\\')
                    escaped = true;
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            foreach (var alias in aliases)
            {
                if (alias.Name.Length == 0 || position + alias.Name.Length > body.Length ||
                    !body.AsSpan(position, alias.Name.Length).SequenceEqual(alias.Name.AsSpan()))
                    continue;
                var before = position == 0 ? '\0' : body[position - 1];
                var after = position + alias.Name.Length == body.Length ? '\0' : body[position + alias.Name.Length];
                if (IsAsmIdentifierPart(before) || before is '%' or '$' or '.' || IsAsmIdentifierPart(after))
                    continue;
                references.Add(new InlineAssemblyReferenceSyntax(
                    operands[alias.Index].Source,
                    new TextSpan(bodyStart + position, alias.Name.Length),
                    alias.Name,
                    alias.Index));
                position += alias.Name.Length - 1;
                break;
            }
        }
        return references.ToImmutable();
    }

    private static bool IsAsmIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}
