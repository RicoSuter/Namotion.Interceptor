using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// A value filter over the scalar properties of a subject: comparisons (<c>== != &lt; &lt;= &gt; &gt;=</c>) of a
/// property against a number, <c>true</c>, <c>false</c>, <c>null</c> or a string, combined with
/// <c>&amp;&amp;</c> (<c>and</c>), <c>||</c> (<c>or</c>) and parentheses, where <c>&amp;&amp;</c> binds tighter.
/// Properties resolve through the path provider, so a filter only sees properties the provider exposes.
/// A missing property, a null value against a non-null literal, a NaN value or an incompatible type never matches.
/// </summary>
internal sealed class SubjectFilterExpression
{
    private const int MaximumExpressionLength = 1000;
    private const int MaximumNestingDepth = 32;

    private readonly Node _root;

    private SubjectFilterExpression(Node root)
    {
        _root = root;
    }

    /// <summary>
    /// Parses <paramref name="text"/>. On failure <paramref name="error"/> reads
    /// <c>where: &lt;message&gt; at position &lt;zero-based index&gt;</c>.
    /// </summary>
    public static bool TryParse(
        string text,
        [NotNullWhen(true)] out SubjectFilterExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            var root = new Parser(text).Parse();
            expression = new SubjectFilterExpression(root);
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            expression = null;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Evaluates the filter against the current property values of <paramref name="subject"/>.
    /// </summary>
    public bool Matches(RegisteredSubject subject, PathProviderBase pathProvider) =>
        _root.Matches(subject, pathProvider);

    private enum ComparisonOperator
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual
    }

    private abstract class Node
    {
        public abstract bool Matches(RegisteredSubject subject, PathProviderBase pathProvider);
    }

    private sealed class AndNode(Node left, Node right) : Node
    {
        public override bool Matches(RegisteredSubject subject, PathProviderBase pathProvider) =>
            left.Matches(subject, pathProvider) && right.Matches(subject, pathProvider);
    }

    private sealed class OrNode(Node left, Node right) : Node
    {
        public override bool Matches(RegisteredSubject subject, PathProviderBase pathProvider) =>
            left.Matches(subject, pathProvider) || right.Matches(subject, pathProvider);
    }

    /// <summary>
    /// The literal is a <see cref="double"/>, a <see cref="bool"/>, a <see cref="string"/> or null.
    /// </summary>
    private sealed class ComparisonNode(string propertyName, ComparisonOperator comparisonOperator, object? literal) : Node
    {
        // Parsed once here instead of on every evaluation.
        private readonly DateTimeOffset? _literalInstant = literal is string literalText ? TryParseInstant(literalText) : null;

        public override bool Matches(RegisteredSubject subject, PathProviderBase pathProvider)
        {
            var property = pathProvider.TryGetPropertyFromSegment(subject, propertyName);
            if (property is null ||
                property.IsAttribute ||
                property.CanContainSubjects ||
                !pathProvider.IsPropertyIncluded(property))
            {
                return false;
            }

            return Compare(property.GetValue());
        }

        private bool Compare(object? value)
        {
            if (literal is null)
            {
                return comparisonOperator switch
                {
                    ComparisonOperator.Equal => value is null,
                    ComparisonOperator.NotEqual => value is not null,
                    _ => false
                };
            }

            if (value is null)
            {
                return false;
            }

            var isEquality = comparisonOperator is ComparisonOperator.Equal or ComparisonOperator.NotEqual;
            int? order = (value, literal) switch
            {
                (bool boolean, bool literalBoolean) when isEquality => boolean == literalBoolean ? 0 : 1,
                (Enum enumValue, string name) when isEquality =>
                    string.Equals(GetEnumName(enumValue), name, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
                (string text, string literalText) => isEquality
                    ? string.Equals(text, literalText, StringComparison.OrdinalIgnoreCase) ? 0 : 1
                    : string.CompareOrdinal(text, literalText),
                (DateTimeOffset instant, string) => _literalInstant is { } literalInstant
                    ? instant.CompareTo(literalInstant)
                    : null,
                (DateTime dateTime, string) => _literalInstant is { } literalInstant
                    ? ToInstant(dateTime).CompareTo(literalInstant)
                    : null,
                // CompareTo orders NaN below every number, so NaN is handled like an incompatible type.
                (float single, double) when float.IsNaN(single) => null,
                (double floatingPoint, double) when double.IsNaN(floatingPoint) => null,
                // Widening a float to double would make 0.1f differ from the literal 0.1.
                (float single, double number) => single.CompareTo((float)number),
                (_, double number) when IsNumber(value) =>
                    Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo(number),
                _ => null
            };

            return order is { } result && comparisonOperator switch
            {
                ComparisonOperator.Equal => result == 0,
                ComparisonOperator.NotEqual => result != 0,
                ComparisonOperator.Less => result < 0,
                ComparisonOperator.LessOrEqual => result <= 0,
                ComparisonOperator.Greater => result > 0,
                _ => result >= 0
            };
        }

        // The cached name of a defined value, so a match does not allocate; ToString only for flag combinations
        // and undefined values.
        private static string GetEnumName(Enum enumValue) =>
            Enum.GetName(enumValue.GetType(), enumValue) ?? enumValue.ToString();

        private static bool IsNumber(object value) =>
            value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

        // A DateTime without a kind is read as UTC, matching how a bare literal is read.
        private static DateTimeOffset ToInstant(DateTime dateTime) =>
            dateTime.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(dateTime, TimeSpan.Zero)
                : new DateTimeOffset(dateTime);

        private static DateTimeOffset? TryParseInstant(string text) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                ? instant
                : null;
    }

