using System.Collections.Immutable;
using System.Text;

namespace CTilde;

public sealed record CTildeFormattingOptions(int IndentSize = 4, int MaxLineLength = 120, string NewLine = "\n");

public static class CTildeFormatter
{
    public static string Format(SourceText source, CTildeFormattingOptions? options = null) =>
        Format(SyntaxTree.Parse(source), options);

    public static string Format(SyntaxTree tree, CTildeFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        options ??= new CTildeFormattingOptions();
        if (options.IndentSize <= 0 || options.MaxLineLength < 40 || options.NewLine.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (tree.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            tree.SkippedTokens.Length != 0 || tree.Tokens.Any(token => token.IsMissing))
            throw new InvalidOperationException("C~ source must parse without errors before it can be formatted.");

        var tokens = tree.Tokens.Where(token => token.Kind != SyntaxKind.EndOfFileToken).ToImmutableArray();
        var facts = FormattingFacts.Create(tree.Root, tokens);
        var writer = new FormattingWriter(options);
        SyntaxToken? previous = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var next = index + 1 < tokens.Length ? tokens[index + 1] : null;
            if (facts.SwitchIndentStops.Contains(token.Span.Start))
                writer.Indent = Math.Max(0, writer.Indent - 1);
            if (facts.BlankLineStarts.Contains(token.Span.Start))
                writer.EnsureBlankLine();
            else if (facts.NewLineStarts.Contains(token.Span.Start))
                writer.EnsureContinuationLine();
            writer.WriteTrivia(token.LeadingTrivia);

            if (token.Kind == SyntaxKind.AsmTextToken)
            {
                writer.WriteRaw(token.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                writer.WriteTrivia(token.TrailingTrivia);
                previous = token;
                continue;
            }

            switch (token.Kind)
            {
                case SyntaxKind.OpenBraceToken:
                    writer.EnsureNewLine();
                    writer.WriteToken("{", false);
                    if (next?.Kind != SyntaxKind.AsmTextToken)
                        writer.NewLine();
                    writer.Indent++;
                    break;
                case SyntaxKind.CloseBraceToken:
                    writer.Indent = Math.Max(0, writer.Indent - 1);
                    if (previous?.Kind != SyntaxKind.AsmTextToken)
                        writer.EnsureNewLine();
                    writer.WriteToken("}", false);
                    if (next?.Kind is not SyntaxKind.SemicolonToken and not SyntaxKind.CommaToken and
                        not SyntaxKind.CloseParenToken and not SyntaxKind.CloseBracketToken)
                        writer.NewLine();
                    break;
                case SyntaxKind.SemicolonToken:
                    writer.WriteToken(";", false);
                    if (facts.ForHeaderSemicolons.Contains(token.Span.Start))
                        writer.Space();
                    else
                        writer.NewLine();
                    break;
                case SyntaxKind.CommaToken:
                    writer.WriteToken(",", false);
                    if (facts.LineBreakAfter.Contains(token.Span.Start))
                        writer.NewLine();
                    else if (writer.ShouldWrap && !facts.NoWrapCommas.Contains(token.Span.Start))
                        writer.NewLine(1);
                    else
                        writer.Space();
                    break;
                case SyntaxKind.ColonToken:
                    if (!facts.LineBreakAfter.Contains(token.Span.Start))
                        writer.Space();
                    writer.WriteToken(":", false);
                    if (facts.LineBreakAfter.Contains(token.Span.Start))
                    {
                        writer.NewLine();
                        writer.Indent++;
                    }
                    else
                        writer.Space();
                    break;
                case SyntaxKind.OpenParenToken:
                    writer.WriteToken("(", NeedsSpaceBeforeOpenParen(previous));
                    break;
                case SyntaxKind.CloseParenToken:
                    writer.TrimSpace();
                    writer.WriteToken(")", false);
                    break;
                case SyntaxKind.OpenBracketToken:
                    writer.WriteToken("[", false);
                    break;
                case SyntaxKind.CloseBracketToken:
                    writer.TrimSpace();
                    writer.WriteToken("]", false);
                    if (facts.AttributeEnds.Contains(token.Span.End))
                        writer.NewLine(facts.ContinuationAttributeEnds.Contains(token.Span.End) ? 1 : 0);
                    break;
                case SyntaxKind.DotToken:
                    writer.TrimSpace();
                    writer.WriteToken(".", false);
                    break;
                default:
                    var isOperator = facts.SpacedOperators.Contains(token.Span.Start);
                    if (isOperator)
                    {
                        if (writer.ShouldWrap)
                            writer.NewLine(1);
                        writer.Space();
                        writer.WriteToken(token.Text, false);
                        writer.Space();
                    }
                    else
                    {
                        var space = NeedsSpace(previous, token, facts);
                        writer.WriteToken(token.Text, space);
                    }
                    break;
            }
            writer.WriteTrivia(token.TrailingTrivia);
            previous = token;
        }

        var endOfFile = tree.Tokens.FirstOrDefault(token => token.Kind == SyntaxKind.EndOfFileToken);
        if (endOfFile is not null)
        {
            writer.WriteTrivia(endOfFile.LeadingTrivia);
            writer.WriteTrivia(endOfFile.TrailingTrivia);
        }
        writer.EnsureNewLine();
        return writer.ToString();
    }

    private static bool NeedsSpaceBeforeOpenParen(SyntaxToken? previous) => previous?.Kind is
        SyntaxKind.IfKeyword or SyntaxKind.ForKeyword or SyntaxKind.ForeachKeyword or SyntaxKind.WhileKeyword or
        SyntaxKind.SwitchKeyword or SyntaxKind.CatchKeyword or SyntaxKind.LockKeyword or SyntaxKind.StaticKeyword;

    private static bool NeedsSpace(SyntaxToken? previous, SyntaxToken current, FormattingFacts facts)
    {
        if (previous is null)
            return false;
        if (facts.NoSpaceAfter.Contains(previous.Span.Start))
            return false;
        if (previous.Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.DotToken)
            return false;
        if (current.Kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.DotToken or
            SyntaxKind.CommaToken or SyntaxKind.SemicolonToken or SyntaxKind.ColonToken)
            return false;
        if (current.Kind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken ||
            previous.Kind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
            return false;
        if (facts.UnaryOperators.Contains(previous.Span.Start) || facts.UnaryOperators.Contains(current.Span.Start))
            return facts.UnaryOperators.Contains(current.Span.Start) && IsWord(previous.Kind);
        if (previous.Kind is SyntaxKind.GreaterToken or SyntaxKind.GreaterGreaterToken && IsWord(current.Kind))
            return true;
        if (current.Kind is SyntaxKind.LessToken or SyntaxKind.GreaterToken ||
            previous.Kind is SyntaxKind.LessToken or SyntaxKind.GreaterToken)
            return false;
        if (previous.Kind == SyntaxKind.OperatorKeyword)
            return true;
        if (current.Kind == SyntaxKind.OpenBraceToken || previous.Kind == SyntaxKind.CloseBraceToken)
            return true;
        return IsWord(previous.Kind) && IsWord(current.Kind) ||
            previous.Kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken && IsWord(current.Kind) ||
            IsWord(previous.Kind) && current.Kind == SyntaxKind.OpenBraceToken ||
            previous.Kind == SyntaxKind.StarToken && IsWord(current.Kind);
    }

    private static bool IsWord(SyntaxKind kind) => kind is SyntaxKind.IdentifierToken or SyntaxKind.NumberToken or
        SyntaxKind.StringToken or SyntaxKind.CharacterToken or SyntaxKind.RuneToken || kind.ToString().EndsWith("Keyword", StringComparison.Ordinal);

    private sealed class FormattingWriter(CTildeFormattingOptions options)
    {
        private readonly StringBuilder _builder = new();
        private bool _lineStart = true;
        private int _column;
        private int _consecutiveNewLines;
        private int _nextLineExtraIndent;
        public int Indent { get; set; }
        public bool ShouldWrap => _column >= options.MaxLineLength - 40;

        public void WriteTrivia(ImmutableArray<SyntaxTrivia> trivia)
        {
            var sawEndOfLine = false;
            foreach (var item in trivia)
            {
                if (item.Kind == SyntaxTriviaKind.EndOfLine)
                {
                    sawEndOfLine = true;
                    continue;
                }
                if (item.Kind == SyntaxTriviaKind.Whitespace)
                    continue;
                var text = item.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
                if (item.Kind is SyntaxTriviaKind.SingleLineComment or SyntaxTriviaKind.DocumentationComment)
                {
                    if (!_lineStart && !sawEndOfLine)
                        Space();
                    else
                        EnsureNewLine();
                    WriteRaw(text);
                    NewLine();
                }
                else
                {
                    if (!_lineStart)
                        Space();
                    WriteRaw(text);
                    if (text.Contains('\n'))
                        NewLine();
                    else
                        Space();
                }
                sawEndOfLine = false;
            }
        }

        public void WriteToken(string text, bool leadingSpace)
        {
            if (leadingSpace)
                Space();
            WriteIndent();
            _builder.Append(text);
            _column += text.Length;
            _consecutiveNewLines = 0;
        }

        public void WriteRaw(string text)
        {
            WriteIndent();
            _builder.Append(text);
            var lastNewLine = text.LastIndexOf('\n');
            if (lastNewLine >= 0)
            {
                _lineStart = lastNewLine == text.Length - 1;
                _column = _lineStart ? 0 : text.Length - lastNewLine - 1;
            }
            else
            {
                _column += text.Length;
                _lineStart = false;
            }
            _consecutiveNewLines = 0;
        }

        public void Space()
        {
            if (_lineStart || _builder.Length == 0 || _builder[^1] is ' ' or '\n')
                return;
            _builder.Append(' ');
            _column++;
        }

        public void TrimSpace()
        {
            while (_builder.Length > 0 && _builder[^1] == ' ')
            {
                _builder.Length--;
                _column--;
            }
        }

        public void NewLine(int continuationIndent = 0)
        {
            TrimSpace();
            if (_builder.Length == 0 || _builder[^1] != '\n')
                _builder.Append(options.NewLine);
            _lineStart = true;
            _column = 0;
            _consecutiveNewLines = Math.Min(2, _consecutiveNewLines + 1);
            if (continuationIndent > 0)
            {
                _builder.Append(' ', (Indent + continuationIndent) * options.IndentSize);
                _column = (Indent + continuationIndent) * options.IndentSize;
                _lineStart = false;
            }
        }

        public void EnsureNewLine()
        {
            if (!_lineStart)
                NewLine();
        }

        public void EnsureBlankLine()
        {
            EnsureNewLine();
            if (_consecutiveNewLines < 2)
            {
                _builder.Append(options.NewLine);
                _consecutiveNewLines = 2;
            }
        }

        public void EnsureContinuationLine()
        {
            EnsureNewLine();
            _nextLineExtraIndent = 1;
        }

        private void WriteIndent()
        {
            if (!_lineStart)
                return;
            var width = (Indent + _nextLineExtraIndent) * options.IndentSize;
            _builder.Append(' ', width);
            _column = width;
            _nextLineExtraIndent = 0;
            _lineStart = false;
        }

        public override string ToString() => _builder.ToString();
    }

    private sealed record FormattingFacts(
        HashSet<int> AttributeEnds,
        HashSet<int> BlankLineStarts,
        HashSet<int> NewLineStarts,
        HashSet<int> ForHeaderSemicolons,
        HashSet<int> LineBreakAfter,
        HashSet<int> SpacedOperators,
        HashSet<int> UnaryOperators,
        HashSet<int> NoSpaceAfter,
        HashSet<int> SwitchIndentStops,
        HashSet<int> ContinuationAttributeEnds,
        HashSet<int> NoWrapCommas)
    {
        public static FormattingFacts Create(CompilationUnitSyntax root, ImmutableArray<SyntaxToken> tokens)
        {
            var facts = new FormattingFacts([], [], [], [], [], [], [], [], [], [], []);
            Visit(root, facts, tokens);
            if (root.Usings.Length > 0 && root.Namespace is not null)
                AddBoundaryAfter(root.Usings[^1].Span.End, root.Namespace.Span.End, facts.BlankLineStarts, tokens);
            for (var index = 1; index < root.Types.Length; index++)
                AddBoundaryAfter(root.Types[index - 1].Span.End, root.Types[index].Span.End, facts.BlankLineStarts, tokens);
            if (root.Types.Length > 0 && root.Namespace is not null)
                AddBoundaryAfter(root.Namespace.Span.End, root.Types[0].Span.End, facts.BlankLineStarts, tokens);
            else if (root.Types.Length > 0 && root.Usings.Length > 0)
                AddBoundaryAfter(root.Usings[^1].Span.End, root.Types[0].Span.End, facts.BlankLineStarts, tokens);
            return facts;
        }

        private static void Visit(SyntaxNode node, FormattingFacts facts, ImmutableArray<SyntaxToken> tokens)
        {
            if (node is AttributeSyntax attribute)
                facts.AttributeEnds.Add(attribute.Span.End);
            if (node is ParameterSyntax parameter)
            {
                foreach (var parameterAttribute in parameter.Attributes)
                    facts.ContinuationAttributeEnds.Add(parameterAttribute.Span.End);
                if (!parameter.Attributes.IsDefaultOrEmpty)
                    AddBoundaryAfter(parameter.Attributes[0].Span.Start, parameter.Attributes[0].Span.End,
                        facts.NewLineStarts, tokens);
            }
            if (node is TypeSyntax typeSyntax && !typeSyntax.TypeArguments.IsDefaultOrEmpty && typeSyntax.TypeArguments.Length > 1)
                for (var index = 0; index + 1 < typeSyntax.TypeArguments.Length; index++)
                    AddTokenBetween(typeSyntax.TypeArguments[index].Span.End, typeSyntax.TypeArguments[index + 1].Span.Start,
                        SyntaxKind.CommaToken, facts.NoWrapCommas, tokens);
            if (node is TypeDeclarationSyntax type)
            {
                for (var index = 1; index < type.Members.Length; index++)
                    if (type.Members[index - 1] is not FieldDeclarationSyntax || type.Members[index] is not FieldDeclarationSyntax)
                        AddBoundaryAfter(type.Members[index - 1].Span.End, type.Members[index].Span.End, facts.BlankLineStarts, tokens);
                for (var index = 0; index + 1 < type.EnumMembers.Length; index++)
                    AddTokenBetween(type.EnumMembers[index].Span.End, type.EnumMembers[index + 1].Span.Start, SyntaxKind.CommaToken, facts.LineBreakAfter, tokens);
            }
            if (node is ForStatementSyntax @for)
                foreach (var token in tokens.Where(token => token.Kind == SyntaxKind.SemicolonToken && token.Span.Start >= @for.Span.Start && token.Span.End <= @for.Body.Span.Start).Take(2))
                    facts.ForHeaderSemicolons.Add(token.Span.Start);
            if (node is SwitchLabelSyntax label)
                AddTokenBetween(label.Span.Start, label.Span.End, SyntaxKind.ColonToken, facts.LineBreakAfter, tokens);
            if (node is SwitchStatementSyntax @switch)
            {
                var labels = @switch.Sections.SelectMany(section => section.Labels).ToArray();
                for (var index = 1; index < labels.Length; index++)
                    AddBoundaryAfter(labels[index].Span.Start, labels[index].Span.End, facts.SwitchIndentStops, tokens);
                var close = tokens.LastOrDefault(token => token.Kind == SyntaxKind.CloseBraceToken && token.Span.End == @switch.Span.End);
                if (close is not null)
                    facts.SwitchIndentStops.Add(close.Span.Start);
            }
            if (node is BinaryExpressionSyntax binary)
                AddTokenBetween(binary.Left.Span.End, binary.Right.Span.Start, binary.OperatorKind, facts.SpacedOperators, tokens);
            if (node is AssignmentExpressionSyntax assignment)
                AddTokenBetween(assignment.Left.Span.End, assignment.Right.Span.Start, assignment.OperatorKind, facts.SpacedOperators, tokens);
            if (node is FieldDeclarationSyntax { Initializer: not null } field)
                AddTokenBetween(field.Name.Length + field.Span.Start, field.Initializer.Span.Start, SyntaxKind.EqualsToken, facts.SpacedOperators, tokens);
            if (node is LocalDeclarationStatementSyntax { Initializer: not null } local)
                AddTokenBetween(local.Span.Start, local.Initializer.Span.Start, SyntaxKind.EqualsToken, facts.SpacedOperators, tokens);
            if (node is UnaryExpressionSyntax unary)
                AddTokenBetween(unary.Span.Start, unary.Span.End, unary.OperatorKind, facts.UnaryOperators, tokens);
            if (node is CastExpressionSyntax cast)
                AddTokenBetween(cast.Span.Start, cast.Expression.Span.Start, SyntaxKind.CloseParenToken, facts.NoSpaceAfter, tokens);
            AddControlFlowBody(node, facts.NewLineStarts, tokens);
            foreach (var child in node.ChildNodesAndTokens())
                if (child.Node is not null)
                    Visit(child.Node, facts, tokens);
        }

        private static void AddTokenBetween(int start, int end, SyntaxKind kind, HashSet<int> destination, ImmutableArray<SyntaxToken> tokens)
        {
            var token = tokens.FirstOrDefault(candidate => candidate.Kind == kind && candidate.Span.Start >= start && candidate.Span.End <= end);
            if (token is not null)
                destination.Add(token.Span.Start);
        }

        private static void AddBoundaryAfter(int previousEnd, int currentEnd, HashSet<int> destination, ImmutableArray<SyntaxToken> tokens)
        {
            var token = tokens.FirstOrDefault(candidate => candidate.Span.Start >= previousEnd && candidate.Span.Start < currentEnd);
            if (token is not null)
                destination.Add(token.Span.Start);
        }

        private static void AddControlFlowBody(SyntaxNode node, HashSet<int> destination, ImmutableArray<SyntaxToken> tokens)
        {
            StatementSyntax? body = node switch
            {
                IfStatementSyntax { Then: not BlockStatementSyntax } value => value.Then,
                StaticIfStatementSyntax { Then: not BlockStatementSyntax } value => value.Then,
                WhileStatementSyntax { Body: not BlockStatementSyntax } value => value.Body,
                DoStatementSyntax { Body: not BlockStatementSyntax } value => value.Body,
                ForStatementSyntax { Body: not BlockStatementSyntax } value => value.Body,
                ForeachStatementSyntax { Body: not BlockStatementSyntax } value => value.Body,
                _ => null,
            };
            if (body is not null)
                AddBoundaryAfter(node.Span.Start, body.Span.End, destination, tokens.Where(token => token.Span.Start >= body.Span.Start).ToImmutableArray());
            var elseBody = node switch
            {
                IfStatementSyntax { Else: not null and not BlockStatementSyntax and not IfStatementSyntax } value => value.Else,
                StaticIfStatementSyntax { Else: not null and not BlockStatementSyntax and not StaticIfStatementSyntax } value => value.Else,
                _ => null,
            };
            if (elseBody is not null)
                AddBoundaryAfter(elseBody.Span.Start, elseBody.Span.End, destination, tokens.Where(token => token.Span.Start >= elseBody.Span.Start).ToImmutableArray());
        }
    }
}
