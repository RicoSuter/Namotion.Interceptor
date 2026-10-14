using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a transaction that captures property changes and commits them atomically.
/// Changes are buffered until <see cref="CommitAsync"/> is called.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "Transaction buffering, commit, failure handling and disposal coordinate one shared state; splitting these responsibilities would fragment its synchronization and lifecycle invariants.")]
public sealed class SubjectTransaction : IDisposable
{
    private static readonly AsyncLocal<SubjectTransaction?> CurrentTransaction = new();
    private static int _activeTransactionCount;

    [ThreadStatic]
    private static SubjectTransaction? _commitModelAccessTransaction;

    [ThreadStatic]
    private static SubjectTransaction? _commitValidationTransaction;

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
    private readonly ImmutableArray<ITransactionHandler> _handlers;

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Preserves insertion order so that commit replays changes in the order they were written.
    /// Access must be synchronized via <see cref="_pendingChangesLock"/>.
    /// </summary>
    private static readonly ObjectPool<OrderedDictionary<PropertyReference, SubjectPropertyChange>> PendingChangesPool
        = new(() => new OrderedDictionary<PropertyReference, SubjectPropertyChange>(PropertyReference.Comparer));

    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Handoff from Dispose to EndCommit: when Dispose runs mid-commit, EndCommit releases the exclusive lock
    // once the commit finishes. Accessed only under _pendingChangesLock (no volatile needed; never read lock-free).
    private bool _disposeRequestedDuringCommit;

    // Set while commit handlers check the snapshot, before _isCommitting: captures throw so the checked snapshot is
    // the one that commits. Written only under _pendingChangesLock.
    private bool _isValidating;

    // The changes whose source write failed, set only while the handlers check the set right before the local apply.
    // Read only by the committing thread inside its commit validation scope, so it needs no lock.
    private IReadOnlyList<SubjectPropertyChange>? _commitValidationExcludedChanges;

    /// <summary>
    /// Gets the current transaction in this execution context, or null if none is active.
    /// </summary>
    public static SubjectTransaction? Current => CurrentTransaction.Value;

    /// <summary>
    /// Enables runtime validation that a custom <see cref="ITransactionWriter"/> fulfills the in-place
    /// marking contract of <see cref="ITransactionWriter.WriteToSourcesAsync"/>: the commit then fails
    /// terminally if the writer moved or replaced a snapshot slot. Intended for developing custom writers
    /// (costs one array allocation and sweep per commit); process-wide, set once at startup.
    /// </summary>
    public static bool ValidateWriterContract { get; set; }

    /// <summary>
    /// Returns true when the current write is a commit applying the value of <paramref name="property"/> that
    /// <paramref name="handler"/> checked in <see cref="ITransactionHandler.OnCommitting"/>, or a rollback restoring
    /// it. Reference-type values match by identity, value-type values by equality. A call by one of the transaction's
    /// handlers consumes the mark, so it returns true at most once per applied value.
    /// </summary>
    /// <typeparam name="TProperty">The type of the written value.</typeparam>
    /// <param name="handler">The handler asking whether it already checked the write.</param>
    /// <param name="property">The property being written.</param>
    /// <param name="value">The value being written.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCommitWrite<TProperty>(ITransactionHandler handler, PropertyReference property, TProperty value)
    {
        return CommitWriteMarker.TryConsume(handler, property, value);
    }

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
    /// Gets a value indicating whether commit handlers are checking the snapshot.
    /// </summary>
    // Lock-free early check; the authoritative check stays in TryCaptureChange under the lock.
    internal bool IsValidating => Volatile.Read(ref _isValidating);

    /// <summary>
    /// Gets a value indicating whether the transaction has reached a terminal committed state.
    /// </summary>
    internal bool IsCommitted => _isCommitted;

    /// <summary>
    /// Gets a value indicating whether the transaction has been disposed.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>
    /// Gets a value indicating whether this thread is synchronously replaying this transaction's model work.
    /// </summary>
    internal bool IsCommitModelAccessAuthorized => ReferenceEquals(_commitModelAccessTransaction, this);

    /// <summary>
    /// Gets a value indicating whether this thread runs this transaction's handlers right before the local apply.
    /// </summary>
    internal bool IsCommitValidationAuthorized => ReferenceEquals(_commitValidationTransaction, this);

