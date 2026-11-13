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
    public async Task WhenAnInputContinuesAChainBeyondTheLimit_ThenItIsDroppedAndCounted()
    {
        // Arrange
        var queue = new AutomationInputQueue();

        // Act
        var isEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth + 1));
        var resynchronize = Assert.IsType<ResynchronizeInput>(await queue.ReadAsync(CancellationToken.None));
        var faults = queue.TakeDroppedInputs(resynchronize).Faults;
        var faultsAfterTaking = queue.TakeDroppedInputs(resynchronize).Faults;

        // Assert
        Assert.False(isEnqueued);
        Assert.Equal(new QueueFaults(0, 1), faults);
        Assert.Equal(new QueueFaults(0, 0), faultsAfterTaking);
    }

    [Fact]
    public async Task WhenTheQueueIsFull_ThenInputsAreDroppedAndCounted()
    {
        // Arrange
        var queue = new AutomationInputQueue();
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            Assert.True(queue.TryEnqueue(TickInput.Instance));
        }

        // Act
        var isEnqueued = queue.TryEnqueue(TickInput.Instance);
        var (accepted, resynchronize) = await ReadUntilResynchronizationAsync(queue);

        // Assert
        Assert.False(isEnqueued);
        Assert.Equal(AutomationInputQueue.Capacity, accepted.Count);
        Assert.Equal(new QueueFaults(1, 0), queue.TakeDroppedInputs(resynchronize).Faults);
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
    public async Task WhenAnInputIsAtTheLimit_ThenItIsAcceptedAndTheNextDepthIsRejected()
    {
        // Arrange
        var queue = new AutomationInputQueue();

        // Act
        var isAtLimitEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth));
        var isBeyondLimitEnqueued = queue.TryEnqueue(new GraphChangedInput(AutomationInputQueue.MaxChainDepth + 1));
        var (accepted, resynchronize) = await ReadUntilResynchronizationAsync(queue);

        // Assert
        Assert.True(isAtLimitEnqueued);
        Assert.False(isBeyondLimitEnqueued);
        Assert.Equal(AutomationInputQueue.MaxChainDepth, Assert.Single(accepted).ChainDepth);
        Assert.Equal(new QueueFaults(0, 1), queue.TakeDroppedInputs(resynchronize).Faults);
    }

    [Fact]
    public async Task WhenAnInputIsDropped_ThenAResynchronizationWithItIsQueuedBehindTheAcceptedInputs()
    {
        // Arrange
        var queue = new AutomationInputQueue();
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            queue.TryEnqueue(TickInput.Instance);
        }

        // Act
        queue.TryEnqueue(new GraphChangedInput(0));
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            await queue.ReadAsync(CancellationToken.None);
        }

        var last = await queue.ReadAsync(CancellationToken.None);

        // Assert
        var dropped = queue.TakeDroppedInputs(Assert.IsType<ResynchronizeInput>(last));
        Assert.True(dropped.HasGraphChange);
        Assert.Equal(new QueueFaults(1, 0), dropped.Faults);
        Assert.False(queue.HasWork);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(AutomationInputQueue.MaxChainDepth + 1, false)]
    public async Task WhenAnActivationIsDropped_ThenTheTakenInputsHaveAnOverloadOnlyWhenTheQueueWasFull(int chainDepth, bool hasOverload)
    {
        // Arrange
        var queue = new AutomationInputQueue();
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            queue.TryEnqueue(TickInput.Instance);
        }

        // Act
        queue.TryEnqueue(new ActivationInput([], chainDepth));
        var (_, resynchronize) = await ReadUntilResynchronizationAsync(queue);
        var dropped = queue.TakeDroppedInputs(resynchronize);

        // Assert
        Assert.Single(dropped.Activations);
        Assert.Equal(hasOverload, dropped.HasOverload);
    }

    [Fact]
    public async Task WhenWritersStayFasterThanTheWorker_ThenAtMostOneResynchronizationIsQueuedAndItTakesEveryDrop()
    {
        // Arrange
        var queue = new AutomationInputQueue();
        for (var index = 0; index < AutomationInputQueue.Capacity; index++)
        {
            queue.TryEnqueue(TickInput.Instance);
        }

        // Act
        var resynchronizations = 0;
        var accepted = 0;
        var dropsTaken = 0;
        for (var read = 0; read < 10 * AutomationInputQueue.Capacity; read++)
        {
            accepted += queue.TryEnqueue(new GraphChangedInput(0)) ? 1 : 0;
            accepted += queue.TryEnqueue(new GraphChangedInput(0)) ? 1 : 0;
            if (await queue.ReadAsync(CancellationToken.None) is ResynchronizeInput resynchronize)
            {
                resynchronizations++;
                dropsTaken += queue.TakeDroppedInputs(resynchronize).Faults.Overloaded;
            }
        }

        while (queue.HasWork)
        {
            if (await queue.ReadAsync(CancellationToken.None) is ResynchronizeInput resynchronize)
            {
                dropsTaken += queue.TakeDroppedInputs(resynchronize).Faults.Overloaded;
            }
        }

        // Assert
        Assert.InRange(resynchronizations, 1, 10);
        Assert.Equal(2 * 10 * AutomationInputQueue.Capacity, accepted + dropsTaken);
    }

    private static async Task<(List<AutomationInput> Accepted, ResynchronizeInput Resynchronize)> ReadUntilResynchronizationAsync(AutomationInputQueue queue)
    {
        var accepted = new List<AutomationInput>();
        while (true)
        {
            var input = await queue.ReadAsync(CancellationToken.None);
            if (input is ResynchronizeInput resynchronize)
            {
                return (accepted, resynchronize);
            }

            accepted.Add(input);
        }
    }
}
