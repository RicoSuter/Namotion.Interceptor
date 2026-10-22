using System.Globalization;

namespace HomeBlaze.Services.Expressions;

internal abstract class ExpressionNode;

/// <summary>A number (a boxed <see cref="double"/>), a boolean, a text or null.</summary>
internal sealed class LiteralNode(object? value) : ExpressionNode
{
    public object? Value { get; } = value;
}

internal sealed class PathNode(string path) : ExpressionNode
{
    public string Path { get; } = path;
}

internal sealed class NotNode(ExpressionNode operand) : ExpressionNode
{
    public ExpressionNode Operand { get; } = operand;
}

internal sealed class AndNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    public ExpressionNode Left { get; } = left;

    public ExpressionNode Right { get; } = right;
}

internal sealed class OrNode(ExpressionNode left, ExpressionNode right) : ExpressionNode
{
    public ExpressionNode Left { get; } = left;

    public ExpressionNode Right { get; } = right;
}

internal enum ComparisonOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual
}

internal sealed class ComparisonNode(ExpressionNode left, ComparisonOperator comparisonOperator, ExpressionNode right) : ExpressionNode
{
    public ExpressionNode Left { get; } = left;

    public ComparisonOperator Operator { get; } = comparisonOperator;

    public ExpressionNode Right { get; } = right;

    public bool IsEquality => Operator is ComparisonOperator.Equal or ComparisonOperator.NotEqual;

    /// <summary>The instant of a text literal on the left, parsed once, for the comparison with a date.</summary>
    public DateTimeOffset? LeftInstant { get; } = TryParseInstant(left);

    /// <summary>The instant of a text literal on the right, parsed once, for the comparison with a date.</summary>
    public DateTimeOffset? RightInstant { get; } = TryParseInstant(right);

    // A text without an offset is read as UTC.
    private static DateTimeOffset? TryParseInstant(ExpressionNode node) =>
        node is LiteralNode { Value: string text } &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}
