using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Interceptor that captures property changes during transactions.
/// Should be registered before PropertyChangeObservable/Queue to suppress notifications during capture.
/// Also manages the per-context transaction lock for serialized transactions.
/// </summary>
public sealed class SubjectTransactionInterceptor : IReadInterceptor, IWriteInterceptor
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Acquires the transaction lock for this context.
    /// Used by serialized transactions to ensure only one transaction executes at a time.
    /// </summary>
    internal async ValueTask<IDisposable> AcquireLockAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser(_lock);
    }

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
            // Validate context binding (only when transaction is bound to an interceptor)
            if (transaction.Interceptor is { } transactionInterceptor)
            {
                var subjectInterceptor = context.Property.Subject.Context.TryGetService<SubjectTransactionInterceptor>();

                if (transactionInterceptor != subjectInterceptor)
                {
                    throw new InvalidOperationException(
                        $"Cannot modify property '{context.Property.Metadata.Name}' - transaction is bound to a different context.");
                }
            }

            var currentContext = SubjectChangeContext.Current;

            // Preserve original old value for first write (used for conflict detection at commit)
            if (!transaction.PendingChanges.TryGetValue(context.Property, out var existingChange))
            {
                var propertyChange = SubjectPropertyChange.Create(
                    context.Property,
                    source: currentContext.Source,
                    changedTimestamp: currentContext.ChangedTimestamp,
                    receivedTimestamp: currentContext.ReceivedTimestamp,
                    context.CurrentValue,
                    context.NewValue);

                transaction.PendingChanges[context.Property] = propertyChange;
            }
            else
            {
                // Last write wins, but preserve original old value
                var propertyChange = SubjectPropertyChange.Create(
                    context.Property,
                    source: currentContext.Source,
                    changedTimestamp: currentContext.ChangedTimestamp,
                    receivedTimestamp: currentContext.ReceivedTimestamp,
                    existingChange.GetOldValue<TProperty>(),
                    context.NewValue);

                transaction.PendingChanges[context.Property] = propertyChange;
            }

            return; // Do NOT call next() - chain stops here
        }

        next(ref context); // No transaction or committing - normal flow
    }

    private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                semaphore.Release();
                _disposed = true;
            }
        }
    }
}
