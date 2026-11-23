namespace Namotion.Devices.Tesla.Mapping;

/// <summary>
/// Keeps the timestamp of the newest data applied to one section, so that an older snapshot from the
/// cache never overwrites a newer reading. Not thread-safe: only the polling loop of the account uses it.
/// </summary>
internal sealed class TeslaSectionClock
{
    private DateTimeOffset _newest = DateTimeOffset.MinValue;

    /// <returns>True when data with this timestamp is not older than the data applied before; data without a timestamp always applies.</returns>
    public bool TryAdvance(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return true;
        }

        if (timestamp.Value < _newest)
        {
            return false;
        }

        _newest = timestamp.Value;
        return true;
    }
}
