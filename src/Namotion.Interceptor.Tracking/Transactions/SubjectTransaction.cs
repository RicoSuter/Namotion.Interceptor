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
    /// Gets whether any transaction is active in any context. It can briefly stay true after the last one ended, which
    /// only costs an unnecessary AsyncLocal read.
    /// </summary>
    internal static bool HasActiveTransaction => Volatile.Read(ref _activeTransactionCount) > 0;

    private readonly TransactionFailureHandling _failureHandling;
    private readonly TransactionRequirement _requirement;
    private readonly TimeSpan _commitTimeout;

    // A mutable struct: never copy it or make this field readonly (see TransactionHandlerSet).
    private TransactionHandlerSet _handlerSet;

    /// <summary>
    /// One change per property (last write wins), in first-write order, which is the commit order. Guarded by
    /// <see cref="_pendingChangesLock"/>.
    /// </summary>
    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _pendingChanges = PendingChangesPool.Rent();
    private readonly Lock _pendingChangesLock = new();

    // Check-only entries (see RecordsCheckOnlyEntries): writes of the landed value of a property without a pending
    // change. A write of another value replaces the entry, so the keys stay disjoint from _pendingChanges. Allocated
    // lazily; guarded by _pendingChangesLock.
    private OrderedDictionary<PropertyReference, SubjectPropertyChange>? _checkOnlyChanges;

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private int _commitStarted;
    private int _isDisposed;

    // Set by a Dispose during the commit, so EndCommit releases the exclusive lock. Guarded by _pendingChangesLock.
    private bool _disposeRequestedDuringCommit;

    // Written only by the committing thread as it enters and leaves a commit scope, and read only after
    // IsInCommitScope matched on that same thread, so they need no synchronization. The stage is null outside a
    // handler check. The excluded properties are those whose source write failed, set only during the pre-apply check.
    private TransactionCheckStage? _checkStage;
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
    /// the property's value from before the transaction (with <see cref="TransactionConflictBehavior.Ignore"/>, the landed
    /// value before the commit check), which no handler checked. Values match by
    /// <see cref="EqualityComparer{T}.Default"/>, so a value whose <c>Equals</c> is looser than what the handler checks
    /// can pass an equal, unchecked value as the checked one.
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
        return HasActiveTransaction && CommitWriteMarker.TryConsume(handler, property, value);
    }

    /// <summary>
    /// Sets the current transaction of the caller's execution context, see <see cref="TransactionAwaiter"/>.
    /// </summary>
    internal static void SetCurrent(SubjectTransaction? transaction)
    {
        CurrentTransaction.Value = transaction;
    }

    internal bool IsCommitting => _isCommitting;

    /// <summary>Gets whether the commit finished, successfully or terminally failed.</summary>
    internal bool IsCommitted => _isCommitted;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <summary>
    /// Gets a value indicating whether this thread runs this transaction's synchronous commit work: a handler check, the
    /// model reads before the source writes, or the local apply and its rollback.
    /// </summary>
    internal bool IsInCommitScope => ReferenceEquals(_commitScopeTransaction, this);

    /// <summary>
    /// Gets a value indicating whether the commit scope runs a handler check, where reads return the checked values
    /// and writes throw. Only meaningful when <see cref="IsInCommitScope"/> is true.
    /// </summary>
    internal bool IsInHandlerCheck => _checkStage.HasValue;

    /// <summary>Gets the interceptor of the context this transaction is bound to.</summary>
    internal SubjectTransactionInterceptor Interceptor { get; }

    /// <summary>Gets the transaction handlers, see <see cref="TransactionHandlerSet"/>.</summary>
    internal ImmutableArray<ITransactionHandler> Handlers => ImmutableCollectionsMarshal.AsImmutableArray(_handlerSet.Handlers);

    /// <summary>Gets the lock that guards the pending changes and the capture state.</summary>
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
    /// subjects, see <see cref="TransactionHandlerSet.Include"/>, which also states when this returns false or throws.
    /// </summary>
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
            return _pendingChanges is { Count: > 0 } pendingChanges ? pendingChanges.Values.ToArray() : [];
        }
    }

    /// <summary>
    /// Tries to read a pending value for the given property. Returns false when the transaction no longer captures or
    /// no pending value exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown while a commit is in progress.</exception>
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
                var checkOnlyChanges = _checkStage == TransactionCheckStage.Commit ? _checkOnlyChanges : null;
                if (_pendingChanges?.TryGetValue(property, out var change) == true ||
                    checkOnlyChanges?.TryGetValue(property, out change) == true)
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
    /// Captures a change under the lock. The first write keeps <paramref name="currentValue"/>, the landed value, as
    /// old value for conflict detection; later writes keep that old value. <paramref name="restoresLandedValue"/> (a
    /// write of the landed value with the equality check) removes the pending change instead, if any, and records a
    /// check-only entry when <see cref="RecordsCheckOnlyEntries"/>. Returns false when disposal won the race, so the
    /// caller continues the write chain.
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
            if (restoresLandedValue)
            {
                // Also without a pending change to remove (another write back won the race, or an earlier interceptor
                // normalized the value to the landed value), the write is unchanged and must not be captured.
                pendingChanges.Remove(property);
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
    /// validates, or when the transaction no longer captures or a commit has started.
    /// </summary>
    internal void RecordCheckOnly<TProperty>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TProperty value)
    {
        lock (_pendingChangesLock)
        {
            // A commit that started after the caller's lock-free check owns the snapshot; the write stays dropped.
            if (!_isCommitting && CanCaptureUnderLock() && !_pendingChanges.ContainsKey(property))
            {
                (_checkOnlyChanges ??= new(PropertyReference.Comparer))[property] = SubjectPropertyChange.Create(
                    property, origin, changedTimestamp, receivedTimestamp, value, value);
            }
        }
    }

    /// <summary>
    /// Returns true with the pending value when this transaction holds a pending change for
    /// <paramref name="property"/> and is capturing, or is committing outside this thread's commit scope.
    /// </summary>
    internal bool TryGetPendingValueForWriteBack<TProperty>(PropertyReference property, out TProperty value)
    {
        value = default!;
        if (IsDisposed || _isCommitted)
        {
            return false;
        }

        // The commit's own applies and rollbacks rely on the equality drop. A write back from another thread or the
        // writer's flow must reach the transaction interceptor, which rejects it while the commit runs, so the
        // undo it intends is not lost silently.
        if (_isCommitting && IsInCommitScope)
        {
            return false;
        }

        lock (_pendingChangesLock)
        {
            if (_pendingChanges is null || !_pendingChanges.TryGetValue(property, out var change))
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

    /// <summary>Gets the context this transaction is bound to.</summary>
    public IInterceptorSubjectContext Context { get; }

    /// <summary>Gets the conflict behavior for this transaction.</summary>
    public TransactionConflictBehavior ConflictBehavior { get; }

    /// <summary>Gets the locking mode for this transaction.</summary>
    public TransactionLocking Locking { get; }

    /// <summary>Gets the check mode for this transaction.</summary>
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
    /// Begins a new transaction bound to <paramref name="context"/>, see
    /// <see cref="SubjectTransactionExtensions.BeginTransactionAsync"/>. Exclusive locking waits for the transaction
    /// lock; optimistic locking returns immediately and takes the lock only during the commit.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="failureHandling">The failure handling mode.</param>
    /// <param name="locking">The locking mode.</param>
    /// <param name="requirement">The transaction requirement.</param>
    /// <param name="conflictBehavior">The conflict detection behavior.</param>
    /// <param name="checkMode">The check mode.</param>
    /// <param name="commitTimeout">The commit timeout, or <see cref="Timeout.InfiniteTimeSpan"/> for none.</param>
    /// <param name="cancellationToken">Cancels waiting for the exclusive lock.</param>
    /// <returns>The new transaction.</returns>
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
    /// Commits all pending changes. With an <see cref="ITransactionWriter"/>, they are written to their sources first,
    /// then applied to the local model. Partial failures are handled per the <see cref="TransactionFailureHandling"/>
    /// the transaction began with.
    /// </summary>
    /// <remarks>
    /// For Optimistic locking, the lock is acquired at the start of commit and released after completion. For the
    /// stages of a commit, see Commit Flow in docs/tracking-transactions.md.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the transaction has been disposed, or was disposed while the commit waited for the lock or the
    /// commit check ran.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when commit is called from a different async flow than the one the transaction is active in,
    /// when the transaction was already committed, or when another commit is already in progress.
    /// </exception>
    /// <exception cref="SubjectTransactionException">
    /// Thrown when one or more changes failed to commit. A registered <see cref="ITransactionWriter"/>
    /// that throws instead of reporting failures, or violates its contract, makes the transaction terminal (it must be
    /// disposed, not retried) and may leave its sources in an undefined state.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> cancels the wait for the optimistic lock, or when a writer
    /// propagates the commit timeout from its source writes or from the revert after a source write failure or reported
    /// writer error in <see cref="TransactionFailureHandling.Rollback"/> mode. Nothing was applied locally and the
    /// transaction stays retryable, but a source that accepted a write can keep the new value, see the Failure Table in
    /// docs/tracking-transactions.md.
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
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(SubjectTransaction));
            }

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
            // Without a writer the commit is local and, with the exclusive lock already held, runs synchronously
            // without a CancellationTokenSource or async state machine. Only an optimistic lock wait goes async.
            var lockTask = AcquireOptimisticLockIfNeededAsync(cancellationToken);
            if (lockTask.IsCompletedSuccessfully)
            {
                try
                {
                    CommitWithoutWriter(lockTask.Result);
                }
                catch (Exception exception)
                {
                    // Reported through the task, as on the async paths, so callers see the failure at the await.
                    return ValueTask.FromException(exception);
                }

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

    /// <summary>Synchronous local commit when no <see cref="ITransactionWriter"/> is registered.</summary>
    private void CommitWithoutWriter(IDisposable? commitLock)
    {
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            var (changes, _) = SnapshotAndCheckChanges(out rentedArray);

            SubjectTransactionException? failure = null;
            using (EnterCommitModelAccess())
            {
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

    /// <summary>Commit when an <see cref="ITransactionWriter"/> is registered.</summary>
    private async ValueTask CommitWithWriterAsync(ITransactionWriter writer, CancellationToken cancellationToken)
    {
        IDisposable? commitLock = null;
        SubjectPropertyChange[]? rentedArray = null;
        try
        {
            commitLock = await AcquireOptimisticLockIfNeededAsync(cancellationToken).ConfigureAwait(false);
            var (changes, checkOnlyChanges) = SnapshotAndCheckChanges(out rentedArray);
            if (changes.IsEmpty)
            {
                // Only check-only entries, accepted by the check and conflict detection: nothing to write or apply.
                FinishCommit();
                return;
            }

            using var timeoutCts = CreateCommitTimeoutCts();
            var commitToken = timeoutCts?.Token ?? CancellationToken.None;

            SubjectTransactionException? failure;
            try
            {
                failure = await ReconcileWithWriterAsync(writer, changes, checkOnlyChanges, commitToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsCommitCancellation(exception, commitToken))
            {
                // The writer ran, so a retry would write the snapshot again. What was applied locally is unknown here.
                FinishCommit();
                throw new SubjectTransactionException(
                    "The transaction writer violated its contract during commit. Sources may be in an undefined " +
                    "state and the local model may hold new values; the transaction is terminal and must be " +
                    "disposed, not retried.",
                    appliedChanges: [],
                    failedChanges: changes.ToArray(),
                    errors: [exception]);
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

    /// <summary>Implements the failure and rollback matrix of the writer path. Returns null on full success.</summary>
    private async ValueTask<SubjectTransactionException?> ReconcileWithWriterAsync(
        ITransactionWriter writer,
        Memory<SubjectPropertyChange> changes,
        Memory<SubjectPropertyChange> checkOnlyChanges,
        CancellationToken cancellationToken)
    {
        var capturedProperties = ValidateWriterContract ? CaptureSnapshotProperties(changes.Span) : null;

        // A throwing writer returns neither the written set nor the revert state, so its sources cannot be reverted,
        // and a contract violation follows source writes a retry would repeat: both are terminal. Only the commit
        // timeout propagates, which stays retryable (the built-in writer throws it only from its revert phase).
        SourceWriteResult writeResult;
        try
        {
            writeResult = await writer
                .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);

            // A record struct returned as default carries null lists.
            writeResult = new SourceWriteResult(
                writeResult.Written ?? [], writeResult.Failed ?? [], writeResult.Errors ?? [], writeResult.RevertState);

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

        // Rollback reverts the written sources on any source failure, also on errors reported without failed changes.
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

        // Inbound updates and raw writes can change what the handlers read while the sources are written, so the set
        // is checked again right before it lands. Each change was checked against the others, so a rejection
        // invalidates the whole set in both failure handling modes.
        var rejectionErrors = RunPreApplyCheck(changes.Span, checkOnlyChanges.Span, failedSource);
        if (rejectionErrors is not null)
        {
            // Timeout not propagated here: a retry would lose the rejection, and once the local model changed
            // it would apply the snapshot again onto it, so the timeout is reported as a failed revert instead.
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
            // source writes. A failed revert lists its property a second time, so a stuck property can be told apart.
            var toRevert = SubjectPropertyChangeOperations.FilterByProperty(written, applyFailed, keepMatches: true);
            var bestEffortRevert = await RevertSourceWritesSafelyAsync(writer, toRevert, revertState, propagateTimeout: false, cancellationToken).ConfigureAwait(false);
            return CreateBestEffortFailureException(
                applied,
                SubjectPropertyChangeOperations.Concat(failedSource, applyFailed, bestEffortRevert.Failed),
                SubjectPropertyChangeOperations.Concat(sourceErrors, applyErrors, bestEffortRevert.Errors));
        }

        return CreateFailureAfterSuccessfulLocalApply(changes, applied, failedSource, sourceErrors);
    }

    /// <summary>
    /// Returns the changes a pre-apply rejection or a failed apply reports after handlers checked them. Handlers
    /// checked the set as a whole, so once one change fails none commits: every change is reported, source-write
    /// failures first, then the changes whose local revert failed, then those whose source revert failed and local
    /// revert did not, so a stuck property appears exactly a second time.
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> GetCheckedSetFailedChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> failedSource,
        IReadOnlyList<SubjectPropertyChange> revertFailed,
        IReadOnlyList<SubjectPropertyChange> sourceRevertFailed)
    {
        return SubjectPropertyChangeOperations.Concat(
            failedSource,
            SubjectPropertyChangeOperations.ExcludeByProperty(changes, failedSource),
            revertFailed,
            SubjectPropertyChangeOperations.FilterByProperty(sourceRevertFailed, revertFailed, keepMatches: false));
    }

    /// <summary>
    /// Returns the changes a Rollback commit without handlers reports after a failed apply: the apply failures, the
    /// other changes without a source whose local revert succeeded, then the changes whose local revert failed, then
    /// those whose source revert failed and local revert did not. Only a failed apply whose source revert failed
    /// appears a second time; every other change appears at most once.
    /// </summary>
    private static IReadOnlyList<SubjectPropertyChange> GetRolledBackFailedChanges(
        ReadOnlySpan<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange> written,
        IReadOnlyList<SubjectPropertyChange> applyFailed,
        IReadOnlyList<SubjectPropertyChange> revertFailed,
        IReadOnlyList<SubjectPropertyChange> sourceRevertFailed)
    {
        // Nothing failed at a source here, so every change outside 'written' has no source.
        return SubjectPropertyChangeOperations.Concat(
            applyFailed,
            SubjectPropertyChangeOperations.ExcludeByProperty(changes, written, applyFailed, revertFailed),
            revertFailed,
            SubjectPropertyChangeOperations.FilterByProperty(sourceRevertFailed, revertFailed, keepMatches: false));
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
            var result = await writer.RevertSourceWritesAsync(written, revertState, cancellationToken).ConfigureAwait(false);
            return new SourceRevertResult(result.Failed ?? [], result.Errors ?? []);
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
    /// Clears the pending changes and marks the transaction committed. Call it before throwing a commit failure, so
    /// later reads do not return stale pending values.
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
            // Under the lock so Dispose observes the commit end consistently. A Dispose during the commit deferred the
            // exclusive lock release to here, so no other transaction interleaves with the apply, and left the pending
            // dictionary, which is detached here and returned after the lock so ownership transfers exactly once.
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
            // Cleared, or the pool keeps the referenced subjects, sources and boxed values alive.
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
    /// Snapshots the pending changes, followed by the check-only entries, into <paramref name="rentedArray"/>, which the
    /// caller returns also when this throws, and runs the context check, the Ignore rebase, the commit check and conflict
    /// detection on them. Returns the pending changes the commit writes and applies, and the check-only entries.
    /// </summary>
    private (Memory<SubjectPropertyChange> Changes, Memory<SubjectPropertyChange> CheckOnlyChanges) SnapshotAndCheckChanges(
        out SubjectPropertyChange[] rentedArray)
    {
        Memory<SubjectPropertyChange> checkedChanges;
        int changeCount;
        lock (_pendingChangesLock)
        {
            // A dispose while the commit waited for the optimistic lock detached the pending changes.
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(SubjectTransaction));
            }

            // Rent before changing state so an allocation failure leaves the transaction reusable.
            changeCount = _pendingChanges?.Count ?? 0;
            var checkOnlyCount = CheckedCheckOnlyCount;
            rentedArray = ArrayPool<SubjectPropertyChange>.Shared.Rent(changeCount + checkOnlyCount);

            // Set while holding the lock so no write can be captured, and no handler included, between the snapshot
            // and the end of the commit.
            _isCommitting = true;

            _pendingChanges?.Values.CopyTo(rentedArray, 0);
            if (checkOnlyCount > 0)
            {
                _checkOnlyChanges!.Values.CopyTo(rentedArray, changeCount);
            }

            checkedChanges = rentedArray.AsMemory(0, changeCount + checkOnlyCount);
        }

        // Every change was written back before the snapshot, for example while an optimistic commit waited for the
        // lock: nothing to check.
        if (checkedChanges.IsEmpty)
        {
            return (checkedChanges, checkedChanges);
        }

        var changes = checkedChanges[..changeCount];
        ThrowIfConflicting(SubjectPropertyChangeOperations.DetectSubjectsOutsideContext(checkedChanges.Span, Interceptor));
        if (ConflictBehavior == TransactionConflictBehavior.Ignore)
        {
            using (EnterCommitModelAccess())
            {
                RebaseOldValuesOnLandedValues(changes.Span);
            }
        }

        RunCommitCheck(checkedChanges.Span);

        if (ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            using (EnterCommitModelAccess())
            {
                ThrowIfConflicting(SubjectPropertyChangeOperations.DetectChangeConflicts(checkedChanges.Span));
            }
        }

        return (changes, checkedChanges[changeCount..]);
    }

    /// <summary>
    /// Calls the handlers with the snapshot before anything is written. A handler exception propagates unchanged unless
    /// the transaction was disposed meanwhile; the caller's <see cref="EndCommit"/> then keeps the pending changes for
    /// a retry.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the transaction was disposed while the handlers ran, also in place of a handler exception.
    /// </exception>
    private void RunCommitCheck(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        if (_handlerSet.Handlers.Length == 0)
        {
            return;
        }

        try
        {
            RunHandlerCheck(changes, excludedProperties: null, TransactionCheckStage.Commit);
        }
        catch when (IsDisposed)
        {
            // A dispose from another flow drops the pending values the handlers read, so their verdict is void.
        }

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
        using var scope = new CommitScope(this, stage, excludedProperties);
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
                var count = SubjectPropertyChangeOperations.CopyExcludingProperties(changes, failedProperties, rentedArray);

                // Nothing lands without a landing change, so the entries are checked only alongside one.
                if (count > 0)
                {
                    CopyCheckOnlyChangesWithLandedValues(checkOnlyChanges, rentedArray.AsSpan(count));
                    count += checkOnlyChanges.Length;
                }

                landingChanges = new ReadOnlySpan<SubjectPropertyChange>(rentedArray, 0, count);
            }

            if (landingChanges.IsEmpty)
            {
                return null;
            }

            // A dispose from another flow mid-commit drops the pending values this check reads, so the set is
            // rejected instead of landing unchecked.
            if (IsDisposed)
            {
                return [new ObjectDisposedException(nameof(SubjectTransaction))];
            }

            RunHandlerCheck(landingChanges, failedProperties, TransactionCheckStage.PreApply);
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
    /// Copies the check-only entries into <paramref name="destination"/> with their captured origin and timestamps and
    /// their current landed value as old and new value.
    /// </summary>
    private void CopyCheckOnlyChangesWithLandedValues(
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
    }

    private static void ThrowIfConflicting(List<PropertyReference>? conflictingProperties)
    {
        if (conflictingProperties is not null)
        {
            throw new SubjectTransactionConflictException(conflictingProperties);
        }
    }

    /// <summary>
    /// Replaces the old value of each snapshot slot whose landed value changed since the capturing write by that landed
    /// value, so the commit check receives, and a revert restores, the value this commit overwrites.
    /// </summary>
    private static void RebaseOldValuesOnLandedValues(Span<SubjectPropertyChange> changes)
    {
        for (var index = 0; index < changes.Length; index++)
        {
            var change = changes[index];
            if (change.Property.Metadata.GetValue is not { } getValue)
            {
                continue;
            }

            var landedValue = getValue(change.Property.Subject);
            if (!Equals(landedValue, change.GetOldValue<object?>()))
            {
                changes[index] = SubjectPropertyChange
                    .Create(change.Property, change.Origin, change.ChangedTimestamp, change.ReceivedTimestamp, landedValue, landedValue)
                    .MergeWithNewer(change);
            }
        }
    }

    private CancellationTokenSource? CreateCommitTimeoutCts()
    {
        return _commitTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_commitTimeout);
    }

    private CommitScope EnterCommitModelAccess() => new(this, checkStage: null, checkExcludedProperties: null);

    /// <summary>
    /// Authorizes synchronous commit-owned work of the transaction on this thread: a handler check when
    /// <c>checkStage</c> is set (see <see cref="TryGetCheckedValue{TProperty}"/>), and model access otherwise. The
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
            TransactionCheckStage? checkStage,
            HashSet<PropertyReference>? checkExcludedProperties)
        {
            // Dispose resets the check state instead of restoring it, which holds only while the scopes of one
            // transaction run one after another.
            Debug.Assert(!ReferenceEquals(_commitScopeTransaction, transaction), "Commit scopes of one transaction must not nest.");
            _transaction = transaction;
            _previousTransaction = _commitScopeTransaction;
            transaction._checkStage = checkStage;
            transaction._checkExcludedProperties = checkExcludedProperties;
            _commitScopeTransaction = transaction;
        }

        public void Dispose()
        {
            _commitScopeTransaction = _previousTransaction;
            _transaction._checkStage = null;
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

            // Only when it holds this transaction, so a cross-flow Dispose cannot clear an unrelated one.
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
