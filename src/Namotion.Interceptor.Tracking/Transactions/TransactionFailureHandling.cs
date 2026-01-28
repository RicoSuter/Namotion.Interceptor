namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transaction commit handles partial failures when writing to external sources.
/// </summary>
public enum TransactionFailureHandling
{
    /// <summary>
    /// Best-effort mode: Apply successful changes to the in-process model, rollback failed ones.
    /// For each property, if the source write succeeds but local apply fails, the source is rolled back
    /// to maintain per-property consistency. A <see cref="SubjectTransactionException"/> is thrown containing all failures.
    /// This maximizes successful writes but may result in partial updates across properties.
    /// </summary>
    BestEffort,

    /// <summary>
    /// Rollback mode: Attempt to revert successful source writes on failure.
    /// If any source write fails, attempts to write the original values back to sources that succeeded.
    /// If revert also fails, both the original failure and revert failures are reported.
    /// No changes are applied to the in-process model on failure.
    /// This mode provides the strongest consistency guarantee between in-memory state and external sources.
    /// </summary>
    Rollback
}
