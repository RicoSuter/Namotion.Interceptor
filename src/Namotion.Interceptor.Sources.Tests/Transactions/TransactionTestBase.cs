using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public abstract class TransactionTestBase
{
    protected static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking()
            .WithSourceTransactions();
    }

    protected static Mock<ISubjectSource> CreateSucceedingSource()
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return mock;
    }

    protected static Mock<ISubjectSource> CreateFailingSource(string message = "Source write failed")
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(message));
        return mock;
    }

    protected static Mock<ISubjectSource> CreateSourceWithBatchSize(int batchSize)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(batchSize);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return mock;
    }
}
