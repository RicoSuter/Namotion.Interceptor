using System.Buffers;
using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a transaction that captures property changes and commits them atomically.
/// Changes are buffered until <see cref="CommitAsync"/> is called.
/// </summary>
public sealed class SubjectTransaction : IDisposable
{
    private static readonly AsyncLocal<SubjectTransaction?> CurrentTransaction = new();
    private static int _activeTransactionCount;

    /// <summary>
    /// Gets a value indicating whether any transaction is currently active across all contexts.
    /// This is a fast-path check using a volatile read; it may briefly return true after
    /// all transactions have completed due to memory visibility delays. This is safe because
    /// a false positive only results in an unnecessary AsyncLocal read.
    /// </summary>
    internal static bool HasActiveTransaction => Volatile.Read(ref _activeTransactionCount) > 0;

    private readonly TransactionFailureHandling _failureHandling;
    private readonly TransactionLocking _locking;
    private readonly TransactionRequirement _requirement;
    private readonly TimeSpan _commitTimeout;
    private readonly IDisposable? _lockReleaser; // null for Optimistic until commit

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _isDisposed;

    /// <summary>
    /// Gets the current transaction in this execution context, or null if none is active.
    /// </summary>
    public static SubjectTransaction? Current => CurrentTransaction.Value;

    /// <summary>
    /// Sets the current transaction in this execution context.
    /// This is needed for async patterns where AsyncLocal must be set in the caller's context.
    /// </summary>
    internal static void SetCurrent(SubjectTransaction? transaction)
    {
        CurrentTransaction.Value = transaction;
    }

    /// <summary>
    /// Gets a value indicating whether the transaction is currently committing changes.
    /// </summary>
    internal bool IsCommitting => _isCommitting;

    /// <summary>
    /// Gets the interceptor this transaction is bound to (for cross-context validation).
    /// </summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Thread-safe for concurrent property writes within the same transaction.
    /// </summary>
    internal ConcurrentDictionary<PropertyReference, SubjectPropertyChange> PendingChanges { get; } = new(PropertyReference.Comparer);

    /// <summary>
    /// Gets the pending changes as a read-only list.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges() => PendingChanges.Values.ToList();

    /// <summary>
    /// Gets the context this transaction is bound to.
    /// </summary>
    public IInterceptorSubjectContext Context { get; }

    /// <summary>
    /// Gets the conflict behavior for this transaction.
    /// </summary>
    public TransactionConflictBehavior ConflictBehavior { get; }

    /// <summary>
    /// Gets the locking mode for this transaction.
    /// </summary>
    public TransactionLocking Locking => _locking;

    private SubjectTransaction(
        IInterceptorSubjectContext context,
        SubjectTransactionInterceptor interceptor,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TimeSpan commitTimeout,
        IDisposable? lockReleaser)
    {
        Context = context;
        Interceptor = interceptor;
        _failureHandling = failureHandling;
        _locking = locking;
        _requirement = requirement;
        ConflictBehavior = conflictBehavior;
        _commitTimeout = commitTimeout;
        _lockReleaser = lockReleaser;

        // Increment in constructor ensures counter is always paired with Dispose
        Interlocked.Increment(ref _activeTransactionCount);
    }

