using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
public sealed class SubjectTransactionInterceptor : IReadInterceptor, IWriteInterceptor,
    ISingletonContextService<SubjectTransactionInterceptor>
{
    private readonly SemaphoreSlim _exclusiveTransactionLock = new(1, 1);

    /// <summary>
    /// Acquires the transaction lock for this context. Used by optimistic transactions to hold the lock for the
    /// duration of a commit.
    /// </summary>
    /// <returns>An object whose first disposal releases the lock.</returns>
    internal async ValueTask<IDisposable> AcquireTransactionLockAsync(CancellationToken cancellationToken)
    {
        await _exclusiveTransactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser(_exclusiveTransactionLock);
    }

    /// <summary>
    /// Acquires the transaction lock for this context without a releaser object. Used by exclusive transactions to hold
    /// the lock for their lifetime. Once the returned task completes, the caller must call
    /// <see cref="ReleaseExclusiveTransactionLock"/> exactly once.
    /// </summary>
    internal Task AcquireExclusiveTransactionLockAsync(CancellationToken cancellationToken)
    {
        return _exclusiveTransactionLock.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the lock acquired by <see cref="AcquireExclusiveTransactionLockAsync"/>.
    /// </summary>
    internal void ReleaseExclusiveTransactionLock()
    {
        _exclusiveTransactionLock.Release();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        if (!SubjectTransaction.HasActiveTransaction)
        {
            return next(ref context);
        }

        var transaction = SubjectTransaction.Current;
        if (transaction is null || transaction.IsDisposed || transaction.IsCommitted)
        {
            return next(ref context);
        }

        if (transaction.IsCommitting && TryReadWhileCommitting(transaction, ref context, next, out var committingValue))
        {
            return committingValue;
        }

        // Recorder-active reads are the landed-model view. This access linearizes at the non-committing
        // observation above; do not hold the transaction lock while a user getter executes.
        if (DerivedPropertyChangeHandler.IsRecordingDerivedProperty)
        {
            return next(ref context);
        }

        if (transaction.TryGetPendingValue<TProperty>(context.Property, out var pendingValue))
        {
            return pendingValue;
        }

        return next(ref context);
    }

    /// <summary>
    /// Reads a property while <paramref name="transaction"/> commits. Returns false when the transaction stopped
    /// committing and the caller continues the capturing read path; throws when the read is not authorized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadWhileCommitting<TProperty>(
        SubjectTransaction transaction,
        ref PropertyReadContext<TProperty> context,
        ReadInterceptionDelegate<TProperty> next,
        out TProperty value)
    {
        if (transaction.IsInCommitScope)
        {
            // Recorder-active reads are the landed-model view, also when a check recalculates a derived property.
            value = transaction.IsInHandlerCheck &&
                !DerivedPropertyChangeHandler.IsRecordingDerivedProperty &&
                transaction.TryGetCheckedValue<TProperty>(context.Property, out var checkedValue)
                ? checkedValue
                : next(ref context);
            return true;
        }

        if (transaction.IsInactiveUnderLockOrThrowIfCommitting())
        {
            value = next(ref context);
            return true;
        }

        value = default!;
        return false;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!SubjectTransaction.HasActiveTransaction)
        {
            next(ref context);
            return;
        }

        // Settable-derived writes are immediate and outside the transaction. This access linearizes at the
        // non-committing observation in WritesThrough; do not hold the transaction lock while a user setter
        // executes.
        var transaction = SubjectTransaction.Current;
        if (WritesThrough(transaction) || context.Property.Metadata.IsDerived)
        {
            next(ref context);
            return;
        }

        // Singleton per context, so instance identity is context identity. The subject's context is then the
        // transaction's, whose handlers the transaction resolved when it began.
        if (!ReferenceEquals(this, transaction.Interceptor))
        {
            ThrowTransactionContextMismatch(context.Property);
        }

        InvokeCapturingHandlers(transaction, context.Property, context.NewValue, context.Origin);

        // Origin comparison can run user equality code, so resolve it before taking the transaction lock.
        var resolvedOrigin = context.GetFinalOrigin();
        var changedTimestamp = context.WriteTimestampForPublishing;
        var receivedTimestamp = SubjectChangeContext.Current.ReceivedTimestamp;

        // Equality can run user code, so resolve it before taking the transaction lock, same as the origin above.
        var restoresLandedValue = IsWriteBackWithEqualityCheck(ref context);

        if (transaction.TryCaptureChange(
                context.Property,
                resolvedOrigin,
                changedTimestamp,
                receivedTimestamp,
                context.CurrentValue,
                context.NewValue,
                restoresLandedValue))
        {
            return;
        }

        next(ref context);
    }

    /// <summary>
    /// Records a write of the landed value, which the equality check drops as unchanged, for the commit check when
    /// <paramref name="transaction"/>, the current flow's transaction, captures and <see cref="SubjectTransaction.RecordsCheckOnlyEntries"/> (see
    /// <see cref="SubjectTransaction.RecordCheckOnly{TProperty}"/>). Otherwise, and for a derived property or a
    /// subject of another context, the write stays dropped.
    /// </summary>
    internal static void RecordUnchangedWrite<TProperty>(SubjectTransaction transaction, ref PropertyWriteContext<TProperty> context)
    {
        if (transaction is not { RecordsCheckOnlyEntries: true, IsDisposed: false, IsCommitted: false } ||
            transaction.IsCommitting ||
            context.Property.Metadata.IsDerived)
        {
            return;
        }

        if (!ReferenceEquals(context.Property.Subject.TryGetContext(), transaction.Context))
        {
            return;
        }

        InvokeCapturingHandlers(transaction, context.Property, context.NewValue, context.Origin);
        transaction.RecordCheckOnly(
            context.Property,
            context.GetFinalOrigin(),
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            context.NewValue);
    }

    // Only with the equality check: without it every write is captured, as outside a transaction every write lands.
    private static bool IsWriteBackWithEqualityCheck<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        // The value comparison first: most captured writes differ, so the service lookup runs only for write backs.
        return EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue) &&
            context.Property.Subject.TryGetContext() is { } subjectContext &&
            !subjectContext.GetServices<PropertyValueEqualityCheckHandler>().IsDefaultOrEmpty;
    }

    /// <summary>
    /// Whether a write passes straight through: no transaction, a finished one, or a committing one that
    /// <see cref="ShouldWriteThroughWhileCommitting"/> lets through.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool WritesThrough([NotNullWhen(false)] SubjectTransaction? transaction)
    {
        if (transaction is null || transaction.IsDisposed || transaction.IsCommitted)
        {
            return true;
        }

        return transaction.IsCommitting && ShouldWriteThroughWhileCommitting(transaction);
    }

    /// <summary>
    /// Returns true when a write while <paramref name="transaction"/> commits goes straight to the model, and false
    /// when the transaction stopped committing and the caller continues the capturing write path. Throws when the
    /// write is not authorized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldWriteThroughWhileCommitting(SubjectTransaction transaction)
    {
        if (transaction.IsInCommitScope)
        {
            if (transaction.IsInHandlerCheck)
            {
                SubjectTransaction.ThrowWriteDuringHandlerCheck();
            }

            return true;
        }

        return transaction.IsInactiveUnderLockOrThrowIfCommitting();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InvokeCapturingHandlers<TProperty>(
        SubjectTransaction transaction,
        PropertyReference property,
        TProperty newValue,
        ChangeOrigin origin)
    {
        var handlers = transaction.Handlers;
        if (!handlers.IsEmpty)
        {
            InvokeCapturingHandlersCore(handlers, transaction, property, newValue, origin);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeCapturingHandlersCore<TProperty>(
        ImmutableArray<ITransactionHandler> handlers,
        SubjectTransaction transaction,
        PropertyReference property,
        TProperty newValue,
        ChangeOrigin origin)
    {
        var context = new TransactionCaptureContext<TProperty>(transaction, property, newValue, origin);
        foreach (var handler in handlers)
        {
            handler.OnCapturing(in context);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTransactionContextMismatch(PropertyReference property)
    {
        throw new InvalidOperationException(
            $"Cannot modify property '{property.Metadata.Name}': Transaction is bound to a different context.");
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
