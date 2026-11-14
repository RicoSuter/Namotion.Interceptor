namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Defers hosted-service starts captured in this deferral until it and its enclosing deferrals are disposed.
/// </summary>
/// <remarks>
/// Disposal releases captured starts even when configuration throws.
/// Do not await a captured service's startup, or its detach, before disposing the deferral: both wait
/// for that startup.
/// Deferrals must be disposed in reverse creation order in the creating execution flow.
/// </remarks>
public sealed class HostedServiceStartDeferral : IDisposable
{
    private readonly AsyncLocal<HostedServiceStartDeferral?> _current;
    private readonly HostedServiceStartDeferral? _parent;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HostedServiceStartDeferral(AsyncLocal<HostedServiceStartDeferral?> current)
    {
        _current = current;
        _parent = current.Value;
        current.Value = this;
    }

    /// <summary>Releases the starts captured in this deferral, once its enclosing deferrals are released too.</summary>
    public void Dispose()
    {
        // Restore this flow's parent even if another flow already disposed the deferral.
        if (ReferenceEquals(_current.Value, this))
        {
            _current.Value = _parent;
        }

        _completion.TrySetResult();
    }

    internal bool IsReady => _completion.Task.IsCompleted && (_parent is null || _parent.IsReady);

    internal async Task WaitAsync()
    {
        for (var deferral = this; deferral is not null; deferral = deferral._parent)
        {
            await deferral._completion.Task.ConfigureAwait(false);
        }
    }
}
