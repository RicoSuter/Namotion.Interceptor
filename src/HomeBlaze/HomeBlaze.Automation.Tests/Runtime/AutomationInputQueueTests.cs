using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationInputQueueTests
{
    [Fact]
    public void WhenNoRunIsEntered_ThenCausedInputsStartAChain()
    {
        // Act
        var depth = AutomationInputQueue.CausedChainDepth;

        // Assert
        Assert.Equal(0, depth);
    }

    [Fact]
    public async Task WhenARunIsEntered_ThenInputsCausedOnItsFlowAreOneDeeper()
    {
        // Arrange
        int insideRun;
        int afterAwait;

        // Act
        using (AutomationInputQueue.EnterRun(3))
        {
            insideRun = AutomationInputQueue.CausedChainDepth;
            await Task.Yield();
            afterAwait = AutomationInputQueue.CausedChainDepth;
        }

        // Assert
        Assert.Equal(4, insideRun);
        Assert.Equal(4, afterAwait);
        Assert.Equal(0, AutomationInputQueue.CausedChainDepth);
    }

    [Fact]
    public void WhenAnInputContinuesAChainBeyondTheLimit_ThenItIsDroppedAndCounted()
    {
        // Arrange
        var queue = new AutomationInputQueue();

        // Act
        var isEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth + 1));
        var faults = queue.TakeFaults();
        var faultsAfterTaking = queue.TakeFaults();

        // Assert
        Assert.False(isEnqueued);
        Assert.Equal(new QueueFaults(0, 1), faults);
        Assert.False(faultsAfterTaking.HasFaults);
    }

    [Fact]
    public void WhenTheQueueIsFull_ThenInputsAreDroppedAndCounted()
    {
        // Arrange
        var queue = new AutomationInputQueue();
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            Assert.True(queue.TryEnqueue(TickInput.Instance));
        }

        // Act
        var isEnqueued = queue.TryEnqueue(TickInput.Instance);

        // Assert
        Assert.False(isEnqueued);
        Assert.Equal(AutomationInputQueue.Capacity, queue.Count);
        Assert.Equal(new QueueFaults(1, 0), queue.TakeFaults());
    }

    [Fact]
    public async Task WhenInputsAreRead_ThenTheyArriveInOrder()
    {
        // Arrange
        var queue = new AutomationInputQueue();
        queue.TryEnqueue(new GraphChangedInput(1));
        queue.TryEnqueue(new GraphChangedInput(2));

        // Act
        var first = await queue.ReadAsync(CancellationToken.None);
        var second = await queue.ReadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, first.ChainDepth);
        Assert.Equal(2, second.ChainDepth);
    }

    [Fact]
    public async Task WhenWorkCapturedTheContextInsideARun_ThenItReadsNoChainAfterTheRunEnded()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> captured;
        var scope = AutomationInputQueue.EnterRun(AutomationInputQueue.MaxChainDepth);
        try
        {
            captured = Task.Run(async () =>
            {
                await gate.Task;
                return AutomationInputQueue.CausedChainDepth;
            });
        }
        finally
        {
            scope.Dispose();
        }

        // Act
        gate.SetResult();
        var depth = await captured;

        // Assert
        Assert.Equal(0, depth);
    }

    [Fact]
    public void WhenScopesAreNested_ThenDisposingTheInnerOneRestoresTheOuterDepth()
    {
        // Arrange
        var outer = AutomationInputQueue.EnterRun(1);
        var inner = AutomationInputQueue.EnterRun(5);
        var innerDepth = AutomationInputQueue.CausedChainDepth;

        // Act
        inner.Dispose();
        var restoredDepth = AutomationInputQueue.CausedChainDepth;
        outer.Dispose();

        // Assert
        Assert.Equal(6, innerDepth);
        Assert.Equal(2, restoredDepth);
        Assert.Equal(0, AutomationInputQueue.CausedChainDepth);
    }

    [Fact]
    public void WhenAnInputIsAtTheLimit_ThenItIsAcceptedAndTheNextDepthIsRejected()
    {
        // Arrange
        var queue = new AutomationInputQueue();

        // Act
        var isAtLimitEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth));
        var isBeyondLimitEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth + 1));

        // Assert
        Assert.True(isAtLimitEnqueued);
        Assert.False(isBeyondLimitEnqueued);
        Assert.Equal(1, queue.Count);
        Assert.Equal(new QueueFaults(0, 1), queue.TakeFaults());
    }
}
