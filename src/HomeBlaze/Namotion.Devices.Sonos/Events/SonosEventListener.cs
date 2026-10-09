using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// Receives UPnP NOTIFY requests from Sonos speakers and manages the subscriptions that produce them.
/// </summary>
internal sealed class SonosEventListener : IAsyncDisposable
{
    private const string EventPathPrefix = "/event/";
    private static readonly TimeSpan DefaultSubscriptionLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsBySid = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private Task? _acceptLoop;
    private string? _callbackBaseUri;

    internal SonosEventListener(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    internal bool IsListening => _listener?.IsListening == true;

    internal IReadOnlyCollection<SonosEventSubscription> Subscriptions => _subscriptionsByKey.Values.ToArray();

    /// <summary>
    /// Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken or cannot be bound.
    /// </summary>
    internal void Start(string callbackHost, int port, string listenHost = "+")
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{listenHost}:{port}/");
        listener.Start();

        _listener = listener;
        _callbackBaseUri = $"http://{callbackHost}:{port}{EventPathPrefix}";
        _acceptLoop = AcceptLoopAsync(listener);
    }

    internal async Task<SonosEventSubscription> SubscribeAsync(
        string key, Uri eventUri, Action<string> handler, CancellationToken cancellationToken)
    {
        var subscription = new SonosEventSubscription(key, eventUri, handler);

        // Registered before the request: Sonos sends the initial full-state NOTIFY right after accepting, often
        // before its SUBSCRIBE response arrives, so that NOTIFY can only be matched by its callback path.
        _subscriptionsByKey[key] = subscription;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventUri);
            request.Headers.TryAddWithoutValidation("CALLBACK", $"<{_callbackBaseUri}{key}>");
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-1800");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var sid = GetHeader(response, "SID")
                ?? throw new InvalidOperationException($"The subscription to {eventUri} returned no SID.");
            subscription.RenewAt = DateTimeOffset.UtcNow + GetLifetime(response) / 2;
            _subscriptionsBySid[sid] = subscription;
            subscription.Sid = sid;
            return subscription;
        }
        catch
        {
            _subscriptionsByKey.TryRemove(new KeyValuePair<string, SonosEventSubscription>(key, subscription));
            throw;
        }
    }

    /// <summary>
    /// Renews a subscription. A rejected renewal forgets the subscription so the caller subscribes again.
    /// </summary>
    internal async Task<bool> RenewAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
        request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-1800");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Forget(subscription);
            return false;
        }

        subscription.RenewAt = DateTimeOffset.UtcNow + GetLifetime(response) / 2;
        return true;
    }

    internal async Task UnsubscribeAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        Forget(subscription);
        if (subscription.Sid is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Best effort: an unreachable speaker drops the subscription itself once it expires.
    /// </summary>
    internal async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptionsByKey.Values)
        {
            try
            {
                await UnsubscribeAsync(subscription, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                _logger.LogDebug(exception, "Unsubscribing Sonos events {Key} failed.", subscription.Key);
            }
        }
    }

    private void Forget(SonosEventSubscription subscription)
    {
        _subscriptionsByKey.TryRemove(new KeyValuePair<string, SonosEventSubscription>(subscription.Key, subscription));
        if (subscription.Sid is { } sid)
        {
            _subscriptionsBySid.TryRemove(new KeyValuePair<string, SonosEventSubscription>(sid, subscription));
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.HttpMethod != "NOTIFY")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var subscription = Resolve(request);
            if (subscription is null)
            {
                // 412 tells the speaker to drop a subscription we no longer hold.
                response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            try
            {
                subscription.Handler(body);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Applying the Sonos event {Key} failed.", subscription.Key);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Handling a Sonos event request failed.");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            response.Close();
        }
    }

    private SonosEventSubscription? Resolve(HttpListenerRequest request)
    {
        var sid = request.Headers["SID"];
        if (sid is not null && _subscriptionsBySid.TryGetValue(sid, out var bySid))
        {
            return bySid;
        }

        // Only a subscription still waiting for its SID accepts a NOTIFY by path; once it has one, a different
        // SID is a stale subscription from an earlier run.
        var path = request.Url?.AbsolutePath;
        if (path is not null &&
            path.StartsWith(EventPathPrefix, StringComparison.Ordinal) &&
            _subscriptionsByKey.TryGetValue(Uri.UnescapeDataString(path[EventPathPrefix.Length..]), out var byKey) &&
            byKey.Sid is null)
        {
            return byKey;
        }

        return null;
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static TimeSpan GetLifetime(HttpResponseMessage response)
    {
        const string prefix = "Second-";
        var timeout = GetHeader(response, "TIMEOUT");
        return timeout is not null &&
            timeout.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(timeout.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : DefaultSubscriptionLifetime;
    }

    public async ValueTask DisposeAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            listener.Stop();
            listener.Close();
            if (_acceptLoop is not null)
            {
                await _acceptLoop;
            }
        }

        _subscriptionsByKey.Clear();
        _subscriptionsBySid.Clear();
    }
}
