using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Voice;

public class TalkerQueueTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly short[] _frame = new short[GameVoiceSettings.SamplesPerFrame];

    private short? Dequeue(TalkerQueue queue) => queue.TryDequeue(_frame) ? _frame[0] : null;

    private static void FillToStart(TalkerQueue queue)
    {
        for (var index = 0; index < queue.StartFrames; index++)
        {
            queue.Enqueue(VoiceFrames.Create(0));
        }
    }

    private void DrainUntilDry(TalkerQueue queue)
    {
        var isPlaying = true;
        while (isPlaying)
        {
            isPlaying = queue.TryDequeue(_frame);
        }
    }

    // Lets the queue run dry while playing until its start size is the given one.
    private void GrowTo(TalkerQueue queue, int startFrames)
    {
        while (queue.StartFrames < startFrames)
        {
            FillToStart(queue);
            DrainUntilDry(queue);
        }
    }

    [Fact]
    public void WhenTheQueueIsEmpty_ThenItDoesNotPlay()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);

        // Act
        var sample = Dequeue(queue);

        // Assert
        Assert.Null(sample);
        Assert.False(queue.IsPlaying);
        Assert.Equal(GameVoiceSettings.ServerQueueStart, queue.StartFrames);
    }

    [Fact]
    public void WhenTheQueueHoldsItsStartSize_ThenItPlaysTheFramesInOrder()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        queue.Enqueue(VoiceFrames.Create(1));
        queue.Enqueue(VoiceFrames.Create(2));

        // Act
        var samples = new[] { Dequeue(queue), Dequeue(queue) };

        // Assert
        Assert.Equal(new short?[] { 1, 2 }, samples);
    }

    [Fact]
    public void WhenItRunsDryWhilePlaying_ThenItGrowsAndWaitsForTheLargerStartSize()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        queue.Enqueue(VoiceFrames.Create(1));
        Dequeue(queue);

        // Act
        var dry = Dequeue(queue);
        queue.Enqueue(VoiceFrames.Create(2));
        var beforeStart = Dequeue(queue);
        queue.Enqueue(VoiceFrames.Create(3));
        var afterStart = Dequeue(queue);

        // Assert
        Assert.Null(dry);
        Assert.Equal(GameVoiceSettings.ServerQueueStart + GameVoiceSettings.BufferGrowth, queue.StartFrames);
        Assert.Null(beforeStart);
        Assert.Equal((short?)2, afterStart);
    }

    [Fact]
    public void WhenMoreFramesArriveThanOneGrowthStepAboveTheStart_ThenTheOldestAreDropped()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);

        // Act
        queue.Enqueue(VoiceFrames.Create(1));
        queue.Enqueue(VoiceFrames.Create(2));
        queue.Enqueue(VoiceFrames.Create(3));

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueStart + GameVoiceSettings.BufferGrowth, queue.Count);
        Assert.Equal(new short?[] { 2, 3 }, new[] { Dequeue(queue), Dequeue(queue) });
    }

    [Fact]
    public void WhenTheStartSizeReachedTheMaximum_ThenTheQueueKeepsAtMostTheMaximum()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        GrowTo(queue, GameVoiceSettings.ServerQueueMaximum);

        // Act
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 2; sample++)
        {
            queue.Enqueue(VoiceFrames.Create(sample));
        }

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueMaximum, queue.StartFrames);
        Assert.Equal(GameVoiceSettings.ServerQueueMaximum, queue.Count);
        Assert.Equal((short?)3, Dequeue(queue));
    }

    [Fact]
    public void WhenItPlaysWithoutRunningDryForTheShrinkDelay_ThenTheStartSizeShrinks()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        GrowTo(queue, 2);
        FillToStart(queue);

        // Act
        _timeProvider.Advance(GameVoiceSettings.BufferShrinkDelay);
        Dequeue(queue);

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueStart, queue.StartFrames);
    }

    [Fact]
    public void WhenItPausedForTheShrinkDelayWithoutPlaying_ThenTheStartSizeStays()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        GrowTo(queue, 2);
        _timeProvider.Advance(GameVoiceSettings.BufferShrinkDelay);
        FillToStart(queue);

        // Act
        Dequeue(queue);

        // Assert
        Assert.Equal(2, queue.StartFrames);
    }

    [Fact]
    public void WhenItRanDryLessThanTheShrinkDelayAgo_ThenTheStartSizeStays()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        GrowTo(queue, 2);
        FillToStart(queue);

        // Act
        _timeProvider.Advance(GameVoiceSettings.BufferShrinkDelay - TimeSpan.FromMilliseconds(1));
        Dequeue(queue);

        // Assert
        Assert.Equal(2, queue.StartFrames);
    }
}
