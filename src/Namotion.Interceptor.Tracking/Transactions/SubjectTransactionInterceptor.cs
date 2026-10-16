using System.Collections.Immutable;
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
        if (transaction.IsCommitModelAccessAuthorized)
        {
            value = next(ref context);
            return true;
        }

        if (transaction.IsCommitValidationAuthorized)
        {
            value = transaction.TryGetCommitValidationValue<TProperty>(context.Property, out var validationValue)
                ? validationValue
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

        var transaction = SubjectTransaction.Current;
        if (transaction is null || transaction.IsDisposed || transaction.IsCommitted)
        {
            next(ref context);
            return;
        }

        if (transaction.IsCommitting && ShouldWriteThroughWhileCommitting(transaction))
        {
            next(ref context);
            return;
        }

        // Settable-derived writes are immediate and outside the transaction. This access linearizes at the
        // non-committing observation above; do not hold the transaction lock while a user setter executes.
        if (context.Property.Metadata.IsDerived)
        {
            next(ref context);
            return;
        }

        var subjectContext = context.Property.Subject.Context;
        ValidateTransactionContext(subjectContext, context.Property, transaction);

        transaction.IncludeHandlersOf(subjectContext);
        InvokeCapturingHandlers(transaction, context.Property, context.NewValue, context.Origin);

        // Origin comparison can run user equality code, so resolve it before taking the transaction lock.
        var resolvedOrigin = context.GetFinalOrigin();
        var changedTimestamp = context.WriteTimestampForPublishing;
        var receivedTimestamp = SubjectChangeContext.Current.ReceivedTimestamp;

        // Equality can run user code, so resolve it before taking the transaction lock, same as the origin above.
        var restoresLandedValue = EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue);

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
    /// Returns true when a write while <paramref name="transaction"/> commits goes straight to the model, and false
    /// when the transaction stopped committing and the caller continues the capturing write path. Throws when the
    /// write is not authorized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldWriteThroughWhileCommitting(SubjectTransaction transaction)
    {
        if (transaction.IsCommitModelAccessAuthorized)
        {
            return true;
        }

        if (transaction.IsCommitValidationAuthorized)
        {
            SubjectTransaction.ThrowCaptureDuringCommitValidation();
        }

        return transaction.IsInactiveUnderLockOrThrowIfCommitting();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateTransactionContext(
        IInterceptorSubjectContext subjectContext,
        PropertyReference property,
        SubjectTransaction transaction)
    {
        var subjectInterceptors = subjectContext.GetServices<SubjectTransactionInterceptor>();
        var isBoundToThisContext = subjectInterceptors.Length == 1
            ? ReferenceEquals(subjectInterceptors[0], transaction.Interceptor)
            : ContainsByReference(subjectInterceptors, transaction.Interceptor);

        if (!isBoundToThisContext)
        {
            ThrowTransactionContextMismatch(property);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InvokeCapturingHandlers<TProperty>(
        SubjectTransaction transaction,
        PropertyReference property,
        TProperty newValue,
        ChangeOrigin origin)
    {
        var handlers = transaction.Handlers;
        if (!handlers.IsDefaultOrEmpty)
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
        if (transaction.IsValidating)
        {
            SubjectTransaction.ThrowCaptureDuringCommitValidation();
        }

        foreach (var handler in handlers)
        {
            handler.OnCapturing(transaction, property, newValue, origin);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTransactionContextMismatch(PropertyReference property)
    {
        throw new InvalidOperationException(
            $"Cannot modify property '{property.Metadata.Name}': Transaction is bound to a different context.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ContainsByReference(
        ImmutableArray<SubjectTransactionInterceptor> interceptors,
        SubjectTransactionInterceptor target)
    {
        for (var index = 0; index < interceptors.Length; index++)
        {
            if (ReferenceEquals(interceptors[index], target))
            {
                return true;
            }
        }

        return false;
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
