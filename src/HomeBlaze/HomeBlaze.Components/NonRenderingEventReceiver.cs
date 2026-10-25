using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Components;

/// <summary>
/// Handles an event in place of the component that declares the handler, which is then not rendered afterwards.
/// </summary>
internal sealed class NonRenderingEventReceiver(Func<Task> handler) : IHandleEvent
{
    public Task HandleAsync() => handler();

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? argument) => callback.InvokeAsync(argument);
}
