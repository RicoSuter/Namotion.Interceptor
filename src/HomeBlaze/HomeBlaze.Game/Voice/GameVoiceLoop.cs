namespace HomeBlaze.Game.Voice;

/// <summary>
/// Runs the voice tick every <see cref="GameVoiceSettings.FrameDuration"/> while active and pauses while inactive. One
/// thread switches it; any thread may read whether it pauses.
/// </summary>
internal sealed class GameVoiceLoop(GameVoiceMixer mixer, TimeProvider timeProvider, Action tick)
{
    private readonly Lock _lock = new();
    private volatile bool _isActive;
    private volatile bool _isPaused;

    // Guarded by _lock: the task the paused loop waits for, completed when the loop becomes active.
    private TaskCompletionSource? _activated;

    /// <summary>
    /// Whether the running loop waits to become active.
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Lets the loop tick or pauses it after its current tick; only one thread may call it.
    /// </summary>
    public void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        TaskCompletionSource? activated;
        lock (_lock)
        {
            _isActive = isActive;
            activated = isActive ? _activated : null;
            if (isActive)
            {
                _activated = null;
            }
        }

        activated?.TrySetResult();
    }

    /// <summary>
    /// Runs the tick while active and waits while inactive, until canceled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                _isPaused = !_isActive;
                await WaitUntilActiveAsync(cancellationToken);
                _isPaused = false;

                // The last tick before the pause may have mixed frames, which the next talker must not glide from.
                mixer.SkipTick();
                await TickWhileActiveAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
        finally
        {
            _isPaused = false;
        }
    }

    private Task WaitUntilActiveAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_isActive)
            {
                return Task.CompletedTask;
            }

            _activated ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _activated.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task TickWhileActiveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GameVoiceSettings.FrameDuration, timeProvider);
        while (_isActive && await timer.WaitForNextTickAsync(cancellationToken))
        {
            tick();
        }
    }
}
