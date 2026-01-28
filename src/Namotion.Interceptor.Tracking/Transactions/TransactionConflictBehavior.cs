namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Controls handling of concurrent modifications during a transaction.
/// </summary>
public enum TransactionConflictBehavior
{
    /// <summary>
    /// Detect conflicts at commit time by comparing captured old values with current values.
    /// Throws <see cref="SubjectTransactionConflictException"/> if any property was modified since the transaction started.
    /// Use for command-style transactions requiring consistency.
    /// </summary>
    FailOnConflict,

    /// <summary>
    /// Ignore conflicts, last write wins.
    /// Use for UI sync where latest user input should always apply.
    /// </summary>
    Ignore
}
