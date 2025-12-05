using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public class SubjectTransactionAsyncTests
{
    [Fact]
    public async Task BeginTransactionAsync_ReturnsTransaction()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx = await context.BeginTransactionAsync();

        Assert.NotNull(tx);
        Assert.Same(context, tx.Context);
    }

    [Fact]
    public async Task BeginTransactionAsync_SerializesTransactionsPerContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx1 = await context.BeginTransactionAsync();

        var tx2Task = context.BeginTransactionAsync();

        // tx2 should be waiting
        await Task.Delay(50);
        Assert.False(tx2Task.IsCompleted);

        // Dispose tx1 to release lock
        await tx1.DisposeAsync();

        // Now tx2 should complete
        await using var tx2 = await tx2Task;
        Assert.NotNull(tx2);
    }

    // Note: WriteProperty_ToDifferentContext_ThrowsInvalidOperationException test
    // is part of Task 7 (Update SubjectTransactionInterceptor for Context Validation)
    // and will be added in that task.
}
