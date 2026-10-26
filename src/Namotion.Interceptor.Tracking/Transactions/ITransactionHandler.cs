using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Takes part in the transactions that begin on the context it is registered on, or that write a subject of that
/// context: it is called for the writes the transaction captures and checks the final set at commit.
/// </summary>
/// <remarks>
/// Handlers of the transaction's context are resolved when the transaction begins, those of a written subject's
/// context when a subject of it is first written. A handler registered later does not take part when registered on
/// the transaction's context, and is not guaranteed to when registered on an already included subject context. Once
/// resolved, a handler takes part in the rest of the transaction: it is called for later writes to any subject and
/// checks the whole final set, so a handler that cares only about some subjects filters by
/// <see cref="PropertyReference.Subject"/>. A handler equal to one that already takes part is not added again.
/// Handlers are called in the order they were resolved, each context's in its service order (registration order
/// unless ordering attributes say otherwise); the first handler that throws stops the rest. Only one handler can
/// consume the commit write mark of an applied value (see <see cref="SubjectTransaction.TryConsumeCommitWrite"/>),
/// and a value type with a loose <c>Equals</c> can pass an equal, unchecked value as the checked one. A write to a
/// subject whose context does not route through the transaction interceptor is not intercepted and lands
/// immediately, even during a check.
/// </remarks>
public interface ITransactionHandler
{
    /// <summary>
    /// Called for a write the transaction is about to capture or cancel, outside the transaction lock. Throwing
    /// rejects the write: nothing is captured and the exception propagates to the setter. Handlers must not write
    /// properties. A validating handler skips its write-time check when <see cref="SubjectTransaction.ValidationMode"/>
    /// is <see cref="TransactionValidationMode.Deferred"/>.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="transaction">The capturing transaction.</param>
    /// <param name="property">The written property.</param>
    /// <param name="newValue">The written value.</param>
    /// <param name="origin">The attempted origin of the write.</param>
    void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin);

    /// <summary>
    /// Called in the commit check with the final set of changes, before any source write or local apply, and, with a
    /// registered <see cref="ITransactionWriter"/>, in the pre-apply check after the source writes and right before
    /// the local apply, with only the changes that will be applied (skipped when there are none). Property reads on
    /// the calling thread return the transaction's values for the passed changes and the landed values otherwise. In
    /// both checks, property reads and writes from other threads that carry the transaction throw
    /// <see cref="InvalidOperationException"/>. Handlers must not write properties and must not assume a single call
    /// per commit.
    /// </summary>
    /// <remarks>
    /// Throwing in the commit check aborts the commit without writing anything and leaves the transaction retryable
    /// unless it was disposed; the exception propagates unchanged. Throwing in the pre-apply check lands none of the
    /// passed changes in either failure handling mode: their source writes are reverted, every passed change is
    /// reported as failed, and the transaction is terminal.
    /// </remarks>
    /// <param name="transaction">The committing transaction.</param>
    /// <param name="changes">
    /// The final set of changes in insertion order; in the pre-apply check only the changes that will be applied.
    /// </param>
    void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes);
}
