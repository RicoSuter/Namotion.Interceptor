using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Applies an arithmetic operator to two values that are not null. Numbers of any numeric type compute as
/// <see cref="double"/>. A null result means the pair has no arithmetic or the result is no finite number, so the value
/// is missing.
/// </summary>
internal static class ExpressionArithmetic
{
    /// <summary>Applies the operator to two values that are not null.</summary>
    /// <returns>The result, or null when the values have no arithmetic or the result is out of range.</returns>
    public static object? Apply(ArithmeticOperator arithmeticOperator, object left, object right)
    {
        if (ExpressionComparer.IsNumber(left) && ExpressionComparer.IsNumber(right))
        {
            return ApplyToNumbers(arithmeticOperator, ToDouble(left), ToDouble(right));
        }

        return null;
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

        // A division by zero gives infinity or NaN, which is unknown like NaN in a comparison.
        return double.IsFinite(result) ? result : null;
    }

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
