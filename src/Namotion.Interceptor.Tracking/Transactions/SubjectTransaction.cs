using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private static SubjectTransaction? _commitScopeTransaction;

    private static readonly ObjectPool<OrderedDictionary<PropertyReference, SubjectPropertyChange>> PendingChangesPool
        = new(() => new OrderedDictionary<PropertyReference, SubjectPropertyChange>(PropertyReference.Comparer));

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

    // A mutable struct: never copy it or make this field readonly (see TransactionHandlerSet).
    private TransactionHandlerSet _handlerSet;

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Preserves insertion order so that commit replays changes in the order they were written.
    /// Access must be synchronized via <see cref="_pendingChangesLock"/>.
    /// </summary>
    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    // Pending entries cancelled by a write back to the landed value. They stay in _pendingChanges so a later
    // write revives them with their original old value and position, which keeps conflict detection intact;
    // every reader must treat them as absent. Always a subset of the _pendingChanges keys. Allocated on the
    // first cancel; accessed only under _pendingChangesLock.
    private HashSet<PropertyReference>? _cancelledChanges;

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Handoff from Dispose to EndCommit: when Dispose runs mid-commit, EndCommit releases the exclusive lock
    // once the commit finishes. Accessed only under _pendingChangesLock (no volatile needed; never read lock-free).
    private bool _disposeRequestedDuringCommit;

    // Written only by the committing thread as it enters and leaves a commit scope, and read only after
    // IsInCommitScope matched on that same thread, so it needs no synchronization.
    private bool _isCheckingCommit;

    // The properties whose source write failed, set only during the pre-apply check. Read only by the committing
    // thread inside its check scope, so it needs no lock.
    private HashSet<PropertyReference>? _checkExcludedProperties;

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
    /// <remarks>
    /// Only one handler can consume a mark. When another handler that also intercepts writes checks the same write
    /// afterwards, this returns false for it, so it must check the write again. It also returns false for the applies
    /// after the first failed apply of a commit, and for every apply after the transaction was disposed during the
    /// commit.
    /// </remarks>
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
    /// Gets a value indicating whether the transaction has reached a terminal committed state.
    /// </summary>
    internal bool IsCommitted => _isCommitted;

    /// <summary>
    /// Gets a value indicating whether the transaction has been disposed.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>
    /// Gets a value indicating whether this thread runs this transaction's synchronous commit work: a handler check,
    /// conflict detection, or the local apply and its rollback.
    /// </summary>
    internal bool IsInCommitScope => ReferenceEquals(_commitScopeTransaction, this);

    /// <summary>
    /// Gets a value indicating whether the commit scope runs a handler check, where reads return the checked values
    /// and writes throw. Only meaningful when <see cref="IsInCommitScope"/> is true.
    /// </summary>
    internal bool IsCheckingCommit => _isCheckingCommit;

    /// <summary>
    /// Gets the interceptor this transaction is bound to (for cross-context validation).
    /// </summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>
    /// Gets the transaction handlers resolved from the context when the transaction began, followed by those added from
    /// the contexts of subjects when they are written.
    /// </summary>
    internal ImmutableArray<ITransactionHandler> Handlers => ImmutableCollectionsMarshal.AsImmutableArray(_handlerSet.Handlers);

    // Applies carry the commit write mark only when handlers checked the final set.
    private SubjectTransaction? MarkingTransaction => _handlerSet.Handlers.Length == 0 ? null : this;

    /// <summary>
    /// Adds the transaction handlers of <paramref name="subjectContext"/> that are not registered on the transaction's
    /// context, when a subject of it is written and the transaction is about to capture the write.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown while a commit is in progress, like the capture would.
    /// </exception>
    internal void IncludeHandlersOf(IInterceptorSubjectContext subjectContext)
    {
        _handlerSet.Include(subjectContext, this, _pendingChangesLock);
    }

    /// <summary>
    /// Gets a snapshot of the pending changes as a read-only list, in insertion order.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges()
    {
        lock (_pendingChangesLock)
        {
            var pendingChanges = _pendingChanges;
            if (pendingChanges is null)
            {
                return [];
            }

            var result = new List<SubjectPropertyChange>(GetLiveChangeCount());
            foreach (var change in pendingChanges.Values)
            {
                if (!IsCancelled(change.Property))
                {
                    result.Add(change);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Gets the number of pending changes that are not cancelled. Must be called under <see cref="_pendingChangesLock"/>.
    /// </summary>
    private int GetLiveChangeCount()
    {
        return (_pendingChanges?.Count ?? 0) - (_cancelledChanges?.Count ?? 0);
    }

    /// <summary>
    /// Returns true when the pending change for <paramref name="property"/> is cancelled. Must be called under
    /// <see cref="_pendingChangesLock"/>.
    /// </summary>
    private bool IsCancelled(PropertyReference property)
    {
        return _cancelledChanges is { Count: > 0 } cancelledChanges && cancelledChanges.Contains(property);
    }

    /// <summary>
    /// Tries to read a pending value for the given property.
    /// Returns false if the transaction is committing or no pending value exists.
    /// </summary>
    internal bool TryGetPendingValue<TProperty>(PropertyReference property, out TProperty value)
    {
        lock (_pendingChangesLock)
        {
            if (!CanCaptureUnderLock())
            {
                value = default!;
                return false;
            }

            if (_pendingChanges.TryGetValue(property, out var change) && !IsCancelled(property))
            {
                value = change.GetNewValue<TProperty>();
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Reads a pending value for the handlers during the commit check or the pre-apply check. Returns false when the property is not part of the transaction, its change was cancelled, or its
    /// source write failed.
    /// </summary>
    internal bool TryGetCheckedValue<TProperty>(PropertyReference property, out TProperty value)
    {
        if (_checkExcludedProperties is null || !_checkExcludedProperties.Contains(property))
        {
            lock (_pendingChangesLock)
            {
                if (_pendingChanges is not null && _pendingChanges.TryGetValue(property, out var change) && !IsCancelled(property))
                {
                    value = change.GetNewValue<TProperty>();
                    return true;
                }
            }
        }

        value = default!;
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
    /// then cancelled instead of updated. A later write revives a cancelled change with its original old
    /// value and position.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a concurrent commit is in progress (TOCTOU race).
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
            if (!CanCaptureUnderLock())
            {
                return false;
            }

            var pendingChanges = _pendingChanges;
            var isFirstWrite = !pendingChanges.TryGetValue(property, out var existingChange);

            if (!isFirstWrite)
            {
                // A write back to the landed value cancels the pending change, so no no-op change reaches
                // sources, batch limits or conflicts. Removing the entry instead would lose the original old
                // value, and a later write would silently overwrite a concurrent change landed in between.
                if (restoresLandedValue)
                {
                    (_cancelledChanges ??= new HashSet<PropertyReference>(PropertyReference.Comparer)).Add(property);
                    return true;
                }

                _cancelledChanges?.Remove(property);
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
            return transaction._pendingChanges?.ContainsKey(property) == true && !transaction.IsCancelled(property);
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
            return !CanCaptureUnderLock();
        }
    }

    /// <summary>
    /// Returns false when the transaction no longer captures (disposed or committed), and throws while a commit is in
    /// progress. Must be called under <see cref="_pendingChangesLock"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown while a commit is in progress.</exception>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_pendingChanges))]
    internal bool CanCaptureUnderLock()
    {
        Debug.Assert(_pendingChangesLock.IsHeldByCurrentThread, "Must be called under the pending changes lock.");
        if (Volatile.Read(ref _isDisposed) != 0 || _isCommitted || _pendingChanges is null)
        {
            return false;
        }

        if (_isCommitting)
        {
            throw new InvalidOperationException(
                "Cannot access transactional property while commit is in progress. " +
                "Source writes and transaction writer callbacks run on the committing flow: build the " +
                "payload from the supplied changes instead of reading or writing subject properties. " +
                "This is also reported when the transaction is used from another thread.");
        }

        return true;
    }

    internal static void ThrowWriteDuringCommitCheck()
    {
        throw new InvalidOperationException(
            "Cannot write a property while transaction handlers check the commit. " +
            "Transaction handlers must not write properties.");
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
        _handlerSet = new TransactionHandlerSet(context);

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
    /// The commit runs these stages in order:
    /// <list type="number">
    /// <item><description>
    /// Commit check: the transaction's <see cref="ITransactionHandler"/>s check the final set.
    /// </description></item>
    /// <item><description>
    /// Conflict detection: with <see cref="TransactionConflictBehavior.FailOnConflict"/>, the captured old values are
    /// compared with the current values, which catches changes from other transactions and external sources since
    /// the transaction started.
    /// </description></item>
    /// <item><description>
    /// Source writes, when an <see cref="ITransactionWriter"/> is registered.
    /// </description></item>
    /// <item><description>
    /// Pre-apply check, when an <see cref="ITransactionWriter"/> is registered: the handlers check the changes that
    /// will be applied again.
    /// </description></item>
    /// <item><description>
    /// Apply: the changes are applied to the local model in insertion order.
    /// </description></item>
    /// </list>
    /// For Optimistic locking, the lock is acquired at the start of commit and released after completion.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the transaction has been disposed, or was disposed while the commit check ran.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when commit is called from a different async flow than the one the transaction is active in,
    /// when the transaction was already committed, or when another commit is already in progress.
    /// </exception>
    /// <exception cref="SubjectTransactionException">
    /// Thrown when one or more changes failed to commit. A registered <see cref="ITransactionWriter"/>
    /// that throws instead of reporting failures makes the transaction terminal (it must be disposed,
    /// not retried) and may leave its sources un-reverted.
    /// </exception>
    /// <exception cref="Exception">
    /// Any exception a transaction handler throws in the commit check propagates unchanged. Nothing was written, and
    /// the transaction stays retryable with its pending changes.
    /// </exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ValidateCanCommit();

        lock (_pendingChangesLock)
        {
            if (GetLiveChangeCount() == 0)
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
            var (rented, changes) = SnapshotChanges();
            rentedArray = rented;
            RunCommitCheck(changes.Span);

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
            var (rented, changes) = SnapshotChanges();
            rentedArray = rented;
            RunCommitCheck(changes.Span);

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
        var rollBackSourceFailure = _failureHandling == TransactionFailureHandling.Rollback &&
            (failedSource.Count > 0 || sourceErrors.Count > 0);

        // What the handlers read can change while the sources are written (inbound updates, raw writes), so the
        // final set is checked again right before it lands. Its reads return the pending values of every checked
        // change, so a rejection invalidates the whole checked set in both failure handling modes.
        var rejectionErrors = rollBackSourceFailure ? null : RunPreApplyCheck(changes.Span, failedSource);
        if (rollBackSourceFailure || rejectionErrors is not null)
        {
            var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, afterLocalApply: false, cancellationToken).ConfigureAwait(false);

            // A rejection fails every checked change, which includes every written one. After a source failure, the
            // written changes that were reverted cleanly are not reported.
            var notApplied = rejectionErrors is null
                ? SubjectPropertyChangeOperations.Concat(
                    SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, written), revert.Failed)
                : SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, []);
            return CreateFailureException(
                [],
                SubjectPropertyChangeOperations.Concat(failedSource, notApplied),
                SubjectPropertyChangeOperations.Concat(sourceErrors, rejectionErrors ?? [], revert.Errors));
        }

        return await ApplyAfterSourceWritesAsync(writer, changes, writeResult, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the checked set locally after the source writes, reverting sources where the failure handling requires
    /// it. Returns null on full success.
    /// </summary>
    private async ValueTask<SubjectTransactionException?> ApplyAfterSourceWritesAsync(
        ITransactionWriter writer,
        Memory<SubjectPropertyChange> changes,
        SourceWriteResult writeResult,
        CancellationToken cancellationToken)
    {
        var (written, failedSource, sourceErrors, revertState) = writeResult;

        // Apply the whole snapshot except the source-write failures in a single pass. Without any, this is the entire
        // snapshot, and ApplyLocalChanges returns an empty Successful set.
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
                var sourceRevert = await RevertSourceWritesSafelyAsync(writer, written, revertState, afterLocalApply: true, cancellationToken).ConfigureAwait(false);
                // The no-source local changes were applied then reverted; under all-or-nothing they did not
                // commit, so report them as failed too (consistent with the source-write-failure branch and
                // the documented Rollback contract). Exclude source-bound changes (in 'written'; failedSource
                // is empty on this branch) and anything already reported as an apply/revert failure.
                var rolledBackLocals = SubjectPropertyChangeOperations.ExcludeByProperty(
                    changes.Span, written, SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed));
                var sourceRevertFailed = SubjectPropertyChangeOperations.ExceptReported(
                    sourceRevert.Failed, SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed));
                return CreateFailureException(
                    [],
                    SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, rolledBackLocals, revertFailed, sourceRevertFailed),
                    SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, revertErrors, sourceRevert.Errors));
            }

            // BestEffort: keep source == model for failed-apply properties by reverting only the source writes whose
            // property failed to apply (matched by Property equality).
            var toRevert = SubjectPropertyChangeOperations.IntersectByProperty(applyFailed, written);
            var bestEffortRevert = await RevertSourceWritesSafelyAsync(writer, toRevert, revertState, afterLocalApply: true, cancellationToken).ConfigureAwait(false);
            return CreateFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(
                    failedSource, applyFailed, SubjectPropertyChangeOperations.ExceptReported(bestEffortRevert.Failed, applyFailed)),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
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
    /// to sources that already accepted writes. Only the commit timeout propagates, and only when
    /// <paramref name="afterLocalApply"/> is false: once the local model changed, a retry would replay the
    /// snapshot onto it, so the timeout is reported as a failed revert too.
    /// </summary>
    private static async ValueTask<SourceRevertResult> RevertSourceWritesSafelyAsync(
        ITransactionWriter writer,
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        bool afterLocalApply,
        CancellationToken cancellationToken)
    {
        try
        {
            return await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (afterLocalApply || !IsCommitCancellation(exception, cancellationToken))
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
            _cancelledChanges = null;
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
                _cancelledChanges = null;
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

    private (SubjectPropertyChange[] RentedArray, Memory<SubjectPropertyChange> Changes) SnapshotChanges()
    {
        lock (_pendingChangesLock)
        {
            // Rent before changing state so an allocation failure leaves the transaction reusable.
            var pendingChanges = _pendingChanges;
            var changeCount = GetLiveChangeCount();
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount);

            // Set while holding the lock so no write can be captured, and no handler included, between the snapshot
            // and the end of the commit.
            _isCommitting = true;

            var index = 0;
            if (pendingChanges is not null)
            {
                foreach (var change in pendingChanges.Values)
                {
                    if (!IsCancelled(change.Property))
                    {
                        rentedArray[index++] = change;
                    }
                }
            }

            Debug.Assert(index == changeCount, "Cancelled changes must be a subset of the pending changes.");

            return (rentedArray, rentedArray.AsMemory(0, changeCount));
        }
    }

    /// <summary>
    /// Calls the handlers with the snapshot before anything is written. A handler exception propagates unchanged; the
    /// caller's <see cref="EndCommit"/> then keeps the pending changes for a retry.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction was disposed while the handlers ran.</exception>
    private void RunCommitCheck(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (_handlerSet.Handlers.Length == 0)
        {
            return;
        }

        RunCommitHandlers(changes, excludedProperties: null);

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(SubjectTransaction));
        }
    }

    /// <summary>
    /// Calls the handlers on this thread with reads returning the pending values, except those of
    /// <paramref name="excludedProperties"/>, and writes throwing.
    /// </summary>
    private void RunCommitHandlers(ReadOnlySpan<SubjectPropertyChange> changes, HashSet<PropertyReference>? excludedProperties)
    {
        _checkExcludedProperties = excludedProperties;
        try
        {
            using (EnterCommitCheck())
            {
                foreach (var handler in _handlerSet.Handlers)
                {
                    handler.OnCommitting(this, changes);
                }
            }
        }
        finally
        {
            _checkExcludedProperties = null;
        }
    }

    /// <summary>
    /// Calls the handlers again right before the local apply with the changes that will be applied, with reads
    /// returning the transaction's values for them. Returns null when they accept the set, or the errors of their
    /// rejection otherwise.
    /// </summary>
    private IReadOnlyList<Exception>? RunPreApplyCheck(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> failedSource)
    {
        // A transaction disposed from another flow mid-commit has dropped the pending values the reads need.
        if (_handlerSet.Handlers.Length == 0 || IsDisposed)
        {
            return null;
        }

        // The writer marks accepted snapshot slots as Confirmed, but handlers must see the captured origin, which the
        // pending changes still hold. Changes whose source write failed do not land, so the handlers neither receive
        // them nor read their values.
        var failedProperties = SubjectPropertyChangeOperations.CreatePropertySet(failedSource);
        var (rentedArray, checkedCount) = RentLandingPendingChanges(failedProperties);
        try
        {
            if (checkedCount == 0)
            {
                return null;
            }

            try
            {
                RunCommitHandlers(new ReadOnlySpan<SubjectPropertyChange>(rentedArray, 0, checkedCount), failedProperties);
                return null;
            }
            catch (Exception exception)
            {
                return PreApplyCheckRejection.GetErrors(exception, SubjectPropertyChangeOperations.ExcludeByProperty(changes, failedSource, []));
            }
        }
        finally
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
        }
    }

    /// <summary>
    /// Copies the pending changes that are not cancelled and whose property is not in
    /// <paramref name="failedProperties"/> into a rented array, in insertion order. The caller returns the array to
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    private (SubjectPropertyChange[] RentedArray, int Count) RentLandingPendingChanges(
        HashSet<PropertyReference>? failedProperties)
    {
        lock (_pendingChangesLock)
        {
            var pendingChanges = _pendingChanges;
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(GetLiveChangeCount());
            var count = 0;
            if (pendingChanges is not null)
            {
                foreach (var change in pendingChanges.Values)
                {
                    if (!IsCancelled(change.Property) &&
                        (failedProperties is null || !failedProperties.Contains(change.Property)))
                    {
                        rentedArray[count++] = change;
                    }
                }
            }

            return (rentedArray, count);
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

    private CommitScope EnterCommitModelAccess() => new(this, isCheck: false);

    private CommitScope EnterCommitCheck() => new(this, isCheck: true);

    /// <summary>
    /// Authorizes synchronous commit-owned work of the transaction on this thread: a handler check when
    /// <c>isCheck</c> is true, model access otherwise. The previous identity is restored on disposal, so a scope of
    /// another transaction can nest. Callers must dispose the scope before awaiting external code.
    /// </summary>
    private readonly struct CommitScope : IDisposable
    {
        private readonly SubjectTransaction _transaction;
        private readonly SubjectTransaction? _previousTransaction;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S3010", Justification = "This scope deliberately installs the current thread's commit identity and restores the previous identity on disposal, including nested scopes.")]
        public CommitScope(SubjectTransaction transaction, bool isCheck)
        {
            // Dispose resets the check flag instead of restoring it, which holds only while the scopes of one
            // transaction run one after another.
            Debug.Assert(!ReferenceEquals(_commitScopeTransaction, transaction), "Commit scopes of one transaction must not nest.");
            _transaction = transaction;
            _previousTransaction = _commitScopeTransaction;
            transaction._isCheckingCommit = isCheck;
            _commitScopeTransaction = transaction;
        }

        public void Dispose()
        {
            _commitScopeTransaction = _previousTransaction;
            _transaction._isCheckingCommit = false;
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
                _cancelledChanges = null;
                committing = _isCommitting;
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
