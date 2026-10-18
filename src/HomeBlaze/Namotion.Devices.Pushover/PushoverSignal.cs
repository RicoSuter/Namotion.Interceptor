namespace Namotion.Devices.Pushover;

/// <summary>
/// Wakes the service loop: any number of <see cref="Set"/> calls while the loop runs wake one wait.
/// </summary>
internal sealed class PushoverSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        // Avoids a first-chance exception on every redundant signal. The catch below is a backstop for the race
        // between this check and the release.
        if (_semaphore.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending; the loop handles every request when it wakes.
        }
    }

    /// <summary>
    /// Waits for a signal or <paramref name="timeout"/>; returns without throwing when <paramref name="cancellationToken"/>
    /// is canceled.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _semaphore.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The loop condition ends the loop.
        }
    }
}