    /// <summary>
    /// Gets the interceptor this transaction is bound to (for cross-context validation).
    /// </summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>
    /// Gets the transaction handlers resolved from the context when the transaction began.
    /// </summary>
    internal ImmutableArray<ITransactionHandler> Handlers => _handlers;

    // Applies carry the commit write mark only when handlers checked the final set.
    private SubjectTransaction? MarkingTransaction => _handlers.IsDefaultOrEmpty ? null : this;

    /// <summary>
    /// Gets a snapshot of the pending changes as a read-only list, in insertion order.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges()
    {
        lock (_pendingChangesLock)
        {
            return _pendingChanges?.Values.ToList() ?? [];
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
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                value = default!;
                return false;
            }

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
    /// Reads a pending value for the handlers that run right before the local apply, while the transaction is
    /// committing. Returns false when the property is not part of the transaction or its source write failed.
    /// </summary>
    internal bool TryGetCommitValidationValue<TProperty>(PropertyReference property, out TProperty value)
    {
        if (!IsExcludedFromCommitValidation(property))
        {
            lock (_pendingChangesLock)
            {
                if (_pendingChanges is not null && _pendingChanges.TryGetValue(property, out var change))
                {
                    value = change.GetNewValue<TProperty>();
                    return true;
                }
            }
        }

        value = default!;
        return false;
    }

    private bool IsExcludedFromCommitValidation(PropertyReference property)
    {
        var excludedChanges = _commitValidationExcludedChanges;
        if (excludedChanges is null)
        {
            return false;
        }

        for (var index = 0; index < excludedChanges.Count; index++)
        {
            if (PropertyReference.Comparer.Equals(excludedChanges[index].Property, property))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Atomically captures a property change into the pending dictionary.
    /// First write preserves <paramref name="currentValue"/> as old value for conflict detection;
    /// subsequent writes preserve the original old value (last write wins). Returns false when
    /// disposal wins the race, allowing the caller to continue the normal write chain.
    /// </summary>
    /// <param name="property">The property being written.</param>
    /// <param name="origin">The resolved origin of the change.</param>
    /// <param name="changedTimestamp">The timestamp to publish for this change.</param>
    /// <param name="receivedTimestamp">The timestamp the change was received, if any.</param>
    /// <param name="currentValue">The landed model value seen by the write interceptor chain.</param>
    /// <param name="newValue">The value being written.</param>
    /// <param name="restoresLandedValue">
    /// True when the new value equals the landed value; an existing pending change for the property is
    /// then cancelled instead of updated.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a concurrent commit is in progress (TOCTOU race) or commit handlers are checking the snapshot.
    /// </exception>
    internal bool TryCaptureChange<TProperty>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TProperty currentValue,
        TProperty newValue,
        bool restoresLandedValue)
    {
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                return false;
            }

            ThrowIfCommittingConcurrently();
            if (_isValidating)
            {
                ThrowCaptureDuringCommitValidation();
            }

            var pendingChanges = _pendingChanges;
            var isFirstWrite = !pendingChanges.TryGetValue(property, out var existingChange);

            // A write back to the landed value cancels an existing pending change, so the outcome matches
            // never having written the property: no no-op change reaches sources, batch limits or conflicts.
            if (!isFirstWrite && restoresLandedValue)
            {
                pendingChanges.Remove(property);
                return true;
            }

            pendingChanges[property] = SubjectPropertyChange.Create(
                property,
                origin: origin,
                changedTimestamp: changedTimestamp,
                receivedTimestamp: receivedTimestamp,
                isFirstWrite ? currentValue : existingChange.GetOldValue<TProperty>(),
                newValue);
            return true;
        }
    }

    /// <summary>
    /// Returns true when the current flow's transaction is capturing and already holds a pending change for
    /// <paramref name="property"/>.
    /// </summary>
    internal static bool HasPendingChangeInCurrentTransaction(PropertyReference property)
    {
        var transaction = CurrentTransaction.Value;
        if (transaction is null || transaction.IsDisposed || transaction._isCommitted || transaction._isCommitting)
        {
            return false;
        }

        lock (transaction._pendingChangesLock)
        {
            return transaction._pendingChanges?.ContainsKey(property) == true;
        }
    }

