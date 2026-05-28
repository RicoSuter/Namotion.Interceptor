using System.Linq.Expressions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Extracts dotted property paths from LINQ expressions.
/// </summary>
public static class ExpressionPathHelper
{
    public static string GetPathFromExpression(Expression expression)
    {
        var current = expression;

        // Unwrap Convert/ConvertChecked nodes (e.g., boxing to object)
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        // Capture the (unwrapped) root node so the error message reports what actually failed,
        // not the original outer expression.
        var unwrapped = current;

        var parts = new List<string>();
        while (current is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression;
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException(
                $"Expression must be a member access (e.g., x => x.Property), but got {unwrapped.NodeType}.",
                nameof(expression));
        }

        parts.Reverse();
        return string.Join(".", parts);
    }
}
