namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Controls handling of concurrent modifications during a transaction. With either behavior, a commit throws
/// <see cref="SubjectTransactionConflictException"/> when a written subject left the transaction's context.
/// </summary>
public enum TransactionConflictBehavior
{
    /// <summary>
    /// Detect conflicts at commit time by comparing captured old values with landed values.
    /// Throws <see cref="SubjectTransactionConflictException"/> if the landed value of a written property changed since
    /// the write that captured its pending change.
    /// Use for command-style transactions requiring consistency.
    /// </summary>
    FailOnConflict,

    /// <summary>
    /// Ignore conflicts, last write wins.
    /// The old values, and so the values a revert of a failed commit restores, are the landed values before the commit
    /// check.
    /// Use for UI sync where latest user input should always apply.
    /// </summary>
    Ignore
}
