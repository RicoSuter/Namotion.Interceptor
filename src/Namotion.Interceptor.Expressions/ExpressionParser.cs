using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Parses an expression by recursive descent. Throws a <see cref="FormatException"/> whose message reads
/// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
/// </summary>
internal sealed class ExpressionParser(string text, char pathSeparator, ImmutableArray<string> variables)
{
    internal const int MaximumExpressionLength = 1000;
    internal const int MaximumNestingDepth = 32;

    private const decimal OutOfRangeTicks = 2m * long.MaxValue;
    private const int MaximumDecimalDigits = 28;

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
            throw CreateUnexpectedCharacterError();
        }

        return new ParsedExpression(text, pathSeparator, variables, body, pipes, [.. _paths]);
    }

    // An operator glued to the value after it, as in "a -5", or to the token before it, as in "a- b", is the likely mistake.
    private FormatException CreateUnexpectedCharacterError()
    {
        var character = text[_position];
        if (IsArithmeticSymbol(character) && !StandsAlone(_position))
        {
            return CreateError($"put a space after '{character}'");
        }

        var previous = _position - 1;
        while (previous > 0 && char.IsWhiteSpace(text[previous]))
        {
            previous--;
        }

        if (previous > 0 && IsArithmeticSymbol(text[previous]) && !char.IsWhiteSpace(text[previous - 1]))
        {
            _position = previous;
            return CreateError($"put a space before '{text[previous]}'");
        }

        return CreateError($"unexpected '{character}'");
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
        var left = ParseSum(operatorText: null, bareWordIsText: false);
        if (ReadOperator() is not { } comparison)
        {
            return left;
        }

        return new ComparisonNode(left, comparison.Operator, ParseSum(comparison.Text, bareWordIsText: true));
    }

    // '+' and '-' bind tighter than comparisons and looser than '*' and '/'.
    private ExpressionNode ParseSum(string? operatorText, bool bareWordIsText)
    {
        var node = ParseProduct(operatorText, bareWordIsText);
        while (TryConsumeArithmeticOperator(isAdditive: true) is { } arithmetic)
        {
            node = new ArithmeticNode(node, arithmetic.Operator, ParseProduct(arithmetic.Symbol, bareWordIsText: false));
        }

        return node;
    }

    private ExpressionNode ParseProduct(string? operatorText, bool bareWordIsText)
    {
        var node = ParseOperand(operatorText, bareWordIsText);
        while (TryConsumeArithmeticOperator(isAdditive: false) is { } arithmetic)
        {
            node = new ArithmeticNode(node, arithmetic.Operator, ParseOperand(arithmetic.Symbol, bareWordIsText: false));
        }

        return node;
    }

    // The operator before a right operand, or null for an operand that no operator precedes.
    private ExpressionNode ParseOperand(string? operatorText, bool bareWordIsText)
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

        if (character == '-' && !StartsWithNumber(text.AsSpan(_position + 1)))
        {
            return ParseNegation(start);
        }

        if (character is '"' or '\'')
        {
            return new LiteralNode(ReadString(character));
        }

        return ParseWord(start, operatorText, bareWordIsText);
    }

    // A minus that is no sign of a number negates the operand after it and binds tighter than '*' and '/'. A chain of '-'
    // recurses once per character, so it counts against the nesting limit like a parenthesis.
    private NegationNode ParseNegation(int start)
    {
        EnterNesting(start);
        _position++;
        var operand = ParseOperand("-", bareWordIsText: false);
        _nestingDepth--;
        return new NegationNode(operand);
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

    private ExpressionNode ParseWord(int start, string? operatorText, bool bareWordIsText)
    {
        var token = ReadToken();
        if (token.Length == 0 || IsArithmeticToken(token))
        {
            _position = start;
            throw CreateError(ExpectedValue(operatorText));
        }

        if (IsNumber(token))
        {
            // A duration ends with its unit, a number never ends with a letter.
            return char.IsAsciiLetter(token[^1]) ? new LiteralNode(ParseDuration(token, start)) : ParseNumber(token, start);
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

        return CreateWordNode(token, bareWordIsText);
    }

    private ExpressionNode CreateWordNode(string token, bool bareWordIsText)
    {
        // A bare word after a comparison operator is text, such as the name of an enum value, unless arithmetic follows it.
        if (bareWordIsText && IsBareWord(token) && !IsArithmeticOperatorNext())
        {
            return new LiteralNode(token);
        }

        return CreatePathNode(token);
    }

    private PathNode CreatePathNode(string token)
    {
        if (!_paths.Contains(token))
        {
            _paths.Add(token);
        }

        return new PathNode(token, pathSeparator, variables);
    }

    private LiteralNode ParseNumber(string token, int start)
    {
        if (TryParseDecimal(token, out var exact))
        {
            return new LiteralNode(ExpressionArithmetic.Normalize(exact));
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (!double.IsFinite(number))
            {
                _position = start;
                throw CreateError($"number out of range '{token}'");
            }

            return new LiteralNode(number);
        }

        if (IsArithmeticSymbol(token[^1]))
        {
            _position = start + token.Length - 1;
            throw CreateError($"put a space before '{token[^1]}'");
        }

        _position = start;
        throw CreateError($"invalid number '{token}'");
    }

    // A number without an exponent and with at most 28 digits, none of them beyond the 28th decimal place, is exactly a
    // decimal; more digits would make decimal.TryParse round.
    private static bool TryParseDecimal(string token, out decimal number)
    {
        number = 0;
        var digits = token.AsSpan().TrimStart("+-").TrimStart('0');
        var point = digits.IndexOf('.');
        var fractionLength = point < 0 ? 0 : digits.Length - point - 1;
        var digitCount = point < 0 ? digits.Length : digits.Length - 1;
        return fractionLength <= MaximumDecimalDigits && digitCount <= MaximumDecimalDigits &&
            decimal.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    // A duration is one or more pairs of a number and a unit, from the largest unit to the smallest and each unit at most
    // once, with an optional sign before the first pair: 90s, 1h30m, 1.5d, -5m. The ticks add up as decimals, so the
    // whole range of TimeSpan is exact.
    private TimeSpan ParseDuration(string token, int start)
    {
        var rest = token.AsSpan();
        var isNegative = rest[0] == '-';
        if (rest[0] is '-' or '+')
        {
            rest = rest[1..];
        }

        var ticks = 0m;
        var previousRank = int.MaxValue;
        while (rest.Length > 0)
        {
            if (!TryReadDurationPart(ref rest, previousRank, out var rank, out var partTicks))
            {
                _position = start;
                throw CreateError($"invalid duration '{token}'");
            }

            previousRank = rank;
            ticks += partTicks;
        }

        ticks = decimal.Round(isNegative ? -ticks : ticks);
        if (ticks < TimeSpan.MinValue.Ticks || ticks > TimeSpan.MaxValue.Ticks)
        {
            _position = start;
            throw CreateError($"duration out of range '{token}'");
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    // Reads one number and its unit from the front of the text, and fails unless the unit is smaller than the previous one.
    // A part beyond the range of TimeSpan counts as OutOfRangeTicks, so that the parts add up without overflow and the
    // duration stays out of range with either sign.
    private static bool TryReadDurationPart(ref ReadOnlySpan<char> rest, int previousRank, out int rank, out decimal ticks)
    {
        rank = -1;
        ticks = 0;
        var numberLength = 0;
        while (numberLength < rest.Length && (char.IsAsciiDigit(rest[numberLength]) || rest[numberLength] == '.'))
        {
            numberLength++;
        }

        if (numberLength == 0 ||
            !decimal.TryParse(rest[..numberLength], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        var remaining = rest[numberLength..];
        if (!TryReadUnit(remaining, out rank, out var unitLength, out var ticksPerUnit) || rank >= previousRank)
        {
            return false;
        }

        ticks = amount < OutOfRangeTicks / ticksPerUnit ? amount * ticksPerUnit : OutOfRangeTicks;
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

    /// <summary>Whether the token is a keyword, an operator or a literal, without regard to case.</summary>
    internal static bool IsKeyword(string token) => IsOperatorKeyword(token) || TryCreateKeywordLiteral(token) is not null;

    private static bool IsOperatorKeyword(string token) =>
        token.Equals("and", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("or", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("not", StringComparison.OrdinalIgnoreCase);

    private static bool IsArithmeticToken(string token) => token.Length == 1 && IsArithmeticSymbol(token[0]);

    // With '.' as separator a dot is the decimal point of a number, so only the other separators make a token a path.
    private bool IsNumber(string token) =>
        IsNumberStart(token) && (pathSeparator == '.' || !token.Contains(pathSeparator));

    private bool IsBareWord(string token) =>
        token.AsSpan().IndexOfAny(pathSeparator, '[') < 0 && !ExpressionVariables.TryGetVariable(token, pathSeparator, variables, out _, out _);

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

        return null;
    }

    // An arithmetic operator stands alone. Inside a token such as "notes-v2.md" it is part of a name, and directly before a
    // digit, as in "-5", it is the sign of a number.
    private (ArithmeticOperator Operator, string Symbol)? TryConsumeArithmeticOperator(bool isAdditive)
    {
        if (!IsArithmeticOperatorNext() || ReadArithmeticSymbol(isAdditive) is not { } arithmetic)
        {
            return null;
        }

        _position++;
        return arithmetic;
    }

    private bool IsArithmeticOperatorNext()
    {
        SkipWhitespace();
        return _position < text.Length && IsArithmeticSymbol(text[_position]) && StandsAlone(_position);
    }

    private (ArithmeticOperator Operator, string Symbol)? ReadArithmeticSymbol(bool isAdditive) =>
        (isAdditive, text[_position]) switch
        {
            (true, '+') => (ArithmeticOperator.Add, "+"),
            (true, '-') => (ArithmeticOperator.Subtract, "-"),
            (false, '*') => (ArithmeticOperator.Multiply, "*"),
            (false, '/') => (ArithmeticOperator.Divide, "/"),
            _ => null
        };

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
            !EndsWord(end))
        {
            return false;
        }

        _position = end;
        return true;
    }

    private bool EndsWord(int end) =>
        end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] == '(';

    // White space, a parenthesis, a pipe or the end follows the operator, so "a -)" reports the missing value after it.
    private bool StandsAlone(int operatorPosition) =>
        EndsWord(operatorPosition + 1) || text[operatorPosition + 1] is ')' or '|';

    private static bool IsArithmeticSymbol(char character) => character is '+' or '-' or '*' or '/';

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
        return StartsWithNumber(unsigned);
    }

    private static bool StartsWithNumber(ReadOnlySpan<char> value) => value switch
    {
        [var first, ..] when char.IsAsciiDigit(first) => true,
        ['.', var second, ..] => char.IsAsciiDigit(second),
        _ => false
    };

    private static string ExpectedValue(string? operatorText) =>
        operatorText is null ? "expected a value" : $"expected a value after '{operatorText}'";

    private FormatException CreateError(string message) => new($"{message} at position {_position}");
}
