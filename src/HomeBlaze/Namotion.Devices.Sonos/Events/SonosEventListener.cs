using System.Buffers;
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
    private static readonly string RequestedTimeout = $"Second-{(int)DefaultSubscriptionLifetime.TotalSeconds}";
    private static readonly HttpMethod SubscribeMethod = new("SUBSCRIBE");
    private static readonly HttpMethod UnsubscribeMethod = new("UNSUBSCRIBE");

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly TimeSpan _minimumLifetime;
    private readonly TimeProvider _clock;
    private readonly Action? _firstEventReceived;
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SonosEventSubscription> _subscriptionsBySid = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private Task? _acceptLoop;
    private string? _callbackBaseUri;
    private volatile bool _disposing;
    private volatile bool _acceptFailed;
    private int _inFlightHandlers;
    private readonly TaskCompletionSource _handlersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="httpClient">The client for SUBSCRIBE and UNSUBSCRIBE requests, borrowed and not disposed.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="minimumLifetime">The shortest lifetime a renewal is scheduled for, whatever the speaker grants.</param>
    /// <param name="clock">The clock renewals are scheduled on.</param>
    /// <param name="firstEventReceived">
    /// Called on the thread of the NOTIFY when a subscription receives its first event. Runs as part of the handler,
    /// so it must not block on disposing the listener either.
    /// </param>
    internal SonosEventListener(
        HttpClient httpClient,
        ILogger logger,
        TimeSpan minimumLifetime,
        TimeProvider clock,
        Action? firstEventReceived = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _minimumLifetime = minimumLifetime;
        _clock = clock;
        _firstEventReceived = firstEventReceived;
    }

    internal bool IsListening => !_disposing && !_acceptFailed && _listener?.IsListening == true;

    internal IReadOnlyCollection<SonosEventSubscription> Subscriptions => _subscriptionsByKey.Values.ToArray();

    /// <summary>
    /// The URI speakers send events to, without the subscription key; null until started.
    /// </summary>
    internal string? CallbackBaseUri => _callbackBaseUri;

    /// <summary>
    /// Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken or cannot be bound,
    /// <see cref="InvalidOperationException"/> when already started and <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    internal void Start(string callbackHost, int port, string listenHost = "+")
    {
        ObjectDisposedException.ThrowIf(_disposing, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The Sonos event listener is already started.");
        }

        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://{listenHost}:{port}/");
            listener.Start();
        }
        catch
        {
            listener.Close();
            throw;
        }

        _listener = listener;
        _callbackBaseUri = $"http://{callbackHost}:{port}{EventPathPrefix}";
        _acceptLoop = AcceptLoopAsync(listener);
    }

    /// <summary>
    /// Subscribes to the events of a service. Throws <see cref="InvalidOperationException"/> when the key is already
    /// subscribed or the listener is not started, <see cref="ObjectDisposedException"/> after disposal,
    /// <see cref="HttpRequestException"/> when the SUBSCRIBE request fails, and <see cref="OperationCanceledException"/>
    /// when the subscription is unsubscribed concurrently.
    /// </summary>
    /// <remarks>
    /// The handler runs on the thread pool and must not block on disposing the listener, which waits for running handlers.
    /// </remarks>
    internal async Task<SonosEventSubscription> SubscribeAsync(
        string key, Uri eventUri, Action<string> handler, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposing, this);
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
            using var request = new HttpRequestMessage(SubscribeMethod, eventUri);
            request.Headers.TryAddWithoutValidation("CALLBACK", $"<{_callbackBaseUri}{key}>");
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("TIMEOUT", RequestedTimeout);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var sid = GetHeader(response, "SID")
                ?? throw new InvalidOperationException($"The subscription to {eventUri} returned no SID.");
            subscription.SubscribedAt = _clock.GetTimestamp();
            subscription.RenewAt = _clock.GetTimestampAfter(GetLifetime(response) / 2);

            // Unsubscribed while the request was in flight: Forget found no SID, so its caller sent nothing and the
            // speaker still holds the subscription. The lock makes exactly one of the two sides own the UNSUBSCRIBE.
            bool orphaned;
            lock (subscription.SyncRoot)
            {
                orphaned = !_subscriptionsByKey.TryGetValue(key, out var current) || !ReferenceEquals(current, subscription);
                if (!orphaned)
                {
                    // Indexed before the SID is published: Resolve reads without the lock, and once a subscription
                    // has its SID it no longer accepts a NOTIFY by path, so the other order answers one with 412.
                    _subscriptionsBySid[sid] = subscription;
                }

                subscription.Sid = sid;
            }

            if (orphaned)
            {
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
    internal async Task RenewAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        var sid = subscription.Sid
            ?? throw new InvalidOperationException($"The Sonos event {subscription.Key} has no SID to renew.");

        using var request = new HttpRequestMessage(SubscribeMethod, subscription.EventUri);
        request.Headers.TryAddWithoutValidation("SID", sid);
        request.Headers.TryAddWithoutValidation("TIMEOUT", RequestedTimeout);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Forget(subscription);
            return;
        }

        subscription.RenewAt = _clock.GetTimestampAfter(GetLifetime(response) / 2);
    }

    internal async Task UnsubscribeAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        if (Forget(subscription) is { } sid)
        {
            await SendUnsubscribeAsync(subscription.EventUri, sid, subscription.Key, cancellationToken);
        }
    }

    private async Task TryUnsubscribeOrphanAsync(Uri eventUri, string sid, CancellationToken cancellationToken)
    {
        try
        {
            await SendUnsubscribeAsync(eventUri, sid, $"orphaned {sid}", cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation(exception,
                "The orphaned Sonos subscription {Sid} could not be cancelled; the speaker drops it once it expires.", sid);
        }
    }

    // The label names the subscription in the log text: its key, or its SID for an orphan.
    private async Task SendUnsubscribeAsync(Uri eventUri, string sid, string label, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(UnsubscribeMethod, eventUri);
        request.Headers.TryAddWithoutValidation("SID", sid);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        // 412: the speaker no longer knows the subscription, so nothing is left behind.
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger.LogDebug("Unsubscribing the Sonos events {Subscription} returned {StatusCode}; the subscription was already gone.",
                label, response.StatusCode);
        }
        else if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Unsubscribing the Sonos events {Subscription} returned {StatusCode}; the speaker drops the subscription once it expires.",
                label, response.StatusCode);
        }
    }

    /// <summary>
    /// Unsubscribes every subscription concurrently. Best effort: an unreachable speaker drops the subscription itself
    /// once it expires.
    /// </summary>
    internal Task UnsubscribeAllAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_subscriptionsByKey.Values.Select(subscription => TryUnsubscribeAsync(subscription, cancellationToken)));

    private async Task TryUnsubscribeAsync(SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await UnsubscribeAsync(subscription, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation(exception,
                "The Sonos event subscription {Key} could not be cancelled; the speaker drops it once it expires.", subscription.Key);
        }
    }

    /// <returns>The SID the speaker knows the subscription by, or null when the SUBSCRIBE response has not arrived.</returns>
    private string? Forget(SonosEventSubscription subscription)
    {
        lock (subscription.SyncRoot)
        {
            _subscriptionsByKey.TryRemove(new KeyValuePair<string, SonosEventSubscription>(subscription.Key, subscription));
            var sid = subscription.Sid;
            if (sid is not null)
            {
                _subscriptionsBySid.TryRemove(new KeyValuePair<string, SonosEventSubscription>(sid, subscription));
            }

            return sid;
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                _ = HandleAsync(context);
            }
        }
        catch (Exception exception)
        {
            if (_disposing || !listener.IsListening)
            {
                return;
            }

            // Ending the loop is deliberate: retrying a failing listener would spin.
            _acceptFailed = true;
            _logger.LogError(exception, "The Sonos event listener stopped accepting requests.");
            try
            {
                listener.Stop();
            }
            catch (Exception stopException)
            {
                _logger.LogDebug(stopException, "Stopping the failed Sonos event listener failed.");
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        HttpStatusCode status;
        try
        {
            status = await ProcessAsync(context);
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

    private async Task<HttpStatusCode> ProcessAsync(HttpListenerContext context)
    {
        var request = context.Request;
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

        var body = request.ContentLength64 > MaxNotifyBodyBytes ? null : await ReadBodyAsync(request);
        if (body is null)
        {
            // Closing the connection keeps the listener from draining the unread rest of the body.
            context.Response.KeepAlive = false;
            return HttpStatusCode.RequestEntityTooLarge;
        }

        // Counted before the disposing check: DisposeAsync sets the flag first and then waits for the count, so
        // a handler is either refused here or waited for there.
        Interlocked.Increment(ref _inFlightHandlers);
        try
        {
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

            // Any NOTIFY proves that the speaker reaches the callback, also one whose body failed to apply.
            if (subscription.MarkEventReceived())
            {
                ReportFirstEvent(subscription);
            }

            return HttpStatusCode.OK;
        }
        finally
        {
            if (Interlocked.Decrement(ref _inFlightHandlers) == 0 && _disposing)
            {
                _handlersDrained.TrySetResult();
            }
        }
    }

    private void ReportFirstEvent(SonosEventSubscription subscription)
    {
        try
        {
            _firstEventReceived?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reporting the first Sonos event {Key} failed.", subscription.Key);
        }
    }

    /// <returns>The body, or null when it exceeds <see cref="MaxNotifyBodyBytes"/>. Also bounds chunked bodies, which declare no length.</returns>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffered = new MemoryStream(request.ContentLength64 > 0 ? (int)request.ContentLength64 : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await request.InputStream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                if (buffered.Length + read > MaxNotifyBodyBytes)
                {
                    return null;
                }

                buffered.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        buffered.Position = 0;
        using var reader = new StreamReader(buffered, request.ContentEncoding);
        return await reader.ReadToEndAsync();
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

    private TimeSpan GetLifetime(HttpResponseMessage response)
    {
        const string prefix = "Second-";
        var timeout = GetHeader(response, "TIMEOUT");
        var lifetime = timeout is not null &&
            timeout.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(timeout.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : DefaultSubscriptionLifetime;

        // A tiny grant such as Second-0 would otherwise schedule a renewal at every wake of the connection loop.
        return lifetime > _minimumLifetime ? lifetime : _minimumLifetime;
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

        // A full-fence read pairs with the Interlocked operations of the handlers: the last handler to leave
        // signals once the flag is set, and when none is running the signal comes from here.
        if (Interlocked.CompareExchange(ref _inFlightHandlers, 0, 0) == 0)
        {
            _handlersDrained.TrySetResult();
        }

        await _handlersDrained.Task;

        _subscriptionsByKey.Clear();
        _subscriptionsBySid.Clear();
    }
}
