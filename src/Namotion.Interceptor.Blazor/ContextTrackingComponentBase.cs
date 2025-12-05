using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Blazor;

/// <summary>
/// Base class for Blazor components that automatically subscribe to
/// property changes from an interceptor context.
/// Subclasses must implement <see cref="TrackingContext"/> to provide the context.
/// </summary>
public abstract class ContextTrackingComponentBase : ComponentBase, IDisposable
{
    private IDisposable? _subscription;

    /// <summary>
    /// The context to track for property changes.
    /// Implement this property to provide the context from your specific source.
    /// </summary>
    protected abstract IInterceptorSubjectContext? TrackingContext { get; }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        SubscribeToChanges();
    }

    /// <summary>
    /// Subscribes to property changes from the tracking context.
    /// Called automatically in OnInitialized.
    /// Can be called again if the context changes.
    /// </summary>
    protected void SubscribeToChanges()
    {
        _subscription?.Dispose();
        _subscription = TrackingContext?
            .GetPropertyChangeObservable()
            .Subscribe(_ => InvokeAsync(StateHasChanged));
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
    }
}
