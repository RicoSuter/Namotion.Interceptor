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

    private readonly TransactionMode _mode;
    private readonly TransactionRequirement _requirement;
    private readonly TransactionConflictBehavior _conflictBehavior;
    private readonly IDisposable _lockReleaser;

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
    internal ConcurrentDictionary<PropertyReference, SubjectPropertyChange> PendingChanges { get; } = new();

    /// <summary>
    /// Gets the pending changes as a read-only list.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges() => PendingChanges.Values.ToList();

    /// <summary>
    /// Gets the context this transaction is bound to.
    /// </summary>
    public IInterceptorSubjectContext Context { get; }

    /// <summary>
    /// Gets the timestamp when the transaction started.
    /// </summary>
    public DateTimeOffset StartTimestamp { get; }

    /// <summary>
    /// Gets the conflict behavior for this transaction.
    /// </summary>
    public TransactionConflictBehavior ConflictBehavior => _conflictBehavior;

    private SubjectTransaction(
        IInterceptorSubjectContext context,
        SubjectTransactionInterceptor interceptor,
        TransactionMode mode,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        DateTimeOffset startTimestamp,
        IDisposable lockReleaser)
    {
        Context = context;
        Interceptor = interceptor;
        _mode = mode;
        _requirement = requirement;
        _conflictBehavior = conflictBehavior;
        StartTimestamp = startTimestamp;
        _lockReleaser = lockReleaser;

        // Increment in constructor ensures counter is always paired with Dispose
        Interlocked.Increment(ref _activeTransactionCount);
    }

    /// <summary>
    /// Begins a new exclusive transaction bound to the specified context.
    /// Waits if another transaction is active on this context, ensuring only one
    /// transaction executes at a time per context (exclusive lock).
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="mode">The transaction mode controlling failure handling behavior.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="conflictBehavior">The conflict detection behavior.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when transactions are not enabled or when nested transaction is attempted.</exception>
    internal static async ValueTask<SubjectTransaction> BeginExclusiveTransactionAsync(
        IInterceptorSubjectContext context,
        TransactionMode mode,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
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

        var transactionLock = await interceptor.AcquireExclusiveTransactionLockAsync(cancellationToken).ConfigureAwait(false);

        // Don't set CurrentTransaction.Value here because it won't flow to caller's context
        // The caller (extension method) will call SetCurrent after awaiting this
        // Counter increment is in constructor to ensure it's always paired with Dispose
        return new SubjectTransaction(
            context,
            interceptor,
            mode,
            requirement,
            conflictBehavior,
            DateTimeOffset.UtcNow,
            transactionLock);
    }

    /// <summary>
    /// Commits all pending changes. If external write handlers are configured on subjects' contexts,
    /// changes are written to external sources first, then applied to the in-process model.
    /// The behavior on partial failure depends on the <see cref="_mode"/> specified at transaction creation.
    /// </summary>
    /// <remarks>
    /// Conflict detection compares captured OldValue with current value at commit time.
    /// This catches changes from other transactions and external sources that occurred
    /// after the transaction started. However, concurrent non-transactional writes
    /// during the commit phase are not detected (optimistic concurrency model).
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="TransactionException">Thrown when one or more external source writes failed.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
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

        _isCommitting = true;

        var changes = PendingChanges.Values.ToList();
        try
        {
            if (_conflictBehavior == TransactionConflictBehavior.FailOnConflict)
            {
                // Check for conflicts at commit time if FailOnConflict behavior is enabled
                // Value-based conflict detection: Compare captured OldValue with the current actual value

                var conflictingProperties = new List<PropertyReference>();
                foreach (var change in changes)
                {
                    var currentValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
                    var capturedOldValue = change.GetOldValue<object?>();

                    if (!Equals(currentValue, capturedOldValue))
                    {
                        conflictingProperties.Add(change.Property);
                    }
                }

                if (conflictingProperties.Count > 0)
                {
                    _isCommitting = false;
                    throw new TransactionConflictException(conflictingProperties);
                }
            }
        }
        catch
        {
            _isCommitting = false;
            throw;
        }

        // 2. Call write handler on the context (single writer - no context grouping needed)
        var allSuccessfulChanges = new List<SubjectPropertyChange>();
        var allFailedChanges = new List<SourceWriteFailure>();

        var writeHandler = Context.TryGetService<ITransactionWriter>();
        if (writeHandler != null)
        {
            var result = await writeHandler.WriteChangesAsync(changes, _mode, _requirement, cancellationToken);
            allSuccessfulChanges.AddRange(result.SuccessfulChanges);
            allFailedChanges.AddRange(result.FailedChanges);
        }
        else
        {
            // No handler configured: All changes are successful
            allSuccessfulChanges.AddRange(changes);
        }

        // 3. Handle mode-specific behavior
        var shouldApplyChanges = _mode switch
        {
            // BestEffort: Apply all successful changes even if some failed
            TransactionMode.BestEffort => true,
            // Rollback: Only apply if ALL changes succeeded
            TransactionMode.Rollback => allFailedChanges.Count == 0,
            _ => true
        };

        // _isCommitting already set to true above (for conflict detection)
        try
        {
            if (shouldApplyChanges)
            {
                // 4. Apply successful changes to in-process model (full interceptor chain runs)
                foreach (var change in allSuccessfulChanges)
                {
                    var metadata = change.Property.Metadata;
                    metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
                }
            }
        }
        finally
        {
            // 5. Partial cleanup - clear pending changes but let Dispose handle AsyncLocal
            // AsyncLocal changes in async context don't propagate back to caller
            PendingChanges.Clear();
            _isCommitted = true;
        }

        // 6. Throw if any external writes failed
        if (allFailedChanges.Count > 0)
        {
            var message = _mode switch
            {
                TransactionMode.BestEffort => "One or more external sources failed. Successfully written changes have been applied.",
                TransactionMode.Rollback => "One or more external sources failed. Rollback was attempted. No changes have been applied to the in-process model.",
                _ => "One or more external sources failed."
            };

            throw new TransactionException(message, allSuccessfulChanges, allFailedChanges);
        }
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
            _lockReleaser.Dispose();
        }
    }
}
