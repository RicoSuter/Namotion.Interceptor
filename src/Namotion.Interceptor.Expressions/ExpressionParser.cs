using System.Collections.Immutable;
using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Parses an expression by recursive descent. Throws a <see cref="FormatException"/> whose message reads
/// <c>&lt;message&gt; at position &lt;zero-based index&gt;</c>.
/// </summary>
internal sealed class ExpressionParser(string text)
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

        return new ParsedExpression(text, body, pipes, [.. _paths]);
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

        if (character is '"' or '\'')
        {
            return new LiteralNode(ReadString(character));
        }

        var token = ReadToken();
        if (token.Length == 0)
        {
            throw CreateError(ExpectedValue(operatorText));
        }

        if (token is "+" or "-" or "*" or "/")
        {
            _position = start;
            throw CreateError("arithmetic is not supported");
        }

        if (IsNumberStart(token) && !token.Contains('/'))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                _position = start;
                throw CreateError($"invalid number '{token}'");
            }

            return new LiteralNode(number);
        }

        if (token.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new LiteralNode(True);
        }

        if (token.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new LiteralNode(False);
        }

        if (token.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return new LiteralNode(null);
        }

        if (token.Equals("and", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("or", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("not", StringComparison.OrdinalIgnoreCase))
        {
            _position = start;
            throw CreateError(ExpectedValue(operatorText));
        }

        // A bare word after an operator is text, such as the name of an enum value.
        if (operatorText is not null && token.AsSpan().IndexOfAny('/', '[') < 0 && !ExpressionVariables.TryGetVariable(token, out _))
        {
            return new LiteralNode(token);
        }

        if (!_paths.Contains(token))
        {
            _paths.Add(token);
        }

        return new PathNode(token);
    }

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
            (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '('))
        {
            return false;
        }

        _position = end;
        return true;
    }

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
        char.IsWhiteSpace(character) || character is '(' or ')' or '"' or '\'' or '|' or '&' or '=' or '!' or '<' or '>';

    // A digit, or a sign or a dot followed by a digit. "../x" and "./x" are paths.
    private static bool IsNumberStart(string token)
    {
        var unsigned = token[0] is '-' or '+' ? token.AsSpan(1) : token.AsSpan();
        return unsigned.Length > 0 &&
            (char.IsAsciiDigit(unsigned[0]) || (unsigned[0] == '.' && unsigned.Length > 1 && char.IsAsciiDigit(unsigned[1])));
    }

    private static string ExpectedValue(string? operatorText) =>
        operatorText is null ? "expected a value" : $"expected a value after '{operatorText}'";

    private FormatException CreateError(string message) => new($"{message} at position {_position}");
}
