using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of writing source-bound changes to their external sources during transaction commit.
/// The writer performs only external source I/O; it never applies changes in-process. The
/// transaction applies the in-process model and orchestrates rollback based on this result.
/// </summary>
/// <param name="Written">
/// The source-bound changes that reached their source and can therefore be reverted there. The
/// transaction holds this list opaquely and passes it back to <see cref="ITransactionWriter.RevertAsync"/>
/// on rollback.
/// </param>
/// <param name="Failed">
/// The source-bound changes whose source write failed. These are excluded from the in-process apply
/// and reported as failures.
/// </param>
/// <param name="Errors">
/// The errors that occurred while writing to sources, typically one per source that failed.
/// </param>
public readonly record struct SourceWriteResult(
    IReadOnlyList<SubjectPropertyChange> Written,
    IReadOnlyList<SubjectPropertyChange> Failed,
    IReadOnlyList<Exception> Errors);
