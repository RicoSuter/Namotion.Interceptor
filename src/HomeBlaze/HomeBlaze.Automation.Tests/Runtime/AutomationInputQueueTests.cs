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
}
