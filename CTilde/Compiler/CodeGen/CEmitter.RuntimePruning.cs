using System.Text;
using System.Text.RegularExpressions;

namespace CTilde;

internal sealed partial class CEmitter
{
    private static readonly Regex PrivateRuntimeFunctionPattern = new(
        @"\bstatic(?:\s+CT_[A-Z0-9_]+)*\s+[^;={}]*?\b(?<name>ct_[A-Za-z0-9_]+)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PrivateRuntimeDataPattern = new(
        @"^\s*static(?:\s+CT_UNUSED)?\s+.*?\b(?<name>ct_[A-Za-z0-9_]+)\s*(?:\[[^\]]*\]\s*)?(?:=|;)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RuntimeIdentifierPattern = new(
        @"\bct_[A-Za-z0-9_]+\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string PruneRuntimeHelpers(string prefix, string externalRoots)
    {
        var sanitized = SanitizeGeneratedC(prefix);
        var spans = FindPrivateRuntimeSymbolSpans(prefix, sanitized);
        var candidateNames = spans.Where(span => span.IsDefinition)
            .Select(span => span.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (candidateNames.Count == 0)
            return prefix;

        var candidateSpans = spans.Where(span => candidateNames.Contains(span.Name)).ToArray();
        var rootText = prefix.ToCharArray();
        foreach (var span in candidateSpans)
            Array.Fill(rootText, ' ', span.Start, span.End - span.Start);

        var reachable = RuntimeIdentifiers(new string(rootText))
            .Concat(RuntimeIdentifiers(externalRoots))
            .Where(candidateNames.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var dependencies = candidateSpans.Where(span => span.IsDefinition)
            .GroupBy(span => span.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(span => RuntimeIdentifiers(prefix[span.Start..span.End]))
                    .Where(candidateNames.Contains)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var pending = new Queue<string>(reachable.OrderBy(name => name, StringComparer.Ordinal));
        while (pending.TryDequeue(out var name))
        {
            if (!dependencies.TryGetValue(name, out var called))
                continue;
            foreach (var dependency in called.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (reachable.Add(dependency))
                    pending.Enqueue(dependency);
            }
        }

        var result = new StringBuilder(prefix);
        foreach (var span in candidateSpans.Where(span => !reachable.Contains(span.Name)).OrderByDescending(span => span.Start))
            result.Remove(span.Start, span.End - span.Start);
        return result.ToString();
    }

    private static IReadOnlyList<RuntimeSymbolSpan> FindPrivateRuntimeSymbolSpans(string source, string sanitized)
    {
        var result = new List<RuntimeSymbolSpan>();
        var lineStart = 0;
        var braceDepth = 0;
        while (lineStart < sanitized.Length)
        {
            var lineEnd = sanitized.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = sanitized.Length;
            var line = sanitized[lineStart..lineEnd];
            if (braceDepth == 0)
            {
                var match = PrivateRuntimeFunctionPattern.Match(line);
                if (match.Success)
                {
                    var name = match.Groups["name"].Value;
                    var openParenthesis = lineStart + match.Index + match.Length - 1;
                    var closeParenthesis = FindMatching(sanitized, openParenthesis, '(', ')');
                    if (closeParenthesis >= 0)
                    {
                        var next = SkipWhitespace(sanitized, closeParenthesis + 1);
                        if (next < sanitized.Length && sanitized[next] == ';')
                        {
                            result.Add(new RuntimeSymbolSpan(name, lineStart, EndOfLine(source, next + 1), false));
                        }
                        else if (next < sanitized.Length && sanitized[next] == '{')
                        {
                            var closeBrace = FindMatching(sanitized, next, '{', '}');
                            if (closeBrace >= 0)
                            {
                                result.Add(new RuntimeSymbolSpan(name, lineStart, EndOfLine(source, closeBrace + 1), true));
                                lineEnd = EndOfLine(sanitized, closeBrace + 1) - 1;
                            }
                        }
                    }
                }
                else
                {
                    var data = PrivateRuntimeDataPattern.Match(line);
                    if (data.Success)
                    {
                        var declarationEnd = FindDeclarationEnd(sanitized, lineStart);
                        if (declarationEnd >= 0)
                        {
                            result.Add(new RuntimeSymbolSpan(
                                data.Groups["name"].Value,
                                lineStart,
                                EndOfLine(source, declarationEnd + 1),
                                true));
                            lineEnd = EndOfLine(sanitized, declarationEnd + 1) - 1;
                        }
                    }
                }
            }

            for (var index = lineStart; index < lineEnd; index++)
            {
                if (sanitized[index] == '{')
                    braceDepth++;
                else if (sanitized[index] == '}')
                    braceDepth--;
            }
            lineStart = lineEnd < sanitized.Length ? lineEnd + 1 : sanitized.Length;
        }
        return result;
    }

    private static int FindDeclarationEnd(string text, int start)
    {
        var parentheses = 0;
        var braces = 0;
        var brackets = 0;
        for (var index = start; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case ';' when parentheses == 0 && braces == 0 && brackets == 0:
                    return index;
            }
        }
        return -1;
    }

    private static int FindMatching(string text, int start, char open, char close)
    {
        var depth = 0;
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == open)
                depth++;
            else if (text[index] == close && --depth == 0)
                return index;
        }
        return -1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        return start;
    }

    private static int EndOfLine(string text, int start)
    {
        var newline = text.IndexOf('\n', start);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static IEnumerable<string> RuntimeIdentifiers(string source)
    {
        var sanitized = SanitizeGeneratedC(source);
        foreach (Match match in RuntimeIdentifierPattern.Matches(sanitized))
            yield return match.Value;
    }

    private static string SanitizeGeneratedC(string source)
    {
        var result = source.ToCharArray();
        var state = CScanState.Code;
        for (var index = 0; index < result.Length; index++)
        {
            var current = result[index];
            var next = index + 1 < result.Length ? result[index + 1] : '\0';
            switch (state)
            {
                case CScanState.Code when current == '/' && next == '/':
                    result[index] = result[index + 1] = ' ';
                    index++;
                    state = CScanState.LineComment;
                    break;
                case CScanState.Code when current == '/' && next == '*':
                    result[index] = result[index + 1] = ' ';
                    index++;
                    state = CScanState.BlockComment;
                    break;
                case CScanState.Code when current == '"':
                    result[index] = ' ';
                    state = CScanState.String;
                    break;
                case CScanState.Code when current == '\'':
                    result[index] = ' ';
                    state = CScanState.Character;
                    break;
                case CScanState.LineComment:
                    if (current == '\n')
                        state = CScanState.Code;
                    else
                        result[index] = ' ';
                    break;
                case CScanState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        state = CScanState.Code;
                    }
                    else if (current != '\n' && current != '\r')
                    {
                        result[index] = ' ';
                    }
                    break;
                case CScanState.String:
                case CScanState.Character:
                    var terminator = state == CScanState.String ? '"' : '\'';
                    if (current == '\\' && next != '\0')
                    {
                        result[index] = ' ';
                        if (next != '\n' && next != '\r')
                            result[index + 1] = ' ';
                        index++;
                    }
                    else
                    {
                        if (current == terminator)
                            state = CScanState.Code;
                        if (current != '\n' && current != '\r')
                            result[index] = ' ';
                    }
                    break;
            }
        }
        return new string(result);
    }

    private sealed record RuntimeSymbolSpan(string Name, int Start, int End, bool IsDefinition);

    private enum CScanState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        Character
    }
}
