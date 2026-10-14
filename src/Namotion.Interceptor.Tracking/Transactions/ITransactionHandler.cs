using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Takes part in a transaction when it is registered on the context the transaction begins on, resolved when the
/// transaction begins, or on the context of a subject written in the transaction, resolved when the subject is written;
/// a handler registered on the transaction's context after it began does not take part. A handler then takes part in
/// the rest of the transaction: it is called for later writes to any subject and checks the whole final set, so a
/// handler that cares only about some subjects filters by <see cref="PropertyReference.Subject"/>. Handlers are called
/// in the order they were resolved, each context's in its service order (registration order unless ordering attributes
/// say otherwise); the first handler that throws stops the rest.
/// </summary>
public interface ITransactionHandler
{
    /// <summary>
    /// Called for a write the transaction is about to capture, outside the transaction lock. Throwing rejects the
    /// write: nothing is captured and the exception propagates to the setter. Handlers must not write properties.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="transaction">The capturing transaction.</param>
    /// <param name="property">The written property.</param>
    /// <param name="newValue">The written value.</param>
    /// <param name="origin">The attempted origin of the write.</param>
    void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin);

    /// <summary>
    /// Called with the final set of changes before any source write or local apply and, with a registered
    /// <see cref="ITransactionWriter"/>, a second time after the source writes and right before the local apply,
    /// with only the changes that will be applied (skipped when there are none). Property reads return the
    /// transaction's values for the passed changes and the landed values otherwise. Handlers must not write
    /// properties and must not assume a single call per commit.
    /// </summary>
    /// <remarks>
    /// Throwing on the first call aborts the commit without writing anything and leaves the transaction retryable;
    /// the exception propagates unchanged. Throwing on the second call fails the affected changes (the failed
    /// changes of a <see cref="SubjectTransactionException"/>, or every passed change for any other exception or when
    /// none of those failed changes was passed), which is handled like a failed local apply and makes the transaction
    /// terminal.
    /// </remarks>
    /// <param name="transaction">The committing transaction.</param>
    /// <param name="changes">
    /// The final set of changes in insertion order; on the second call only the changes that will be applied.
    /// </param>
    void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes);
}
