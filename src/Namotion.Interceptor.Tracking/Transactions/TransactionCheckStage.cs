namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies which check of a commit calls <see cref="ITransactionHandler.OnCommitting"/>.
/// </summary>
public enum TransactionCheckStage
{
    /// <summary>
    /// The commit check, before conflict detection and any source write or local apply. A rejection aborts the commit
    /// without writing anything, propagates unchanged and leaves the transaction retryable; when the transaction was
    /// disposed during the check, <see cref="ObjectDisposedException"/> is thrown instead.
    /// </summary>
    Commit,

    /// <summary>
    /// The pre-apply check, with a registered <see cref="ITransactionWriter"/> only: after the source writes, right
    /// before the local apply. Skipped when nothing will be applied, and in
    /// <see cref="TransactionFailureHandling.Rollback"/> mode after a source write failure or writer error. A rejection
    /// reverts the source writes, lands nothing in either failure handling mode, reports every change of the
    /// transaction as failed and ends the transaction.
    /// </summary>
    PreApply
}
