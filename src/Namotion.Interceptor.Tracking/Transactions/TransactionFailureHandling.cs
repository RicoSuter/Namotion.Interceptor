namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transaction commit handles partial failures when writing to external sources.
/// </summary>
public enum TransactionFailureHandling
{
    /// <summary>
    /// Best-effort mode: Apply successful changes to the local model, rollback failed ones.
    /// If local apply fails, attempts to restore any local mutation and successful source write for that property.
    /// A <see cref="SubjectTransactionException"/> reports apply and rollback failures.
    /// This maximizes successful writes but may result in partial updates across properties.
    /// </summary>
    BestEffort,

    /// <summary>
    /// Rollback mode: Attempt to restore all local mutations and successful source writes on failure.
    /// If any source write fails, local replay is skipped. If local replay fails, mutated properties are restored
    /// in reverse order before source writes are reverted. A <see cref="SubjectTransactionException"/> reports
    /// the original failure and any rollback failures; restoration is not guaranteed when rollback also fails.
    /// </summary>
    Rollback
}
