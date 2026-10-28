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

    // A mutable struct: never copy it or make this field readonly (see TransactionHandlerSet).
    private TransactionHandlerSet _handlerSet;

    /// <summary>
    /// Last write wins if the same property is written multiple times.
    /// Preserves insertion order so that commit replays changes in the order they were written.
    /// Access must be synchronized via <see cref="_pendingChangesLock"/>.
    /// </summary>
    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    // Deferred mode with FailOnConflict only (see RecordsCheckOnlyEntries): check-only entries, writes of the
    // landed value of a property without a pending change.
    // The commit check validates them and conflict detection checks them, so the check never validates a value that
    // moved meanwhile, but the commit neither writes, applies nor publishes them, and GetPendingChanges does not list
    // them. A later write of another value replaces the entry with a pending change, so the keys stay disjoint from
    // _pendingChanges. Allocated on the first such write; accessed only under _pendingChangesLock.
    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _checkOnlyChanges;

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Handoff from Dispose to EndCommit: when Dispose runs mid-commit, EndCommit releases the exclusive lock
    // once the commit finishes. Accessed only under _pendingChangesLock (no volatile needed; never read lock-free).
    private bool _disposeRequestedDuringCommit;

    // Written only by the committing thread as it enters and leaves a commit scope, and read only after
    // IsInCommitScope matched on that same thread, so they need no synchronization. The excluded properties are those
    // whose source write failed, set only during the pre-apply check.
    private bool _isInHandlerCheck;
    private bool _isInCommitCheck;
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
    /// it. Reference-type values match by identity, value-type values by equality.
    /// </summary>
    /// <remarks>
    /// The first call by one of the transaction's handlers consumes the mark, so another handler that checks the same
    /// write afterwards gets false and must check it again. Returns false for every apply after the transaction was
    /// disposed during the commit.
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
    internal bool IsInHandlerCheck => _isInHandlerCheck;

    /// <summary>
    /// Gets the interceptor this transaction is bound to (for cross-context validation).
    /// </summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>
    /// Gets the transaction handlers, see <see cref="TransactionHandlerSet"/>.
    /// </summary>
    internal ImmutableArray<ITransactionHandler> Handlers => ImmutableCollectionsMarshal.AsImmutableArray(_handlerSet.Handlers);

    /// <summary>
    /// Gets the lock that guards the pending changes and the capture state.
    /// </summary>
    internal Lock PendingChangesLock => _pendingChangesLock;

    // Applies carry the commit write mark only when handlers checked the final set.
    private SubjectTransaction? MarkingTransaction => _handlerSet.Handlers.Length == 0 ? null : this;

    /// <summary>
    /// Gets whether writes of an unchanged value are recorded as check-only entries. Conflict detection is what
    /// keeps an entry sound, since the check reads its value from write time, so with
    /// <see cref="TransactionConflictBehavior.Ignore"/> such writes stay dropped.
    /// </summary>
    internal bool RecordsCheckOnlyEntries =>
        CheckMode == TransactionCheckMode.Deferred && ConflictBehavior == TransactionConflictBehavior.FailOnConflict;

    // Without handlers nothing validates the entries, so the commit leaves them out, conflict detection included.
    private int CheckedCheckOnlyCount => _handlerSet.Handlers.Length == 0 ? 0 : _checkOnlyChanges?.Count ?? 0;

    // Handlers checked the changes as a set, so once one fails to apply the rest no longer land as checked.
    private bool RollsBackOnApplyFailure => _failureHandling == TransactionFailureHandling.Rollback || MarkingTransaction is not null;

    /// <summary>
    /// Adds the handlers of <paramref name="subjectContext"/> before the transaction captures a write to one of its
    /// subjects, see <see cref="TransactionHandlerSet.Include"/>, which also states when this returns false.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown, like the capture, while a commit is in progress and <paramref name="subjectContext"/> adds handlers,
    /// unless <paramref name="skipWhileCommitting"/> is true.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IncludeHandlersOf(
        IInterceptorSubjectContext subjectContext,
        ImmutableArray<SubjectTransactionInterceptor> subjectInterceptors,
        bool skipWhileCommitting = false)
    {
        return _handlerSet.Include(subjectContext, subjectInterceptors, this, skipWhileCommitting);
    }

    /// <summary>
    /// Gets a snapshot of the pending changes as a read-only list, in insertion order.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges()
    {
        lock (_pendingChangesLock)
        {
            var changeCount = _pendingChanges?.Count ?? 0;
            if (changeCount == 0)
            {
                return [];
            }

            var result = new SubjectPropertyChange[changeCount];
            CopyPendingChanges(result);
            return result;
        }
    }

    /// <summary>
    /// Copies the pending changes to <paramref name="destination"/> in insertion order. Must be called under
    /// <see cref="_pendingChangesLock"/>.
    /// </summary>
    private void CopyPendingChanges(Span<SubjectPropertyChange> destination)
    {
        if (_pendingChanges is not null)
        {
            var index = 0;
            foreach (var change in _pendingChanges.Values)
            {
                destination[index++] = change;
            }
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
            if (!CanCaptureUnderLock())
            {
                value = default!;
                return false;
            }

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
    /// Reads a pending value for the handlers during the commit check or the pre-apply check, and in the commit check
    /// also a check-only value. Returns false when the property is not part of the transaction or its source write
    /// failed (the check's excluded properties).
    /// </summary>
    internal bool TryGetCheckedValue<TProperty>(PropertyReference property, out TProperty value)
    {
        if (_checkExcludedProperties is null || !_checkExcludedProperties.Contains(property))
        {
            lock (_pendingChangesLock)
            {
                if (TryGetCheckedChange(property, out var change))
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
    /// Returns the pending change of <paramref name="property"/>, or in the commit check its check-only entry. Must
    /// be called under <see cref="_pendingChangesLock"/>.
    /// </summary>
    private bool TryGetCheckedChange(PropertyReference property, out SubjectPropertyChange change)
    {
        if (_pendingChanges is not null && _pendingChanges.TryGetValue(property, out change))
        {
            return true;
        }

        change = default;
        return _isInCommitCheck && _checkOnlyChanges is not null && _checkOnlyChanges.TryGetValue(property, out change);
    }

    /// <summary>
    /// Atomically captures a property change into the pending dictionary. The first write preserves
    /// <paramref name="currentValue"/>, the landed model value, as old value for conflict detection; subsequent writes
    /// preserve the original old value (last write wins). <paramref name="restoresLandedValue"/> is true when the write
    /// restores the landed value and the subject's context has the equality check: a pending change is then removed,
    /// the same as never writing the property (without one the write is captured as usual), and replaced by a
    /// check-only entry for the landed value when <see cref="RecordsCheckOnlyEntries"/>.
    /// Returns false when disposal wins the race, allowing the caller to continue the normal write chain.
    /// </summary>
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
            if (restoresLandedValue && pendingChanges.Remove(property))
            {
                if (RecordsCheckOnlyEntries)
                {
                    (_checkOnlyChanges ??= new(PropertyReference.Comparer))[property] = SubjectPropertyChange.Create(
                        property, origin, changedTimestamp, receivedTimestamp, newValue, newValue);
                }

                return true;
            }

            _checkOnlyChanges?.Remove(property);
            var isFirstWrite = !pendingChanges.TryGetValue(property, out var existingChange);

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
    /// Records a write of the landed value of <paramref name="property"/>, which the equality check drops, as a
    /// check-only entry (see <see cref="_checkOnlyChanges"/>) whose old and new value are
    /// <paramref name="value"/>. Does nothing when the property has a pending change, which the commit check already
    /// validates, and returns false, dropping the write as unchanged, when the transaction no longer captures or a
    /// commit has started.
    /// </summary>
    internal bool TryRecordCheckOnly<TProperty>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TProperty value)
    {
        lock (_pendingChangesLock)
        {
            // A commit that started after the caller's lock-free check owns the snapshot; the write stays dropped.
            if (_isCommitting || !CanCaptureUnderLock())
            {
                return false;
            }

            if (!_pendingChanges.ContainsKey(property))
            {
                (_checkOnlyChanges ??= new(PropertyReference.Comparer))[property] = SubjectPropertyChange.Create(
                    property, origin, changedTimestamp, receivedTimestamp, value, value);
            }

            return true;
        }
    }

    /// <summary>
    /// Returns true with the pending value when the current flow's transaction holds a pending change for
    /// <paramref name="property"/> and is capturing, or is committing outside this thread's commit scope.
    /// </summary>
    internal static bool TryGetPendingValueInCurrentTransaction<TProperty>(PropertyReference property, out TProperty value)
    {
        value = default!;
        var transaction = CurrentTransaction.Value;
        if (transaction is null || transaction.IsDisposed || transaction._isCommitted)
        {
            return false;
        }

        // The commit's own applies and rollbacks rely on the equality drop. A write back from another thread or the
        // writer's flow must reach the transaction interceptor, which rejects it while the commit runs, so the
        // undo it intends is not lost silently.
        if (transaction._isCommitting && transaction.IsInCommitScope)
        {
            return false;
        }

        lock (transaction._pendingChangesLock)
        {
            if (transaction._pendingChanges is null || !transaction._pendingChanges.TryGetValue(property, out var change))
            {
                return false;
            }

            value = change.GetNewValue<TProperty>();
            return true;
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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowWriteDuringHandlerCheck()
    {
        throw new InvalidOperationException(
            "Cannot write a property while transaction handlers run a check. " +
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
    /// Gets the check mode for this transaction.
    /// </summary>
    public TransactionCheckMode CheckMode { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The private constructor explicitly initializes the transaction context, policies and handlers as one coherent state.")]
    private SubjectTransaction(
        IInterceptorSubjectContext context,
        SubjectTransactionInterceptor interceptor,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        TransactionCheckMode checkMode,
        TimeSpan commitTimeout,
        ImmutableArray<ITransactionHandler> handlers)
    {
        Context = context;
        Interceptor = interceptor;
        _failureHandling = failureHandling;
        Locking = locking;
        _requirement = requirement;
        ConflictBehavior = conflictBehavior;
        CheckMode = checkMode;
        _commitTimeout = commitTimeout;
        _handlerSet = new TransactionHandlerSet(handlers);

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
    /// <param name="checkMode">The check mode.</param>
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
        TransactionCheckMode checkMode,
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

        if (locking == TransactionLocking.Exclusive)
        {
            await interceptor.AcquireExclusiveTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // The caller assigns the transaction so the value flows into its execution context.
            return new SubjectTransaction(
                context,
                interceptor,
                failureHandling,
                locking,
                requirement,
                conflictBehavior,
                checkMode,
                commitTimeout,
                context.GetServices<ITransactionHandler>());
        }
        catch when (locking == TransactionLocking.Exclusive)
        {
            // Resolving the handlers can throw, and no transaction exists yet to release the lock on dispose.
            interceptor.ReleaseExclusiveTransactionLock();
            throw;
        }
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
    /// <item><description>Source writes, when an <see cref="ITransactionWriter"/> is registered.</description></item>
    /// <item><description>
    /// Pre-apply check, when an <see cref="ITransactionWriter"/> is registered: the handlers check again the changes
    /// that will be applied.
    /// </description></item>
    /// <item><description>Apply: the changes are applied to the local model in insertion order.</description></item>
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
    /// the transaction stays retryable with its pending changes unless it was disposed.
    /// </exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ValidateCanCommit();

        lock (_pendingChangesLock)
        {
            if ((_pendingChanges?.Count ?? 0) == 0 && CheckedCheckOnlyCount == 0)
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
            var (rented, changes, checkedChanges) = SnapshotChanges();
            rentedArray = rented;
            RunCommitCheck(checkedChanges.Span);

            SubjectTransactionException? failure = null;
            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(checkedChanges.Span);

                var (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude: null, MarkingTransaction);

                if (applyFailed.Count > 0)
                {
                    if (RollsBackOnApplyFailure)
                    {
                        var (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied, MarkingTransaction);
                        failure = CreateRollbackFailureException(
                            MarkingTransaction is null
                                ? SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed)
                                : GetCheckedSetFailedChanges(changes.Span, [], revertFailed, []),
                            SubjectPropertyChangeOperations.Concat(applyErrors, revertErrors),
                            localRevertFailed: revertFailed.Count > 0);
                    }
                    else
                    {
                        failure = CreateBestEffortFailureException(applied, applyFailed, applyErrors);
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
            var (rented, changes, checkedChanges) = SnapshotChanges();
            rentedArray = rented;
            RunCommitCheck(checkedChanges.Span);

            using (EnterCommitModelAccess())
            {
                ThrowIfConflictsDetected(checkedChanges.Span);
            }

            if (changes.IsEmpty)
            {
                // Only check-only entries, accepted by the check and conflict detection: nothing to write or apply.
                FinishCommit();
                return;
            }

            using var timeoutCts = CreateCommitTimeoutCts();
            var commitToken = timeoutCts?.Token ?? CancellationToken.None;

            var failure = await ReconcileWithWriterAsync(writer, changes, checkedChanges[changes.Length..], commitToken).ConfigureAwait(false);

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
        Memory<SubjectPropertyChange> checkOnlyChanges,
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
            var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, propagateTimeout: true, cancellationToken).ConfigureAwait(false);

            // The written changes that were reverted cleanly are not reported.
            return CreateRollbackFailureException(
                SubjectPropertyChangeOperations.Concat(
                    failedSource,
                    SubjectPropertyChangeOperations.ExcludeByProperty(changes.Span, failedSource, written),
                    revert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, revert.Errors),
                localRevertFailed: false);
        }

        // What the handlers read can change while the sources are written (inbound updates, raw writes), so the
        // final set is checked again right before it lands. Its reads return the pending values of every checked
        // change and the landed value of a property written with its unchanged value, so a rejection invalidates the
        // whole checked set in both failure handling modes.
        var rejectionErrors = RunPreApplyCheck(changes.Span, checkOnlyChanges.Span, failedSource);
        if (rejectionErrors is not null)
        {
            // Timeout not propagated here: a retry would lose the rejection, and once the local model changed
            // it would replay the snapshot onto it, so the timeout is reported as a failed revert instead.
            var revert = await RevertSourceWritesSafelyAsync(writer, written, revertState, propagateTimeout: false, cancellationToken).ConfigureAwait(false);
            return CreateRollbackFailureException(
                GetCheckedSetFailedChanges(changes.Span, failedSource, [], revert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, rejectionErrors, revert.Errors),
                localRevertFailed: false);
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

        // Without source-write failures ApplyLocalChanges returns an empty Successful set on success.
        var exclude = failedSource.Count == 0 ? null : failedSource;
        IReadOnlyList<SubjectPropertyChange> applied;
        IReadOnlyList<SubjectPropertyChange> applyFailed;
        IReadOnlyList<Exception> applyErrors;
        IReadOnlyList<SubjectPropertyChange> revertFailed = [];
        IReadOnlyList<Exception> revertErrors = [];
        using (EnterCommitModelAccess())
        {
            (applied, applyFailed, applyErrors) = SubjectPropertyChangeOperations.ApplyLocalChanges(changes.Span, exclude, MarkingTransaction);
            if (applyFailed.Count > 0 && RollsBackOnApplyFailure)
            {
                (revertFailed, revertErrors) = SubjectPropertyChangeOperations.RevertLocalChanges(applied, MarkingTransaction);
            }
        }

        if (applyFailed.Count > 0)
        {
            if (RollsBackOnApplyFailure)
            {
                // All-or-nothing: revert local applies, then the source writes.
                var sourceRevert = await RevertSourceWritesSafelyAsync(writer, written, revertState, propagateTimeout: false, cancellationToken).ConfigureAwait(false);
                Debug.Assert(
                    MarkingTransaction is not null || failedSource.Count == 0,
                    "Without handlers only Rollback gets here, which reverts every source after a source write failure before the apply.");
                return CreateRollbackFailureException(
                    MarkingTransaction is null
                        ? GetRolledBackFailedChanges(changes.Span, written, applyFailed, revertFailed, sourceRevert.Failed)
                        : GetCheckedSetFailedChanges(changes.Span, failedSource, revertFailed, sourceRevert.Failed),
                    SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, revertErrors, sourceRevert.Errors),
                    localRevertFailed: revertFailed.Count > 0);
            }

            // BestEffort without handlers: keep source == model for failed-apply properties by reverting only their
            // source writes. A failed revert is one of those writes (a throwing writer fails all of them, and a
            // reporting writer may only report requested ones, see ITransactionWriter.RevertSourceWritesAsync), so
            // applyFailed already reports it.
            var toRevert = SubjectPropertyChangeOperations.FilterByProperty(written, applyFailed, keepMatches: true);
            var bestEffortRevert = await RevertSourceWritesSafelyAsync(writer, toRevert, revertState, propagateTimeout: false, cancellationToken).ConfigureAwait(false);
            return CreateBestEffortFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, applyFailed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
        }

        return CreateFailureAfterSuccessfulLocalApply(changes, applied, failedSource, sourceErrors);
    }

    /// <summary>
    /// Returns the changes a pre-apply rejection or a failed apply reports after handlers checked them. Handlers
    /// checked the set as a whole, so once one change fails none commits: every change is reported, source-write
    /// failures first, then the changes whose local revert failed, then those whose source revert failed, so a stuck
    /// property appears a second time.
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> GetCheckedSetFailedChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> failedSource,
        IReadOnlyList<SubjectPropertyChange> revertFailed,
        IReadOnlyList<SubjectPropertyChange> sourceRevertFailed)
    {
        return SubjectPropertyChangeOperations.Concat(
            failedSource,
            SubjectPropertyChangeOperations.ExcludeByProperty(changes, failedSource, []),
            revertFailed,
            SubjectPropertyChangeOperations.FilterByProperty(sourceRevertFailed, revertFailed, keepMatches: false));
    }

    /// <summary>
    /// Returns the changes a Rollback commit without handlers reports after a failed apply: the apply and revert
    /// failures, and the changes without a source, which were applied and reverted.
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> GetRolledBackFailedChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> written,
        IReadOnlyList<SubjectPropertyChange> applyFailed,
        IReadOnlyList<SubjectPropertyChange> revertFailed,
        IReadOnlyList<SubjectPropertyChange> sourceRevertFailed)
    {
        // Nothing failed at a source here, so every change outside 'written' has no source.
        var reported = SubjectPropertyChangeOperations.Concat(applyFailed, revertFailed);
        return SubjectPropertyChangeOperations.Concat(
            applyFailed,
            SubjectPropertyChangeOperations.ExcludeByProperty(changes, written, reported),
            revertFailed,
            SubjectPropertyChangeOperations.FilterByProperty(sourceRevertFailed, reported, keepMatches: false));
    }

    private SubjectTransactionException? CreateFailureAfterSuccessfulLocalApply(
        Memory<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> applied,
        IReadOnlyList<SubjectPropertyChange> failedSource,
        IReadOnlyList<Exception> sourceErrors)
    {
        if (failedSource.Count > 0 || sourceErrors.Count > 0)
        {
            // Without exclusions ApplyLocalChanges leaves its successful list empty, so the snapshot
            // must be copied before the pooled storage is returned at commit completion.
            var appliedChanges = failedSource.Count == 0 ? changes.ToArray() : applied;
            return CreateBestEffortFailureException(appliedChanges, failedSource, sourceErrors);
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
    /// Converts a contract-violating throw from <see cref="ITransactionWriter.RevertSourceWritesAsync"/> into "every
    /// requested revert failed" so the commit stays on the reported-failure path and becomes terminal. Only the
    /// commit timeout propagates, and only when <paramref name="propagateTimeout"/> is true.
    /// </summary>
    private static async ValueTask<SourceRevertResult> RevertSourceWritesSafelyAsync(
        ITransactionWriter writer,
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        bool propagateTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!propagateTimeout || !IsCommitCancellation(exception, cancellationToken))
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
            _checkOnlyChanges = null;
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
                _disposeRequestedDuringCommit = false;
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
            ReleaseExclusiveLock();
        }

        if (!_isCommitted)
        {
            // Only failures that escape before FinishCommit are retryable; later failures are terminal.
            Volatile.Write(ref _commitStarted, 0);
        }
    }

    /// <summary>
    /// Releases the exclusive lock acquired when the transaction began. Called exactly once: by
    /// <see cref="Dispose"/>, or by <see cref="EndCommit"/> when Dispose ran during the commit.
    /// </summary>
    private void ReleaseExclusiveLock()
    {
        if (Locking == TransactionLocking.Exclusive)
        {
            Interceptor.ReleaseExclusiveTransactionLock();
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

    /// <summary>
    /// Snapshots the pending changes, followed by the check-only entries. <c>Changes</c> holds the pending changes
    /// the commit writes and applies, <c>CheckedChanges</c> also the check-only entries, for the commit check and
    /// conflict detection.
    /// </summary>
    private (SubjectPropertyChange[] RentedArray, Memory<SubjectPropertyChange> Changes, Memory<SubjectPropertyChange> CheckedChanges) SnapshotChanges()
    {
        lock (_pendingChangesLock)
        {
            // Rent before changing state so an allocation failure leaves the transaction reusable.
            var changeCount = _pendingChanges?.Count ?? 0;
            var checkOnlyCount = CheckedCheckOnlyCount;
            var checkedCount = changeCount + checkOnlyCount;
            var rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(checkedCount);

            // Set while holding the lock so no write can be captured, and no handler included, between the snapshot
            // and the end of the commit.
            _isCommitting = true;

            CopyPendingChanges(rentedArray);
            if (checkOnlyCount > 0)
            {
                _checkOnlyChanges!.Values.CopyTo(rentedArray, changeCount);
            }

            return (rentedArray, rentedArray.AsMemory(0, changeCount), rentedArray.AsMemory(0, checkedCount));
        }
    }

    /// <summary>
    /// Calls the handlers with the snapshot before anything is written. A handler exception propagates unchanged; the
    /// caller's <see cref="EndCommit"/> then keeps the pending changes for a retry.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the transaction was disposed while the handlers ran.
    /// </exception>
    private void RunCommitCheck(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (_handlerSet.Handlers.Length == 0)
        {
            return;
        }

        RunHandlerCheck(changes, excludedProperties: null, TransactionCheckStage.Commit);

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(SubjectTransaction));
        }
    }

    /// <summary>
    /// Calls the handlers on this thread with reads returning the checked values (see
    /// <see cref="TryGetCheckedValue{TProperty}"/>) and writes throwing.
    /// </summary>
    private void RunHandlerCheck(
        ReadOnlySpan<SubjectPropertyChange> changes,
        HashSet<PropertyReference>? excludedProperties,
        TransactionCheckStage stage)
    {
        using var scope = EnterHandlerCheck(excludedProperties, isCommitCheck: stage == TransactionCheckStage.Commit);
        var context = new TransactionCheckContext(this, changes, stage);
        foreach (var handler in _handlerSet.Handlers)
        {
            handler.OnCommitting(in context);
        }
    }

    /// <summary>
    /// Calls the handlers again right before the local apply with the snapshot slots of the changes that will be
    /// applied, followed by the check-only entries with their current landed value. Returns null when they accept the
    /// set, or otherwise, when a handler or a landed-value read throws, the errors of a thrown
    /// <see cref="SubjectTransactionException"/> that has any, or the thrown exception itself.
    /// </summary>
    private IReadOnlyList<Exception>? RunPreApplyCheck(
        ReadOnlySpan<SubjectPropertyChange> changes,
        ReadOnlySpan<SubjectPropertyChange> checkOnlyChanges,
        IReadOnlyList<SubjectPropertyChange> failedSource)
    {
        if (_handlerSet.Handlers.Length == 0)
        {
            return null;
        }

        // A dispose from another flow mid-commit drops the pending values this check reads, so the set is rejected
        // instead of landing unchecked.
        if (IsDisposed)
        {
            return [new ObjectDisposedException(nameof(SubjectTransaction))];
        }

        // The snapshot slots carry the origin each change is applied with: the writer marked the source-accepted ones
        // Confirmed. Changes whose source write failed do not land, so the handlers neither receive them nor read
        // their values.
        var failedProperties = SubjectPropertyChangeOperations.CreatePropertySet(failedSource);
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            var landingChanges = changes;
            if (failedProperties is not null || !checkOnlyChanges.IsEmpty)
            {
                rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changes.Length + checkOnlyChanges.Length);
                var count = CopyLandingChanges(changes, checkOnlyChanges, failedProperties, rentedArray);
                landingChanges = new ReadOnlySpan<SubjectPropertyChange>(rentedArray, 0, count);
            }

            if (!landingChanges.IsEmpty)
            {
                RunHandlerCheck(landingChanges, failedProperties, TransactionCheckStage.PreApply);
            }

            return IsDisposed ? [new ObjectDisposedException(nameof(SubjectTransaction))] : null;
        }
        catch (Exception exception)
        {
            // Also catches a throwing landed-value read of a check-only entry, so the sources are reverted.
            return exception is SubjectTransactionException { Errors.Count: > 0 } transactionException
                ? transactionException.Errors
                : [exception];
        }
        finally
        {
            if (rentedArray is not null)
            {
                ArrayPool<SubjectPropertyChange>.Shared.Return(rentedArray, clearArray: true);
            }
        }
    }

    /// <summary>
    /// Copies the changes that will be applied into <paramref name="destination"/>, followed by the check-only entries
    /// when at least one change is applied, and returns the number of copied changes.
    /// </summary>
    private int CopyLandingChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        ReadOnlySpan<SubjectPropertyChange> checkOnlyChanges,
        HashSet<PropertyReference>? failedProperties,
        Span<SubjectPropertyChange> destination)
    {
        int count;
        if (failedProperties is null)
        {
            changes.CopyTo(destination);
            count = changes.Length;
        }
        else
        {
            count = SubjectPropertyChangeOperations.CopyExcludingProperties(changes, failedProperties, destination);
        }

        // Nothing lands without a landing change, so the entries are checked only alongside one.
        return count == 0 ? 0 : count + CopyCheckOnlyChangesWithLandedValues(checkOnlyChanges, destination[count..]);
    }

    /// <summary>
    /// Copies the check-only entries into <paramref name="destination"/> with their captured origin and timestamps and
    /// their current landed value as old and new value, and returns the number of copied entries.
    /// </summary>
    private int CopyCheckOnlyChangesWithLandedValues(
        ReadOnlySpan<SubjectPropertyChange> checkOnlyChanges,
        Span<SubjectPropertyChange> destination)
    {
        using (EnterCommitModelAccess())
        {
            for (var index = 0; index < checkOnlyChanges.Length; index++)
            {
                var entry = checkOnlyChanges[index];

                // Never applied, so a value changed since conflict detection loses no update and is only validated.
                var landedValue = entry.Property.Metadata.GetValue?.Invoke(entry.Property.Subject);
                destination[index] = SubjectPropertyChange.Create(
                    entry.Property, entry.Origin, entry.ChangedTimestamp, entry.ReceivedTimestamp, landedValue, landedValue);
            }
        }

        return checkOnlyChanges.Length;
    }

    private void ThrowIfConflictsDetected(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var conflictingProperties = SubjectPropertyChangeOperations.DetectChangeConflicts(changes);
            if (conflictingProperties is not null)
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

    private CommitScope EnterCommitModelAccess() => new(this, isHandlerCheck: false, isCommitCheck: false, checkExcludedProperties: null);

    private CommitScope EnterHandlerCheck(HashSet<PropertyReference>? excludedProperties, bool isCommitCheck)
        => new(this, isHandlerCheck: true, isCommitCheck, excludedProperties);

    /// <summary>
    /// Authorizes synchronous commit-owned work of the transaction on this thread: a handler check when
    /// <c>isHandlerCheck</c> is true (see <see cref="TryGetCheckedValue{TProperty}"/>), and model access otherwise. The
    /// previous identity is restored on disposal, so a scope of another transaction can nest. Callers must dispose the
    /// scope before awaiting external code.
    /// </summary>
    private readonly struct CommitScope : IDisposable
    {
        private readonly SubjectTransaction _transaction;
        private readonly SubjectTransaction? _previousTransaction;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S3010", Justification = "This scope deliberately installs the current thread's commit identity and restores the previous identity on disposal, including nested scopes.")]
        public CommitScope(
            SubjectTransaction transaction,
            bool isHandlerCheck,
            bool isCommitCheck,
            HashSet<PropertyReference>? checkExcludedProperties)
        {
            // Dispose resets the check state instead of restoring it, which holds only while the scopes of one
            // transaction run one after another.
            Debug.Assert(!ReferenceEquals(_commitScopeTransaction, transaction), "Commit scopes of one transaction must not nest.");
            _transaction = transaction;
            _previousTransaction = _commitScopeTransaction;
            transaction._isInHandlerCheck = isHandlerCheck;
            transaction._isInCommitCheck = isCommitCheck;
            transaction._checkExcludedProperties = checkExcludedProperties;
            _commitScopeTransaction = transaction;
        }

        public void Dispose()
        {
            _commitScopeTransaction = _previousTransaction;
            _transaction._isInHandlerCheck = false;
            _transaction._isInCommitCheck = false;
            _transaction._checkExcludedProperties = null;
        }
    }

    private SubjectTransactionException CreateBestEffortFailureException(
        IReadOnlyList<SubjectPropertyChange> successful,
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<Exception> errors)
    {
        Debug.Assert(_failureHandling == TransactionFailureHandling.BestEffort, "Only reached in BestEffort mode; Rollback always routes through CreateRollbackFailureException.");
        return new SubjectTransactionException(
            "One or more changes failed. Successful changes have been applied.", successful, failed, errors);
    }

    /// <summary>
    /// Creates the exception of a commit that landed nothing or rolled back its local applies, in either failure
    /// handling mode.
    /// </summary>
    private static SubjectTransactionException CreateRollbackFailureException(
        IReadOnlyList<SubjectPropertyChange> failed,
        IReadOnlyList<Exception> errors,
        bool localRevertFailed)
    {
        var message = localRevertFailed
            ? "One or more changes failed. Rollback was attempted, but some changes could not be reverted in the local model."
            : "One or more changes failed. Rollback was attempted. No changes have been applied to the local model.";
        return new SubjectTransactionException(message, [], failed, errors);
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
                _checkOnlyChanges = null;
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
                ReleaseExclusiveLock();
            }
        }
    }
}