    /// <summary>
    /// Rechecks an interceptor's lock-free state observation. Returns true when the transaction became
    /// inactive, throws while a live commit still owns the model, and otherwise returns false so the caller
    /// can continue the live non-committing path.
    /// </summary>
    internal bool IsInactiveUnderLockOrThrowIfCommitting()
    {
        lock (_pendingChangesLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
            {
                return true;
            }

            ThrowIfCommittingConcurrently();
            return false;
        }
    }

    private void ThrowIfCommittingConcurrently()
    {
        if (_isCommitting)
        {
            throw new InvalidOperationException(
                "Cannot access transactional property while commit is in progress. " +
                "Source writes and transaction writer callbacks run on the committing flow: build the " +
                "payload from the supplied changes instead of reading or writing subject properties. " +
                "This is also reported when the transaction is used from another thread.");
        }
    }

    internal static void ThrowCaptureDuringCommitValidation()
    {
        throw new InvalidOperationException(
            "Cannot write a transactional property while transaction handlers check the commit. " +
            "Transaction handlers must not write properties. " +
            "This is also reported when the transaction is used from another thread.");
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

    /// <summary>
    /// Gets the validation mode for this transaction.
    /// </summary>
    public TransactionValidationMode ValidationMode { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The private constructor explicitly initializes the transaction context, policies and acquired lock ownership as one coherent state.")]
    private SubjectTransaction(
        IInterceptorSubjectContext context,
        SubjectTransactionInterceptor interceptor,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TransactionValidationMode validationMode,
        TimeSpan commitTimeout,
        IDisposable? lockReleaser)
    {
        Context = context;
        Interceptor = interceptor;
        _failureHandling = failureHandling;
        Locking = locking;
        _requirement = requirement;
        ConflictBehavior = conflictBehavior;
        ValidationMode = validationMode;
        _commitTimeout = commitTimeout;
        _lockReleaser = lockReleaser;
        _handlers = context.GetServices<ITransactionHandler>();

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
    /// <param name="validationMode">The validation mode.</param>
    /// <param name="commitTimeout">Timeout for commit operations. User cancellation is ignored during commit and only timeout-based cancellation is used. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable timeout.</param>
    /// <param name="cancellationToken">The cancellation token (used before commit starts, ignored during commit).</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when transactions are not enabled or when nested transaction is attempted.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The factory forwards the complete set of transaction policies to the private constructor as one coherent state.")]
    internal static async ValueTask<SubjectTransaction> BeginTransactionAsync(
        IInterceptorSubjectContext context,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TransactionValidationMode validationMode,
        TimeSpan commitTimeout,
        CancellationToken cancellationToken)
    {
        // Check before acquiring the context lock so an attempted nested exclusive transaction cannot deadlock.
        if (CurrentTransaction.Value is { IsDisposed: false })
        {
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        var interceptor = context.TryGetService<SubjectTransactionInterceptor>()
            ?? throw new InvalidOperationException(
                "Transactions are not enabled. Call WithTransactions() when creating the context.");

        IDisposable? transactionLock = null;

        if (locking == TransactionLocking.Exclusive)
        {
            transactionLock = await interceptor.AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        }

        // The caller assigns the transaction so the value flows into its execution context.
        return new SubjectTransaction(
            context,
            interceptor,
            failureHandling,
            locking,
            requirement,
            conflictBehavior,
            validationMode,
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when commit is called from a different async flow than the one the transaction is active in,
    /// when the transaction was already committed, or when another commit is already in progress.
    /// </exception>
    /// <exception cref="SubjectTransactionException">
    /// Thrown when one or more changes failed to commit. A registered <see cref="ITransactionWriter"/>
    /// that throws instead of reporting failures makes the transaction terminal (it must be disposed,
    /// not retried) and may leave its sources un-reverted.
    /// </exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ValidateCanCommit();

        lock (_pendingChangesLock)
        {
            if (_pendingChanges is null || _pendingChanges.Count == 0)
            {
                _isCommitted = true;
                return default;
            }
        }

        ITransactionWriter? writer;
        try
        {
            writer = Context.TryGetService<ITransactionWriter>();
        }
        catch
        {
            // No commit work has started, so a corrected registration can be retried.
            Volatile.Write(ref _commitStarted, 0);
            throw;
        }

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

            SubjectTransactionException? failure = null;
            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(changes.Span);

                var (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude: null, MarkingTransaction);

                if (applyFailed.Count > 0)
                {
                    if (_failureHandling == TransactionFailureHandling.Rollback)
                    {
                        var (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied, MarkingTransaction);
                        failure = CreateFailureException([], SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed), SubjectPropertyChangeOperations.Concat(applyErrors, revertErrors));
                    }
                    else
                    {
                        failure = CreateFailureException(applied, applyFailed, applyErrors);
                    }
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

            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(changes.Span);
            }

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
        // The writer marks accepted snapshot slots with the confirming source so the local apply and
        // revert notifications are echo-suppressed by the outbound connector queue (#343).
        var capturedProperties = ValidateWriterContract ? CaptureSnapshotProperties(changes.Span) : null;

        // A throwing writer returns neither the written set nor the revert state, so sources cannot be
        // reverted: report a full failure, which the caller makes terminal. A contract violation is
        // terminal too (source writes already happened, a retry would repeat them). Only a commit-timeout
        // OperationCanceledException propagates and stays retryable; the built-in writer throws it
        // only from its revert phase (write-phase cancellation is reported as a failure).
        SourceWriteResult writeResult;
        try
        {
            writeResult = await writer
                .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);

            VerifySnapshotIntegrity(capturedProperties, changes.Span);
        }
        catch (Exception exception) when (!IsCommitCancellation(exception, cancellationToken))
        {
            return new SubjectTransactionException(
                "The transaction writer threw an exception or violated its contract during commit. Sources " +
                "may be in an undefined, un-reverted state; the transaction is terminal and must be disposed, " +
                "not retried.",
                appliedChanges: [],
                failedChanges: changes.ToArray(),
                errors: [exception]);
        }

        var (written, failedSource, sourceErrors, revertState) = writeResult;

        // Source failures leave local changes uncommitted, and written source changes must be reverted.
        // Errors without failed changes from custom writers must still be reported.
        if (_failureHandling == TransactionFailureHandling.Rollback && (failedSource.Count > 0 || sourceErrors.Count > 0))
        {
            var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, cancellationToken).ConfigureAwait(false);
            var notApplied = SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, written);
            return CreateFailureException(
                [],
                SubjectPropertyChangeOperations.Concat(failedSource, notApplied, revert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, revert.Errors));
        }

        return await ApplyAfterSourceWritesAsync(writer, changes, writeResult, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the final set again and applies it locally after the source writes, reverting sources where the
    /// failure handling requires it. Returns null on full success.
    /// </summary>
    private async ValueTask<SubjectTransactionException?> ApplyAfterSourceWritesAsync(
        ITransactionWriter writer,
        Memory<SubjectPropertyChange> changes,
        SourceWriteResult writeResult,
        CancellationToken cancellationToken)
    {
        var (written, failedSource, sourceErrors, revertState) = writeResult;

        // What the handlers read can change while the sources are written (inbound updates, raw writes), so the
        // final set is checked again right before it lands.
        var (invalid, invalidErrors) = CheckBeforeLocalApply(changes.Span, failedSource);
        IReadOnlyList<SubjectPropertyChange> invalidWritten = [];
        if (invalid.Count > 0)
        {
            if (_failureHandling == TransactionFailureHandling.Rollback)
            {
                var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, cancellationToken).ConfigureAwait(false);
                var notApplied = SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, invalid, written);
                return CreateFailureException(
                    [],
                    SubjectPropertyChangeOperations.Concat(invalid, notApplied, revert.Failed),
                    SubjectPropertyChangeOperations.Concat(invalidErrors, revert.Errors));
            }

            // BestEffort: skip the invalid changes like failed source writes, and revert the sources that accepted them.
            invalidWritten = SubjectPropertyChangeOperations.IntersectByProperty(invalid, written);
            failedSource = SubjectPropertyChangeOperations.Concat(failedSource, invalid);
            sourceErrors = SubjectPropertyChangeOperations.Concat(sourceErrors, invalidErrors);
        }

        // Apply the whole snapshot except the source-write failures in a single pass. With no source
        // failure, this is the entire snapshot, and ApplyAllChanges returns an empty Successful set.
        var exclude = failedSource.Count == 0 ? null : failedSource;
        IReadOnlyList<SubjectPropertyChange> applied;
        IReadOnlyList<SubjectPropertyChange> applyFailed;
        IReadOnlyList<Exception> applyErrors;
        IReadOnlyList<SubjectPropertyChange> revertFailed = [];
        IReadOnlyList<Exception> revertErrors = [];
        using (EnterCommitModelAccess())
        {
            (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude, MarkingTransaction);
            if (applyFailed.Count > 0 && _failureHandling == TransactionFailureHandling.Rollback)
            {
                (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied, MarkingTransaction);
            }
        }

        if (applyFailed.Count > 0)
        {
            if (_failureHandling == TransactionFailureHandling.Rollback)
            {
                // All-or-nothing: revert local applies, then the source writes.
                var sourceRevert = await RevertSourceWritesSafelyAsync(writer, written, revertState, cancellationToken).ConfigureAwait(false);
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
            var toRevert = SubjectPropertyChangeOperations.Concat(SubjectPropertyChangeOperations.IntersectByProperty(applyFailed, written), invalidWritten);
            var bestEffortRevert = await RevertSourceWritesSafelyAsync(writer, toRevert, revertState, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, bestEffortRevert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
        }

        if (invalidWritten.Count > 0)
        {
            var invalidRevert = await RevertSourceWritesSafelyAsync(writer, invalidWritten, revertState, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, invalidRevert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, invalidRevert.Errors));
        }

        return CreateFailureAfterSuccessfulLocalApply(changes, exclude, applied, failedSource, sourceErrors);
    }

    private SubjectTransactionException? CreateFailureAfterSuccessfulLocalApply(
        Memory<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange>? exclude,
        IReadOnlyList<SubjectPropertyChange> applied,
        IReadOnlyList<SubjectPropertyChange> failedSource,
        IReadOnlyList<Exception> sourceErrors)
    {
        if (failedSource.Count > 0 || sourceErrors.Count > 0)
        {
            // Without exclusions ApplyLocalChanges leaves its successful list empty, so the snapshot
            // must be copied before the pooled storage is returned at commit completion.
            var appliedChanges = exclude is null ? changes.ToArray() : applied;
            return CreateFailureException(appliedChanges, failedSource, sourceErrors);
        }

        return null;
    }

    private static PropertyReference[] CaptureSnapshotProperties(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var properties = new PropertyReference[changes.Length];
        for (var i = 0; i < changes.Length; i++)
        {
            properties[i] = changes[i].Property;
        }
        return properties;
    }

    /// <summary>
    /// Verifies no snapshot slot was moved or replaced by the writer. Checks slot identity only; tampered
    /// values or timestamps are not detected. No-op when validation is disabled (null capture).
    /// </summary>
    private static void VerifySnapshotIntegrity(PropertyReference[]? capturedProperties, ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (capturedProperties is null)
        {
            return;
        }

        for (var i = 0; i < changes.Length; i++)
        {
            if (!PropertyReference.Comparer.Equals(changes[i].Property, capturedProperties[i]))
            {
                throw new InvalidOperationException(
                    "The transaction writer violated its contract: it changed the property at snapshot slot " +
                    $"{i} instead of only marking the accepting source. A writer must change only the Source " +
                    "of an accepted slot and must never move a change to a different slot.");
            }
        }
    }

    /// <summary>
    /// Converts a contract-violating throw from <see cref="ITransactionWriter.RevertSourceWritesAsync"/>
    /// into "every requested revert failed" so the commit stays on the reported-failure path and becomes
    /// terminal; a propagating throw would leave the transaction retryable, and a retry would re-push
    /// to sources that already accepted writes. Only the commit timeout propagates.
    /// </summary>
    private static async ValueTask<SourceRevertResult> RevertSourceWritesSafelyAsync(
        ITransactionWriter writer,
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        try
        {
            return await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCommitCancellation(exception, cancellationToken))
        {
            return new SourceRevertResult(written, [exception]);
        }
    }

    private static bool IsCommitCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Clears pending changes and marks the transaction committed BEFORE any exception is thrown so
    /// later property reads do not return stale captured values via TryGetPendingValue.
    /// </summary>
    private void FinishCommit()
    {
        lock (_pendingChangesLock)
        {
            _pendingChanges?.Clear();
            _isCommitted = true;
        }
    }

    /// <summary>
    /// Common cleanup for every commit path (success or failure): resets commit state for retry on
    /// failure, returns the pooled snapshot array, and releases the optimistic lock.
    /// </summary>
    private void EndCommit(SubjectPropertyChange[]? rentedArray, IDisposable? commitLock)
    {
        bool releaseDeferredLock;
        OrderedDictionary<PropertyReference, SubjectPropertyChange>? pendingChangesToReturn = null;
        lock (_pendingChangesLock)
        {
            // Clear under the lock so Dispose observes the in-flight commit consistently. A disposing caller
            // defers exclusive-lock release until commit completion so another transaction cannot interleave
            // with the apply pass. If it also owns the pending dictionary, detach it here and return it after
            // the lock so ownership transfers exactly once.
            _isCommitting = false;
            releaseDeferredLock = _disposeRequestedDuringCommit;
            if (releaseDeferredLock)
            {
                pendingChangesToReturn = _pendingChanges;
                _pendingChanges = null;
            }
        }

        if (pendingChangesToReturn is not null)
        {
            PendingChangesPool.Return(pendingChangesToReturn);
        }

        if (rentedArray != null)
        {
            // clearArray: true because SubjectPropertyChange holds object references (subject, source,
            // boxed value holders); leaving them in the pooled buffer would keep those graphs alive
            // until the slot is next overwritten.
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
        }

        // Release the optimistic lock before allowing a retry to start.
        commitLock?.Dispose();

        if (releaseDeferredLock)
        {
            _lockReleaser?.Dispose();
        }

        if (!_isCommitted)
        {
            // Only failures that escape before FinishCommit are retryable; later failures are terminal.
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
        if (_handlers.IsDefaultOrEmpty)
        {
            return SnapshotChanges(setCommitting: true);
        }

        var (rentedArray, changes) = SnapshotChanges(setCommitting: false);
        try
        {
            InvokeCommitHandlers(changes.Span);

            lock (_pendingChangesLock)
            {
                _isValidating = false;
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(SubjectTransaction));
                }

                _isCommitting = true;
            }

            return (rentedArray, changes);
        }
        catch
        {
            lock (_pendingChangesLock)
            {
                _isValidating = false;
            }

            // The caller has not received the array yet, so EndCommit cannot return it.
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
            throw;
        }
    }

    private (SubjectPropertyChange[] RentedArray, Memory<SubjectPropertyChange> Changes) SnapshotChanges(bool setCommitting)
    {
        lock (_pendingChangesLock)
        {
            // Rent before changing state so an allocation failure leaves the transaction reusable.
            var pendingChanges = _pendingChanges;
            var changeCount = pendingChanges?.Count ?? 0;
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount);

            // Set while holding the lock so no write can be captured between the snapshot and the commit.
            if (setCommitting)
            {
                _isCommitting = true;
            }
            else
            {
                _isValidating = true;
            }

            var index = 0;
            if (pendingChanges is not null)
            {
                foreach (var change in pendingChanges.Values)
                {
                    rentedArray[index++] = change;
                }
            }

            return (rentedArray, rentedArray.AsMemory(0, changeCount));
        }
    }

