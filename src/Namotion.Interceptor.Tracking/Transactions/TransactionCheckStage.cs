namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies which check of a commit calls <see cref="ITransactionHandler.OnCommitting"/>.
/// </summary>
public enum TransactionCheckStage
{
    /// <summary>
    /// The commit check, before conflict detection and any source write or local apply. A rejection aborts the commit
    /// without writing anything and leaves the transaction retryable.
    /// </summary>
    Commit,

    /// <summary>
    /// The pre-apply check, with a registered <see cref="ITransactionWriter"/> only: after the source writes, right
    /// before the local apply. A rejection reverts the source writes, lands nothing and ends the transaction.
    /// </summary>
    PreApply
}
