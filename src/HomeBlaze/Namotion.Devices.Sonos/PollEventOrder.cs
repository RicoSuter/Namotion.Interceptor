namespace Namotion.Devices.Sonos;

/// <summary>
/// Orders the polls of one kind of state against its events, by the sequence <see cref="SonosSystem.NextOrder"/> hands
/// out, which a wall-clock step cannot reorder. Not thread-safe: the owner's lock guards it.
/// </summary>
internal struct PollEventOrder
{
    private long _lastPollStartedAt;
    private long _lastEventAt;

    /// <summary>
    /// Records an event applied at <paramref name="order"/>.
    /// </summary>
    internal void RecordEvent(long order) => _lastEventAt = order;

    /// <summary>
    /// Records a poll that started at <paramref name="pollStartedAt"/> and returns whether its values apply: false
    /// when a poll that started later was applied already, or when an event was applied after the poll started, since
    /// both read newer state.
    /// </summary>
    internal bool TryApplyPoll(long pollStartedAt)
    {
        if (pollStartedAt < _lastPollStartedAt)
        {
            return false;
        }

        _lastPollStartedAt = pollStartedAt;
        return _lastEventAt <= pollStartedAt;
    }
}
