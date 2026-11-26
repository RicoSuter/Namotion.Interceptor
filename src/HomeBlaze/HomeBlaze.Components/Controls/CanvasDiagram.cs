using Excubo.Blazor.Diagrams;
using Microsoft.JSInterop;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// The diagram of a canvas layout, which can be removed, by a switch of tabs or a navigation, while it still measures its
/// element without ending the circuit.
/// </summary>
public sealed class CanvasDiagram : Diagram, IDisposable
{
    // Set and read on the dispatcher of the renderer only.
    private bool _isDisposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await base.OnAfterRenderAsync(firstRender);
        }
        catch (JSException) when (_isDisposed)
        {
            // The diagram measures its element over several round trips after its first renders (checked for
            // Excubo.Blazor.Diagrams 4.1.136), and a measurement after a render removed the element fails in the browser.
            // Only a failure of a disposed diagram is ignored, so that any other one still surfaces.
        }
    }

    // Diagram.Dispose is not virtual, so the interface is implemented again, which the renderer calls.
    void IDisposable.Dispose()
    {
        _isDisposed = true;
        base.Dispose();
    }
}
