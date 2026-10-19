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
}
