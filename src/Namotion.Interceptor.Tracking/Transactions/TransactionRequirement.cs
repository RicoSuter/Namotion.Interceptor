namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies validation requirements that must be satisfied when committing a transaction.
/// </summary>
public enum TransactionRequirement
{
    /// <summary>
    /// No requirements: Multiple sources and multiple batches are allowed.
    /// </summary>
    None,

    /// <summary>
    /// Single write operation requirement: All changes must be written in a single <c>WriteChangesAsync</c> call.
    /// This requires: (1) all changes belong to a single source, and (2) the number of changes does not exceed
    /// the source's <c>WriteBatchSize</c>. Changes to properties without a source are always allowed.
    /// This constraint enables simpler rollback semantics and reduces the risk of partial failures.
    /// </summary>
    SingleWrite
}
