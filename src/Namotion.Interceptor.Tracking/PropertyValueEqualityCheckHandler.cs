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
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue) ||
            IsWriteBackOfPendingChange(context.Property, context.NewValue))
        {
            next(ref context);
        }
    }

    // CurrentValue is the landed value, not the transaction's. A write of it is a change when the transaction holds
    // another value, so it is passed on and the transaction removes its pending change.
    private static bool IsWriteBackOfPendingChange<TProperty>(PropertyReference property, TProperty newValue)
    {
        return SubjectTransaction.HasActiveTransaction &&
            SubjectTransaction.TryGetPendingValueInCurrentTransaction(property, out TProperty pendingValue) &&
            !EqualityComparer<TProperty>.Default.Equals(pendingValue, newValue);
    }
}
