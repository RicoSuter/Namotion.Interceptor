namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies when transaction handlers validate the values written in a transaction. The modes are compared in
/// the validation documentation (docs/validation.md).
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
    /// write or local apply, with reads returning the transaction's values. Write order and intermediate values do
    /// not matter.
    /// </summary>
    Deferred
}
