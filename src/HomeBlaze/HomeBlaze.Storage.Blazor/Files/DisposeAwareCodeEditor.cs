using BlazorMonaco.Editor;
using Microsoft.JSInterop;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>A Monaco editor whose setup stops once it is disposed.</summary>
public sealed class DisposeAwareCodeEditor : StandaloneCodeEditor
{
    private volatile bool _isDisposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The renderer still runs the first call for an editor that a later render removed, such as the editor of a page
        // that is left right after it opened. BlazorMonaco then skips creating the editor and calls into it anyway, and the
        // failed call ends the circuit.
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await base.OnAfterRenderAsync(firstRender);
        }
        catch (JSException) when (_isDisposed)
        {
            // Removed while the setup ran.
        }
    }

    public override void Dispose()
    {
        _isDisposed = true;
        base.Dispose();
    }
}
