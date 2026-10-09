namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// One UPnP event subscription with a Sonos service.
/// </summary>
internal sealed class SonosEventSubscription
{
    private volatile string? _sid;

    internal SonosEventSubscription(string key, Uri eventUri, Action<string> handler)
    {
        Key = key;
        EventUri = eventUri;
        Handler = handler;
    }

    /// <summary>
    /// The callback path segment, <c>{uuid}/{service}</c>.
    /// </summary>
    internal string Key { get; }

    internal Uri EventUri { get; }

    /// <summary>
    /// Serializes recording the SID against forgetting the subscription.
    /// </summary>
    internal object SyncRoot { get; } = new();

    internal Action<string> Handler { get; }

    /// <summary>
    /// The subscription id, null until the speaker answered the SUBSCRIBE request.
    /// </summary>
    internal string? Sid
    {
        get => _sid;
        set => _sid = value;
    }

    internal DateTimeOffset RenewAt { get; set; }
}
