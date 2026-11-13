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
    /// All changes must be written in a single <c>WriteChangesAsync</c> call: they belong to a single source and do
    /// not exceed its <c>WriteBatchSize</c>. Changes to properties without a source are always allowed.
    /// </summary>
    SingleWrite
}
