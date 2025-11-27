using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a transaction that captures property changes and commits them atomically.
/// Changes are buffered until <see cref="CommitAsync"/> is called.
/// </summary>
public sealed class SubjectTransaction : IDisposable
{
    private static readonly AsyncLocal<SubjectTransaction?> _current = new();

    private bool _isCommitting;
    private bool _isCommitted;
    private bool _isDisposed;

    /// <summary>
    /// Gets the current transaction in this execution context, or null if none is active.
    /// </summary>
    public static SubjectTransaction? Current => _current.Value;

    /// <summary>
    /// Gets a value indicating whether the transaction is currently committing changes.
    /// </summary>
    internal bool IsCommitting => _isCommitting;

    private SubjectTransaction()
    {
    }

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a nested transaction is attempted.</exception>
    public static SubjectTransaction BeginTransaction()
    {
        if (_current.Value != null)
            throw new InvalidOperationException("Nested transactions are not supported.");

        var transaction = new SubjectTransaction();
        _current.Value = transaction;
        return transaction;
    }

    /// <summary>
    /// Internal storage for pending changes. Keyed by PropertyReference.
    /// Last write wins if same property is written multiple times.
    /// </summary>
    internal Dictionary<PropertyReference, SubjectPropertyChange> PendingChanges { get; } = new();

    /// <summary>
    /// Gets the pending changes as a read-only list.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> GetPendingChanges() => PendingChanges.Values.ToList();

    /// <summary>
    /// Commits all pending changes. If external write callbacks are configured on subjects' contexts,
    /// changes are written to external sources first, then applied to the in-process model.
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

        // 1. Group changes by context and call write callbacks (external source writes)
        var allSuccessfulChanges = new List<SubjectPropertyChange>();
        var allFailures = new List<Exception>();

        var changesByContext = changes.GroupBy(c => c.Property.Subject.Context);
        foreach (var contextGroup in changesByContext)
        {
            var context = contextGroup.Key;
            var contextChanges = contextGroup.ToList();
            var interceptor = context.TryGetService<SubjectTransactionInterceptor>();

            if (interceptor?.WriteChangesCallback != null)
            {
                var result = await interceptor.WriteChangesCallback(contextChanges, cancellationToken);
                allSuccessfulChanges.AddRange(result.SuccessfulChanges);
                allFailures.AddRange(result.Failures);
            }
            else
            {
                // No callback configured - all changes are successful
                allSuccessfulChanges.AddRange(contextChanges);
            }
        }

        // 2. Mark as committing (interceptor passes through)
        _isCommitting = true;

        try
        {
            // 3. Apply successful changes to in-process model (full interceptor chain runs)
            foreach (var change in allSuccessfulChanges)
            {
                var metadata = change.Property.Metadata;
                metadata.SetValue?.Invoke(change.Property.Subject, change.GetNewValue<object?>());
            }
        }
        finally
        {
            // 4. Partial cleanup - clear pending changes but let Dispose handle AsyncLocal
            // AsyncLocal changes in async context don't propagate back to caller
            PendingChanges.Clear();
            _isCommitted = true;
        }

        // 5. Throw if any external writes failed
        if (allFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more external sources failed. Successfully written changes have been applied.",
                allFailures);
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
            _current.Value = null;
            _isDisposed = true;
        }
    }
}
