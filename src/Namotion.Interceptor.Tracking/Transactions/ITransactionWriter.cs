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
    /// For each change a source accepts, the implementation must replace that slot in <paramref name="changes"/>
    /// with the same change marked by the accepting source (<see cref="SubjectPropertyChange.WithSource"/>),
    /// and only after the source write succeeded. Marking a slot lets the commit's local apply and any local
    /// revert publish notifications that the outbound connector queue for that source recognizes as echoes;
    /// failed and never-written slots must be left untouched. The implementation must change only the
    /// <see cref="SubjectPropertyChange.Source"/> of a slot, never move a change to a different slot.
    /// An implementation that does not mark accepted slots still commits correctly but degrades gracefully
    /// to the pre-marking behavior: the apply notifications carry no source, so the outbound connector
    /// queue does not recognize them as echoes and pushes each committed value to its source a second time.
    /// <paramref name="changes"/> is a pooled buffer owned by the commit: it must not be retained or mutated
    /// after the returned task completes. When writing to multiple sources in parallel, each writer must
    /// touch only the slots of the changes bound to its own source.
    /// Enable <see cref="SubjectTransaction.ValidateWriterContract"/> while developing an implementation:
    /// the commit then verifies after every write that no slot was moved or replaced and fails terminally
    /// on a violation.
    /// </remarks>
    /// <param name="changes">The commit snapshot to classify, write, and mark in place.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The result reporting which source-bound changes reached their source (<see cref="SourceWriteResult.Written"/>),
    /// which failed (<see cref="SourceWriteResult.Failed"/>), and the corresponding errors.
    /// </returns>
    ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
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
