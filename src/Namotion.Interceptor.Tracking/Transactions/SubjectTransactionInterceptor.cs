using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Interceptor that captures property changes during transactions.
/// Must run before all downstream interceptors to suppress side effects during capture.
/// Also manages the per-context transaction lock for serialized transactions.
/// </summary>
[RunsBefore(typeof(DerivedPropertyChangeHandler))]
[RunsBefore(typeof(PropertyChangeObservable))]
[RunsBefore(typeof(PropertyChangeQueue))]
public sealed class SubjectTransactionInterceptor : IReadInterceptor, IWriteInterceptor
{
    private readonly SemaphoreSlim _exclusiveTransactionLock = new(1, 1);

    /// <summary>
    /// Acquires the transaction lock for this context.
    /// Used by serialized transactions to ensure only one transaction executes at a time.
    /// </summary>
    internal async ValueTask<IDisposable> AcquireTransactionLockAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTransactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser(_exclusiveTransactionLock);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        // Fast path: Skip transaction check when no transaction is active (avoids AsyncLocal read)
        if (!SubjectTransaction.HasActiveTransaction)
        {
            return next(ref context);
        }

        var transaction = SubjectTransaction.Current;
        if (transaction is { IsCommitting: false })
        {
            lock (transaction.PendingChangesLock)
            {
                if (transaction.PendingChanges.TryGetValue(context.Property, out var change))
                {
                    // Return pending value if transaction active and not committing
                    return change.GetNewValue<TProperty>();
                }
            }
        }

        return next(ref context);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Fast path: Skip transaction check when no transaction is active (avoids AsyncLocal read)
        if (!SubjectTransaction.HasActiveTransaction)
        {
            next(ref context);
            return;
        }

        var transaction = SubjectTransaction.Current;
        if (transaction is { IsCommitting: false } &&
            !context.Property.Metadata.IsDerived)
        {
            // Validate context binding
            var subjectInterceptor = context.Property.Subject.Context.TryGetService<SubjectTransactionInterceptor>();
            if (subjectInterceptor != transaction.Interceptor)
            {
                throw new InvalidOperationException(
                    $"Cannot modify property '{context.Property.Metadata.Name}': Transaction is bound to a different context.");
            }

            var currentContext = SubjectChangeContext.Current;

            lock (transaction.PendingChangesLock)
            {
                var isFirstWrite = !transaction.PendingChanges.TryGetValue(context.Property, out var existingChange);
                if (isFirstWrite)
                {
                    // Preserve the original old value for first write (used for conflict detection at commit)
                    transaction.PendingChanges[context.Property] = SubjectPropertyChange.Create(
                        context.Property,
                        source: currentContext.Source,
                        changedTimestamp: currentContext.ChangedTimestamp,
                        receivedTimestamp: currentContext.ReceivedTimestamp,
                        context.CurrentValue,
                        context.NewValue);
                }
                else
                {
                    // Last write wins, but preserve original old value
                    transaction.PendingChanges[context.Property] = SubjectPropertyChange.Create(
                        context.Property,
                        source: currentContext.Source,
                        changedTimestamp: currentContext.ChangedTimestamp,
                        receivedTimestamp: currentContext.ReceivedTimestamp,
                        existingChange.GetOldValue<TProperty>(),
                        context.NewValue);
                }
            }

            return; // Do NOT call next(): Interceptor chain stops here
        }

        next(ref context); // No transaction or committing: Normal flow
    }

    private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
