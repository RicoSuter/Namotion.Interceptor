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

    // One frame duration of steady speech: a frame arrives and the mixer takes one.
    private void Step(TalkerQueue queue, short sample)
    {
        _timeProvider.Advance(GameVoiceSettings.FrameDuration);
        queue.Enqueue(VoiceFrames.Create(sample));
        Assert.True(queue.TryDequeue(_frame));
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
        Assert.Equal(1, queue.Underruns);
        Assert.Equal(GameVoiceSettings.ServerQueueStart + GameVoiceSettings.BufferGrowth, queue.StartFrames);
        Assert.Null(beforeStart);
        Assert.Equal((short?)2, afterStart);
    }

    [Fact]
    public void WhenFramesArriveAtTheMaximum_ThenTheOldestIsDropped()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);

        // Act
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 1; sample++)
        {
            queue.Enqueue(VoiceFrames.Create(sample));
        }

        // Assert
        Assert.Equal(GameVoiceSettings.ServerQueueMaximum, queue.Count);
        Assert.Equal(1, queue.Trims);
        Assert.Equal((short?)2, Dequeue(queue));
    }

    [Fact]
    public void WhenABurstFollowsAGap_ThenTheWindowEndKeepsEveryFrame()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        const int burst = GameVoiceSettings.ServerQueueMaximum - 1;
        queue.Enqueue(VoiceFrames.Create(1));
        Dequeue(queue);
        _timeProvider.Advance(GameVoiceSettings.TrimWindow / 2);

        // Act
        for (short sample = 2; sample <= burst + 1; sample++)
        {
            queue.Enqueue(VoiceFrames.Create(sample));
        }

        _timeProvider.Advance(GameVoiceSettings.TrimWindow / 2);
        var samples = Enumerable.Range(0, burst).Select(_ => Dequeue(queue)).ToArray();

        // Assert
        Assert.Equal(0, queue.Trims);
        Assert.Equal(Enumerable.Range(2, burst).Select(sample => (short?)sample), samples);
    }

    [Fact]
    public void WhenTheLevelStaysAboveTheHeadroomForAWindow_ThenTheWindowEndDropsDownToTheStartSize()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        const int level = GameVoiceSettings.ServerQueueStart + GameVoiceSettings.TrimHeadroom + 1;
        for (short sample = 1; sample < level; sample++)
        {
            queue.Enqueue(VoiceFrames.Create(sample));
        }

        // Act
        // Every step adds and takes one frame, so each dequeue finds the same level until the window ends.
        var nextSample = (short)level;
        var start = _timeProvider.GetUtcNow();
        while (_timeProvider.GetUtcNow() - start < GameVoiceSettings.TrimWindow)
        {
            Step(queue, nextSample++);
        }

        // Assert
        Assert.Equal(1, queue.Trims);
        Assert.Equal(GameVoiceSettings.ServerQueueStart - 1, queue.Count);
    }

    [Fact]
    public void WhenTheStartSizeShrinksBelowTheStandingLevel_ThenAWindowEndDropsTheExcess()
    {
        // Arrange
        using var queue = new TalkerQueue(_timeProvider);
        const int grownStart = GameVoiceSettings.ServerQueueStart + GameVoiceSettings.TrimHeadroom + 2;
        GrowTo(queue, grownStart);
        FillToStart(queue);

        // Act
        var nextSample = (short)0;
        var start = _timeProvider.GetUtcNow();
        while (queue.StartFrames > GameVoiceSettings.ServerQueueStart
            || _timeProvider.GetUtcNow() - start < GameVoiceSettings.BufferShrinkDelay * grownStart)
        {
            Step(queue, nextSample++);
        }

        // Assert
        // Each step finds Count + 1 frames at its dequeue.
        Assert.InRange(queue.Trims, 1, grownStart);
        Assert.InRange(queue.Count + 1, GameVoiceSettings.ServerQueueStart, GameVoiceSettings.ServerQueueStart + GameVoiceSettings.TrimHeadroom);
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
        Assert.Equal(2, queue.Trims);
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
