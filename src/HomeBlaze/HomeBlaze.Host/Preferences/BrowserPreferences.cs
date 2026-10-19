using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HomeBlaze.Host.Preferences;

/// <summary>
/// Preferences that a browser remembers, one cookie per key. The prerendered page and the interactive circuit read the same values.
/// </summary>
/// <remarks>Resolve it during the first render of a circuit: the values handed over from the prerender are available only until that render completes.</remarks>
public sealed class BrowserPreferences : IAsyncDisposable
{
    private const string CookiePrefix = "homeblaze.";
    private const string StateKey = "browser-preferences";
    private const string ModulePath = "./_content/HomeBlaze.Host/js/cookies.js";

    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<BrowserPreferences> _logger;
    private readonly Dictionary<string, string> _values;
    private readonly PersistingComponentStateSubscription _persistingSubscription;
    private Task<IJSObjectReference>? _moduleTask;

    public BrowserPreferences(
        PersistentComponentState applicationState,
        IHttpContextAccessor httpContextAccessor,
        IJSRuntime jsRuntime,
        ILogger<BrowserPreferences> logger)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;

        // Only the prerender has the request with the cookies, so it hands them over to the circuit.
        _values = applicationState.TryTakeFromJson<Dictionary<string, string>>(StateKey, out var persisted) && persisted is not null
            ? persisted
            : ReadCookies(httpContextAccessor.HttpContext?.Request.Cookies);

        _persistingSubscription = applicationState.RegisterOnPersisting(() =>
        {
            applicationState.PersistAsJson(StateKey, _values);
            return Task.CompletedTask;
        }, RenderMode.InteractiveServer);
    }

    /// <summary>
    /// Gets the stored value of a preference, or null when the browser has none.
    /// </summary>
    public string? Get(string key) => _values.GetValueOrDefault(key);

    /// <summary>
    /// Stores a preference for this circuit and in the browser for a year. A value the browser cannot store still applies to this circuit.
    /// </summary>
    /// <remarks>Call it only once the circuit is interactive, such as from an event handler or <c>OnAfterRenderAsync</c>: during the prerender no JavaScript runs.</remarks>
    public async ValueTask SetAsync(string key, string value)
    {
        _values[key] = value;
        try
        {
            _moduleTask ??= _jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
            var module = await _moduleTask;
            await module.InvokeVoidAsync("setCookie", GetCookieName(key), value);
        }
        catch (JSException exception)
        {
            ForgetFailedImport();
            _logger.LogWarning(exception, "Could not store the browser preference {Key}.", key);
        }
        catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
        {
            // A timed-out call surfaces as OperationCanceledException. Neither is worth an error page: the value already applies to this circuit.
            ForgetFailedImport();
        }
    }

    // A failed import is retried by the next write rather than cached for the life of the circuit.
    private void ForgetFailedImport()
    {
        if (_moduleTask is { IsCompletedSuccessfully: false, IsCompleted: true })
        {
            _moduleTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _persistingSubscription.Dispose();
        if (_moduleTask is { IsCompletedSuccessfully: true })
        {
            try
            {
                await _moduleTask.Result.DisposeAsync();
            }
            catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
            {
                // The circuit is gone, and the module with it.
            }
        }
    }

    private static string GetCookieName(string key) => CookiePrefix + key;

    private static Dictionary<string, string> ReadCookies(IRequestCookieCollection? cookies)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cookies is not null)
        {
            foreach (var (name, value) in cookies)
            {
                if (name.StartsWith(CookiePrefix, StringComparison.Ordinal))
                {
                    values[name[CookiePrefix.Length..]] = value;
                }
            }
        }

        return values;
    }
}
