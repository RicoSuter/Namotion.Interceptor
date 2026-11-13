namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transactions are synchronized with respect to other transactions.
/// </summary>
public enum TransactionLocking
{
    /// <summary>
    /// Acquires the lock when the transaction begins and holds it until disposal, so only one transaction per context
    /// is active at a time and others wait.
    /// </summary>
    Exclusive,

    /// <summary>
    /// No lock is acquired at the start; multiple transactions can run concurrently.
    /// A lock is only acquired during the commit phase to serialize the application of changes.
    /// Conflicts are detected via <see cref="TransactionConflictBehavior.FailOnConflict"/>.
    /// </summary>
    Optimistic
}
