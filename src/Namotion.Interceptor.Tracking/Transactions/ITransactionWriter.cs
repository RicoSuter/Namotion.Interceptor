using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Writes transaction changes to their sources during a commit. Register an implementation as a service of the
/// <see cref="IInterceptorSubjectContext"/>.
/// </summary>
/// <remarks>
/// Both callbacks run while the caller's ambient transaction is committing, so subject property reads and writes on a
/// flow carrying it throw <see cref="InvalidOperationException"/> unless the transaction was disposed meanwhile. Do not
/// access subject properties from a callback, getters included, since state outside the frozen snapshot can make a
/// source payload inconsistent, and do not suppress execution-context flow to bypass this. Build source operations from
/// the arguments, writer-owned configuration and any subject state captured before
/// <see cref="SubjectTransaction.CommitAsync"/>.
/// </remarks>
public interface ITransactionWriter
{
    /// <summary>
    /// Writes every source-bound change to its source (best-effort per source) and reports the per-change outcome,
    /// without applying anything to the local model. Changes without a source are neither written nor returned. Also
    /// enforces <see cref="TransactionRequirement.SingleWrite"/>.
    /// </summary>
    /// <remarks>
    /// Report failures in the result, never throw: a throw returns neither the written set nor the revert state, so
    /// the transaction fails terminally with nothing reverted. Only the commit timeout's
    /// <see cref="OperationCanceledException"/> propagates and leaves the transaction retryable. After, and only after,
    /// a source accepts a change,
    /// replace its slot in <paramref name="changes"/> with the same change marked by the accepting source
    /// (<see cref="SubjectPropertyChange.WithOrigin"/>), so that source's outbound queue recognizes the commit's apply
    /// and revert notifications as echoes. Change only the slot's <see cref="SubjectPropertyChange.Origin"/>, never
    /// move a change to another slot, and leave failed slots untouched. Not marking is allowed, but the queue then
    /// re-pushes each committed value. <paramref name="changes"/> is a pooled buffer owned by the commit: do not retain
    /// or mutate it after the returned task completes. Parallel per-source writers must touch only their own source's
    /// slots. Enable <see cref="SubjectTransaction.ValidateWriterContract"/> while developing an implementation.
    /// </remarks>
    /// <param name="changes">The commit snapshot to classify, write, and mark in place.</param>
    /// <param name="requirement">The transaction requirement to enforce.</param>
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
    /// Reverts written changes by writing their old values back to the sources they were written to.
    /// </summary>
    /// <remarks>
    /// Report failures in the result instead of throwing: a throw counts as every requested revert failing, and the
    /// commit fails terminally. Only the commit timeout's <see cref="OperationCanceledException"/> during the revert
    /// after a source write failure or reported writer error in <see cref="TransactionFailureHandling.Rollback"/> mode
    /// propagates and leaves the transaction retryable. <see cref="SourceRevertResult.Failed"/> must contain only
    /// changes from <paramref name="written"/>.
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
