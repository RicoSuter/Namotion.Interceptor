using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Parses an expression by recursive descent. Throws a <see cref="FormatException"/> whose message reads
/// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
/// </summary>
internal sealed class ExpressionParser(string text, char pathSeparator)
{
    internal const int MaximumExpressionLength = 1000;
    internal const int MaximumNestingDepth = 32;

    // Boxed once, so that evaluating a literal allocates nothing.
    private static readonly object True = true;
    private static readonly object False = false;

    // Two-character operators first, so "<=" is not read as "<".
    private static readonly (string Symbol, ComparisonOperator Operator)[] Operators =
    [
        ("==", ComparisonOperator.Equal),
        ("!=", ComparisonOperator.NotEqual),
        ("<=", ComparisonOperator.LessOrEqual),
        (">=", ComparisonOperator.GreaterOrEqual),
        ("<", ComparisonOperator.Less),
        (">", ComparisonOperator.Greater)
    ];

    private static readonly SearchValues<char> DelimiterSymbols = SearchValues.Create("()\"'|&=!<>");

    private readonly List<string> _paths = [];
    private int _position;
    private int _nestingDepth;

    public ParsedExpression Parse()
    {
        if (text.Length > MaximumExpressionLength)
        {
            _position = MaximumExpressionLength;
            throw CreateError($"expression is longer than {MaximumExpressionLength} characters");
        }

        var body = ParseOr();
        var pipes = ImmutableArray<ExpressionPipe>.Empty;
        while (TryConsumePipeSeparator())
        {
            pipes = pipes.Add(ParsePipe());
        }

        SkipWhitespace();
        if (_position < text.Length)
        {
            ThrowIfArithmetic();
            throw CreateError($"unexpected '{text[_position]}'");
        }

        return new ParsedExpression(text, pathSeparator, body, pipes, [.. _paths]);
    }

    private ExpressionNode ParseOr()
    {
        var node = ParseAnd();
        while (TryConsumeSymbol("||") || TryConsumeKeyword("or"))
        {
            node = new OrNode(node, ParseAnd());
        }

        return node;
    }

    private ExpressionNode ParseAnd()
    {
        var node = ParseNot();
        while (TryConsumeSymbol("&&") || TryConsumeKeyword("and"))
        {
            node = new AndNode(node, ParseNot());
        }

        return node;
    }

    private ExpressionNode ParseNot()
    {
        SkipWhitespace();
        var notPosition = _position;
        if (!TryConsumeKeyword("not") && !TryConsumeBang())
        {
            return ParseComparison();
        }

        // A chain of '!' recurses once per character, so it counts against the nesting limit like a parenthesis.
        EnterNesting(notPosition);
        var operand = ParseNot();
        _nestingDepth--;
        return new NotNode(operand);
    }

    private ExpressionNode ParseComparison()
    {
        var left = ParseOperand(operatorText: null);
        if (ReadOperator() is not { } comparison)
        {
            return left;
        }

        return new ComparisonNode(left, comparison.Operator, ParseOperand(comparison.Text));
    }

    // The operator before a right operand, or null for an operand that no operator precedes.
    private ExpressionNode ParseOperand(string? operatorText)
    {
        SkipWhitespace();
        var start = _position;
        if (_position >= text.Length)
        {
            throw CreateError(ExpectedValue(operatorText));
        }

        var character = text[_position];
        if (character == '(')
        {
            return ParseGroup(start);
        }

        if (character is '"' or '\'')
        {
            return new LiteralNode(ReadString(character));
        }

        return ParseWord(start, operatorText);
    }

    private ExpressionNode ParseGroup(int start)
    {
        EnterNesting(start);
        _position++;
        var node = ParseOr();
        if (!TryConsumeSymbol(")"))
        {
            throw CreateError("expected ')'");
        }

        _nestingDepth--;
        return node;
    }

    private ExpressionNode ParseWord(int start, string? operatorText)
    {
        var token = ReadToken();
        if (token.Length == 0)
        {
            throw CreateError(ExpectedValue(operatorText));
        }

        if (IsArithmeticToken(token))
        {
            _position = start;
            throw CreateError("arithmetic is not supported");
        }

        if (IsNumber(token))
        {
            return TryParseDuration(token, out var duration) ? new LiteralNode(duration) : ParseNumber(token, start);
        }

        if (TryCreateKeywordLiteral(token) is { } literal)
        {
            return literal;
        }

        if (IsOperatorKeyword(token))
        {
            _position = start;
            throw CreateError(ExpectedValue(operatorText));
        }

        return CreateWordNode(token, operatorText);
    }

