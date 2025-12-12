using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for transaction support on IInterceptorSubjectContext.
/// </summary>
public static class SubjectSerializedTransactionExtensions
{
    /// <summary>
    /// Begins a new serialized transaction bound to this context.
    /// Waits if another transaction is active on this context, ensuring only one
    /// transaction executes at a time per context.
    /// </summary>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="mode">The transaction mode controlling failure handling behavior. Defaults to <see cref="TransactionMode.Rollback"/>.</param>
    /// <param name="requirement">The transaction requirement for validation. Defaults to <see cref="TransactionRequirement.None"/>.</param>
    /// <param name="conflictBehavior">The conflict detection behavior. Defaults to <see cref="TransactionConflictBehavior.FailOnConflict"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    public static TransactionAwaitable BeginSerializedTransactionAsync(
        this IInterceptorSubjectContext context,
        TransactionMode mode = TransactionMode.Rollback,
        TransactionRequirement requirement = TransactionRequirement.None,
        TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
        CancellationToken cancellationToken = default)
    {
        var task = SubjectTransaction.BeginSerializedTransactionAsync(context, mode, requirement, conflictBehavior, cancellationToken);
        return new TransactionAwaitable(task);
    }
}

/// <summary>
/// Custom awaitable that sets AsyncLocal after the underlying task completes.
/// This ensures the transaction is visible in the caller's execution context.
/// </summary>
public readonly struct TransactionAwaitable
{
    private readonly ValueTask<SubjectTransaction> _task;

    internal TransactionAwaitable(ValueTask<SubjectTransaction> task)
    {
        _task = task;
    }

    /// <summary>
    /// Gets a value indicating whether the underlying task has completed.
    /// </summary>
    public bool IsCompleted => _task.IsCompleted;

    public TransactionAwaiter GetAwaiter() => new(_task.GetAwaiter());
}

/// <summary>
/// Custom awaiter that sets AsyncLocal in the caller's context after GetResult.
/// </summary>
public readonly struct TransactionAwaiter : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<SubjectTransaction> _awaiter;

    internal TransactionAwaiter(ValueTaskAwaiter<SubjectTransaction> awaiter)
    {
        _awaiter = awaiter;
    }

    public bool IsCompleted => _awaiter.IsCompleted;

    public SubjectTransaction GetResult()
    {
        var transaction = _awaiter.GetResult();
        // Set the AsyncLocal in the caller's execution context
        // This happens when the awaiter resumes the caller, in the caller's context
        SubjectTransaction.SetCurrent(transaction);
        return transaction;
    }

    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}
