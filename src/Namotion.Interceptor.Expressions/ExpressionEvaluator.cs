namespace Namotion.Interceptor.Expressions;

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
                return path.Variable is { } variable
                    ? context.ResolveVariable(variable, path.MemberPath)
                    : context.ResolvePath(path.Path);

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
            case NotNode notNode:
                return !EvaluateTruth(notNode.Operand, ref context);

            case AndNode andNode:
                return EvaluateAnd(andNode, ref context);

            case OrNode orNode:
                return EvaluateOr(orNode, ref context);

            case ComparisonNode comparison:
                return Compare(comparison, ref context);

            default:
                return EvaluateOperandTruth(node, ref context);
        }
    }

    private static bool? EvaluateAnd<TContext>(AndNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        // False wins over unknown, so the right side is not read when the left one is false.
        var left = EvaluateTruth(node.Left, ref context);
        if (left == false)
        {
            return false;
        }

        var right = EvaluateTruth(node.Right, ref context);
        if (right == false)
        {
            return false;
        }

        return left == true && right == true ? true : null;
    }

    private static bool? EvaluateOr<TContext>(OrNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        // True wins over unknown, so the right side is not read when the left one is true.
        var left = EvaluateTruth(node.Left, ref context);
        if (left == true)
        {
            return true;
        }

        var right = EvaluateTruth(node.Right, ref context);
        if (right == true)
        {
            return true;
        }

        return left == false && right == false ? false : null;
    }

    // A bare operand is true only when it is the boolean true, and unknown when its value is missing.
    private static bool? EvaluateOperandTruth<TContext>(ExpressionNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        return Evaluate(node, ref context).Value switch
        {
            bool boolean => boolean,
            null => null,
            _ => false
        };
    }

    private static bool? Compare<TContext>(ComparisonNode node, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        // A comparison with the literal null tests for a missing value and is never unknown.
        if (node.Left is LiteralNode { Value: null })
        {
            return CompareWithMissing(node.Operator, node.Right, ref context);
        }

        if (node.Right is LiteralNode { Value: null })
        {
            return CompareWithMissing(node.Operator, node.Left, ref context);
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

        return ApplyOperator(node.Operator, order);
    }

    private static bool? CompareWithMissing<TContext>(ComparisonOperator comparisonOperator, ExpressionNode other, ref TContext context)
        where TContext : struct, IExpressionContext
    {
        var isMissing = Evaluate(other, ref context).Value is null;
        return comparisonOperator switch
        {
            ComparisonOperator.Equal => isMissing,
            ComparisonOperator.NotEqual => !isMissing,
            _ => null
        };
    }

    private static bool ApplyOperator(ComparisonOperator comparisonOperator, int order) => comparisonOperator switch
    {
        ComparisonOperator.Equal => order == 0,
        ComparisonOperator.NotEqual => order != 0,
        ComparisonOperator.Less => order < 0,
        ComparisonOperator.LessOrEqual => order <= 0,
        ComparisonOperator.Greater => order > 0,
        _ => order >= 0
    };
}
