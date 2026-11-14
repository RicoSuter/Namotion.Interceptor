using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of writing source-bound changes to their sources during a commit, from which the transaction applies the
/// local model and orchestrates rollback.
/// </summary>
/// <param name="Written">
/// The source-bound changes that reached their source, passed back to
/// <see cref="ITransactionWriter.RevertSourceWritesAsync"/> on rollback. Entries need not carry the accepting source;
/// the snapshot is marked in place instead (see <see cref="ITransactionWriter.WriteToSourcesAsync"/>).
/// </param>
/// <param name="Failed">
/// The source-bound changes whose source write failed, which are not applied and reported as failures.
/// </param>
/// <param name="Errors">
/// The errors of the source writes, typically one per failed source. Any error fails the commit, even with an empty
/// <paramref name="Failed"/>, but condemns no specific change, so list the failed changes whenever possible.
/// </param>
/// <param name="RevertState">
/// Opaque writer-owned state identifying the source each change was written to, passed back to
/// <see cref="ITransactionWriter.RevertSourceWritesAsync"/> so reverts target the sources actually written, not the
/// current source binding.
/// </param>
public readonly record struct SourceWriteResult(
    IReadOnlyList<SubjectPropertyChange> Written,
    IReadOnlyList<SubjectPropertyChange> Failed,
    IReadOnlyList<Exception> Errors,
    object? RevertState);