    private ExpressionNode CreateWordNode(string token, string? operatorText)
    {
        // A bare word after an operator is text, such as the name of an enum value.
        if (operatorText is not null && IsBareWord(token))
        {
            return new LiteralNode(token);
        }

        if (!_paths.Contains(token))
        {
            _paths.Add(token);
        }

        return new PathNode(token, pathSeparator);
    }

    private LiteralNode ParseNumber(string token, int start)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            _position = start;
            throw CreateError($"invalid number '{token}'");
        }

        return new LiteralNode(number);
    }

    // A duration is one or more pairs of a number and a unit, from the largest unit to the smallest and each unit at most
    // once, with an optional sign before the first pair: 90s, 1h30m, 1.5d, -5m. Such a token was an invalid number before,
    // so reading it as a duration changes no expression that parsed.
    private static bool TryParseDuration(string token, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var rest = token.AsSpan();
        var isNegative = rest[0] == '-';
        if (rest[0] is '-' or '+')
        {
            rest = rest[1..];
        }

        var ticks = 0d;
        var previousRank = int.MaxValue;
        while (rest.Length > 0)
        {
            if (!TryReadDurationPart(ref rest, previousRank, out var rank, out var partTicks))
            {
                return false;
            }

            previousRank = rank;
            ticks += partTicks;
        }

        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks((long)Math.Round(ticks));
        if (isNegative)
        {
            duration = duration.Negate();
        }

        return true;
    }

    // Reads one number and its unit from the front of the text, and fails unless the unit is smaller than the previous one.
    private static bool TryReadDurationPart(ref ReadOnlySpan<char> rest, int previousRank, out int rank, out double ticks)
    {
        rank = -1;
        ticks = 0;
        var numberLength = 0;
        while (numberLength < rest.Length && (char.IsAsciiDigit(rest[numberLength]) || rest[numberLength] == '.'))
        {
            numberLength++;
        }

        if (numberLength == 0 ||
            !double.TryParse(rest[..numberLength], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        var remaining = rest[numberLength..];
        if (!TryReadUnit(remaining, out rank, out var unitLength, out var ticksPerUnit) || rank >= previousRank)
        {
            return false;
        }

        ticks = amount * ticksPerUnit;
        rest = remaining[unitLength..];
        return true;
    }

    // "ms" before "m", so that milliseconds are not read as minutes.
    private static bool TryReadUnit(ReadOnlySpan<char> text, out int rank, out int length, out long ticksPerUnit)
    {
        (rank, length, ticksPerUnit) = text switch
        {
            ['m', 's', ..] => (0, 2, TimeSpan.TicksPerMillisecond),
            ['s', ..] => (1, 1, TimeSpan.TicksPerSecond),
            ['m', ..] => (2, 1, TimeSpan.TicksPerMinute),
            ['h', ..] => (3, 1, TimeSpan.TicksPerHour),
            ['d', ..] => (4, 1, TimeSpan.TicksPerDay),
            _ => (-1, 0, 0L)
        };

        return rank >= 0;
    }

    private static LiteralNode? TryCreateKeywordLiteral(string token)
    {
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new LiteralNode(True);
        }

        if (token.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new LiteralNode(False);
        }

        return token.Equals("null", StringComparison.OrdinalIgnoreCase) ? new LiteralNode(null) : null;
    }

    private static bool IsOperatorKeyword(string token) =>
        token.Equals("and", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("or", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("not", StringComparison.OrdinalIgnoreCase);

    private static bool IsArithmeticToken(string token) => token is "+" or "-" or "*" or "/";

    // With '.' as separator a dot is the decimal point of a number, so only the other separators make a token a path.
    private bool IsNumber(string token) =>
        IsNumberStart(token) && (pathSeparator == '.' || !token.Contains(pathSeparator));

    private bool IsBareWord(string token) =>
        token.AsSpan().IndexOfAny(pathSeparator, '[') < 0 && !ExpressionVariables.TryGetVariable(token, pathSeparator, out _);

    private (ComparisonOperator Operator, string Text)? ReadOperator()
    {
        SkipWhitespace();
        foreach (var (symbol, comparisonOperator) in Operators)
        {
            if (TryConsumeSymbol(symbol))
            {
                return (comparisonOperator, symbol);
            }
        }

        if (_position < text.Length && text[_position] == '=')
        {
            throw CreateError("use '==' for equality");
        }

        ThrowIfArithmetic();
        return null;
    }

    private ExpressionPipe ParsePipe()
    {
        SkipWhitespace();
        var start = _position;
        while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
        {
            _position++;
        }

        if (_position == start)
        {
            throw CreateError("expected a pipe name after '|'");
        }

        var name = text[start.._position];
        var arguments = ImmutableArray<string>.Empty;
        while (_position < text.Length && text[_position] == ':')
        {
            _position++;
            arguments = arguments.Add(ReadPipeArgument());
        }

        return new ExpressionPipe(name, arguments);
    }

    private string ReadPipeArgument()
    {
        if (_position < text.Length && text[_position] is '"' or '\'')
        {
            return ReadString(text[_position]);
        }

        var start = _position;
        while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not (':' or '|'))
        {
            _position++;
        }

        if (_position == start)
        {
            throw CreateError("expected an argument after ':'");
        }

        return text[start.._position];
    }

    // A token runs up to white space, a parenthesis, a quote or an operator character. Inside brackets everything up
    // to the closing bracket belongs to it, so that a key can hold any of these characters.
    private string ReadToken()
    {
        var start = _position;
        var depth = 0;
        while (_position < text.Length)
        {
            var character = text[_position];
            if (character == '[')
            {
                depth++;
            }
            else if (character == ']' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && IsDelimiter(character))
            {
                break;
            }

            _position++;
        }

        return text[start.._position];
    }

    private string ReadString(char quote)
    {
        var start = _position;
        var end = text.IndexOf(quote, start + 1);
        if (end < 0)
        {
            throw CreateError("unterminated string");
        }

        _position = end + 1;
        return text.Substring(start + 1, end - start - 1);
    }

    // An operator of arithmetic stands alone between operands. Inside a token such as "notes-v2.md" it is part of a name.
    private void ThrowIfArithmetic()
    {
        if (_position < text.Length &&
            text[_position] is '+' or '-' or '*' or '/' &&
            (_position + 1 == text.Length || char.IsWhiteSpace(text[_position + 1])))
        {
            throw CreateError("arithmetic is not supported");
        }
    }

    private bool TryConsumePipeSeparator()
    {
        SkipWhitespace();
        if (_position >= text.Length || text[_position] != '|' ||
            (_position + 1 < text.Length && text[_position + 1] == '|'))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool TryConsumeBang()
    {
        SkipWhitespace();
        if (_position >= text.Length || text[_position] != '!' ||
            (_position + 1 < text.Length && text[_position + 1] == '='))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool TryConsumeSymbol(string symbol)
    {
        SkipWhitespace();
        if (string.CompareOrdinal(text, _position, symbol, 0, symbol.Length) != 0)
        {
            return false;
        }

        _position += symbol.Length;
        return true;
    }

    // A keyword ends at white space, a parenthesis or the end, so that a path such as "note/Text" is no "not".
    private bool TryConsumeKeyword(string keyword)
    {
        SkipWhitespace();
        var end = _position + keyword.Length;
        if (end > text.Length ||
            string.Compare(text, _position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
            !EndsKeyword(end))
        {
            return false;
        }

        _position = end;
        return true;
    }

    private bool EndsKeyword(int end) =>
        end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] == '(';

    private void EnterNesting(int position)
    {
        if (++_nestingDepth > MaximumNestingDepth)
        {
            _position = position;
            throw CreateError("expression is nested too deeply");
        }
    }

    private void SkipWhitespace()
    {
        while (_position < text.Length && char.IsWhiteSpace(text[_position]))
        {
            _position++;
        }
    }

    private static bool IsDelimiter(char character) =>
        char.IsWhiteSpace(character) || DelimiterSymbols.Contains(character);

    // A digit, or a sign or a dot followed by a digit. "../x" and "./x" are paths.
    private static bool IsNumberStart(string token)
    {
        var unsigned = token[0] is '-' or '+' ? token.AsSpan(1) : token.AsSpan();
        return unsigned.Length > 0 && StartsWithNumber(unsigned);
    }

    private static bool StartsWithNumber(ReadOnlySpan<char> value) =>
        char.IsAsciiDigit(value[0]) || (value[0] == '.' && value.Length > 1 && char.IsAsciiDigit(value[1]));

    private static string ExpectedValue(string? operatorText) =>
        operatorText is null ? "expected a value" : $"expected a value after '{operatorText}'";

    private FormatException CreateError(string message) => new($"{message} at position {_position}");
}
