using System.Runtime.CompilerServices;
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
            IsWriteBackOfPendingChange(context.Property))
        {
            next(ref context);
        }
    }

    // CurrentValue is the landed value, so inside a transaction a write back to it must still reach the
    // transaction, which cancels the pending change, instead of being dropped as unchanged.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWriteBackOfPendingChange(PropertyReference property)
    {
        return SubjectTransaction.HasActiveTransaction &&
            SubjectTransaction.HasPendingChangeInCurrentTransaction(property);
    }
}