    /// <summary>
    /// Begins a new transaction bound to the specified context.
    /// For Exclusive locking, waits if another transaction is active on this context.
    /// For Optimistic locking, returns immediately and acquires lock only during commit.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="failureHandling">The failure handling mode controlling what happens when writes fail.</param>
    /// <param name="locking">The locking mode controlling transaction synchronization.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="conflictBehavior">The conflict detection behavior.</param>
    /// <param name="commitTimeout">Timeout for commit operations. User cancellation is ignored during commit and only timeout-based cancellation is used. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable timeout.</param>
    /// <param name="cancellationToken">The cancellation token (used before commit starts, ignored during commit).</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when transactions are not enabled or when nested transaction is attempted.</exception>
    internal static async ValueTask<SubjectTransaction> BeginTransactionAsync(
        IInterceptorSubjectContext context,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TimeSpan commitTimeout,
        CancellationToken cancellationToken)
    {
        // Check for nested transactions BEFORE acquiring lock (prevents deadlock)
        if (CurrentTransaction.Value != null)
        {
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        var interceptor = context.TryGetService<SubjectTransactionInterceptor>()
            ?? throw new InvalidOperationException(
                "Transactions are not enabled. Call WithTransactions() when creating the context.");

        IDisposable? transactionLock = null;

        // For Exclusive locking, acquire the lock now
        // For Optimistic locking, skip the lock - we'll acquire it during commit only
        if (locking == TransactionLocking.Exclusive)
        {
            transactionLock = await interceptor.AcquireExclusiveTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        }

        // Don't set CurrentTransaction.Value here because it won't flow to caller's context
        // The caller (extension method) will call SetCurrent after awaiting this
        // Counter increment is in constructor to ensure it's always paired with Dispose
        return new SubjectTransaction(
            context,
            interceptor,
            failureHandling,
            locking,
            requirement,
            conflictBehavior,
            commitTimeout,
            transactionLock);
    }

    /// <summary>
    /// Commits all pending changes. If external write handlers are configured on subjects' contexts,
    /// changes are written to external sources first, then applied to the in-process model.
    /// The behavior on partial failure depends on the <see cref="_failureHandling"/> specified at transaction creation.
    /// </summary>
    /// <remarks>
    /// For Optimistic locking, the lock is acquired at the start of commit and released after completion.
    /// Conflict detection compares captured OldValue with current value at commit time.
    /// This catches changes from other transactions and external sources that occurred
    /// after the transaction started.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="TransactionException">Thrown when one or more external source writes failed.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(SubjectTransaction));

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (PendingChanges.Count == 0)
        {
            _isCommitted = true;
            return;
        }

        // For Optimistic locking, acquire the lock now (for commit phase only)
        IDisposable? commitLock = null;
        if (_locking == TransactionLocking.Optimistic)
        {
            commitLock = await Interceptor.AcquireExclusiveTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        }

        // Rent array from pool to avoid allocation
        var changeCount = PendingChanges.Count;
        var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount);
        try
        {
            _isCommitting = true;

            // Copy pending changes to rented array
            var index = 0;
            foreach (var change in PendingChanges.Values)
            {
                rentedArray[index++] = change;
            }
            var changes = rentedArray.AsMemory(0, changeCount);

            // 1. Conflict detection
            if (ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
            {
                var conflictingProperties = DetectConflicts(changes.Span);
                if (conflictingProperties.Count > 0)
                {
                    throw new TransactionConflictException(conflictingProperties);
                }
            }

            // 2. Create timeout-only token, ignoring user cancellation during commit
            // Use Timeout.InfiniteTimeSpan to disable timeout entirely
            using var timeoutCts = _commitTimeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(_commitTimeout);
            var commitToken = timeoutCts?.Token ?? CancellationToken.None;

            // 3. Call write handler and apply local changes
            var allSuccessfulChanges = new List<SubjectPropertyChange>();
            var allFailedChanges = new List<SubjectPropertyChange>();
            var allErrors = new List<Exception>();
            var localChangesToApply = new List<SubjectPropertyChange>();

            var writeHandler = Context.TryGetService<ITransactionWriter>();
            if (writeHandler != null)
            {
                // Writer handles source-bound changes and returns local changes for us to apply
                var result = await writeHandler.WriteChangesAsync(changes, _failureHandling, _requirement, commitToken);
                allSuccessfulChanges.AddRange(result.SuccessfulChanges);
                allFailedChanges.AddRange(result.FailedChanges);
                allErrors.AddRange(result.Errors);
                localChangesToApply.AddRange(result.LocalChanges);

                // If source writes failed in Rollback mode, don't apply local changes
                if (_failureHandling == TransactionFailureHandling.Rollback && allFailedChanges.Count > 0)
                {
                    // Local changes are not applied - add them to failed for reporting
                    allFailedChanges.AddRange(localChangesToApply);
                    localChangesToApply.Clear();
                }
            }
            else
            {
                // No writer: all changes are local
                foreach (var change in changes.Span)
                {
                    localChangesToApply.Add(change);
                }
            }

            // 4. Apply local changes to in-process model
            if (localChangesToApply.Count > 0)
            {
                var (applied, applyFailed, applyErrors) = localChangesToApply.ApplyAll();
                allSuccessfulChanges.AddRange(applied);
                allFailedChanges.AddRange(applyFailed);
                allErrors.AddRange(applyErrors);

                // In Rollback mode, revert everything if local changes failed
                if (_failureHandling == TransactionFailureHandling.Rollback && applyFailed.Count > 0)
                {
                    // Revert successful local applies
                    var (_, localRevertFailed, localRevertErrors) = applied.ToRollbackChanges().ApplyAll();
                    allFailedChanges.AddRange(localRevertFailed);
                    allErrors.AddRange(localRevertErrors);

                    // Revert source-bound changes by calling writer with rollback changes
                    if (writeHandler != null && allSuccessfulChanges.Count > 0)
                    {
                        // Use BestEffort for rollback - we want to revert as much as possible
                        var rollbackChanges = allSuccessfulChanges.ToRollbackChanges().ToArray();
                        var rollbackResult = await writeHandler.WriteChangesAsync(
                            rollbackChanges.AsMemory(),
                            TransactionFailureHandling.BestEffort,
                            TransactionRequirement.None,
                            commitToken);

                        allFailedChanges.AddRange(rollbackResult.FailedChanges);
                        allErrors.AddRange(rollbackResult.Errors);
                    }

                    // Clear successful changes since we rolled back
                    allSuccessfulChanges.Clear();
                }
            }

            // 5. Cleanup
            PendingChanges.Clear();
            _isCommitted = true;

            // 6. Throw if any writes failed
            if (allFailedChanges.Count > 0)
            {
                var message = _failureHandling switch
                {
                    TransactionFailureHandling.BestEffort => "One or more changes failed. Successfully written changes have been applied.",
                    TransactionFailureHandling.Rollback => "One or more changes failed. Rollback was attempted. No changes have been applied to the in-process model.",
                    _ => "One or more changes failed."
                };

                throw new TransactionException(message, allSuccessfulChanges, allFailedChanges, allErrors);
            }
        }
        finally
        {
            _isCommitting = false;
            // Return the rented array to the pool
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray);
            // Release the commit lock for Optimistic transactions
            commitLock?.Dispose();
        }
    }

    /// <summary>
    /// Detects conflicts by comparing captured OldValue with current actual value.
    /// </summary>
    private static List<PropertyReference> DetectConflicts(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        List<PropertyReference>? conflictingProperties = null;
        foreach (var change in changes)
        {
            var currentValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
            var capturedOldValue = change.GetOldValue<object?>();

            if (!Equals(currentValue, capturedOldValue))
            {
                conflictingProperties ??= [];
                conflictingProperties.Add(change.Property);
            }
        }
        return conflictingProperties ?? [];
    }

    /// <summary>
    /// Disposes the transaction, discarding any uncommitted changes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            Interlocked.Decrement(ref _activeTransactionCount);
            PendingChanges.Clear();
            CurrentTransaction.Value = null;
            _lockReleaser?.Dispose(); // May be null for Optimistic transactions
        }
    }
}
