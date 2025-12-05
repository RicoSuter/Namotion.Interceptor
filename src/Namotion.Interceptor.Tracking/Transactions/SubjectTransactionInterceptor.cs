using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

// Import for GetLastChangedTimestamp extension method
using static Namotion.Interceptor.Tracking.Change.PropertyTimestampExtensions;

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
        if (transaction is { IsCommitting: false })
        {
            // Check for conflicts if FailOnConflict behavior is enabled
            if (transaction.ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
            {
                var lastChangedTimestamp = context.Property.GetLastChangedTimestamp();
                if (lastChangedTimestamp.HasValue && lastChangedTimestamp.Value > transaction.StartTimestamp)
                {
                    throw new TransactionConflictException([context.Property]);
                }
            }

            if (transaction.PendingChanges.TryGetValue(context.Property, out var change))
            {
                return change.GetNewValue<TProperty>();
            }
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
            // Validate context matches (only for context-bound transactions)
            // Check if the subject's context can access the same TransactionLock
            // This handles the case where subject.Context is an InterceptorExecutor
            // with the transaction's context as a fallback
            if (transaction.Context != null)
            {
                var subjectContext = context.Property.Subject.Context;
                var transactionLock = transaction.Context.TryGetService<TransactionLock>();
                var subjectLock = subjectContext.TryGetService<TransactionLock>();

                if (transactionLock != subjectLock)
                {
                    throw new InvalidOperationException(
                        $"Cannot modify property '{context.Property.Metadata.Name}' - transaction is bound to a different context.");
                }
            }

            // Check for conflicts if FailOnConflict behavior is enabled
            if (transaction.ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
            {
                var lastChangedTimestamp = context.Property.GetLastChangedTimestamp();
                if (lastChangedTimestamp.HasValue && lastChangedTimestamp.Value > transaction.StartTimestamp)
                {
                    throw new TransactionConflictException([context.Property]);
                }
            }

            var currentContext = SubjectChangeContext.Current;

            // Preserve original old value for first write
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
