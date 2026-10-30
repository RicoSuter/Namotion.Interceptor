namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transaction commit handles partial failures when writing to external sources.
/// </summary>
public enum TransactionFailureHandling
{
    /// <summary>
    /// Keeps the changes that succeeded, so properties can be partially updated. A property whose source write
    /// succeeded but whose local apply failed has its source write reverted, unless that revert fails or the failed
    /// apply already wrote its value. A <see cref="SubjectTransactionException"/> reports all failures. When
    /// <see cref="ITransactionHandler"/>s checked the changes, a failed local apply instead reverts every change, as in
    /// <see cref="Rollback"/> mode, and reports every change as failed.
    /// </summary>
    BestEffort,

    /// <summary>
    /// On any failure, reverts every source write and every local apply, so the commit lands all changes or none.
    /// Failed reverts are reported along with the original failures. A failed revert, a throwing writer, a commit
    /// timeout or a failed apply that already wrote its value can still leave a property changed, see When a Revert
    /// Fails in docs/tracking-transactions.md.
    /// </summary>
    Rollback
}
