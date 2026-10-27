namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies when transaction handlers validate the values written in a transaction. Handlers read it from
/// <see cref="SubjectTransaction.ValidationMode"/>; the transaction itself only uses it to decide whether the commit check
/// receives writes of unchanged values (see <see cref="Deferred"/>).
/// </summary>
public enum TransactionValidationMode
{
    /// <summary>
    /// Each write is validated when it is written, against the transaction's values so far, and the final set of
    /// values is validated again at commit before any source write or local apply. Write order matters at the
    /// setter; the commit check does not depend on it.
    /// </summary>
    Immediate,

    /// <summary>
    /// Writes are not validated when written. The final set of values is validated at commit, before any source
    /// write or local apply, with reads returning the transaction's values. The commit check does not depend on
    /// write order or intermediate values. With the equality check, it also receives each property written with its
    /// landed value and no pending change, or written back to it, as an entry whose old and new value are equal; the
    /// commit neither writes, applies, publishes nor conflict-checks those entries.
    /// </summary>
    Deferred
}
