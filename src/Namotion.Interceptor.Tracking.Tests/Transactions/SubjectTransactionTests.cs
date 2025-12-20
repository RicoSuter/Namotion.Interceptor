using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionTests
{
    [Fact]
    public async Task BeginTransactionAsync_ThrowsWhenTransactionsNotEnabled()
    {
        // Arrange - context WITHOUT WithTransactions()
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeObservable();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        });

        Assert.Contains("WithTransactions()", exception.Message);
    }
}
