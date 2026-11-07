namespace Namotion.Interceptor.Expressions;

/// <summary>Applies arithmetic and negation to values that are not null, as docs/expressions.md#arithmetic describes; a null result is missing.</summary>
internal static class ExpressionArithmetic
{
    /// <summary>Applies the operator to two values that are not null.</summary>
    /// <returns>The result, or null when the values have no arithmetic or the result is out of range.</returns>
    public static object? Apply(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        // An instant or a duration has no number kind, so it skips the classification of the numbers.
        if (left is DateTimeOffset or TimeSpan or DateTime)
        {
            return ApplyToNonNumbers(arithmeticOperator, left, right);
        }

        var leftKind = ExpressionComparer.GetNumberKind(left);
        var rightKind = ExpressionComparer.GetNumberKind(right);
        if (leftKind != NumberKind.None && rightKind != NumberKind.None)
        {
            return ApplyToNumbers(arithmeticOperator, left, right, leftKind | rightKind);
        }

        return ApplyToNonNumbers(arithmeticOperator, left, right);
    }

    private static object? ApplyToNonNumbers(ArithmeticOperator arithmeticOperator, object left, object right) => arithmeticOperator switch
    {
        ArithmeticOperator.Add => ApplyToTimes(isSubtraction: false, left, right),
        ArithmeticOperator.Subtract => ApplyToTimes(isSubtraction: true, left, right),
        _ => null
    };

    /// <summary>Negates a value that is not null.</summary>
    /// <returns>The number or duration with the other sign, or null for another value or a result out of range.</returns>
    public static object? Negate(object value) => value switch
    {
        float single => float.IsFinite(single) ? -single + 0f : null,
        double number => double.IsFinite(number) ? -number + 0d : null,
        TimeSpan duration => duration == TimeSpan.MinValue ? null : duration.Negate(),
        _ when ExpressionComparer.GetNumberKind(value) != NumberKind.None => -Normalize(ExpressionComparer.ToDecimal(value)),
        _ => null
    };

    // Adding zero turns a negative zero into zero, which a host would otherwise write as "-0". A result that is no finite
    // number, such as that of a division by zero, is missing like a result out of range.
    private static object? ApplyToNumbers(ArithmeticOperator arithmeticOperator, object left, object right, NumberKind kinds)
    {
        // A float with a float or a literal keeps the precision of a float, so that its result compares with a literal as
        // the float itself does. With a double or an integer it computes as double, which keeps the precision of both.
        if (kinds is NumberKind.Float or (NumberKind.Float | NumberKind.Decimal))
        {
            var single = (float)Compute(arithmeticOperator, ExpressionComparer.ToDouble(left), ExpressionComparer.ToDouble(right));
            return float.IsFinite(single) ? single + 0f : null;
        }

        if (ExpressionComparer.IsExact(kinds) &&
            TryComputeExactly(arithmeticOperator, ExpressionComparer.ToDecimal(left), ExpressionComparer.ToDecimal(right), out var exact))
        {
            return Normalize(exact);
        }

        var result = Compute(arithmeticOperator, ExpressionComparer.ToDouble(left), ExpressionComparer.ToDouble(right));
        return double.IsFinite(result) ? result + 0d : null;
    }

    /// <summary>Removes the trailing zeros of a decimal, so that 1.50 times 2 is written as 3 and not as 3.00.</summary>
    // A division by one with the largest scale gives the smallest scale that keeps the value.
    internal static decimal Normalize(decimal value) => value.Scale == 0 ? value : value / 1.0000000000000000000000000000m;

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

                    // The product is checked as well, since a rounded quotient times the divisor can round back to the dividend.
                    result = left / right;
                    return TryComputeExactly(ArithmeticOperator.Multiply, result, right, out var product) && product == left;
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
            return ExpressionComparer.TryGetInstant(left, out var instant)
                ? ApplyToInstant(isSubtraction, instant, right)
                : ApplyToDuration(isSubtraction, left, right);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            // A result beyond the range of TimeSpan or DateTimeOffset is missing, as for a number out of range.
            return null;
        }
    }

    private static object? ApplyToInstant(bool isSubtraction, DateTimeOffset instant, object right) => right switch
    {
        TimeSpan duration => isSubtraction ? instant - duration : instant + duration,
        _ when isSubtraction && ExpressionComparer.TryGetInstant(right, out var other) => instant - other,
        _ => null
    };

    private static object? ApplyToDuration(bool isSubtraction, object left, object right) => (left, right) switch
    {
        (TimeSpan duration, TimeSpan other) => isSubtraction ? duration - other : duration + other,
        (TimeSpan duration, _) when !isSubtraction && ExpressionComparer.TryGetInstant(right, out var instant) => instant + duration,
        _ => null
    };
}
