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

        // A valid path is a member-access chain rooted at the lambda parameter (e.g. x => x.A.B). Anything
        // else (empty chain, mid-chain indexer or method call, static member, captured variable) cannot be
        // a dotted path, so fail fast instead of returning a partial or wrong one.
        if (parts.Count == 0 || current is not ParameterExpression)
        {
            var stopNode = current switch
            {
                null => "a static or empty expression",
                ParameterExpression => "the lambda parameter itself", // parts is empty, e.g. x => x
                _ => current.NodeType.ToString()
            };

            throw new ArgumentException(
                $"Expression must be a simple member access chain rooted at the lambda parameter " +
                $"(e.g., x => x.Property), but got {stopNode}.",
                nameof(expression));
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Extracts the name of a single member access on the lambda parameter (e.g. <c>x =&gt; x.Property</c>).
    /// Used by type-level fluent mapping, where the selector must reference exactly one member of the
    /// configured type. Throws on a member chain, an indexer, or anything else.
    /// </summary>
    public static string GetSingleMemberName(Expression expression)
    {
        var current = expression;

        // Unwrap Convert/ConvertChecked (e.g. boxing to object).
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        if (current is MemberExpression { Expression: ParameterExpression } member)
        {
            return member.Member.Name;
        }

        throw new ArgumentException(
            "Expression must be a single member access on the lambda parameter (e.g., x => x.Property).",
            nameof(expression));
    }
}
