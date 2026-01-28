namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transactions are synchronized with respect to other transactions.
/// </summary>
public enum TransactionLocking
{
    /// <summary>
    /// Acquires an exclusive lock at the start of the transaction.
    /// Only one transaction can be active at a time per context.
    /// Other transactions wait until the current one completes.
    /// </summary>
    Exclusive,

    /// <summary>
    /// No lock is acquired at the start; multiple transactions can run concurrently.
    /// A lock is only acquired during the commit phase to serialize the application of changes.
    /// Conflicts are detected via <see cref="TransactionConflictBehavior.FailOnConflict"/>.
    /// </summary>
    Optimistic
}
