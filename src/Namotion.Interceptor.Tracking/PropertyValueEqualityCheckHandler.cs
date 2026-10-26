using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Interceptor that checks if the new value is different from the current value
/// and only calls the next interceptor when the property has actually changed.
/// </summary>
[RunsFirst]
public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // CurrentValue is the landed value, so inside a transaction a write back to it is passed on: the transaction
        // removes its pending change instead of keeping a value the write meant to undo.
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue) ||
            (SubjectTransaction.HasActiveTransaction && SubjectTransaction.HasPendingChangeInCurrentTransaction(context.Property)))
        {
            next(ref context);
        }
    }
}
