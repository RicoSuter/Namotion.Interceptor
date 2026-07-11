using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

public abstract class TransactionTestBase
{
    /// <summary>
    /// Drains the subscription until the sentinel arrives (excluded from the result);
    /// throws TimeoutException after 10 seconds.
    /// </summary>
    protected static List<SubjectPropertyChange> DrainUntil(
        PropertyChangeQueueSubscription subscription, Func<SubjectPropertyChange, bool> isSentinel)
    {
        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (isSentinel(change))
            {
                return changes;
            }
            changes.Add(change);
        }
        throw new TimeoutException("Sentinel notification was not received within 10 seconds.");
    }

    /// <summary>
    /// Writes a sentinel change on a fresh subject and drains the subscription up to it, returning
    /// everything published before the sentinel.
    /// </summary>
    protected static List<SubjectPropertyChange> DrainWithSentinel(
        IInterceptorSubjectContext context, PropertyChangeQueueSubscription subscription)
    {
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        return DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));
    }

    protected static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking()
            .WithSourceTransactions();
    }

    protected static Mock<ISubjectSource> CreateSucceedingSource(Action? onWrite = null)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                onWrite?.Invoke();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });
        return mock;
    }

    /// <summary>
    /// Creates a source that fails wholesale: it reports an error without enumerating the failed changes.
    /// </summary>
    protected static Mock<ISubjectSource> CreateWholesaleFailingSource(string message = "Source write failed")
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    ReadOnlyMemory<SubjectPropertyChange>.Empty, new InvalidOperationException(message))));
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

}
