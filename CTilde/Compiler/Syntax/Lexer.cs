using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal enum IntegerLiteralSuffix
{
    None,
    Unsigned,
    Long,
    UnsignedLong,
}

internal sealed record NumericLiteralValue(BigInteger Integer, IntegerLiteralSuffix Suffix, float? FloatingPoint);

internal sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    private static readonly IReadOnlyDictionary<string, SyntaxKind> Keywords = new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
    {
        ["bool"] = SyntaxKind.BoolKeyword,
        ["as"] = SyntaxKind.AsKeyword,
        ["asm"] = SyntaxKind.AsmKeyword,
        ["base"] = SyntaxKind.BaseKeyword,
        ["break"] = SyntaxKind.BreakKeyword,
        ["byte"] = SyntaxKind.ByteKeyword,
        ["case"] = SyntaxKind.CaseKeyword,
        ["catch"] = SyntaxKind.CatchKeyword,
        ["clobber"] = SyntaxKind.ClobberKeyword,
        ["char"] = SyntaxKind.CharKeyword,
        ["class"] = SyntaxKind.ClassKeyword,
        ["const"] = SyntaxKind.ConstKeyword,
        ["continue"] = SyntaxKind.ContinueKeyword,
        ["default"] = SyntaxKind.DefaultKeyword,
        ["defer"] = SyntaxKind.DeferKeyword,
        ["delegate"] = SyntaxKind.DelegateKeyword,
        ["do"] = SyntaxKind.DoKeyword,
        ["else"] = SyntaxKind.ElseKeyword,
        ["enum"] = SyntaxKind.EnumKeyword,
        ["false"] = SyntaxKind.FalseKeyword,
        ["finally"] = SyntaxKind.FinallyKeyword,
        ["float"] = SyntaxKind.FloatKeyword,
        ["for"] = SyntaxKind.ForKeyword,
        ["foreach"] = SyntaxKind.ForeachKeyword,
        ["if"] = SyntaxKind.IfKeyword,
        ["in"] = SyntaxKind.InKeyword,
        ["int"] = SyntaxKind.IntKeyword,
        ["internal"] = SyntaxKind.InternalKeyword,
        ["is"] = SyntaxKind.IsKeyword,
        ["long"] = SyntaxKind.LongKeyword,
        ["nint"] = SyntaxKind.NintKeyword,
        ["nuint"] = SyntaxKind.NuintKeyword,
        ["namespace"] = SyntaxKind.NamespaceKeyword,
        ["new"] = SyntaxKind.NewKeyword,
        ["null"] = SyntaxKind.NullKeyword,
        ["object"] = SyntaxKind.ObjectKeyword,
        ["opaque"] = SyntaxKind.OpaqueKeyword,
        ["operator"] = SyntaxKind.OperatorKeyword,
        ["override"] = SyntaxKind.OverrideKeyword,
        ["out"] = SyntaxKind.OutKeyword,
        ["private"] = SyntaxKind.PrivateKeyword,
        ["protected"] = SyntaxKind.ProtectedKeyword,
        ["public"] = SyntaxKind.PublicKeyword,
        ["readonly"] = SyntaxKind.ReadonlyKeyword,
        ["ref"] = SyntaxKind.RefKeyword,
        ["return"] = SyntaxKind.ReturnKeyword,
        ["sbyte"] = SyntaxKind.SbyteKeyword,
        ["sealed"] = SyntaxKind.SealedKeyword,
        ["short"] = SyntaxKind.ShortKeyword,
        ["static"] = SyntaxKind.StaticKeyword,
        ["stackalloc"] = SyntaxKind.StackallocKeyword,
        ["string"] = SyntaxKind.StringKeyword,
        ["struct"] = SyntaxKind.StructKeyword,
        ["switch"] = SyntaxKind.SwitchKeyword,
        ["this"] = SyntaxKind.ThisKeyword,
        ["throw"] = SyntaxKind.ThrowKeyword,
        ["true"] = SyntaxKind.TrueKeyword,
        ["try"] = SyntaxKind.TryKeyword,
        ["uint"] = SyntaxKind.UintKeyword,
        ["ulong"] = SyntaxKind.UlongKeyword,
        ["unmanaged"] = SyntaxKind.UnmanagedKeyword,
        ["unsafe"] = SyntaxKind.UnsafeKeyword,
        ["ushort"] = SyntaxKind.UshortKeyword,
        ["using"] = SyntaxKind.UsingKeyword,
        ["var"] = SyntaxKind.VarKeyword,
        ["virtual"] = SyntaxKind.VirtualKeyword,
        ["void"] = SyntaxKind.VoidKeyword,
        ["while"] = SyntaxKind.WhileKeyword,
        ["get"] = SyntaxKind.GetKeyword,
        ["set"] = SyntaxKind.SetKeyword,
    };

    private int _position;
    private ImmutableArray<SyntaxTrivia> _leadingTrivia = [];
    private bool _pendingAsmBody;
    private bool _scanAsmBody;

    public ImmutableArray<SyntaxToken> Lex()
    {
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (true)
        {
            if (_scanAsmBody)
            {
                _leadingTrivia = [];
                tokens.Add(LexAsmText());
                _scanAsmBody = false;
                continue;
            }
            _leadingTrivia = LexTrivia(stopAfterEndOfLine: false);
            var token = LexToken();
            if (token.Kind != SyntaxKind.EndOfFileToken && !_scanAsmBody)
                token = token with { TrailingTrivia = LexTrivia(stopAfterEndOfLine: true) };
            tokens.Add(token);
            if (token.Kind == SyntaxKind.EndOfFileToken)
                return tokens.ToImmutable();
        }
    }

    private SyntaxToken LexToken()
    {
        if (_position >= source.Length)
            return Token(SyntaxKind.EndOfFileToken, _position, 0);

        var start = _position;
        var current = source[_position];

        if (current == '@' && IsIdentifierStart(PeekRune(1)))
        {
            _position++;
            ReadIdentifierRunes();
            return Token(SyntaxKind.IdentifierToken, start, _position - start, source.Text[(start + 1).._position]);
        }

        if (IsIdentifierStart(PeekRune()))
        {
            ReadIdentifierRunes();
            var text = source.Text[start.._position];
            var kind = Keywords.GetValueOrDefault(text, SyntaxKind.IdentifierToken);
            if (kind == SyntaxKind.AsmKeyword)
                _pendingAsmBody = true;
            return Token(kind, start, _position - start, text);
        }

        if (char.IsAsciiDigit(current))
            return LexNumber();

        if (current == '"')
            return LexQuoted('"', SyntaxKind.StringToken);
        if (current == '\'')
            return LexQuoted('\'', SyntaxKind.CharacterToken);

        foreach (var (text, kind) in MultiCharacterTokens)
        {
            if (Matches(text))
            {
                _position += text.Length;
                return Token(kind, start, text.Length);
            }
        }

        _position++;
        var single = current switch
        {
            '(' => SyntaxKind.OpenParenToken,
            ')' => SyntaxKind.CloseParenToken,
            '{' => SyntaxKind.OpenBraceToken,
            '}' => SyntaxKind.CloseBraceToken,
            '[' => SyntaxKind.OpenBracketToken,
            ']' => SyntaxKind.CloseBracketToken,
            ';' => SyntaxKind.SemicolonToken,
            ':' => SyntaxKind.ColonToken,
            ',' => SyntaxKind.CommaToken,
            '.' => SyntaxKind.DotToken,
            '+' => SyntaxKind.PlusToken,
            '-' => SyntaxKind.MinusToken,
            '*' => SyntaxKind.StarToken,
            '/' => SyntaxKind.SlashToken,
            '%' => SyntaxKind.PercentToken,
            '&' => SyntaxKind.AmpersandToken,
            '|' => SyntaxKind.PipeToken,
            '^' => SyntaxKind.HatToken,
            '~' => SyntaxKind.TildeToken,
            '!' => SyntaxKind.BangToken,
            '=' => SyntaxKind.EqualsToken,
            '<' => SyntaxKind.LessToken,
            '>' => SyntaxKind.GreaterToken,
            _ => SyntaxKind.BadToken,
        };

        if (_pendingAsmBody && single == SyntaxKind.OpenBraceToken)
            _scanAsmBody = true;
        else if (_pendingAsmBody && single is SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken)
            _pendingAsmBody = false;

        if (single == SyntaxKind.BadToken)
            diagnostics.Add("CT0001", $"Invalid character U+{(int)current:X4}.", source, new TextSpan(start, 1));
        return Token(single, start, 1);
    }

    private SyntaxToken LexAsmText()
    {
        var start = _position;
        var depth = 0;
        char quote = '\0';
        var escaped = false;
        while (_position < source.Length)
        {
            var current = source[_position];
            if (quote != '\0')
            {
                _position++;
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
                _position++;
                continue;
            }
            if (current == '{')
            {
                depth++;
                _position++;
                continue;
            }
            if (current == '}')
            {
                if (depth == 0)
                    break;
                depth--;
                _position++;
                continue;
            }
            _position++;
        }
        _pendingAsmBody = false;
        var text = source.Text[start.._position];
        return Token(SyntaxKind.AsmTextToken, start, _position - start, text);
    }

    private static readonly (string Text, SyntaxKind Kind)[] MultiCharacterTokens =
    [
        ("++", SyntaxKind.PlusPlusToken), ("--", SyntaxKind.MinusMinusToken),
        ("+=", SyntaxKind.PlusEqualsToken), ("-=", SyntaxKind.MinusEqualsToken),
        ("*=", SyntaxKind.StarEqualsToken), ("/=", SyntaxKind.SlashEqualsToken),
        ("%=", SyntaxKind.PercentEqualsToken), ("&&", SyntaxKind.AmpersandAmpersandToken),
        ("||", SyntaxKind.PipePipeToken), ("==", SyntaxKind.EqualsEqualsToken),
        ("!=", SyntaxKind.BangEqualsToken), ("<=", SyntaxKind.LessEqualsToken),
        (">=", SyntaxKind.GreaterEqualsToken), ("<<", SyntaxKind.LessLessToken),
        (">>", SyntaxKind.GreaterGreaterToken),
    ];

    private SyntaxToken LexNumber()
    {
        var start = _position;
        var numberBase = 10;
        var floating = false;

        if (Matches("0x") || Matches("0X"))
        {
            numberBase = 16;
            _position += 2;
            ReadDigits(numberBase);
        }
        else if (Matches("0b") || Matches("0B"))
        {
            numberBase = 2;
            _position += 2;
            ReadDigits(numberBase);
        }
        else
        {
            ReadDigits(10);
            if (_position < source.Length && source[_position] == '.' && _position + 1 < source.Length && char.IsAsciiDigit(source[_position + 1]))
            {
                floating = true;
                _position++;
                ReadDigits(10);
            }
        }

        var suffixStart = _position;
        while (_position < source.Length && char.IsAsciiLetter(source[_position]))
            _position++;

        var suffixText = source.Text[suffixStart.._position];
        var normalizedSuffix = suffixText.ToUpperInvariant();
        var integerSuffix = normalizedSuffix switch
        {
            "" => IntegerLiteralSuffix.None,
            "U" => IntegerLiteralSuffix.Unsigned,
            "L" => IntegerLiteralSuffix.Long,
            "UL" or "LU" => IntegerLiteralSuffix.UnsignedLong,
            _ => IntegerLiteralSuffix.None,
        };
        var floatSuffix = normalizedSuffix == "F";
        var validSuffix = floatSuffix
            ? numberBase == 10
            : normalizedSuffix is "" or "U" or "L" or "UL" or "LU";
        floating |= floatSuffix;

        var text = source.Text[start.._position];
        var literalBody = source.Text[start..suffixStart];
        var separatorBody = numberBase == 10 ? literalBody : literalBody[2..];
        if (separatorBody.StartsWith('_') || separatorBody.EndsWith('_') || separatorBody.Contains("__", StringComparison.Ordinal) || separatorBody.Contains("_.", StringComparison.Ordinal) || separatorBody.Contains("._", StringComparison.Ordinal))
            diagnostics.Add("CT0008", "Digit separators must appear between digits.", source, new TextSpan(start, _position - start));
        var digits = literalBody.Replace("_", string.Empty, StringComparison.Ordinal);
        try
        {
            NumericLiteralValue value;
            if (floating)
            {
                if (numberBase != 10 || !float.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result) || float.IsInfinity(result))
                    throw new FormatException();
                if (!validSuffix || !floatSuffix && suffixText.Length > 0)
                    throw new FormatException();
                value = new NumericLiteralValue(BigInteger.Zero, IntegerLiteralSuffix.None, result);
            }
            else
            {
                if (!validSuffix)
                    throw new FormatException();
                if (numberBase != 10)
                    digits = digits[2..];
                if (digits.Length == 0)
                    throw new FormatException();
                value = new NumericLiteralValue(ParseInteger(digits, numberBase), integerSuffix, null);
            }
            return Token(SyntaxKind.NumberToken, start, _position - start, value);
        }
        catch (FormatException)
        {
            diagnostics.Add("CT0002", $"Invalid numeric literal '{text}'.", source, new TextSpan(start, _position - start));
            return Token(SyntaxKind.NumberToken, start, _position - start, new NumericLiteralValue(BigInteger.Zero, IntegerLiteralSuffix.None, null));
        }
    }

    private static BigInteger ParseInteger(string digits, int numberBase)
    {
        var value = BigInteger.Zero;
        foreach (var character in digits)
        {
            var digit = character is >= '0' and <= '9' ? character - '0' : char.ToUpperInvariant(character) - 'A' + 10;
            if (digit < 0 || digit >= numberBase)
                throw new FormatException();
            value = value * numberBase + digit;
        }
        return value;
    }

    private SyntaxToken LexQuoted(char quote, SyntaxKind kind)
    {
        var start = _position++;
        var value = new StringBuilder();
        var terminated = false;
        while (_position < source.Length)
        {
            var character = source[_position++];
            if (character == quote)
            {
                terminated = true;
                break;
            }
            if (character is '\r' or '\n')
                break;
            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (_position >= source.Length)
                break;
            var escapePosition = _position - 1;
            var escape = source[_position++];
            var decoded = escape switch
            {
                '0' => '\0',
                'a' => '\a',
                'b' => '\b',
                't' => '\t',
                'n' => '\n',
                'v' => '\v',
                'f' => '\f',
                'r' => '\r',
                '"' => '"',
                '\'' => '\'',
                '\\' => '\\',
                _ => '\0',
            };
            if (escape == 'x')
            {
                if (_position + 1 < source.Length && IsHex(source[_position]) && IsHex(source[_position + 1]))
                {
                    decoded = (char)((HexValue(source[_position]) << 4) | HexValue(source[_position + 1]));
                    _position += 2;
                }
                else
                {
                    diagnostics.Add("CT0003", "A hexadecimal escape must contain exactly two hexadecimal digits.", source, new TextSpan(escapePosition, Math.Min(4, source.Length - escapePosition)));
                }
            }
            else if (decoded == '\0' && escape != '0')
            {
                diagnostics.Add("CT0004", $"Unknown escape sequence '\\{escape}'.", source, new TextSpan(escapePosition, 2));
                decoded = escape;
            }
            value.Append(decoded);
        }

        if (!terminated)
            diagnostics.Add("CT0005", "Unterminated quoted literal.", source, new TextSpan(start, Math.Max(1, _position - start)));

        if (kind == SyntaxKind.CharacterToken)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToString());
            if (bytes.Length != 1)
                diagnostics.Add("CT0006", "A character literal must contain exactly one UTF-8 code unit.", source, new TextSpan(start, _position - start));
            return Token(kind, start, _position - start, bytes.Length == 0 ? (byte)0 : bytes[0]);
        }

        return Token(kind, start, _position - start, value.ToString());
    }

    private ImmutableArray<SyntaxTrivia> LexTrivia(bool stopAfterEndOfLine)
    {
        var trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        while (_position < source.Length)
        {
            var start = _position;
            if (source[_position] is '\r' or '\n')
            {
                if (source[_position] == '\r' && _position + 1 < source.Length && source[_position + 1] == '\n')
                    _position += 2;
                else
                    _position++;
                trivia.Add(Trivia(SyntaxTriviaKind.EndOfLine, start));
                if (stopAfterEndOfLine)
                    break;
                continue;
            }
            if (char.IsWhiteSpace(source[_position]))
            {
                while (_position < source.Length && char.IsWhiteSpace(source[_position]) && source[_position] is not '\r' and not '\n')
                    _position++;
                trivia.Add(Trivia(SyntaxTriviaKind.Whitespace, start));
                continue;
            }
            if (Matches("///") && (_position + 3 >= source.Length || source[_position + 3] != '/'))
            {
                _position += 3;
                while (_position < source.Length && source[_position] is not '\r' and not '\n')
                    _position++;
                trivia.Add(Trivia(SyntaxTriviaKind.DocumentationComment, start));
                continue;
            }
            if (Matches("//"))
            {
                _position += 2;
                while (_position < source.Length && source[_position] is not '\r' and not '\n')
                    _position++;
                trivia.Add(Trivia(SyntaxTriviaKind.SingleLineComment, start));
                continue;
            }
            if (Matches("/*"))
            {
                _position += 2;
                while (_position < source.Length && !Matches("*/"))
                    _position++;
                if (_position >= source.Length)
                    diagnostics.Add("CT0007", "Unterminated block comment.", source, new TextSpan(start, source.Length - start));
                else
                    _position += 2;
                trivia.Add(Trivia(SyntaxTriviaKind.BlockComment, start));
                continue;
            }
            break;
        }
        return trivia.ToImmutable();
    }

    private SyntaxTrivia Trivia(SyntaxTriviaKind kind, int start) => new(kind, source, TextSpan.FromBounds(start, _position), source.Text[start.._position]);

    private void ReadDigits(int numberBase)
    {
        while (_position < source.Length)
        {
            var character = source[_position];
            if (character == '_' || numberBase == 10 && char.IsAsciiDigit(character) || numberBase == 16 && IsHex(character) || numberBase == 2 && character is '0' or '1')
                _position++;
            else
                return;
        }
    }

    private void ReadIdentifierRunes()
    {
        var first = true;
        while (_position < source.Length)
        {
            var rune = Rune.GetRuneAt(source.Text, _position);
            if (first ? !IsIdentifierStart(rune) : !IsIdentifierPart(rune))
                return;
            _position += rune.Utf16SequenceLength;
            first = false;
        }
    }

    private Rune PeekRune(int offset = 0)
    {
        var position = _position + offset;
        return position >= source.Length ? Rune.ReplacementChar : Rune.GetRuneAt(source.Text, position);
    }

    private static bool IsIdentifierStart(Rune rune) => rune.Value == '_' || Rune.IsLetter(rune);
    private static bool IsIdentifierPart(Rune rune) => IsIdentifierStart(rune) || Rune.IsDigit(rune);
    private bool Matches(string value) => _position + value.Length <= source.Length && source.Text.AsSpan(_position, value.Length).SequenceEqual(value);
    private static bool IsHex(char value) => char.IsAsciiHexDigit(value);
    private static int HexValue(char value) => value <= '9' ? value - '0' : char.ToUpperInvariant(value) - 'A' + 10;

    private SyntaxToken Token(SyntaxKind kind, int start, int length, object? value = null) =>
        new SyntaxToken(kind, source, new TextSpan(start, length), source.Text.Substring(start, length), value) { LeadingTrivia = _leadingTrivia };
}
