namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Calls a component back once per second on its renderer, so that a live preview reads its values outside of a render:
/// a value that changes often then neither renders the component on every change nor makes a render evaluate it.
/// </summary>
public sealed class PreviewTicker : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ITimer _timer;
    private bool _isDisposed;

    /// <param name="timeProvider">The time provider that creates the timer.</param>
    /// <param name="invokeAsync">The <c>InvokeAsync</c> of the component, which runs the tick on its renderer.</param>
    /// <param name="tick">The callback, which is not called once the ticker is disposed.</param>
    public PreviewTicker(TimeProvider timeProvider, Func<Func<Task>, Task> invokeAsync, Func<Task> tick)
    {
        Func<Task> guardedTick = () => _isDisposed ? Task.CompletedTask : tick();

        // Without the execution context of the render that creates it, a tick records no reads into a read tracking
        // scope of that render.
        using (ExecutionContext.SuppressFlow())
        {
            _timer = timeProvider.CreateTimer(_ => _ = invokeAsync(guardedTick), null, Interval, Interval);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _timer.Dispose();
    }
}