    private sealed class Parser(string text)
    {
        private int _position;
        private int _nestingDepth;

        public Node Parse()
        {
            if (text.Length > MaximumExpressionLength)
            {
                _position = MaximumExpressionLength;
                throw CreateError($"expression is longer than {MaximumExpressionLength} characters");
            }

            var node = ParseOr();
            SkipWhitespace();
            if (_position < text.Length)
            {
                throw CreateError($"unexpected '{text[_position]}'");
            }

            return node;
        }

        private Node ParseOr()
        {
            var node = ParseAnd();
            while (TryConsumeSymbol("||") || TryConsumeKeyword("or"))
            {
                node = new OrNode(node, ParseAnd());
            }

            return node;
        }

        private Node ParseAnd()
        {
            var node = ParsePrimary();
            while (TryConsumeSymbol("&&") || TryConsumeKeyword("and"))
            {
                node = new AndNode(node, ParsePrimary());
            }

            return node;
        }

        private Node ParsePrimary()
        {
            SkipWhitespace();
            var openPosition = _position;
            if (TryConsumeSymbol("("))
            {
                if (++_nestingDepth > MaximumNestingDepth)
                {
                    _position = openPosition;
                    throw CreateError("expression is nested too deeply");
                }

                var node = ParseOr();
                if (!TryConsumeSymbol(")"))
                {
                    throw CreateError("expected ')'");
                }

                _nestingDepth--;
                return node;
            }

            var propertyName = ReadIdentifier() ?? throw CreateError("expected a property name");
            var (comparisonOperator, operatorText) = ReadOperator()
                ?? throw CreateError($"expected a comparison operator after '{propertyName}'");
            var literal = ReadLiteral(operatorText);
            return new ComparisonNode(propertyName, comparisonOperator, literal);
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

            return null;
        }

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

        private object? ReadLiteral(string operatorText)
        {
            SkipWhitespace();
            if (_position >= text.Length)
            {
                throw CreateError($"expected a value after '{operatorText}'");
            }

            var character = text[_position];
            if (character is '"' or '\'')
            {
                var start = _position;
                var end = text.IndexOf(character, start + 1);
                if (end < 0)
                {
                    throw CreateError("unterminated string");
                }

                _position = end + 1;
                return text.Substring(start + 1, end - start - 1);
            }

            if (char.IsDigit(character) || character is '-' or '+' or '.')
            {
                var start = _position;
                while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] is '.' or 'e' or 'E' or '-' or '+'))
                {
                    _position++;
                }

                var numberText = text[start.._position];
                if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    _position = start;
                    throw CreateError($"invalid number '{numberText}'");
                }

                return number;
            }

            var word = ReadIdentifier() ?? throw CreateError($"expected a value after '{operatorText}'");
            if (string.Equals(word, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(word, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(word, "null", StringComparison.OrdinalIgnoreCase) ? null : word;
        }

        private string? ReadIdentifier()
        {
            SkipWhitespace();
            var start = _position;
            if (_position >= text.Length || !(char.IsLetter(text[_position]) || text[_position] == '_'))
            {
                return null;
            }

            while (_position < text.Length && IsIdentifierCharacter(text[_position]))
            {
                _position++;
            }

            return text[start.._position];
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

        private bool TryConsumeKeyword(string keyword)
        {
            SkipWhitespace();
            var end = _position + keyword.Length;
            if (end > text.Length ||
                string.Compare(text, _position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
                (end < text.Length && IsIdentifierCharacter(text[end])))
            {
                return false;
            }

            _position = end;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

        private FormatException CreateError(string message) => new($"where: {message} at position {_position}");
    }
}
