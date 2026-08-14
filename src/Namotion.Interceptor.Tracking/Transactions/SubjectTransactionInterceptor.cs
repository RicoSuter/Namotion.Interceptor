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
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        // Fast path: Skip transaction check when no transaction is active (avoids AsyncLocal read)
        if (!SubjectTransaction.HasActiveTransaction)
        {
            return next(ref context);
        }

        var transaction = SubjectTransaction.Current;
        // A disposed transaction holds no pending changes, so this check only skips a lock acquisition and a
        // lookup that would find nothing. The ambient slot can still point at one because a captured
        // ExecutionContext (for example an Rx scheduler thread created inside a transaction) keeps replaying
        // the flow it was captured from.
        if (transaction is { IsCommitting: false, IsDisposed: false } &&
            transaction.TryGetPendingValue<TProperty>(context.Property, out var pendingValue))
        {
            return pendingValue;
        }

        return next(ref context); // No transaction, disposed, committing, or nothing pending: Normal flow
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
        // A disposed transaction is treated as no transaction, so the write falls through to the model:
        // capturing into it would throw, and no commit is left to replay the change on. The ambient slot can
        // still point at one because a captured ExecutionContext (for example an Rx scheduler thread created
        // inside a transaction) keeps replaying the flow it was captured from.
        if (transaction is { IsCommitting: false, IsDisposed: false } && !context.Property.Metadata.IsDerived)
        {
            // Validate context binding
            var subjectInterceptor = context.Property.Subject.Context.TryGetService<SubjectTransactionInterceptor>();
            if (subjectInterceptor != transaction.Interceptor)
            {
                throw new InvalidOperationException(
                    $"Cannot modify property '{context.Property.Metadata.Name}': Transaction is bound to a different context.");
            }

            // Capture is a terminal outcome for the chain: the downstream write (and its origin
            // finalization) never runs. Finalize here so a stamped origin whose captured value was
            // transformed (e.g. an OnChanging clamp) demotes to Local, matching what the terminal
            // write would have produced. Otherwise the stale FromSource survives commit replay and
            // the corrected value is echo-suppressed, leaving the source diverged.
            // Speculative, because the capture below can still lose the race against a cross-flow dispose:
            // the terminal write reads the unfinalized origin to decide whether a source write may discard
            // a committed local write, so an abandoned finalization has to be rolled back.
            var attemptedOrigin = context.AttemptedOriginSnapshot;
            context.FinalizeOrigin();

            if (transaction.TryCaptureChange(
                    context.Property,
                    context.Origin,
                    context.WriteTimestampForPublishing,
                    SubjectChangeContext.Current.ReceivedTimestamp,
                    context.CurrentValue,
                    context.NewValue))
            {
                return; // Captured, interceptor chain stops here
            }

            // Another flow disposed the transaction between the check above and the capture. A disposed
            // transaction is no transaction, so the write falls through to the model with the speculative
            // finalization undone. Throwing here is not an option: this runs inside a property setter, and
            // an exception escaping one on a scheduler thread or a bare thread takes the process down.
            context.AttemptedOriginSnapshot = attemptedOrigin;
        }

        next(ref context); // No transaction, disposed, derived, committing, or capture lost the dispose race: Normal flow
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
