using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(ThreadPoolFloodCollection.Name)]
public class PropertyChangeQueueSubscriptionThreadPoolTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // How long the writer stays blocked waiting for the consumer. A wake-up queued behind the blocked
    // writer never runs while the global queue is flooded, so this bounds the failure, not the passing run.
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenAPoolThreadBlocksAfterWakingTheConsumerWhileTheGlobalQueueIsBusy_ThenTheConsumerStillResumes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var consumerResumed = new ManualResetEventSlim();
        var wait = subscription.WaitToDequeueAsync(CancellationToken.None).AsTask();
        Assert.False(wait.IsCompleted);
        _ = wait.ContinueWith(
            _ => consumerResumed.Set(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var resumedWhileWriterWasBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool resumed;
        using (ThreadPoolGlobalQueueFlood.Start())
        {
            // Act
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    person.FirstName = "John";
                    resumedWhileWriterWasBlocked.TrySetResult(consumerResumed.Wait(ResumeTimeout));
                },
                (object?)null,
                preferLocal: false);
            resumed = await resumedWhileWriterWasBlocked.Task.WaitAsync(TestTimeout);
        }

        // Assert
        Assert.True(resumed, "The consumer should resume while the pool thread that woke it is blocked.");
        Assert.True(await wait);
        Assert.True(subscription.TryDequeueImmediate(out var change));
        Assert.Equal("John", change.GetNewValue<string?>());
    }
}
