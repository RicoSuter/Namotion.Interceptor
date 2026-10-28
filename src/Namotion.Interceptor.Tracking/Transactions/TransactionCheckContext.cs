using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// The changes a commit check passes to <see cref="ITransactionHandler.OnCommitting"/>. Valid only during that call.
/// </summary>
public readonly ref struct TransactionCheckContext
{
    internal TransactionCheckContext(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes, TransactionCheckStage stage)
    {
        Transaction = transaction;
        Changes = changes;
        Stage = stage;
    }

    /// <summary>Gets the committing transaction.</summary>
    public SubjectTransaction Transaction { get; }

    /// <summary>
    /// Gets the checked changes in insertion order. In the <see cref="TransactionCheckStage.Commit"/> stage: the final
    /// set, each with its captured origin; in <see cref="TransactionCheckMode.Deferred"/> mode with
    /// <see cref="TransactionConflictBehavior.FailOnConflict"/> followed by an entry for each property written with its
    /// unchanged value, whose old and new value are equal, which conflict detection checks and the commit neither
    /// writes, applies nor publishes. In the <see cref="TransactionCheckStage.PreApply"/> stage: the changes that will
    /// be applied, each with the origin it is applied with: <see cref="ChangeOriginKind.Confirmed"/> with the
    /// accepting source for a change a source accepted, the captured origin otherwise; followed by the entries for
    /// properties written with their unchanged value, each with its current landed value as old and new value and its
    /// captured origin, normally <see cref="ChangeOriginKind.Local"/>, also when that value came from an inbound
    /// update.
    /// </summary>
    public ReadOnlySpan<SubjectPropertyChange> Changes { get; }

    /// <summary>Gets the check that calls the handler.</summary>
    public TransactionCheckStage Stage { get; }
}
