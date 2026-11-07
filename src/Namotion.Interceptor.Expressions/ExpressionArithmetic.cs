namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Applies an arithmetic operator to two values that are not null. Two integers or decimals compute as
/// <see cref="decimal"/> when the result is exact, and as <see cref="double"/> otherwise; a <see cref="float"/> with any
/// number computes as <see cref="float"/>; every other pair of numbers computes as <see cref="double"/>. An instant minus
/// an instant is a <see cref="TimeSpan"/>, an instant plus or minus a <see cref="TimeSpan"/> is an instant, and two
/// <see cref="TimeSpan"/> values add and subtract. A <see cref="DateTime"/> without a kind is read as UTC. A null result
/// means the pair has no arithmetic or the result is out of range, so the value is missing.
/// </summary>
internal static class ExpressionArithmetic
{
    /// <summary>Applies the operator to two values that are not null.</summary>
    /// <returns>The result, or null when the values have no arithmetic or the result is out of range.</returns>
    public static object? Apply(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        if (ExpressionComparer.IsNumber(left) && ExpressionComparer.IsNumber(right))
        {
            return ApplyToNumbers(arithmeticOperator, left, right);
        }

        return arithmeticOperator switch
        {
            ArithmeticOperator.Add => ApplyToTimes(isSubtraction: false, left, right),
            ArithmeticOperator.Subtract => ApplyToTimes(isSubtraction: true, left, right),
            _ => null
        };
    }

    // Adding zero turns a negative zero into zero, which a host would otherwise write as "-0". A result that is no finite
    // number, such as that of a division by zero, is missing like a result out of range.
    private static object? ApplyToNumbers(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        // A float keeps the precision of a float, so that its result compares with a literal as the float itself does.
        if (left is float || right is float)
        {
            var single = (float)Compute(arithmeticOperator, ExpressionComparer.ToDouble(left), ExpressionComparer.ToDouble(right));
            return float.IsFinite(single) ? single + 0f : null;
        }

        if (ExpressionComparer.IsIntegerOrDecimal(left) && ExpressionComparer.IsIntegerOrDecimal(right) &&
            TryComputeExactly(arithmeticOperator, ExpressionComparer.ToDecimal(left), ExpressionComparer.ToDecimal(right), out var exact))
        {
            return exact;
        }

        var result = Compute(arithmeticOperator, ExpressionComparer.ToDouble(left), ExpressionComparer.ToDouble(right));
        return double.IsFinite(result) ? result + 0d : null;
    }

    private static double Compute(ArithmeticOperator arithmeticOperator, double left, double right) => arithmeticOperator switch
    {
        ArithmeticOperator.Add => left + right,
        ArithmeticOperator.Subtract => left - right,
        ArithmeticOperator.Multiply => left * right,
        _ => left / right
    };

    // Decimal arithmetic rounds instead of failing when a result has too many digits. It did not round when a sum keeps
    // the larger scale of its operands, a product has the sum of their scales, and a quotient times the divisor gives the
    // dividend back.
    private static bool TryComputeExactly(ArithmeticOperator arithmeticOperator, decimal left, decimal right, out decimal result)
    {
        result = 0;
        try
        {
            switch (arithmeticOperator)
            {
                case ArithmeticOperator.Add:
                    result = left + right;
                    return result.Scale == Math.Max(left.Scale, right.Scale);
                case ArithmeticOperator.Subtract:
                    result = left - right;
                    return result.Scale == Math.Max(left.Scale, right.Scale);
                case ArithmeticOperator.Multiply:
                    result = left * right;
                    return result.Scale == left.Scale + right.Scale;
                default:
                    if (right == 0)
                    {
                        return false;
                    }

                    result = left / right;
                    return result * right == left;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
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
