using System.Buffers;
using System.Runtime.InteropServices;
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
    private readonly TransactionLocking _locking;
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
            // No source writer: the entire commit is in-process. For the default (Exclusive) locking
            // mode the lock is already held, so the whole flow runs synchronously without a
            // CancellationTokenSource or an async state machine. Optimistic locking needs an async lock
            // acquisition, so only that case falls back to the async wrapper.
            var lockTask = AcquireOptimisticLockIfNeededAsync(cancellationToken);
            if (lockTask.IsCompletedSuccessfully)
            {
                CommitInProcessOnly(lockTask.Result);
                return default;
            }

            return CommitInProcessAfterLockAsync(lockTask);
        }

        return CommitWithWriterAsync(writer, cancellationToken);
    }

    private async ValueTask CommitInProcessAfterLockAsync(ValueTask<IDisposable?> lockTask)
    {
        IDisposable? commitLock;
        try
        {
            commitLock = await lockTask.ConfigureAwait(false);
        }
        catch
        {
            // A failed optimistic lock acquire must not wedge the transaction: reset so a retry is possible.
            Volatile.Write(ref _commitStarted, 0);
            throw;
        }
        CommitInProcessOnly(commitLock);
    }

    /// <summary>
    /// Fully synchronous in-process commit when no <see cref="ITransactionWriter"/> is registered.
    /// </summary>
    private void CommitInProcessOnly(IDisposable? commitLock)
    {
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            var (rented, changes) = StartCommitAndSnapshotChanges();
            rentedArray = rented;

            ThrowIfConflictsDetected(changes.Span);

            var (applied, applyFailed, applyErrors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes.Span, exclude: null);

            SubjectTransactionException? failure = null;
            if (applyFailed.Count > 0)
            {
                if (_failureHandling == TransactionFailureHandling.Rollback)
                {
                    var (revertFailed, revertErrors) = RevertInProcess(applied);
                    failure = CreateFailureException([], Concat(applyFailed, revertFailed), Concat(applyErrors, revertErrors));
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
        // A throwing writer propagates here and bypasses source-revert (matches prior behavior).
        var (written, failedSource, sourceErrors, revertState) = await writer
            .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);

        // Rollback with any source-write failure: nothing is applied in-process; revert what reached
        // a source and report. The local (no-source) changes are never applied, but they are still
        // reported as failed (they did not commit), matching the all-or-nothing contract.
        if (_failureHandling == TransactionFailureHandling.Rollback && failedSource.Count > 0)
        {
            var revert = await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
            var notApplied = ExcludeByProperty(changes.Span, failedSource, written);
            return CreateFailureException(
                [],
                Concat(failedSource, notApplied, revert.Failed),
                Concat(sourceErrors, revert.Errors));
        }

        // Apply the whole snapshot except the source-write failures, in a single pass. With no source
        // failure this is the entire snapshot and ApplyAllChanges returns an empty Successful set.
        var exclude = failedSource.Count == 0 ? null : failedSource;
        var (applied, applyFailed, applyErrors) = SubjectPropertyChangeExtensions.ApplyAllChanges(changes.Span, exclude);

        if (applyFailed.Count > 0)
        {
            if (_failureHandling == TransactionFailureHandling.Rollback)
            {
                // All-or-nothing: revert in-process applies, then the source writes.
                var (revertFailed, revertErrors) = RevertInProcess(applied);
                var sourceRevert = await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
                return CreateFailureException(
                    [],
                    Concat(failedSource, applyFailed, revertFailed, sourceRevert.Failed),
                    Concat(sourceErrors, applyErrors, revertErrors, sourceRevert.Errors));
            }

            // BestEffort: keep source == model for failed-apply properties by reverting only the
            // source writes whose property failed to apply (matched by Property equality).
            var toRevert = IntersectByProperty(applyFailed, written);
            var bestEffortRevert = await writer.RevertSourceWritesAsync(toRevert, revertState, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                Concat(failedSource, applyFailed, bestEffortRevert.Failed),
                Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
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
    /// Reverts previously-applied in-process changes by applying their inverse values in reverse order.
    /// Returns any revert failures and errors so the caller can fold them into the exception.
    /// </summary>
    private static (IReadOnlyList<SubjectPropertyChange> Failed, IReadOnlyList<Exception> Errors) RevertInProcess(
        IReadOnlyList<SubjectPropertyChange> applied)
    {
        var rollback = applied.ToRollbackChanges();
        var (_, revertFailed, revertErrors) = SubjectPropertyChangeExtensions.ApplyAllChanges(
            CollectionsMarshal.AsSpan(rollback), exclude: null);
        return (revertFailed, revertErrors);
    }

    /// <summary>
    /// Returns the subset of <paramref name="written"/> whose property also appears in
    /// <paramref name="failed"/> (matched by <see cref="SubjectPropertyChange.Property"/>).
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> IntersectByProperty(
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<SubjectPropertyChange> written)
    {
        if (failed.Count == 0 || written.Count == 0)
        {
            return [];
        }

        var failedProperties = new HashSet<PropertyReference>(failed.Count, PropertyReference.Comparer);
        foreach (var change in failed)
        {
            failedProperties.Add(change.Property);
        }

        List<SubjectPropertyChange>? result = null;
        foreach (var change in written)
        {
            if (failedProperties.Contains(change.Property))
            {
                (result ??= new List<SubjectPropertyChange>(failed.Count)).Add(change);
            }
        }

        return result ?? (IReadOnlyList<SubjectPropertyChange>)[];
    }

    /// <summary>
    /// Returns the changes in <paramref name="changes"/> whose property is in neither
    /// <paramref name="excludeFirst"/> nor <paramref name="excludeSecond"/> (matched by
    /// <see cref="SubjectPropertyChange.Property"/>). Used to collect the local (no-source) changes that
    /// were neither written to a source nor failed at a source.
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> ExcludeByProperty(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> excludeFirst,
        IReadOnlyList<SubjectPropertyChange> excludeSecond)
    {
        if (excludeFirst.Count == 0 && excludeSecond.Count == 0)
        {
            return changes.ToArray();
        }

        var excluded = new HashSet<PropertyReference>(
            excludeFirst.Count + excludeSecond.Count, PropertyReference.Comparer);
        foreach (var change in excludeFirst)
        {
            excluded.Add(change.Property);
        }
        foreach (var change in excludeSecond)
        {
            excluded.Add(change.Property);
        }

        List<SubjectPropertyChange>? result = null;
        foreach (var change in changes)
        {
            if (!excluded.Contains(change.Property))
            {
                (result ??= []).Add(change);
            }
        }

        return result ?? (IReadOnlyList<SubjectPropertyChange>)[];
    }

    private static IReadOnlyList<T> Concat<T>(params ReadOnlySpan<IReadOnlyList<T>> lists)
    {
        var total = 0;
        IReadOnlyList<T>? single = null;
        var nonEmptyCount = 0;
        foreach (var list in lists)
        {
            if (list.Count == 0) continue;
            total += list.Count;
            single = list;
            nonEmptyCount++;
        }

        if (total == 0) return [];
        if (nonEmptyCount == 1) return single!; // avoid copying when only one list has items

        var result = new List<T>(total);
        foreach (var list in lists)
        {
            if (list.Count > 0) result.AddRange(list);
        }
        return result;
    }

    /// <summary>
    /// Clears pending changes and marks the transaction committed BEFORE any exception is thrown so
    /// subsequent property reads do not return stale captured values via TryGetPendingValue.
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
        lock (_pendingChangesLock)
        {
            // Clear inside the lock so a concurrent Dispose sees an in-flight commit and skips
            // returning the pooled buffer until this commit has finished using it.
            _isCommitting = false;
        }

        if (rentedArray != null)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray);
        }

        // Release the optimistic lock BEFORE resetting _commitStarted so a retry cannot start
        // before the optimistic lock is actually free.
        commitLock?.Dispose();

        if (!_isCommitted)
        {
            // Allow retry: reset so CommitAsync can be called again after a failure
            // (e.g., conflict detected, timeout, validation during replay).
            Volatile.Write(ref _commitStarted, 0);
        }
    }

    private void ValidateCanCommit()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(SubjectTransaction));

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (Interlocked.CompareExchange(ref _commitStarted, 1, 0) != 0)
            throw new InvalidOperationException("CommitAsync is already in progress.");
    }

    private ValueTask<IDisposable?> AcquireOptimisticLockIfNeededAsync(CancellationToken cancellationToken)
    {
        // Sync fast path for the default (Exclusive) locking mode: lock was already
        // acquired in BeginTransactionAsync, so commit needs no lock here.
        if (_locking != TransactionLocking.Optimistic)
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
            var conflictingProperties = DetectConflicts(changes);
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
            TransactionFailureHandling.BestEffort => "One or more changes failed. Successfully written changes have been applied.",
            TransactionFailureHandling.Rollback => "One or more changes failed. Rollback was attempted. No changes have been applied to the in-process model.",
            _ => "One or more changes failed."
        };

        return new SubjectTransactionException(message, successful, failed, errors);
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

            bool returnBuffer;
            lock (_pendingChangesLock)
            {
                _pendingChanges.Clear();
                // Defensive against misuse only: this branch is reached when Dispose races an in-flight
                // commit, which requires disposing without awaiting CommitAsync (or disposing cross-thread
                // mid-commit). Correct usage (await the commit, or never commit) always pools the buffer.
                // In that misuse case the commit still owns the buffer, so skip the pool return (it is then
                // GC'd) rather than hand a live buffer to another transaction and corrupt its pending changes.
                returnBuffer = !_isCommitting;
            }
            if (returnBuffer)
            {
                PendingChangesPool.Return(_pendingChanges);
            }

            CurrentTransaction.Value = null;
            _lockReleaser?.Dispose(); // May be null for Optimistic transactions
        }
    }
}
