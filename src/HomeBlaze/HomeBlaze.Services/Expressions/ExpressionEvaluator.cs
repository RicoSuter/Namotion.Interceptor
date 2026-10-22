namespace HomeBlaze.Services.Expressions;

internal static class ExpressionEvaluator
{
    public static ExpressionValue Evaluate<TContext>(ExpressionNode node, ref TContext context)
        where TContext : struct, IExpressionContext =>
        throw new NotImplementedException();

    public static bool? EvaluateTruth<TContext>(ExpressionNode node, ref TContext context)
        where TContext : struct, IExpressionContext =>
        throw new NotImplementedException();
}
