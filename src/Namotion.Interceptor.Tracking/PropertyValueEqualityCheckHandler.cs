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
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(ref context);
        }
        else if (SubjectTransaction.HasActiveTransaction && SubjectTransaction.Current is { } transaction)
        {
            // CurrentValue is the landed value, not the transaction's. A write of it is a change when the transaction
            // holds another value, so it is passed on and the transaction removes its pending change.
            if (transaction.TryGetPendingValueForWriteBack(context.Property, out TProperty pendingValue) &&
                !EqualityComparer<TProperty>.Default.Equals(pendingValue, context.NewValue))
            {
                next(ref context);
            }
            else
            {
                // A Deferred transaction with FailOnConflict records the unchanged write for the commit check.
                // Recording follows the async flow of SubjectTransaction.Current, so tasks started in the
                // transaction's flow record too; it is not tied to the thread or sync context.
                SubjectTransactionInterceptor.RecordUnchangedWrite(transaction, ref context);
            }
        }
    }
}
