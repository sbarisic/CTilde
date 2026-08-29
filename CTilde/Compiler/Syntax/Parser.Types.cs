using System.Collections.Immutable;

namespace CTilde;

internal sealed partial class Parser
{
    private TypeSyntax ParseType(bool allowVar = false, bool allowInlineArray = true)
    {
        var start = Current.Span.Start;
        if (Current.Kind == SyntaxKind.DelegateKeyword)
            return ParseFunctionPointerType();
        string name;
        if (IsBuiltInType(Current.Kind) || allowVar && Current.Kind == SyntaxKind.VarKeyword)
            name = NextToken().Text;
        else
            name = ParseQualifiedName();
        var typeArguments = ImmutableArray<TypeSyntax>.Empty;
        if (Current.Kind == SyntaxKind.LessToken)
        {
            NextToken();
            var builder = ImmutableArray.CreateBuilder<TypeSyntax>();
            while (!AtTypeArgumentClose && Current.Kind != SyntaxKind.EndOfFileToken)
            {
                builder.Add(ParseGenericArgument());
                if (Current.Kind != SyntaxKind.CommaToken)
                    break;
                NextToken();
            }
            ConsumeTypeArgumentClose();
            typeArguments = builder.ToImmutable();
        }
        var pointerDepth = 0;
        while (Current.Kind == SyntaxKind.StarToken)
        {
            pointerDepth++;
            NextToken();
        }
        var isArray = false;
        ExpressionSyntax? inlineArrayLength = null;
        if (Current.Kind == SyntaxKind.OpenBracketToken && Peek(1).Kind == SyntaxKind.CloseBracketToken)
        {
            NextToken();
            NextToken();
            isArray = true;
        }
        else if (allowInlineArray && Current.Kind == SyntaxKind.OpenBracketToken)
        {
            NextToken();
            inlineArrayLength = ParseExpression();
            Match(SyntaxKind.CloseBracketToken);
        }
        return new TypeSyntax(_source, TextSpan.FromBounds(start, Peek(-1).Span.End), name, pointerDepth, isArray, TypeArguments: typeArguments, InlineArrayLength: inlineArrayLength);
    }

    private TypeSyntax ParseFunctionPointerType()
    {
        var start = Match(SyntaxKind.DelegateKeyword).Span.Start;
        Match(SyntaxKind.StarToken);
        Match(SyntaxKind.UnmanagedKeyword);
        Match(SyntaxKind.LessToken);
        var elements = ImmutableArray.CreateBuilder<FunctionPointerElementSyntax>();
        while (!AtTypeArgumentClose && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            var elementStart = Current.Span.Start;
            var passingKind = ParsePassingKind();
            var type = ParseType();
            elements.Add(new FunctionPointerElementSyntax(_source, TextSpan.FromBounds(elementStart, type.Span.End), passingKind, type));
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            NextToken();
        }
        var close = ConsumeTypeArgumentClose();
        if (elements.Count == 0)
            Report("CT0110", "A function-pointer signature requires a return type.", close);
        var signature = new FunctionPointerSignatureSyntax(_source, TextSpan.FromBounds(start, close.Span.End), elements.ToImmutable());
        return new TypeSyntax(_source, signature.Span, "delegate*", FunctionPointer: signature, TypeArguments: []);
    }

    private string ParseQualifiedName()
    {
        var first = Match(SyntaxKind.IdentifierToken);
        var parts = new List<string> { first.Text };
        while (Current.Kind == SyntaxKind.DotToken && Peek(1).Kind == SyntaxKind.IdentifierToken)
        {
            NextToken();
            parts.Add(NextToken().Text);
        }
        return string.Join('.', parts);
    }

    private bool LooksLikeLocalDeclaration()
    {
        var index = _position;
        if (_tokens[index].Kind is SyntaxKind.ConstKeyword or SyntaxKind.ReadonlyKeyword)
            index++;
        if (index >= _tokens.Length)
            return false;
        if (!ScanType(ref index, allowVar: true))
            return false;
        return index < _tokens.Length && _tokens[index].Kind == SyntaxKind.IdentifierToken;
    }

    private bool LooksLikeCast()
    {
        var index = _position + 1;
        if (index >= _tokens.Length)
            return false;
        if (!ScanType(ref index, allowVar: false))
            return false;
        return index < _tokens.Length && _tokens[index].Kind == SyntaxKind.CloseParenToken;
    }

    private bool LooksLikeExplicitInterfaceMember()
    {
        var index = _position;
        return ScanType(ref index, allowVar: false) && index + 2 < _tokens.Length &&
            _tokens[index].Kind == SyntaxKind.DotToken && _tokens[index + 1].Kind == SyntaxKind.IdentifierToken &&
            _tokens[index + 2].Kind == SyntaxKind.OpenParenToken;
    }

    private bool ScanType(ref int index, bool allowVar)
    {
        if (index >= _tokens.Length)
            return false;
        if (_tokens[index].Kind == SyntaxKind.DelegateKeyword)
        {
            if (index + 3 >= _tokens.Length || _tokens[index + 1].Kind != SyntaxKind.StarToken ||
                _tokens[index + 2].Kind != SyntaxKind.UnmanagedKeyword || _tokens[index + 3].Kind != SyntaxKind.LessToken)
                return false;
            index += 4;
            var depth = 1;
            while (index < _tokens.Length && depth != 0)
            {
                if (_tokens[index].Kind == SyntaxKind.LessToken)
                    depth++;
                else if (_tokens[index].Kind == SyntaxKind.GreaterToken)
                    depth--;
                else if (_tokens[index].Kind == SyntaxKind.GreaterGreaterToken)
                    depth -= 2;
                index++;
            }
            return depth == 0;
        }
        if (IsBuiltInType(_tokens[index].Kind) || allowVar && _tokens[index].Kind == SyntaxKind.VarKeyword)
            index++;
        else if (_tokens[index].Kind == SyntaxKind.IdentifierToken)
        {
            index++;
            while (index + 1 < _tokens.Length && _tokens[index].Kind == SyntaxKind.DotToken && _tokens[index + 1].Kind == SyntaxKind.IdentifierToken)
                index += 2;
        }
        else
            return false;
        if (index < _tokens.Length && _tokens[index].Kind == SyntaxKind.LessToken)
        {
            var depth = 0;
            do
            {
                if (_tokens[index].Kind == SyntaxKind.LessToken)
                    depth++;
                else if (_tokens[index].Kind == SyntaxKind.GreaterToken)
                    depth--;
                else if (_tokens[index].Kind == SyntaxKind.GreaterGreaterToken)
                    depth -= 2;
                index++;
            }
            while (index < _tokens.Length && depth > 0);
            if (depth != 0)
                return false;
        }
        while (index < _tokens.Length && _tokens[index].Kind == SyntaxKind.StarToken)
            index++;
        if (index + 1 < _tokens.Length && _tokens[index].Kind == SyntaxKind.OpenBracketToken && _tokens[index + 1].Kind == SyntaxKind.CloseBracketToken)
            index += 2;
        else if (index < _tokens.Length && _tokens[index].Kind == SyntaxKind.OpenBracketToken)
        {
            var depth = 1;
            index++;
            while (index < _tokens.Length && depth != 0)
            {
                if (_tokens[index].Kind == SyntaxKind.OpenBracketToken) depth++;
                else if (_tokens[index].Kind == SyntaxKind.CloseBracketToken) depth--;
                index++;
            }
            if (depth != 0) return false;
        }
        return true;
    }
}
