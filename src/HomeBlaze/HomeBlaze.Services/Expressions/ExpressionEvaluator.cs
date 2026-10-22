namespace HomeBlaze.Services.Expressions;

/// <summary>
/// Evaluates nodes with a context taken by reference. A switch over the node types instead of virtual methods keeps the
/// generic context a struct without boxing and needs no generic virtual dispatch under Native AOT.
/// </summary>
internal static class ExpressionEvaluator
{
    public static ExpressionValue Evaluate<TContext>(ExpressionNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        switch (node)
        {
            case LiteralNode literal:
                return ExpressionValue.FromValue(literal.Value);

            case PathNode path:
                return context.ResolvePath(path.Path);

            default:
                return ExpressionValue.FromBoolean(EvaluateTruth(node, ref context));
        }
    }

    /// <summary>Evaluates a node as a condition: true, false, or null for unknown.</summary>
    public static bool? EvaluateTruth<TContext>(ExpressionNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        switch (node)
        {
            case NotNode not:
                return !EvaluateTruth(not.Operand, ref context);

            case AndNode and:
            {
                // False wins over unknown, so the right side is not read when the left one is false.
                var left = EvaluateTruth(and.Left, ref context);
                if (left == false)
                {
                    return false;
                }

                var right = EvaluateTruth(and.Right, ref context);
                return right == false ? false : left == true && right == true ? true : null;
            }

            case OrNode or:
            {
                // True wins over unknown, so the right side is not read when the left one is true.
                var left = EvaluateTruth(or.Left, ref context);
                if (left == true)
                {
                    return true;
                }

                var right = EvaluateTruth(or.Right, ref context);
                return right == true ? true : left == false && right == false ? false : null;
            }

            case ComparisonNode comparison:
                return Compare(comparison, ref context);

            default:
                // A bare operand is true only when it is the boolean true, and unknown when its value is missing.
                return Evaluate(node, ref context).Value switch
                {
                    bool boolean => boolean,
                    null => null,
                    _ => false
                };
        }
    }

    private static bool? Compare<TContext>(ComparisonNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        // A comparison with the literal null tests for a missing value and is never unknown.
        if (node.Left is LiteralNode { Value: null } || node.Right is LiteralNode { Value: null })
        {
            var other = node.Left is LiteralNode { Value: null } ? node.Right : node.Left;
            var isMissing = Evaluate(other, ref context).Value is null;
            return node.Operator switch
            {
                ComparisonOperator.Equal => isMissing,
                ComparisonOperator.NotEqual => !isMissing,
                _ => null
            };
        }

        var left = Evaluate(node.Left, ref context).Value;
        var right = Evaluate(node.Right, ref context).Value;
        if (left is null || right is null)
        {
            return null;
        }

        if (ExpressionComparer.Compare(left, node.LeftInstant, right, node.RightInstant, node.IsEquality) is not { } order)
        {
            return null;
        }

        return node.Operator switch
        {
            ComparisonOperator.Equal => order == 0,
            ComparisonOperator.NotEqual => order != 0,
            ComparisonOperator.Less => order < 0,
            ComparisonOperator.LessOrEqual => order <= 0,
            ComparisonOperator.Greater => order > 0,
            _ => order >= 0
        };
    }
}
