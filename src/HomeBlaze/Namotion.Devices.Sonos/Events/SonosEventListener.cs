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
    private const long MaxNotifyBodyBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultSubscriptionLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsBySid = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private Task? _acceptLoop;
    private string? _callbackBaseUri;
    private volatile bool _disposing;
    private volatile bool _acceptFailed;

    internal SonosEventListener(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    internal bool IsListening => !_disposing && !_acceptFailed && _listener?.IsListening == true;

    internal IReadOnlyCollection<SonosEventSubscription> Subscriptions => _subscriptionsByKey.Values.ToArray();

    /// <summary>
    /// Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken or cannot be bound.
    /// </summary>
    internal void Start(string callbackHost, int port, string listenHost = "+")
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The Sonos event listener is already started.");
        }

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
        if (_callbackBaseUri is null)
        {
            throw new InvalidOperationException("The Sonos event listener must be started before subscribing.");
        }

        var subscription = new SonosEventSubscription(key, eventUri, handler);

        // Registered before the request: Sonos sends the initial full-state NOTIFY right after accepting, often
        // before its SUBSCRIBE response arrives, so that NOTIFY can only be matched by its callback path.
        if (!_subscriptionsByKey.TryAdd(key, subscription))
        {
            throw new InvalidOperationException($"The Sonos event {key} is already subscribed.");
        }

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

            // Unsubscribed while the request was in flight: UnsubscribeAsync had no SID to send, so the speaker
            // still holds the subscription and nothing here would renew or release it.
            if (!_subscriptionsByKey.TryGetValue(key, out var current) || !ReferenceEquals(current, subscription))
            {
                _subscriptionsBySid.TryRemove(new KeyValuePair<string, SonosEventSubscription>(sid, subscription));
                await TryUnsubscribeOrphanAsync(eventUri, sid, cancellationToken);
                throw new OperationCanceledException($"The Sonos event {key} was unsubscribed while subscribing.");
            }

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
        var sid = subscription.Sid
            ?? throw new InvalidOperationException($"The Sonos event {subscription.Key} has no SID to renew.");

        using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", sid);
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
        if (subscription.Sid is not { } sid)
        {
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", sid);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Unsubscribing Sonos events {Key} returned {StatusCode}.", subscription.Key, response.StatusCode);
        }
    }

    private async Task TryUnsubscribeOrphanAsync(Uri eventUri, string sid, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), eventUri);
            request.Headers.TryAddWithoutValidation("SID", sid);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Unsubscribing the orphaned Sonos subscription {Sid} returned {StatusCode}.", sid, response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Unsubscribing the orphaned Sonos subscription {Sid} failed.", sid);
        }
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
                if (!_disposing && listener.IsListening)
                {
                    // Ending the loop is deliberate: retrying a failing listener would spin.
                    _acceptFailed = true;
                    _logger.LogError(exception, "The Sonos event listener stopped accepting requests.");
                }

                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        HttpStatusCode status;
        try
        {
            status = await ProcessAsync(context.Request);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Handling a Sonos event request failed.");
            status = HttpStatusCode.InternalServerError;
        }

        try
        {
            response.StatusCode = (int)status;
            response.Close();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Answering a Sonos event request failed, aborting the connection.");
            try
            {
                response.Abort();
            }
            catch (Exception abortException)
            {
                _logger.LogDebug(abortException, "Aborting a Sonos event connection failed.");
            }
        }
    }

    private async Task<HttpStatusCode> ProcessAsync(HttpListenerRequest request)
    {
        if (request.HttpMethod != "NOTIFY")
        {
            return HttpStatusCode.MethodNotAllowed;
        }

        var subscription = Resolve(request);
        if (subscription is null)
        {
            // 412 tells the speaker to drop a subscription we no longer hold.
            return HttpStatusCode.PreconditionFailed;
        }

        if (request.ContentLength64 > MaxNotifyBodyBytes)
        {
            return HttpStatusCode.RequestEntityTooLarge;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync();
        }

        if (_disposing)
        {
            return HttpStatusCode.ServiceUnavailable;
        }

        try
        {
            subscription.Handler(body);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Applying the Sonos event {Key} failed.", subscription.Key);
        }

        return HttpStatusCode.OK;
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
        _disposing = true;
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
