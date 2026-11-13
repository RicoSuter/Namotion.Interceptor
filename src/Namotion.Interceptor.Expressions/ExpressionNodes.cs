using System.Collections.Immutable;
using System.Globalization;

namespace Namotion.Interceptor.Expressions;

internal abstract class ExpressionNode;

/// <summary>A number (a boxed <see cref="decimal"/> when it is exact, otherwise a boxed <see cref="double"/>), a duration (a boxed <see cref="TimeSpan"/>), a boolean, a text or null.</summary>
internal sealed class LiteralNode(object? value) : ExpressionNode
{
    public object? Value { get; } = value;
}

internal sealed class PathNode : ExpressionNode
{
    public PathNode(string path, char pathSeparator, ImmutableArray<string> variables)
    {
        Path = path;
        // Split once here, so that an evaluation allocates no part of the path.
        if (ExpressionVariables.TryGetVariable(path, pathSeparator, variables, out var variable, out var memberPath))
        {
            Variable = variable;
            MemberPath = memberPath;
        }
    }

    public string Path { get; }

    /// <summary>The variable the path starts with, or null.</summary>
    public string? Variable { get; }

    /// <summary>The rest of the path after the variable and its separator, or null for the variable alone or a path without one.</summary>
    public string? MemberPath { get; }
}

internal sealed class NegationNode(ExpressionNode operand) : ExpressionNode
{
    public ExpressionNode Operand { get; } = operand;
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

    /// <summary>The conversions of a literal on the left, done once.</summary>
    public ConvertedLiteral LeftLiteral { get; } = ConvertedLiteral.Create(left);

    /// <summary>The conversions of a literal on the right, done once.</summary>
    public ConvertedLiteral RightLiteral { get; } = ConvertedLiteral.Create(right);
}

/// <summary>The conversions of a literal operand of a comparison that would otherwise repeat at every evaluation.</summary>
internal readonly struct ConvertedLiteral
{
    /// <summary>The instant of a text literal that is a date, for the comparison with a date; otherwise null.</summary>
    public DateTimeOffset? Instant { get; private init; }

    /// <summary>The double of a number literal, for the comparison with a double or a float; otherwise null.</summary>
    public double? Number { get; private init; }

    public static ConvertedLiteral Create(ExpressionNode node) => node switch
    {
        LiteralNode { Value: string text } => new ConvertedLiteral { Instant = TryParseInstant(text) },
        LiteralNode { Value: decimal number } => new ConvertedLiteral { Number = (double)number },
        LiteralNode { Value: double number } => new ConvertedLiteral { Number = number },
        _ => default
    };

    // A text without an offset is read as UTC.
    private static DateTimeOffset? TryParseInstant(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}

internal enum ArithmeticOperator
{
    Add,
    Subtract,
    Multiply,
    Divide
}

internal sealed class ArithmeticNode(ExpressionNode left, ArithmeticOperator arithmeticOperator, ExpressionNode right) : ExpressionNode
{
    public ExpressionNode Left { get; } = left;

    public ArithmeticOperator Operator { get; } = arithmeticOperator;

    public ExpressionNode Right { get; } = right;
}
