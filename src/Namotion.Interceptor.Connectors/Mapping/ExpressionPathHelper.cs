using System.Linq.Expressions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Extracts dotted property paths from LINQ expressions.
/// </summary>
public static class ExpressionPathHelper
{
    public static string GetPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;
        while (current is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
