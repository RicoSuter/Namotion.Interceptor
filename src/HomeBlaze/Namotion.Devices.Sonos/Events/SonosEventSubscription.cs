namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// One UPnP event subscription with a Sonos service.
/// </summary>
internal sealed class SonosEventSubscription
{
    private volatile string? _sid;
    private int _hasReceivedEvent;

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

    /// <summary>
    /// Whether a NOTIFY arrived, which proves that the speaker reaches the callback. A SID only proves that it
    /// accepted the SUBSCRIBE.
    /// </summary>
    internal bool HasReceivedEvent => Volatile.Read(ref _hasReceivedEvent) == 1;

    /// <summary>
    /// When the speaker accepted the subscription, as a monotonic <see cref="TimeProvider.GetTimestamp"/> value of the
    /// listener's clock. Set before <see cref="Sid"/>.
    /// </summary>
    internal long SubscribedAt { get; set; }

    /// <summary>
    /// When to renew, as a monotonic <see cref="TimeProvider.GetTimestamp"/> value of the listener's clock.
    /// </summary>
    internal long RenewAt { get; set; }

    /// <summary>
    /// Records that a NOTIFY arrived and returns whether it was the first.
    /// </summary>
    internal bool MarkEventReceived() => Interlocked.Exchange(ref _hasReceivedEvent, 1) == 0;
}
