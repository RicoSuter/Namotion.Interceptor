using Microsoft.JSInterop;

namespace HomeBlaze.Host.TimeZones;

/// <summary>
/// Thin wrapper over the timezone JS module: detects the browser zone.
/// </summary>
public sealed class TimeZoneInterop : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;

    public TimeZoneInterop(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    private async ValueTask<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/HomeBlaze.Host/js/timezone.js");

    /// <summary>Returns the browser's IANA zone id, or null if it cannot be read.</summary>
    public async ValueTask<string?> GetBrowserTimeZoneAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string?>("getBrowserTimeZone");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
