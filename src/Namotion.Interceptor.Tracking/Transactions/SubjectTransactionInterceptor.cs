using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Interceptor that captures property changes during transactions.
/// Should be registered before PropertyChangeObservable/Queue to suppress notifications during capture.
/// </summary>
public sealed class SubjectTransactionInterceptor : IReadInterceptor, IWriteInterceptor
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var transaction = SubjectTransaction.Current;

        // Return pending value if transaction active and not committing
        if (transaction is { IsCommitting: false } &&
            transaction.PendingChanges.TryGetValue(context.Property, out var change))
        {
            return change.GetNewValue<TProperty>();
        }

        return next(ref context);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var transaction = SubjectTransaction.Current;

        // Capture if: transaction active AND not committing AND not derived
        if (transaction is { IsCommitting: false } &&
            !context.Property.Metadata.IsDerived)
        {
            // Validate context binding (only when transaction is bound to a context)
            if (transaction.Context is { } transactionContext)
            {
                var subjectContext = context.Property.Subject.Context;
                var transactionLock = transactionContext.TryGetService<TransactionLock>();
                var subjectLock = subjectContext.TryGetService<TransactionLock>();

                if (transactionLock != subjectLock)
                {
                    throw new InvalidOperationException(
                        $"Cannot modify property '{context.Property.Metadata.Name}' - transaction is bound to a different context.");
                }
            }

            var currentContext = SubjectChangeContext.Current;

            // Preserve original old value for first write (used for conflict detection at commit)
            if (!transaction.PendingChanges.TryGetValue(context.Property, out var existingChange))
            {
                var change = SubjectPropertyChange.Create(
                    context.Property,
                    source: currentContext.Source,
                    changedTimestamp: currentContext.ChangedTimestamp,
                    receivedTimestamp: currentContext.ReceivedTimestamp,
                    context.CurrentValue,
                    context.NewValue);

                transaction.PendingChanges[context.Property] = change;
            }
            else
            {
                // Last write wins, but preserve original old value
                var change = SubjectPropertyChange.Create(
                    context.Property,
                    source: currentContext.Source,
                    changedTimestamp: currentContext.ChangedTimestamp,
                    receivedTimestamp: currentContext.ReceivedTimestamp,
                    existingChange.GetOldValue<TProperty>(),
                    context.NewValue);

                transaction.PendingChanges[context.Property] = change;
            }

            return; // Do NOT call next() - chain stops here
        }

        next(ref context); // No transaction or committing - normal flow
    }
}
