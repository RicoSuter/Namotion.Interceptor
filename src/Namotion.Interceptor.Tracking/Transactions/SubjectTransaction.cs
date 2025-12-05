using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a transaction that captures property changes and commits them atomically.
/// Changes are buffered until <see cref="CommitAsync"/> is called.
/// </summary>
public sealed class SubjectTransaction : IDisposable, IAsyncDisposable
{
    private static readonly AsyncLocal<SubjectTransaction?> CurrentTransaction = new();

    private readonly TransactionMode _mode;
    private readonly TransactionRequirement _requirement;
    private readonly TransactionConflictBehavior _conflictBehavior;
    private readonly IDisposable? _lockReleaser;

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private volatile bool _isDisposed;

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
        TransactionMode mode,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        DateTimeOffset startTimestamp,
        IDisposable? lockReleaser)
    {
        Context = context;
        _mode = mode;
        _requirement = requirement;
        _conflictBehavior = conflictBehavior;
        StartTimestamp = startTimestamp;
        _lockReleaser = lockReleaser;
    }

    /// <summary>
    /// Begins a new transaction with the specified mode and requirements.
    /// </summary>
    /// <param name="mode">The transaction mode controlling failure handling behavior. Defaults to <see cref="TransactionMode.Rollback"/> for maximum consistency.</param>
    /// <param name="requirement">The transaction requirement for validation. Defaults to <see cref="TransactionRequirement.None"/>.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a nested transaction is attempted.</exception>
    public static SubjectTransaction BeginTransaction(
        TransactionMode mode = TransactionMode.Rollback,
        TransactionRequirement requirement = TransactionRequirement.None)
    {
        if (CurrentTransaction.Value != null)
            throw new InvalidOperationException("Nested transactions are not supported.");

        var transaction = new SubjectTransaction(
            null!,
            mode,
            requirement,
            TransactionConflictBehavior.FailOnConflict,
            DateTimeOffset.UtcNow,
            null);
        CurrentTransaction.Value = transaction;
        return transaction;
    }

    /// <summary>
    /// Begins a new transaction bound to the specified context.
    /// Waits if another transaction is active on this context.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="mode">The transaction mode controlling failure handling behavior.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="conflictBehavior">The conflict detection behavior.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    internal static async ValueTask<SubjectTransaction> BeginAsync(
        IInterceptorSubjectContext context,
        TransactionMode mode,
        TransactionRequirement requirement,
        TransactionConflictBehavior conflictBehavior,
        CancellationToken cancellationToken)
    {
        // Get or create TransactionLock for this context
        var lockService = context.TryGetService<TransactionLock>();
        if (lockService == null)
        {
            // Add the lock service lazily
            context.TryAddService(
                () => new TransactionLock(),
                existing => existing != null);
            lockService = context.TryGetService<TransactionLock>();
        }

        // Acquire the lock - this serializes concurrent transactions on the same context
        var lockReleaser = await lockService!.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // Check for nested transactions AFTER acquiring lock
        // This allows concurrent transactions from different async contexts (they queue on lock)
        // but prevents nesting within the same async flow
        if (CurrentTransaction.Value != null)
        {
            lockReleaser.Dispose(); // Release lock before throwing
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        var transaction = new SubjectTransaction(
            context,
            mode,
            requirement,
            conflictBehavior,
            DateTimeOffset.UtcNow,
            lockReleaser);
        // Note: Don't set CurrentTransaction.Value here - it won't flow to caller's context
        // The caller (extension method) will call SetCurrent after awaiting this
        return transaction;
    }

    /// <summary>
    /// Internal storage for pending changes. Keyed by PropertyReference.
    /// Last write wins if same property is written multiple times.
    /// Thread-safe for concurrent property writes within the same transaction.
    /// </summary>
    internal ConcurrentDictionary<PropertyReference, SubjectPropertyChange> PendingChanges { get; } = new();

    /// <summary>
    /// Gets the pending changes as a read-only list.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges() => PendingChanges.Values.ToList();

    /// <summary>
    /// Commits all pending changes. If external write handlers are configured on subjects' contexts,
    /// changes are written to external sources first, then applied to the in-process model.
    /// The behavior on partial failure depends on the <see cref="_mode"/> specified at transaction creation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="TransactionException">Thrown when one or more external source writes failed.</exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (PendingChanges.Count == 0)
        {
            // Mark as committed (Dispose will handle AsyncLocal cleanup in caller's context)
            _isCommitted = true;
            return;
        }

        var changes = PendingChanges.Values.ToList();

        // 1. Check for conflicts at commit time if FailOnConflict behavior is enabled
        if (_conflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var conflictingProperties = new List<PropertyReference>();
            foreach (var change in changes)
            {
                var lastChangedTimestamp = change.Property.GetLastChangedTimestamp();
                if (lastChangedTimestamp.HasValue && lastChangedTimestamp.Value > StartTimestamp)
                {
                    conflictingProperties.Add(change.Property);
                }
            }

            if (conflictingProperties.Count > 0)
            {
                throw new TransactionConflictException(conflictingProperties);
            }
        }

        // 2. Group changes by context and call write handlers (external source writes)
        var allSuccessfulChanges = new List<SubjectPropertyChange>();
        var allFailedChanges = new List<SourceWriteFailure>();

        var changesByContext = changes.GroupBy(c => c.Property.Subject.Context);
        foreach (var contextGroup in changesByContext)
        {
            var context = contextGroup.Key;
            var contextChanges = contextGroup.ToList();
            var writeHandler = context.TryGetService<ITransactionWriteHandler>();

            if (writeHandler != null)
            {
                var result = await writeHandler.WriteChangesAsync(contextChanges, _mode, _requirement, cancellationToken);
                allSuccessfulChanges.AddRange(result.SuccessfulChanges);
                allFailedChanges.AddRange(result.FailedChanges);
            }
            else
            {
                // No handler configured - all changes are successful
                allSuccessfulChanges.AddRange(contextChanges);
            }
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

        // 4. Mark as committing (interceptor passes through)
        _isCommitting = true;

        try
        {
            if (shouldApplyChanges)
            {
                // 5. Apply successful changes to in-process model (full interceptor chain runs)
                foreach (var change in allSuccessfulChanges)
                {
                    var metadata = change.Property.Metadata;
                    metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
                }
            }
        }
        finally
        {
            // 6. Partial cleanup - clear pending changes but let Dispose handle AsyncLocal
            // AsyncLocal changes in async context don't propagate back to caller
            PendingChanges.Clear();
            _isCommitted = true;
        }

        // 7. Throw if any external writes failed
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
        if (!_isDisposed)
        {
            PendingChanges.Clear();
            CurrentTransaction.Value = null;
            _lockReleaser?.Dispose();
            _isDisposed = true;
        }
    }

    /// <summary>
    /// Asynchronously disposes the transaction, discarding any uncommitted changes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