    private void InvokeCommitHandlers(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        foreach (var handler in _handlers)
        {
            handler.OnCommitting(this, changes);
        }
    }

    /// <summary>
    /// Calls the handlers again right before the local apply with the changes that will be applied, with reads
    /// returning the transaction's values for them. Returns the changes they rejected: the failed changes of a
    /// <see cref="SubjectTransactionException"/> that match checked changes, or every checked change otherwise.
    /// </summary>
    private (IReadOnlyList<SubjectPropertyChange> Invalid, IReadOnlyList<Exception> Errors) CheckBeforeLocalApply(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> failedSource)
    {
        // A transaction disposed from another flow mid-commit has dropped the pending values the reads need.
        if (_handlers.IsDefaultOrEmpty || Volatile.Read(ref _isDisposed) != 0)
        {
            return ([], []);
        }

        // Changes whose source write failed do not land, so the handlers neither receive them nor read their values.
        var landingChanges = failedSource.Count == 0 ? null : ExcludeFailedSourceChanges(changes, failedSource);
        var checkedChanges = landingChanges is null ? changes : new ReadOnlySpan<SubjectPropertyChange>(landingChanges);
        if (checkedChanges.IsEmpty)
        {
            return ([], []);
        }

        _commitValidationExcludedChanges = landingChanges is null ? null : failedSource;
        try
        {
            using (EnterCommitValidationAccess())
            {
                InvokeCommitHandlers(checkedChanges);
            }

            return ([], []);
        }
        catch (Exception exception)
        {
            return GetRejectedChanges(exception, landingChanges ?? checkedChanges.ToArray());
        }
        finally
        {
            _commitValidationExcludedChanges = null;
        }
    }

