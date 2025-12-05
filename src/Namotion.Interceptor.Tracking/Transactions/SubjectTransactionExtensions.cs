namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for transaction support on IInterceptorSubjectContext.
/// </summary>
public static class SubjectTransactionExtensions
{
    /// <summary>
    /// Begins a new transaction bound to this context.
    /// Waits if another transaction is active on this context.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="mode">The transaction mode controlling failure handling behavior. Defaults to <see cref="TransactionMode.Rollback"/>.</param>
    /// <param name="requirement">The transaction requirement for validation. Defaults to <see cref="TransactionRequirement.None"/>.</param>
    /// <param name="conflictBehavior">The conflict detection behavior. Defaults to <see cref="TransactionConflictBehavior.FailOnConflict"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    public static ValueTask<SubjectTransaction> BeginTransactionAsync(
        this IInterceptorSubjectContext context,
        TransactionMode mode = TransactionMode.Rollback,
        TransactionRequirement requirement = TransactionRequirement.None,
        TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
        CancellationToken cancellationToken = default)
    {
        return SubjectTransaction.BeginAsync(context, mode, requirement, conflictBehavior, cancellationToken);
    }
}
