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

    private readonly TransactionMode _mode;
    private readonly TransactionRequirement _requirement;

    private volatile bool _isCommitting;
    private volatile bool _isCommitted;
    private volatile bool _isDisposed;

    /// <summary>
    /// Gets the current transaction in this execution context, or null if none is active.
    /// </summary>
    public static SubjectTransaction? Current => CurrentTransaction.Value;

    /// <summary>
    /// Gets a value indicating whether the transaction is currently committing changes.
    /// </summary>
    internal bool IsCommitting => _isCommitting;

    private SubjectTransaction(TransactionMode mode, TransactionRequirement requirement)
    {
        _mode = mode;
        _requirement = requirement;
    }

    /// <summary>
    /// Begins a new transaction with the specified mode and requirements.
    /// </summary>
    /// <param name="mode">The transaction mode controlling failure handling behavior. Defaults to <see cref="TransactionMode.BestEffort"/>.</param>
    /// <param name="requirement">The transaction requirement for validation. Defaults to <see cref="TransactionRequirement.None"/>.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a nested transaction is attempted.</exception>
    public static SubjectTransaction BeginTransaction(
        TransactionMode mode = TransactionMode.BestEffort,
        TransactionRequirement requirement = TransactionRequirement.None)
    {
        if (CurrentTransaction.Value != null)
            throw new InvalidOperationException("Nested transactions are not supported.");

        var transaction = new SubjectTransaction(mode, requirement);
        CurrentTransaction.Value = transaction;
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
    /// <exception cref="AggregateException">Thrown when one or more external source writes failed.</exception>
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

        // 1. Group changes by context and call write handlers (external source writes)
        var allSuccessfulChanges = new List<SubjectPropertyChange>();
        var allFailures = new List<Exception>();

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
                allFailures.AddRange(result.Failures);
            }
            else
            {
                // No handler configured - all changes are successful
                allSuccessfulChanges.AddRange(contextChanges);
            }
        }

        // 2. Handle mode-specific behavior
        var shouldApplyChanges = _mode switch
        {
            // BestEffort: Apply all successful changes even if some failed
            TransactionMode.BestEffort => true,
            // Strict/Rollback: Only apply if ALL changes succeeded
            TransactionMode.Strict or TransactionMode.Rollback => allFailures.Count == 0,
            _ => true
        };

        // 3. Mark as committing (interceptor passes through)
        _isCommitting = true;

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
        if (allFailures.Count > 0)
        {
            var message = _mode switch
            {
                TransactionMode.BestEffort => "One or more external sources failed. Successfully written changes have been applied.",
                TransactionMode.Strict => "One or more external sources failed. No changes have been applied to the in-process model.",
                TransactionMode.Rollback => "One or more external sources failed. Rollback was attempted. No changes have been applied to the in-process model.",
                _ => "One or more external sources failed."
            };
            throw new AggregateException(message, allFailures);
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
            _isDisposed = true;
        }
    }
}