    private static SubjectPropertyChange[] ExcludeFailedSourceChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> failedSource)
    {
        var landingChanges = SubjectPropertyChangeOperations.ExcludeByProperty(changes, failedSource, []);
        return landingChanges as SubjectPropertyChange[] ?? [.. landingChanges];
    }

    /// <summary>
    /// Returns the checked changes a handler exception rejects. A <see cref="SubjectTransactionException"/> rejects
    /// its failed changes; when none of them was checked, the rejection fails closed like any other exception and
    /// rejects every checked change.
    /// </summary>
    private static (IReadOnlyList<SubjectPropertyChange> Invalid, IReadOnlyList<Exception> Errors) GetRejectedChanges(
        Exception exception,
        IReadOnlyList<SubjectPropertyChange> checkedChanges)
    {
        if (exception is SubjectTransactionException { FailedChanges.Count: > 0 } transactionException)
        {
            var rejected = SubjectPropertyChangeOperations.IntersectByProperty(transactionException.FailedChanges, checkedChanges);
            if (rejected.Count > 0)
            {
                return (rejected, transactionException.Errors.Count > 0 ? transactionException.Errors : [exception]);
            }
        }

        return (checkedChanges, [exception]);
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

    private CommitModelAccessScope EnterCommitModelAccess() => new(this);

    /// <summary>
    /// Grants model access only to synchronous commit-owned work on this thread. The previous identity is
    /// restored for nested/reentrant use. Callers must dispose the scope before awaiting external code.
    /// </summary>
    private readonly struct CommitModelAccessScope : IDisposable
    {
        private readonly SubjectTransaction? _previousTransaction;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S3010", Justification = "This scope deliberately installs the current thread's commit identity and restores the previous identity on disposal, including nested scopes.")]
        public CommitModelAccessScope(SubjectTransaction transaction)
        {
            _previousTransaction = _commitModelAccessTransaction;
            _commitModelAccessTransaction = transaction;
        }

        public void Dispose()
        {
            _commitModelAccessTransaction = _previousTransaction;
        }
    }

    private CommitValidationAccessScope EnterCommitValidationAccess() => new(this);

    /// <summary>
    /// Lets this thread read the transaction's pending values while it runs the handlers right before the local
    /// apply. The previous identity is restored on disposal. Callers must dispose the scope before awaiting.
    /// </summary>
    private readonly struct CommitValidationAccessScope : IDisposable
    {
        private readonly SubjectTransaction? _previousTransaction;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S3010", Justification = "This scope deliberately installs the current thread's commit validation identity and restores the previous identity on disposal, including nested scopes.")]
        public CommitValidationAccessScope(SubjectTransaction transaction)
        {
            _previousTransaction = _commitValidationTransaction;
            _commitValidationTransaction = transaction;
        }

        public void Dispose()
        {
            _commitValidationTransaction = _previousTransaction;
        }
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
        // Publish disposal before taking the lock so a waiting capture observes it and continues normally.
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
        {
            Interlocked.Decrement(ref _activeTransactionCount);

            bool committing;
            OrderedDictionary<PropertyReference, SubjectPropertyChange>? pendingChangesToReturn = null;
            lock (_pendingChangesLock)
            {
                _pendingChanges?.Clear();
                // A disposal during the handler phase also defers to EndCommit, so the exclusive lock is not released
                // while handlers of this commit still run.
                committing = _isCommitting || _isValidating;
                if (committing)
                {
                    _disposeRequestedDuringCommit = true;
                }
                else
                {
                    // Detach while locked; return the pooled dictionary after releasing the lock.
                    pendingChangesToReturn = _pendingChanges;
                    _pendingChanges = null;
                }
            }

            // Clear the slot only when it holds this transaction so a cross-flow Dispose cannot
            // clobber an unrelated transaction.
            if (CurrentTransaction.Value == this)
            {
                CurrentTransaction.Value = null;
            }

            if (!committing)
            {
                if (pendingChangesToReturn is not null)
                {
                    PendingChangesPool.Return(pendingChangesToReturn);
                }
                _lockReleaser?.Dispose(); // May be null for Optimistic transactions
            }
        }
    }
}
