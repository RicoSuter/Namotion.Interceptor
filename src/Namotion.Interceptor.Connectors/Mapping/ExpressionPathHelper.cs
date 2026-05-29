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

        var parts = new List<string>();
        while (current is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression;
        }

        // A valid path is a chain of member accesses rooted at the lambda parameter (e.g., x => x.A.B).
        // If the walk terminates on anything else - an empty chain, a mid-chain indexer or method call,
        // a static member, or a captured variable - the expression is not representable as a dotted path,
        // so fail fast rather than silently return a partial or wrong path. `current` is the node where
        // the walk stopped, so it names what actually failed.
        if (parts.Count == 0 || current is not ParameterExpression)
        {
            // `current` is the node where the walk stopped (null for a static member access).
            var stopNode = current is null ? "a static or empty expression" : current.NodeType.ToString();
            throw new ArgumentException(
                $"Expression must be a simple member access chain rooted at the lambda parameter " +
                $"(e.g., x => x.Property), but got {stopNode}.",
                nameof(expression));
        }

        parts.Reverse();
        return string.Join(".", parts);
    }
}
