using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A loopback Sonos speaker: answers SOAP actions with canned values, serves a device description and
/// accepts event subscriptions, recording every request.
/// </summary>
internal sealed class FakeSonosSpeaker : IAsyncDisposable
{
    private static readonly HttpClient NotifyClient = new();

    private readonly LoopbackHttpServer _server;
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _faults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _serverErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SoapCall> _calls = new();
    private readonly ConcurrentDictionary<string, string> _callbacks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _unsubscribed = new();
    private readonly ConcurrentQueue<string> _subscribed = new();
    private readonly ConcurrentQueue<(string Path, DateTimeOffset At)> _renewed = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);

    internal FakeSonosSpeaker()
    {
        DeviceDescription = TestFixtures.Read("device-description-ray.xml");
        _server = new LoopbackHttpServer(HandleAsync);
    }

    internal Uri BaseUri => _server.BaseUri;

    internal string Host => $"127.0.0.1:{_server.Port}";

    internal string DeviceDescription { get; set; }

    internal IReadOnlyCollection<SoapCall> Calls => _calls.ToArray();

    internal IReadOnlyCollection<string> Unsubscribed => _unsubscribed.ToArray();

    /// <summary>
    /// The event path of every new subscription requested (a SUBSCRIBE carrying a callback), in arrival order.
    /// </summary>
    internal IReadOnlyCollection<string> Subscribed => _subscribed.ToArray();

    /// <summary>
    /// The event path of every renewal request received (a SUBSCRIBE carrying a SID), in arrival order, one entry per
    /// request. Counts the renewals <see cref="AbortRenewals"/> failed as well as the answered ones.
    /// </summary>
    internal IReadOnlyCollection<string> Renewed => _renewed.Select(renewal => renewal.Path).ToArray();

    /// <summary>
    /// The same requests as <see cref="Renewed"/>, each with the time it arrived.
    /// </summary>
    internal IReadOnlyCollection<(string Path, DateTimeOffset At)> RenewalAttempts => _renewed.ToArray();

    /// <summary>
    /// Fails every renewal with a dropped connection, as an unreachable speaker would.
    /// </summary>
    internal bool AbortRenewals { get; set; }

    /// <summary>
    /// The lifetime granted to subscriptions and renewals, in seconds.
    /// </summary>
    internal int SubscriptionTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Answers every SUBSCRIBE, new or renewal, with HTTP 500.
    /// </summary>
    internal bool FailSubscriptions { get; set; }

    /// <summary>
    /// Sends an initial NOTIFY, without values, to the callback of every new subscription, as a speaker does right
    /// after accepting it. Off acts as a speaker that cannot reach the callback.
    /// </summary>
    internal bool SendInitialEvents { get; set; } = true;

    internal static string SidFor(string eventPath) => "uuid:" + eventPath.Trim('/').Replace('/', '-');

    internal string? GetCallback(string eventPath) =>
        _callbacks.TryGetValue(eventPath, out var callback) ? callback : null;

    internal void Respond(string action, params (string Name, string Value)[] values) =>
        _responses[action] = string.Concat(values.Select(value => $"<{value.Name}>{SecurityElement.Escape(value.Value)}</{value.Name}>"));

    /// <summary>
    /// Answers GetEQ for one EQType, so each equalizer setting can carry its own value.
    /// </summary>
    internal void RespondToEqualizer(string eqType, string value) =>
        _responses[EqualizerKey(eqType)] = $"<CurrentValue>{SecurityElement.Escape(value)}</CurrentValue>";

    /// <summary>
    /// Answers a Browse starting at the index with one page of a longer result.
    /// </summary>
    internal void RespondToBrowsePage(int startingIndex, string result, int numberReturned, int totalMatches) =>
        _responses[BrowsePageKey(startingIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))] =
            $"<Result>{SecurityElement.Escape(result)}</Result><NumberReturned>{numberReturned}</NumberReturned>" +
            $"<TotalMatches>{totalMatches}</TotalMatches><UpdateID>1</UpdateID>";

    /// <summary>
    /// Answers the action with HTTP 500 and a UPnPError SOAP fault carrying the error code.
    /// </summary>
    internal void RespondWithFault(string action, int errorCode) => _faults[action] = errorCode;

    internal void ClearFault(string action) => _faults.TryRemove(action, out _);

    /// <summary>
    /// Answers the action with a bare HTTP 500 and an empty body, as a satellite answers a ContentDirectory Browse.
    /// </summary>
    internal void RespondWithServerError(string action) => _serverErrors[action] = true;

    internal void ClearServerError(string action) => _serverErrors.TryRemove(action, out _);

    /// <summary>
    /// Records but does not answer the action until the returned source completes.
    /// </summary>
    internal TaskCompletionSource HoldAction(string action) =>
        _holds[action] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Records the callback of a new subscription to the event path but does not answer until the returned source completes.
    /// </summary>
    internal TaskCompletionSource HoldSubscribe(string eventPath) =>
        _holds["SUBSCRIBE " + eventPath] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Answers GetZoneGroupState with a household of standalone players, each at its own base URI.
    /// </summary>
    internal void RespondWithTopology(params (string Uuid, string RoomName, Uri BaseUri)[] players) =>
        Respond("GetZoneGroupState", ("ZoneGroupState",
            "<ZoneGroupState><ZoneGroups>" +
            string.Concat(players.Select(player =>
                $"""<ZoneGroup Coordinator="{player.Uuid}" ID="{player.Uuid}:1">{CreateMember(player)}</ZoneGroup>""")) +
            "</ZoneGroups></ZoneGroupState>"));

    /// <summary>
    /// Answers GetZoneGroupState with one group of all players, coordinated by the first.
    /// </summary>
    internal void RespondWithGroup(params (string Uuid, string RoomName, Uri BaseUri)[] players) =>
        Respond("GetZoneGroupState", ("ZoneGroupState",
            "<ZoneGroupState><ZoneGroups>" +
            $"""<ZoneGroup Coordinator="{players[0].Uuid}" ID="{players[0].Uuid}:1">{string.Concat(players.Select(CreateMember))}</ZoneGroup>""" +
            "</ZoneGroups></ZoneGroupState>"));

    private static string CreateMember((string Uuid, string RoomName, Uri BaseUri) player) =>
        $"""<ZoneGroupMember UUID="{player.Uuid}" Location="{player.BaseUri}xml/device_description.xml" ZoneName="{player.RoomName}" SoftwareVersion="97.1-80312" EthLink="0" MoreInfo="" />""";

    /// <summary>
    /// Answers as a single-room household whose only player is this speaker, paused on Spotify Connect.
    /// </summary>
    internal void RespondAsIdlePlayer(string uuid, string roomName)
    {
        const string spotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";
        RespondWithTopology((uuid, roomName, BaseUri));
        Respond("GetZoneInfo",
            ("SerialNumber", "00-00-00-00-00-06:D"), ("SoftwareVersion", "97.1-80312"), ("DisplaySoftwareVersion", "18.8"),
            ("HardwareVersion", "1.38.1.10-2.1"), ("IPAddress", "127.0.0.1"), ("MACAddress", "00:00:00:00:00:06"),
            ("CopyrightInfo", "c"), ("ExtraInfo", ""), ("HTAudioIn", "0"), ("Flags", "0"));
        Respond("GetTransportInfo", ("CurrentTransportState", "PAUSED_PLAYBACK"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
        Respond("GetTransportSettings", ("PlayMode", "NORMAL"), ("RecQualityMode", "NOT_IMPLEMENTED"));
        Respond("GetMediaInfo", ("NrTracks", "1"), ("MediaDuration", "NOT_IMPLEMENTED"), ("CurrentURI", spotifyUri), ("CurrentURIMetaData", ""));
        Respond("GetPositionInfo",
            ("Track", "1"), ("TrackDuration", "NOT_IMPLEMENTED"), ("TrackMetaData", "NOT_IMPLEMENTED"), ("TrackURI", spotifyUri),
            ("RelTime", "NOT_IMPLEMENTED"), ("AbsTime", "NOT_IMPLEMENTED"), ("RelCount", "2147483647"), ("AbsCount", "2147483647"));
        Respond("GetRemainingSleepTimerDuration", ("RemainingSleepTimerDuration", ""), ("CurrentSleepTimerGeneration", "0"));
        Respond("GetVolume", ("CurrentVolume", "44"));
        Respond("GetMute", ("CurrentMute", "0"));
        Respond("GetBass", ("CurrentBass", "0"));
        Respond("GetTreble", ("CurrentTreble", "0"));
        Respond("GetLoudness", ("CurrentLoudness", "1"));
        RespondToEqualizer("NightMode", "1");
        RespondToEqualizer("DialogLevel", "0");
        Respond("GetGroupVolume", ("CurrentVolume", "44"));
        Respond("GetGroupMute", ("CurrentMute", "0"));
        Respond("Browse", ("Result", TestFixtures.Read("favorites.xml")), ("NumberReturned", "4"), ("TotalMatches", "4"), ("UpdateID", "1"));
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;

        switch (request.HttpMethod)
        {
            case "GET" when path == "/xml/device_description.xml":
                await WriteAsync(response, DeviceDescription);
                return;

            case "SUBSCRIBE" when FailSubscriptions:
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return;

            case "SUBSCRIBE":
                if (request.Headers["CALLBACK"] is { } callback)
                {
                    _subscribed.Enqueue(path);
                    _callbacks[path] = callback.Trim('<', '>');
                    if (_holds.TryGetValue("SUBSCRIBE " + path, out var subscribeHold))
                    {
                        await subscribeHold.Task;
                    }
                }

                if (request.Headers["SID"] is not null)
                {
                    _renewed.Enqueue((path, DateTimeOffset.UtcNow));
                    if (AbortRenewals)
                    {
                        // A bare Abort still answers 200 here, so the body is cut short to make the request fail.
                        response.ContentLength64 = 10;
                        await response.OutputStream.WriteAsync("<"u8.ToArray());
                        await response.OutputStream.FlushAsync();
                        response.Abort();
                        return;
                    }
                }

                response.Headers["SID"] = SidFor(path);
                response.Headers["TIMEOUT"] = $"Second-{SubscriptionTimeoutSeconds}";
                if (SendInitialEvents && request.Headers["CALLBACK"] is { } newCallback)
                {
                    // Not awaited: Sonos sends the initial NOTIFY around its SUBSCRIBE response, often before it.
                    _ = SendInitialEventAsync(newCallback.Trim('<', '>'), SidFor(path));
                }

                return;

            case "UNSUBSCRIBE":
                _unsubscribed.Enqueue(path);
                return;

            case "POST":
                // "urn:schemas-upnp-org:service:AVTransport:1#Play"
                var soapAction = (request.Headers["SOAPACTION"] ?? string.Empty).Trim('"');
                var action = soapAction[(soapAction.LastIndexOf('#') + 1)..];
                var service = soapAction.Split(':') is { Length: >= 4 } parts ? parts[3] : string.Empty;

                string body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                _calls.Enqueue(new SoapCall(path, service, action, body));
                if (_holds.TryGetValue(action, out var hold))
                {
                    await hold.Task;
                }

                if (_serverErrors.ContainsKey(action))
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentLength64 = 0;
                    return;
                }

                if (_faults.TryGetValue(action, out var errorCode))
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteAsync(response,
                        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                        "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
                        $"<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{errorCode}</errorCode></UPnPError></detail>" +
                        "</s:Fault></s:Body></s:Envelope>");
                    return;
                }

                var values = GetResponseValues(action, body);
                await WriteAsync(response,
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    $"<s:Body><u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\">{values}</u:{action}Response></s:Body></s:Envelope>");
                return;

            default:
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
        }
    }

    private static async Task SendInitialEventAsync(string callback, string sid)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
            {
                Content = new StringContent(SonosEventBodies.Properties())
            };
            request.Headers.TryAddWithoutValidation("SID", sid);
            using var response = await NotifyClient.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // The listener is gone, as after a stop; a real speaker gives up the same way.
        }
    }

    private static string EqualizerKey(string eqType) => "GetEQ:" + eqType;

    private static string BrowsePageKey(string startingIndex) => "Browse:" + startingIndex;

    private string GetResponseValues(string action, string body)
    {
        if (action == "GetEQ" && TryGetArgument(body, "EQType") is { } eqType &&
            _responses.TryGetValue(EqualizerKey(eqType), out var equalizerValues))
        {
            return equalizerValues;
        }

        if (action == "Browse" && TryGetArgument(body, "StartingIndex") is { } startingIndex &&
            _responses.TryGetValue(BrowsePageKey(startingIndex), out var pageValues))
        {
            return pageValues;
        }

        return _responses.GetValueOrDefault(action, string.Empty);
    }

    private static string? TryGetArgument(string body, string name)
    {
        var start = body.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += name.Length + 2;
        var end = body.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? null : body[start..end];
    }

    private static async Task WriteAsync(HttpListenerResponse response, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentType = "text/xml; charset=\"utf-8\"";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// One SOAP request: the control URL path it was posted to, the service and action from SOAPACTION, and the raw body.
/// </summary>
internal sealed record SoapCall(string Path, string Service, string Action, string Body);
