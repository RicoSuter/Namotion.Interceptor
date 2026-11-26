namespace Namotion.Devices.Sonos.Events;

/// <summary>
/// One UPnP event subscription with a Sonos service.
/// </summary>
internal sealed class SonosEventSubscription
{
    private volatile string? _sid;
    private int _hasReceivedEvent;
    private int _hasMissedEvents;

    // The SEQ of the last NOTIFY, -1 before the first.
    private long _lastSequence = -1;

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
    /// When the lifetime the speaker granted with the last accepted SUBSCRIBE or renewal ends, as a monotonic
    /// <see cref="TimeProvider.GetTimestamp"/> value of the listener's clock.
    /// </summary>
    internal long ExpiresAt { get; set; }

    /// <summary>
    /// Whether the last renewal got no answer, so the speaker may have dropped the subscription.
    /// </summary>
    internal bool HasFailedRenewal { get; set; }

    /// <summary>
    /// Records that events may be missing from the state: one was lost or could not be applied.
    /// </summary>
    internal void ReportMissedEvents() => Volatile.Write(ref _hasMissedEvents, 1);

    /// <summary>
    /// Returns whether events were missed since the last call and clears the report.
    /// </summary>
    internal bool TakeMissedEvents() => Interlocked.Exchange(ref _hasMissedEvents, 0) == 1;

    /// <summary>
    /// Records the SEQ header of a NOTIFY and reports missed events when it does not follow the previous one: the
    /// first event of a subscription carries 0, and each later one the next value, wrapping from
    /// <see cref="uint.MaxValue"/> to 1.
    /// </summary>
    internal void RecordSequence(uint sequence)
    {
        // Concurrent NOTIFYs that arrive out of order read as a gap, which only costs a full poll.
        var previous = Interlocked.Exchange(ref _lastSequence, sequence);
        var expected = previous switch
        {
            < 0 => 0,
            uint.MaxValue => 1,
            _ => previous + 1
        };

        if (sequence != expected)
        {
            ReportMissedEvents();
        }
    }

    /// <summary>
    /// Records that a NOTIFY arrived and returns whether it was the first.
    /// </summary>
    internal bool MarkEventReceived() => Interlocked.Exchange(ref _hasReceivedEvent, 1) == 0;
}
