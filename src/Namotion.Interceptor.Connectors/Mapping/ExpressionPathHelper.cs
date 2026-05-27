using System.Linq.Expressions;

namespace Namotion.Interceptor.Connectors.Mapping;

public static class ExpressionPathHelper
{
    public static string GetPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;
        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }
        return string.Join(".", parts);
    }
}
