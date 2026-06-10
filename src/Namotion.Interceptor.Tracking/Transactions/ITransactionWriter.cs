using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Handler for writing transaction changes to external sources.
/// Implement this interface and register it with <see cref="IInterceptorSubjectContext"/>
/// to enable external source writes during transaction commit.
/// </summary>
public interface ITransactionWriter
{
    /// <summary>
    /// Writes every source-bound change to its source (best-effort per source) and reports the
    /// per-change outcome. Does not apply anything to the local model. Performs classification (source vs
    /// local) and the SingleWrite requirement check, since it alone knows the SetSource mappings.
    /// Local (no-source) changes are neither written nor returned; the transaction applies them.
    /// </summary>
    /// <remarks>
    /// Implementations must report per-source failures via <see cref="SourceWriteResult"/> rather than
    /// throwing: reverting requires the written set and revert state returned here. A throw returns
    /// neither, so the transaction fails terminally with every change reported as failed and any writes
    /// that already reached other sources left un-reverted.
    /// Implementations should return each written change re-marked with the source that accepted it
    /// (its <see cref="SubjectPropertyChange.Source"/> set to that source, preserving values and
    /// timestamps). The commit substitutes these marked changes into its snapshot so the local apply
    /// publishes notifications that outbound connector queues recognize as echoes of that source.
    /// Implementations that return the original changes keep the legacy behavior where committed
    /// values are also re-pushed to the source by the background change queue.
    /// </remarks>
    /// <param name="changes">The property changes to classify and write.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The result reporting which source-bound changes reached their source (<see cref="SourceWriteResult.Written"/>),
    /// which failed (<see cref="SourceWriteResult.Failed"/>), and the corresponding errors.
    /// </returns>
    ValueTask<SourceWriteResult> WriteToSourcesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reverts previously-written changes at their sources (for rollback) by writing the inverse
    /// values back to each source.
    /// </summary>
    /// <remarks>
    /// Implementations must report revert failures via <see cref="SourceRevertResult"/> rather than
    /// throwing. A throw is treated as if every requested revert failed and the commit fails terminally.
    /// </remarks>
    /// <param name="written">The previously-written changes to revert, as returned in <see cref="SourceWriteResult.Written"/>.</param>
    /// <param name="revertState">The opaque writer-owned state from <see cref="SourceWriteResult.RevertState"/> identifying the original target sources.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result reporting any revert failures and their errors.</returns>
    ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken);
}
