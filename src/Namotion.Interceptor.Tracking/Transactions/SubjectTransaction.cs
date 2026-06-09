using System.Buffers;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Performance;

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
    private readonly TransactionRequirement _requirement;
    private readonly TimeSpan _commitTimeout;
    private readonly IDisposable? _lockReleaser; // null for Optimistic until commit

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Preserves insertion order so that commit replays changes in the order they were written.
    /// Access must be synchronized via <see cref="_pendingChangesLock"/>.
    /// </summary>
    private static readonly ObjectPool<OrderedDictionary<PropertyReference, SubjectPropertyChange>> PendingChangesPool
        = new(() => new OrderedDictionary<PropertyReference, SubjectPropertyChange>(PropertyReference.Comparer));

    private readonly OrderedDictionary<PropertyReference, SubjectPropertyChange> _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Handoff from Dispose to EndCommit: when Dispose runs mid-commit, EndCommit releases the exclusive lock
    // once the commit finishes. Accessed only under _pendingChangesLock (no volatile needed; never read lock-free).
    private bool _disposeRequestedDuringCommit;

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
    /// Gets a snapshot of the pending changes as a read-only list, in insertion order.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges()
    {
        lock (_pendingChangesLock)
        {
            return _pendingChanges.Values.ToList();
        }
    }

    /// <summary>
    /// Tries to read a pending value for the given property.
    /// Returns false if the transaction is committing or no pending value exists.
    /// </summary>
    internal bool TryGetPendingValue<TProperty>(PropertyReference property, out TProperty value)
    {
        lock (_pendingChangesLock)
        {
            ThrowIfCommittingConcurrently();

            if (_pendingChanges.TryGetValue(property, out var change))
            {
                value = change.GetNewValue<TProperty>();
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Atomically captures a property change into the pending dictionary.
    /// First write preserves <paramref name="currentValue"/> as old value for conflict detection;
    /// subsequent writes preserve the original old value (last write wins).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if a concurrent commit is in progress (TOCTOU race).</exception>
    internal void CaptureChange<TProperty>(
        PropertyReference property,
        object? source,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TProperty currentValue,
        TProperty newValue)
    {
        lock (_pendingChangesLock)
        {
            ThrowIfCommittingConcurrently();

            var isFirstWrite = !_pendingChanges.TryGetValue(property, out var existingChange);
            _pendingChanges[property] = SubjectPropertyChange.Create(
                property,
                source: source,
                changedTimestamp: changedTimestamp,
                receivedTimestamp: receivedTimestamp,
                isFirstWrite ? currentValue : existingChange.GetOldValue<TProperty>(),
                newValue);
        }
    }

    private void ThrowIfCommittingConcurrently()
    {
        if (_isCommitting)
        {
            throw new InvalidOperationException(
                "Cannot access transactional property while commit is in progress. " +
                "This typically indicates the transaction is being used from multiple threads.");
        }
    }

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
    public TransactionLocking Locking { get; }

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
        Locking = locking;
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
            transactionLock = await interceptor.AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
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
    /// changes are written to external sources first, then applied to the local model.
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
    /// <exception cref="SubjectTransactionException">Thrown when one or more changes failed to commit.</exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ValidateCanCommit();

        lock (_pendingChangesLock)
        {
            if (_pendingChanges.Count == 0)
            {
                _isCommitted = true;
                return default;
            }
        }

        var writer = Context.TryGetService<ITransactionWriter>();
        if (writer is null)
        {
            // No source writer: the entire commit is local. For the default (Exclusive) locking
            // mode the lock is already held, so the whole flow runs synchronously without a
            // CancellationTokenSource or an async state machine. Optimistic locking needs an async lock
            // acquisition, so only that case falls back to the async wrapper.
            var lockTask = AcquireOptimisticLockIfNeededAsync(cancellationToken);
            if (lockTask.IsCompletedSuccessfully)
            {
                CommitWithoutWriter(lockTask.Result);
                return default;
            }

            return CommitLocalAfterLockAsync(lockTask);
        }

        return CommitWithWriterAsync(writer, cancellationToken);
    }

    private async ValueTask CommitLocalAfterLockAsync(ValueTask<IDisposable?> lockTask)
    {
        IDisposable? commitLock;
        try
        {
            commitLock = await lockTask.ConfigureAwait(false);
        }
        catch
        {
            // A failed optimistic lock must not wedge the transaction: reset so a retry is possible.
            Volatile.Write(ref _commitStarted, 0);
            throw;
        }
        CommitWithoutWriter(commitLock);
    }

    /// <summary>
    /// Fully synchronous local commit when no <see cref="ITransactionWriter"/> is registered.
    /// </summary>
    private void CommitWithoutWriter(IDisposable? commitLock)
    {
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            var (rented, changes) = StartCommitAndSnapshotChanges();
            rentedArray = rented;

            ThrowIfConflictsDetected(changes.Span);

            var (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude: null);

            SubjectTransactionException? failure = null;
            if (applyFailed.Count > 0)
            {
                if (_failureHandling == TransactionFailureHandling.Rollback)
                {
                    var (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied);
                    failure = CreateFailureException([], SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed), SubjectPropertyChangeOperations.Concat(applyErrors, revertErrors));
                }
                else
                {
                    failure = CreateFailureException(applied, applyFailed, applyErrors);
                }
            }

            FinishCommit();

            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            EndCommit(rentedArray, commitLock);
        }
    }

    /// <summary>
    /// Commit when an <see cref="ITransactionWriter"/> is registered.
    /// </summary>
    private async ValueTask CommitWithWriterAsync(ITransactionWriter writer, CancellationToken cancellationToken)
    {
        IDisposable? commitLock = null;
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            commitLock = await AcquireOptimisticLockIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var (rented, changes) = StartCommitAndSnapshotChanges();
            rentedArray = rented;

            ThrowIfConflictsDetected(changes.Span);

            using var timeoutCts = CreateCommitTimeoutCts();
            var commitToken = timeoutCts?.Token ?? CancellationToken.None;

            var failure = await ReconcileWithWriterAsync(writer, changes, commitToken).ConfigureAwait(false);

            FinishCommit();

            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            EndCommit(rentedArray, commitLock);
        }
    }

    /// <summary>
    /// Implements the failure/rollback matrix for the writer path. Returns null on full success.
    /// </summary>
    private async ValueTask<SubjectTransactionException?> ReconcileWithWriterAsync(
        ITransactionWriter writer,
        Memory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // The writer is contractually expected to report failures via SourceWriteResult, not throw.
        // If it throws anyway (and this is not our commit timeout), there is no SourceWriteResult, so we
        // have neither the written set nor the revertState needed to revert; sources cannot be reverted.
        // The local model is untouched (apply runs only after this returns), so report every change as
        // failed. Returning a failure routes through FinishCommit() in the caller, making the transaction
        // terminal (non-retryable) and consistent with a reported full source failure.
        SourceWriteResult writeResult;
        try
        {
            writeResult = await writer
                .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubjectTransactionException(
                "The transaction writer threw an exception during commit. Sources may be in an undefined, " +
                "un-reverted state; the transaction is terminal and must be disposed, not retried.",
                appliedChanges: [],
                failedChanges: changes.ToArray(),
                errors: [exception]);
        }

        var (written, failedSource, sourceErrors, revertState) = writeResult;

        // Rollback with any source-write failure: nothing is applied to the local model; revert what reached
        // a source and report. The local (no-source) changes are never applied, but they are still
        // reported as failed (they did not commit), matching the all-or-nothing contract.
        if (_failureHandling == TransactionFailureHandling.Rollback && failedSource.Count > 0)
        {
            var revert = await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
            var notApplied = SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, written);
            return CreateFailureException(
                [],
                SubjectPropertyChangeOperations.Concat(failedSource, notApplied, revert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, revert.Errors));
        }

        // Apply the whole snapshot except the source-write failures in a single pass. With no source
        // failure, this is the entire snapshot, and ApplyAllChanges returns an empty Successful set.
        var exclude = failedSource.Count == 0 ? null : failedSource;
        var (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude);

        if (applyFailed.Count > 0)
        {
            if (_failureHandling == TransactionFailureHandling.Rollback)
            {
                // All-or-nothing: revert local applies, then the source writes.
                var (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied);
                var sourceRevert = await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
                // The no-source local changes were applied then reverted; under all-or-nothing they did not
                // commit, so report them as failed too (consistent with the source-write-failure branch and
                // the documented Rollback contract). Exclude source-bound changes (in 'written'; failedSource
                // is empty on this branch) and anything already reported as an apply/revert failure.
                var rolledBackLocals = SubjectPropertyChangeOperations.ExcludeByProperty(
                    changes.Span, written, SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed));
                return CreateFailureException(
                    [],
                    SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, rolledBackLocals, revertFailed, sourceRevert.Failed),
                    SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, revertErrors, sourceRevert.Errors));
            }

            // BestEffort: keep source == model for failed-apply properties by reverting only the
            // source writes whose property failed to apply (matched by Property equality).
            var toRevert = SubjectPropertyChangeOperations.IntersectByProperty(applyFailed, written);
            var bestEffortRevert = await writer.RevertSourceWritesAsync(toRevert, revertState, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, bestEffortRevert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
        }

        // Apply succeeded. If sources partially failed (BestEffort, since Rollback handled above),
        // report the partial success.
        if (failedSource.Count > 0)
        {
            return CreateFailureException(applied, failedSource, sourceErrors);
        }

        return null;
    }

    /// <summary>
    /// Clears pending changes and marks the transaction committed BEFORE any exception is thrown so
    /// later property reads do not return stale captured values via TryGetPendingValue.
    /// </summary>
    private void FinishCommit()
    {
        lock (_pendingChangesLock)
        {
            _pendingChanges.Clear();
        }

        _isCommitted = true;
    }

    /// <summary>
    /// Common cleanup for every commit path (success or failure): resets commit state for retry on
    /// failure, returns the pooled snapshot array, and releases the optimistic lock.
    /// </summary>
    private void EndCommit(SubjectPropertyChange[]? rentedArray, IDisposable? commitLock)
    {
        bool releaseDeferredLock;
        lock (_pendingChangesLock)
        {
            // Clear under the lock so a concurrent Dispose observes the in-flight commit consistently: while
            // _isCommitting is true Dispose skips returning the pooled buffer (this commit owns it) and defers
            // the exclusive-lock release to us. Reading the flag here makes that release exactly-once
            // (whichever of Dispose/EndCommit observes the other). The ArrayPool buffer below is owned solely
            // by this commit and is always returned here.
            _isCommitting = false;
            releaseDeferredLock = _disposeRequestedDuringCommit;
        }

        if (rentedArray != null)
        {
            // clearArray: true because SubjectPropertyChange holds object references (subject, source,
            // boxed value holders); leaving them in the pooled buffer would keep those graphs alive
            // until the slot is next overwritten.
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
        }

        // Release the optimistic lock BEFORE resetting _commitStarted so a retry cannot start
        // before the optimistic lock is actually free.
        commitLock?.Dispose();

        // Release the exclusive lock deferred by a concurrent Dispose, now that the commit has fully finished
        // (so no other transaction on this context could interleave with its apply pass).
        if (releaseDeferredLock)
        {
            _lockReleaser?.Dispose();
        }

        if (!_isCommitted)
        {
            // Reset so CommitAsync can be called again, but only for failures that occur BEFORE any
            // change is applied to the local model (conflict detected, optimistic lock acquisition failed,
            // or the commit timed out). A writer that throws is converted to a full failure that runs
            // through FinishCommit, so it is terminal and not retried. Once the apply pass runs,
            // FinishCommit has marked the transaction committed and this branch is skipped, so an
            // apply/validation failure is terminal and cannot be retried.
            Volatile.Write(ref _commitStarted, 0);
        }
    }

    private void ValidateCanCommit()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(SubjectTransaction));

        if (!ReferenceEquals(CurrentTransaction.Value, this))
            throw new InvalidOperationException(
                "Transaction is being committed from a different async flow than the one it is active in. " +
                "Begin, use, commit, and dispose a transaction within the same async flow.");

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (Interlocked.CompareExchange(ref _commitStarted, 1, 0) != 0)
            throw new InvalidOperationException("CommitAsync is already in progress.");
    }

    private ValueTask<IDisposable?> AcquireOptimisticLockIfNeededAsync(CancellationToken cancellationToken)
    {
        // Sync fast path for the default (Exclusive) locking mode: lock was already
        // acquired in BeginTransactionAsync, so commit needs no lock here.
        if (Locking != TransactionLocking.Optimistic)
        {
            return default;
        }
        return AcquireOptimisticLockSlowAsync(cancellationToken);
    }

    private async ValueTask<IDisposable?> AcquireOptimisticLockSlowAsync(CancellationToken cancellationToken)
    {
        return await Interceptor.AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
    }

    private (SubjectPropertyChange[] RentedArray, Memory<SubjectPropertyChange> Changes) StartCommitAndSnapshotChanges()
    {
        lock (_pendingChangesLock)
        {
            // Rent the ArrayPool buffer BEFORE setting _isCommitting so an OOM in Rent leaves the
            // transaction reusable (the flag is never set, no cleanup is owed).
            var changeCount = _pendingChanges.Count;
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount);

            // Set _isCommitting inside the lock to prevent concurrent writes from being
            // captured into _pendingChanges between the copy and the flag update (TOCTOU).
            // Once set, EndCommit (run only in the caller's finally) is responsible for clearing it
            // and returning the rented buffer.
            _isCommitting = true;

            var index = 0;
            foreach (var change in _pendingChanges.Values)
            {
                rentedArray[index++] = change;
            }

            return (rentedArray, rentedArray.AsMemory(0, changeCount));
        }
    }

    private void ThrowIfConflictsDetected(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var conflictingProperties = SubjectPropertyChangeOperations.DetectChangeConflicts(changes);
            if (conflictingProperties.Count > 0)
            {
                throw new SubjectTransactionConflictException(conflictingProperties);
            }
        }
    }

    private CancellationTokenSource? CreateCommitTimeoutCts()
    {
        return _commitTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_commitTimeout);
    }

    private SubjectTransactionException CreateFailureException(
        IReadOnlyList<SubjectPropertyChange> successful,
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<Exception> errors)
    {
        var message = _failureHandling switch
        {
            TransactionFailureHandling.BestEffort => "One or more changes failed. Successful changes have been applied.",
            TransactionFailureHandling.Rollback => "One or more changes failed. Rollback was attempted. No changes have been applied to the local model.",
            _ => "One or more changes failed."
        };

        return new SubjectTransactionException(message, successful, failed, errors);
    }

    /// <summary>
    /// Disposes the transaction, discarding any uncommitted changes.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            Interlocked.Decrement(ref _activeTransactionCount);

            bool committing;
            lock (_pendingChangesLock)
            {
                _pendingChanges.Clear();
                committing = _isCommitting;
                if (committing)
                {
                    _disposeRequestedDuringCommit = true;
                }
            }

            CurrentTransaction.Value = null;

            // A commit in flight (reachable only by misuse: disposing without awaiting CommitAsync) still owns
            // the pooled buffer and the exclusive lock, so skip both here: returning the buffer would corrupt
            // another transaction's pending changes, and releasing the lock would drop mutual exclusion mid-apply.
            // EndCommit releases the deferred lock once the commit completes; the buffer is left unpooled (GC'd).
            if (!committing)
            {
                PendingChangesPool.Return(_pendingChanges);
                _lockReleaser?.Dispose(); // May be null for Optimistic transactions
            }
        }
    }
}
