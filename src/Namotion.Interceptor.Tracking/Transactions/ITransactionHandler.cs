using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Takes part in the transactions that begin on the context it is registered on, or that write a subject of that
/// context: it is called for the writes the transaction captures and checks the final set at commit.
/// </summary>
/// <remarks>
/// Handlers of the transaction's context are resolved when the transaction begins, those of a written subject's context
/// when a subject of it is first written, and again on its next write after that context's services or fallback
/// contexts change. Register handlers before beginning transactions: whether one registered later takes part is
/// undefined. A resolved handler takes part in the rest of the transaction, for later writes to any subject and the
/// whole final set, so filter by <see cref="PropertyReference.Subject"/> to handle only some subjects. A handler equal
/// to one already taking part is not added again. Handlers are called in resolution order, each context's in its
/// service order (registration order unless ordering attributes say otherwise); the first that throws stops the rest.
/// For skipping the write-time check of an applied value, see <see cref="SubjectTransaction.TryConsumeCommitWrite"/>
/// and its caveats. A write to a subject whose context does not route through the transaction interceptor is not
/// intercepted and lands immediately, even during a check. See docs/tracking-transactions.md (Writing a Transaction
/// Handler) for details.
/// </remarks>
public interface ITransactionHandler
{
    /// <summary>
    /// Called, without holding the transaction's capture lock, for a write the transaction is about to capture,
    /// including a write back that removes a pending change and a write of an unchanged value that the commit check
    /// receives (see <see cref="TransactionCheckMode.Deferred"/>). Throwing rejects the write: nothing is captured and
    /// the exception propagates to the setter. Handlers must not write properties. A checking handler skips its
    /// write-time check in <see cref="TransactionCheckMode.Deferred"/> mode (<see cref="SubjectTransaction.CheckMode"/>).
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="context">The write about to be captured.</param>
    void OnCapturing<TProperty>(in TransactionCaptureContext<TProperty> context);

    /// <summary>
    /// Called in each check of a commit, see <see cref="TransactionCheckStage"/>, which
    /// <see cref="TransactionCheckContext.Stage"/> tells apart. Property reads on the calling thread return the
    /// transaction's values for the passed changes and the landed values otherwise; reads and writes from other
    /// threads that carry the transaction throw <see cref="InvalidOperationException"/>. Handlers must not write
    /// properties and must not assume a single call per commit.
    /// </summary>
    /// <remarks>
    /// Throwing rejects the changes; what a rejection does depends on the stage, see <see cref="TransactionCheckStage"/>.
    /// </remarks>
    /// <param name="context">The checked changes, the transaction and the check stage.</param>
    void OnCommitting(in TransactionCheckContext context);
}
