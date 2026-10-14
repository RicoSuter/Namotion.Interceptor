using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Participates in the transactions of the context it is registered on. Handlers are resolved from the
/// transaction's context when the transaction begins and are called in registration order; the first handler
/// that throws stops the rest.
/// </summary>
public interface ITransactionHandler
{
    /// <summary>
    /// Called for a write the transaction is about to capture, outside the transaction lock. Throwing rejects the
    /// write: nothing is captured and the exception propagates to the setter.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="transaction">The capturing transaction.</param>
    /// <param name="property">The written property.</param>
    /// <param name="newValue">The written value.</param>
    /// <param name="origin">The attempted origin of the write.</param>
    void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin);

    /// <summary>
    /// Called with the final set of changes before any source write or local apply and, with a registered
    /// <see cref="ITransactionWriter"/>, a second time after the source writes and right before the local apply.
    /// Property reads return the transaction's values for the changed properties. Handlers must not write
    /// properties and must not assume a single call per commit.
    /// </summary>
    /// <remarks>
    /// Throwing on the first call aborts the commit without writing anything and leaves the transaction retryable;
    /// the exception propagates unchanged. Throwing on the second call fails the affected changes (the failed
    /// changes of a <see cref="SubjectTransactionException"/>, or every change for any other exception), which is
    /// handled like a failed local apply and makes the transaction terminal.
    /// </remarks>
    /// <param name="transaction">The committing transaction.</param>
    /// <param name="changes">The final set of changes, in insertion order.</param>
    void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes);
}
