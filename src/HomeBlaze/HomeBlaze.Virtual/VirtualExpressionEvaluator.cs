using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Virtual;

/// <summary>The getter of an expression property. Never throws: a getter that throws fails the read and the add.</summary>
internal sealed class VirtualExpressionEvaluator(ParsedExpression expression, VirtualDataType dataType, IInterceptorSubject scope, VirtualRuntime runtime)
{
    public VirtualMisses Misses { get; } = new(runtime.Awaiter);

    public object? Evaluate(IInterceptorSubject subject)
    {
        try
        {
            var value = VirtualExpression.Evaluate(expression, runtime.Resolver, scope, out var misses);
            Misses.Record(misses);
            return value.IsMissing ? null : VirtualValueConverter.ToDataType(value.Value, dataType);
        }
        catch (Exception exception)
        {
            // Repeats with every recalculation, so it is not logged as a warning.
            runtime.Logger.LogDebug(exception, "The virtual property expression {Expression} failed.", expression.Text);
            return null;
        }
    }
}
