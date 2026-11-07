namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Applies an arithmetic operator to two values that are not null. Numbers of any numeric type compute as
/// <see cref="double"/>. An instant minus an instant is a <see cref="TimeSpan"/>, an instant plus or minus a
/// <see cref="TimeSpan"/> is an instant, and two <see cref="TimeSpan"/> values add and subtract. A
/// <see cref="DateTime"/> without a kind is read as UTC. A null result means the pair has no arithmetic or the result is
/// out of range, so the value is missing.
/// </summary>
internal static class ExpressionArithmetic
{
    /// <summary>Applies the operator to two values that are not null.</summary>
    /// <returns>The result, or null when the values have no arithmetic or the result is out of range.</returns>
    public static object? Apply(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        if (ExpressionComparer.IsNumber(left) && ExpressionComparer.IsNumber(right))
        {
            return ApplyToNumbers(arithmeticOperator, ExpressionComparer.ToDouble(left), ExpressionComparer.ToDouble(right));
        }

        return arithmeticOperator switch
        {
            ArithmeticOperator.Add => ApplyToTimes(isSubtraction: false, left, right),
            ArithmeticOperator.Subtract => ApplyToTimes(isSubtraction: true, left, right),
            _ => null
        };
    }

    private static object? ApplyToNumbers(ArithmeticOperator arithmeticOperator, double left, double right)
    {
        var result = arithmeticOperator switch
        {
            ArithmeticOperator.Add => left + right,
            ArithmeticOperator.Subtract => left - right,
            ArithmeticOperator.Multiply => left * right,
            _ => left / right
        };

        // A division by zero gives infinity or NaN, which is missing like a result out of range.
        return double.IsFinite(result) ? result : null;
    }

    private static object? ApplyToTimes(bool isSubtraction, object left, object right)
    {
        try
        {
            if (ExpressionComparer.TryGetInstant(left, out var leftInstant))
            {
                return ApplyToInstant(isSubtraction, leftInstant, right);
            }

            return left is TimeSpan leftDuration ? ApplyToDuration(isSubtraction, leftDuration, right) : null;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            // A result beyond the range of TimeSpan or DateTimeOffset is missing, as for a number out of range.
            return null;
        }
    }

    private static object? ApplyToInstant(bool isSubtraction, DateTimeOffset instant, object right)
    {
        if (isSubtraction && ExpressionComparer.TryGetInstant(right, out var rightInstant))
        {
            return instant - rightInstant;
        }

        if (right is TimeSpan duration)
        {
            return isSubtraction ? instant - duration : instant + duration;
        }

        return null;
    }

    private static object? ApplyToDuration(bool isSubtraction, TimeSpan duration, object right)
    {
        if (right is TimeSpan otherDuration)
        {
            return isSubtraction ? duration - otherDuration : duration + otherDuration;
        }

        return !isSubtraction && ExpressionComparer.TryGetInstant(right, out var instant) ? instant + duration : null;
    }
}
