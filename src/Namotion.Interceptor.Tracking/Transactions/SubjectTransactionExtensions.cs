using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for transaction support on IInterceptorSubjectContext.
/// </summary>
public static class SubjectTransactionExtensions
{
    /// <summary>
    /// Default commit timeout of 30 seconds. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable timeout.
    /// </summary>
    public static readonly TimeSpan DefaultCommitTimeout = TimeSpan.FromSeconds(30);

    // The largest delay a CancellationTokenSource accepts.
    private static readonly TimeSpan MaxCommitTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Begins a new transaction bound to this context.
    /// </summary>
    /// <remarks>
    /// Begin, use, commit, and dispose a transaction within the same async flow; committing from a
    /// different flow throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <param name="context">The context to bind the transaction to.</param>
    /// <param name="failureHandling">The failure handling mode controlling what happens when writes fail.</param>
    /// <param name="locking">The locking mode. Exclusive (default) acquires lock at begin; Optimistic acquires only during commit.</param>
    /// <param name="requirement">The transaction requirement for validation. Defaults to <see cref="TransactionRequirement.None"/>.</param>
    /// <param name="conflictBehavior">The conflict detection behavior. Defaults to <see cref="TransactionConflictBehavior.FailOnConflict"/>.</param>
    /// <param name="checkMode">When transaction handlers validate the written values. Defaults to <see cref="TransactionCheckMode.Immediate"/>.</param>
    /// <param name="commitTimeout">
    /// The commit timeout, 30 seconds by default, or <see cref="Timeout.InfiniteTimeSpan"/> for none. Once a commit
    /// runs, only the timeout can cancel it.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the exclusive lock when the transaction begins.</param>
    /// <returns>A new SubjectTransaction instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="commitTimeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>, or
    /// greater than <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days).
    /// </exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The transaction policies are independent optional parameters with defaults, so callers name only the ones they change.")]
    public static TransactionAwaitable BeginTransactionAsync(
        this IInterceptorSubjectContext context,
        TransactionFailureHandling failureHandling,
        TransactionLocking locking = TransactionLocking.Exclusive,
        TransactionRequirement requirement = TransactionRequirement.None,
        TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
        TransactionCheckMode checkMode = TransactionCheckMode.Immediate,
        TimeSpan? commitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = commitTimeout ?? DefaultCommitTimeout;
        if (effectiveTimeout != Timeout.InfiniteTimeSpan && (effectiveTimeout < TimeSpan.Zero || effectiveTimeout > MaxCommitTimeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commitTimeout), effectiveTimeout,
                "The commit timeout must be Timeout.InfiniteTimeSpan or between zero and uint.MaxValue - 1 milliseconds.");
        }

        var task = SubjectTransaction.BeginTransactionAsync(context, failureHandling, locking, requirement, conflictBehavior, checkMode, effectiveTimeout, cancellationToken);
        return new TransactionAwaitable(task);
    }
}

/// <summary>
/// Awaitable that makes the begun transaction current in the caller's execution context.
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
/// Awaiter that makes the begun transaction current in the caller's execution context in GetResult.
/// </summary>
public readonly struct TransactionAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<SubjectTransaction> _awaiter;

    internal TransactionAwaiter(ValueTaskAwaiter<SubjectTransaction> awaiter)
    {
        _awaiter = awaiter;
    }

    public bool IsCompleted => _awaiter.IsCompleted;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S5034", Justification = "The awaiter protocol consumes the completed operation here and sets the transaction in the caller's execution context; an async wrapper would isolate that AsyncLocal assignment.")]
    public SubjectTransaction GetResult()
    {
        SubjectTransaction? transaction = null;
        try
        {
            transaction = _awaiter.GetResult();
            // GetResult runs in the caller's execution context, so the AsyncLocal value flows to the caller.
            SubjectTransaction.SetCurrent(transaction);
            return transaction;
        }
        catch
        {
            // Prevents leaking the active transaction count and the exclusive lock.
            transaction?.Dispose();
            throw;
        }
    }

    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}
