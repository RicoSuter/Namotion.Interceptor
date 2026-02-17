using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

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
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException(message))));
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

    /// <summary>
    /// Creates a source that delays writes by the specified duration.
    /// Useful for testing timeout and cancellation scenarios.
    /// </summary>
    protected static Mock<ISubjectSource> CreateDelayedSource(TimeSpan delay)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken cancellationToken) =>
            {
                await Task.Delay(delay, cancellationToken);
                return WriteResult.Success;
            });
        return mock;
    }

    /// <summary>
    /// Creates a source that fails only for changes matching the predicate.
    /// Useful for testing partial failure scenarios.
    /// </summary>
    protected static Mock<ISubjectSource> CreatePartialFailureSource(Func<SubjectPropertyChange, bool> shouldFail)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                var failed = changes.ToArray().Where(shouldFail).ToArray();
                if (failed.Length == 0)
                {
                    return new ValueTask<WriteResult>(WriteResult.Success);
                }
                return new ValueTask<WriteResult>(
                    WriteResult.PartialFailure(failed.AsMemory(), new InvalidOperationException("Partial failure")));
            });
        return mock;
    }

    /// <summary>
    /// Asserts that the transaction completed successfully with no pending changes.
    /// </summary>
    protected static void AssertTransactionSucceeded(SubjectTransaction transaction)
    {
        Assert.Empty(transaction.GetPendingChanges());
    }

    /// <summary>
    /// Asserts that no transaction is currently active in the execution context.
    /// </summary>
    protected static void AssertNoActiveTransaction()
    {
        Assert.Null(SubjectTransaction.Current);
    }
}
