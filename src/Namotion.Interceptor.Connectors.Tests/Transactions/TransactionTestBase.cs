using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

public abstract class TransactionTestBase
{
    protected static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
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
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));
        return mock;
    }

    protected static Mock<ISubjectSource> CreateFailingSource(string message = "Source write failed")
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Failure(new InvalidOperationException(message))));
        return mock;
    }

    protected static Mock<ISubjectSource> CreateSourceWithBatchSize(int batchSize)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(batchSize);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));
        return mock;
    }
}
