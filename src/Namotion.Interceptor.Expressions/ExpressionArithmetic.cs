namespace Namotion.Interceptor.Expressions;

/// <summary>Applies arithmetic and negation to values that are not null, as docs/expressions.md#arithmetic describes; a null result is missing.</summary>
internal static class ExpressionArithmetic
{
    /// <summary>Applies the operator to two values that are not null.</summary>
    /// <returns>The result, or null when the values have no arithmetic or the result is out of range.</returns>
    public static object? Apply(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        var leftKind = ExpressionComparer.GetNumberKind(left);
        if (leftKind != NumberKind.None)
        {
            var rightKind = ExpressionComparer.GetNumberKind(right);
            if (rightKind != NumberKind.None)
            {
                return ApplyToNumbers(arithmeticOperator, left, right, leftKind | rightKind);
            }
        }

        return ApplyToTimes(arithmeticOperator, left, right);
    }

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

    // Decimal arithmetic rounds instead of failing when a result has too many digits: a sum keeps the larger scale of its
    // operands and a product the sum of their scales unless the digits do not fit, and then the last decimal places are
    // dropped. A result with fewer places is exact only when the dropped digits were all zero. A quotient is exact when
    // it times the divisor gives the dividend back.
    private static bool TryComputeExactly(ArithmeticOperator arithmeticOperator, decimal left, decimal right, out decimal result)
    {
        result = 0;
        try
        {
            switch (arithmeticOperator)
            {
                case ArithmeticOperator.Add:
                    result = left + right;
                    return result.Scale == Math.Max(left.Scale, right.Scale) || IsSumExact(left, right, result.Scale);
                case ArithmeticOperator.Subtract:
                    result = left - right;
                    return result.Scale == Math.Max(left.Scale, right.Scale) || IsSumExact(left, -right, result.Scale);
                case ArithmeticOperator.Multiply:
                    result = left * right;
                    return result.Scale == left.Scale + right.Scale || IsProductExact(left, right, left.Scale + right.Scale - result.Scale);
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

    // The digits of the exact sum below the scale of the result are those of the operands below it, added up. Each
    // remainder is exact and smaller than one unit of that scale, so the test runs on small numbers that never round.
    private static bool IsSumExact(decimal left, decimal right, byte resultScale)
    {
        var unit = new decimal(1, 0, 0, false, resultScale);
        return (left % unit + right % unit) % unit == 0;
    }

    // The exact product is the product of the mantissas, divided by ten once per dropped digit. It has no remainder when
    // the mantissas together hold as many factors of two and as many factors of five as digits were dropped.
    private static bool IsProductExact(decimal left, decimal right, int droppedDigits)
    {
        var leftMantissa = GetMantissa(left);
        var rightMantissa = GetMantissa(right);
        if (leftMantissa == 0 || rightMantissa == 0)
        {
            return true;
        }

        return (int)UInt128.TrailingZeroCount(leftMantissa) + (int)UInt128.TrailingZeroCount(rightMantissa) >= droppedDigits &&
            CountFactorsOfFive(leftMantissa) + CountFactorsOfFive(rightMantissa) >= droppedDigits;
    }

    private static UInt128 GetMantissa(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        return new UInt128((uint)bits[2], ((ulong)(uint)bits[1] << 32) | (uint)bits[0]);
    }

    private static int CountFactorsOfFive(UInt128 mantissa)
    {
        var count = 0;
        while (mantissa % 5 == 0)
        {
            mantissa /= 5;
            count++;
        }

        return count;
    }

    private static object? ApplyToTimes(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        if (arithmeticOperator is ArithmeticOperator.Multiply or ArithmeticOperator.Divide)
        {
            return null;
        }

        var isSubtraction = arithmeticOperator == ArithmeticOperator.Subtract;
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
