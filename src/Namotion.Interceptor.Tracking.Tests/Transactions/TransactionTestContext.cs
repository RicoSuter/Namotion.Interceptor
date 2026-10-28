using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Creates contexts with full property tracking and transactions for the transaction handler tests.
/// </summary>
internal static class TransactionTestContext
{
    public static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    /// <summary>
    /// Creates a context with a registered <see cref="RecordingHandler"/> and, when given, a registered writer.
    /// </summary>
    public static IInterceptorSubjectContext CreateContext(out RecordingHandler handler, ITransactionWriter? writer = null)
    {
        var context = CreateContext();
        handler = new RecordingHandler();
        context.AddService<ITransactionHandler>(handler);
        if (writer is not null)
        {
            context.AddService(writer);
        }

        return context;
    }

    /// <summary>
    /// Creates a subject context that reaches the transactions of <paramref name="transactionContext"/> through a
    /// fallback, with <paramref name="handler"/> registered on it when given.
    /// </summary>
    public static IInterceptorSubjectContext CreateSubjectContext(
        IInterceptorSubjectContext transactionContext, ITransactionHandler? handler = null)
    {
        var context = InterceptorSubjectContext.Create();
        if (handler is not null)
        {
            context.AddService(handler);
        }

        context.AddFallbackContext(transactionContext);
        return context;
    }

    /// <summary>
    /// Begins a BestEffort transaction without leaking it into the caller's flow, as an async method does not.
    /// </summary>
    public static async Task<SubjectTransaction> BeginInOwnFlowAsync(IInterceptorSubjectContext context)
    {
        return await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    }

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on this thread in a flow without the current transaction, like
    /// code that does not carry it, for example an inbound source update or a dispose from another flow.
    /// </summary>
    public static void RunOutsideTransactionFlow(Action action)
    {
        // Run restores the caller's flow afterwards, so clearing the transaction does not leak into it.
        ExecutionContext.Run(ExecutionContext.Capture()!, _ =>
        {
            SubjectTransaction.SetCurrent(null);
            action();
        }, null);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread pool thread in a flow without any async-local state, so also without
    /// the current transaction, and awaits it.
    /// </summary>
    public static async Task RunOnThreadPoolWithoutFlowAsync(Action action)
    {
        Task task;
        var flowControl = ExecutionContext.SuppressFlow();
        try
        {
            task = Task.Run(action);
        }
        finally
        {
            flowControl.Undo();
        }

        await task;
    }
}
