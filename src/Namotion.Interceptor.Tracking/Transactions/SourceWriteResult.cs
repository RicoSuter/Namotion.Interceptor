using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of writing source-bound changes to their external sources during transaction commit.
/// The writer performs only external source I/O; it never applies changes to the local model. The
/// transaction applies the local model and orchestrates rollback based on this result.
/// </summary>
/// <param name="Written">
/// The source-bound changes that reached their source and can therefore be reverted there. The
/// transaction holds this list opaquely and passes it back to <see cref="ITransactionWriter.RevertSourceWritesAsync"/>
/// on rollback.
/// Each change should carry the source that accepted it as its Source; see the remarks on <see cref="ITransactionWriter.WriteToSourcesAsync"/>.
/// </param>
/// <param name="Failed">
/// The source-bound changes whose source write failed. These are excluded from the local apply
/// and reported as failures.
/// </param>
/// <param name="Errors">
/// The errors that occurred while writing to sources, typically one per source that failed.
/// </param>
/// <param name="RevertState">
/// Opaque writer-owned state that identifies which source each written change was written to, handed
/// back to <see cref="ITransactionWriter.RevertSourceWritesAsync"/> so reverts target the exact sources written
/// (never re-derived from the current SetSource mapping).
/// </param>
public readonly record struct SourceWriteResult(
    IReadOnlyList<SubjectPropertyChange> Written,
    IReadOnlyList<SubjectPropertyChange> Failed,
    IReadOnlyList<Exception> Errors,
    object? RevertState);
