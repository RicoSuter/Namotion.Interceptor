namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transaction commit handles partial failures when writing to external sources.
/// </summary>
public enum TransactionMode
{
    /// <summary>
    /// Best-effort mode: Apply all successful changes to the in-process model, even if some sources fail.
    /// Failed changes are not applied. An <see cref="AggregateException"/> is thrown containing all failures.
    /// This maximizes successful writes but may result in partial updates.
    /// </summary>
    BestEffort,

    /// <summary>
    /// Rollback mode (default): Attempt to revert successful source writes on failure.
    /// If any source write fails, attempts to write the original values back to sources that succeeded.
    /// If revert also fails, both the original failure and revert failures are reported.
    /// No changes are applied to the in-process model on failure.
    /// This mode provides the strongest consistency guarantee between in-memory state and external sources.
    /// </summary>
    Rollback
}
