namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies when transaction handlers validate the values written in a transaction. Handlers read it from
/// <see cref="SubjectTransaction.ValidationMode"/>; the transaction itself only uses it to decide whether the commit
/// check receives writes of unchanged values (see <see cref="Deferred"/>).
/// </summary>
public enum TransactionValidationMode
{
    /// <summary>
    /// Each write is validated against the transaction's values so far, so write order matters at the setter, and the
    /// final set is validated again at commit before any source write or local apply, independent of write order.
    /// </summary>
    Immediate,

    /// <summary>
    /// Writes are not validated when written. The final set is validated at commit against the transaction's values,
    /// before any source write or local apply, independent of write order and intermediate values. With the equality
    /// check, <see cref="TransactionConflictBehavior.FailOnConflict"/> and handlers, the commit check also receives
    /// each property written with its landed value, without a pending change or written back to it, as an entry whose
    /// old and new value are equal; conflict detection checks it, but the commit neither writes, applies nor
    /// publishes it.
    /// </summary>
    Deferred
}
