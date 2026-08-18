using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CTilde;

internal sealed record NumericLiteralValue(BigInteger Integer, bool IsUnsigned, float? FloatingPoint);

internal sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    private static readonly IReadOnlyDictionary<string, SyntaxKind> Keywords = new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
    {
        ["bool"] = SyntaxKind.BoolKeyword, ["break"] = SyntaxKind.BreakKeyword,
        ["byte"] = SyntaxKind.ByteKeyword, ["case"] = SyntaxKind.CaseKeyword,
        ["char"] = SyntaxKind.CharKeyword, ["class"] = SyntaxKind.ClassKeyword,
        ["const"] = SyntaxKind.ConstKeyword, ["continue"] = SyntaxKind.ContinueKeyword,
        ["default"] = SyntaxKind.DefaultKeyword, ["do"] = SyntaxKind.DoKeyword,
        ["else"] = SyntaxKind.ElseKeyword, ["enum"] = SyntaxKind.EnumKeyword,
        ["false"] = SyntaxKind.FalseKeyword, ["float"] = SyntaxKind.FloatKeyword,
        ["for"] = SyntaxKind.ForKeyword, ["foreach"] = SyntaxKind.ForeachKeyword,
        ["if"] = SyntaxKind.IfKeyword, ["in"] = SyntaxKind.InKeyword,
        ["int"] = SyntaxKind.IntKeyword, ["internal"] = SyntaxKind.InternalKeyword,
        ["namespace"] = SyntaxKind.NamespaceKeyword, ["new"] = SyntaxKind.NewKeyword,
        ["null"] = SyntaxKind.NullKeyword, ["private"] = SyntaxKind.PrivateKeyword,
        ["protected"] = SyntaxKind.ProtectedKeyword, ["public"] = SyntaxKind.PublicKeyword,
        ["readonly"] = SyntaxKind.ReadonlyKeyword, ["return"] = SyntaxKind.ReturnKeyword,
        ["sbyte"] = SyntaxKind.SbyteKeyword, ["sealed"] = SyntaxKind.SealedKeyword,
        ["short"] = SyntaxKind.ShortKeyword, ["static"] = SyntaxKind.StaticKeyword,
        ["string"] = SyntaxKind.StringKeyword, ["struct"] = SyntaxKind.StructKeyword,
        ["switch"] = SyntaxKind.SwitchKeyword, ["this"] = SyntaxKind.ThisKeyword,
        ["true"] = SyntaxKind.TrueKeyword, ["uint"] = SyntaxKind.UintKeyword,
        ["unsafe"] = SyntaxKind.UnsafeKeyword, ["ushort"] = SyntaxKind.UshortKeyword,
        ["using"] = SyntaxKind.UsingKeyword, ["var"] = SyntaxKind.VarKeyword,
        ["void"] = SyntaxKind.VoidKeyword, ["while"] = SyntaxKind.WhileKeyword,
        ["get"] = SyntaxKind.GetKeyword, ["set"] = SyntaxKind.SetKeyword,
    };

    private int _position;

    public ImmutableArray<SyntaxToken> Lex()
    {
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (true)
        {
            SkipTrivia();
            var token = LexToken();
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
            return Token(Keywords.GetValueOrDefault(text, SyntaxKind.IdentifierToken), start, _position - start, text);
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
            '(' => SyntaxKind.OpenParenToken, ')' => SyntaxKind.CloseParenToken,
            '{' => SyntaxKind.OpenBraceToken, '}' => SyntaxKind.CloseBraceToken,
            '[' => SyntaxKind.OpenBracketToken, ']' => SyntaxKind.CloseBracketToken,
            ';' => SyntaxKind.SemicolonToken, ':' => SyntaxKind.ColonToken,
            ',' => SyntaxKind.CommaToken, '.' => SyntaxKind.DotToken,
            '+' => SyntaxKind.PlusToken, '-' => SyntaxKind.MinusToken,
            '*' => SyntaxKind.StarToken, '/' => SyntaxKind.SlashToken,
            '%' => SyntaxKind.PercentToken, '&' => SyntaxKind.AmpersandToken,
            '|' => SyntaxKind.PipeToken, '^' => SyntaxKind.HatToken,
            '~' => SyntaxKind.TildeToken, '!' => SyntaxKind.BangToken,
            '=' => SyntaxKind.EqualsToken, '<' => SyntaxKind.LessToken,
            '>' => SyntaxKind.GreaterToken, _ => SyntaxKind.BadToken,
        };

        if (single == SyntaxKind.BadToken)
            diagnostics.Add("CT0001", $"Invalid character U+{(int)current:X4}.", source, new TextSpan(start, 1));
        return Token(single, start, 1);
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

        var isUnsigned = false;
        var hasSuffix = false;
        if (_position < source.Length && source[_position] is 'u' or 'U')
        {
            isUnsigned = true;
            hasSuffix = true;
            _position++;
        }
        else if (numberBase == 10 && _position < source.Length && source[_position] is 'f' or 'F')
        {
            floating = true;
            hasSuffix = true;
            _position++;
        }

        var text = source.Text[start.._position];
        var literalBody = hasSuffix ? text[..^1] : text;
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
                value = new NumericLiteralValue(BigInteger.Zero, false, result);
            }
            else
            {
                if (numberBase != 10)
                    digits = digits[2..];
                if (digits.Length == 0)
                    throw new FormatException();
                value = new NumericLiteralValue(ParseInteger(digits, numberBase), isUnsigned, null);
            }
            return Token(SyntaxKind.NumberToken, start, _position - start, value);
        }
        catch (FormatException)
        {
            diagnostics.Add("CT0002", $"Invalid numeric literal '{text}'.", source, new TextSpan(start, _position - start));
            return Token(SyntaxKind.NumberToken, start, _position - start, new NumericLiteralValue(BigInteger.Zero, false, null));
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
                '0' => '\0', 'a' => '\a', 'b' => '\b', 't' => '\t',
                'n' => '\n', 'v' => '\v', 'f' => '\f', 'r' => '\r',
                '"' => '"', '\'' => '\'', '\\' => '\\', _ => '\0',
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

    private void SkipTrivia()
    {
        while (_position < source.Length)
        {
            if (char.IsWhiteSpace(source[_position]))
            {
                _position++;
                continue;
            }
            if (Matches("//"))
            {
                _position += 2;
                while (_position < source.Length && source[_position] is not '\r' and not '\n')
                    _position++;
                continue;
            }
            if (Matches("/*"))
            {
                var start = _position;
                _position += 2;
                while (_position < source.Length && !Matches("*/"))
                    _position++;
                if (_position >= source.Length)
                    diagnostics.Add("CT0007", "Unterminated block comment.", source, new TextSpan(start, source.Length - start));
                else
                    _position += 2;
                continue;
            }
            return;
        }
    }

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
        new(kind, source, new TextSpan(start, length), source.Text.Substring(start, length), value);
}
