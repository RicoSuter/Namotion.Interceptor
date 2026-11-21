namespace Namotion.Devices.Tesla.Polling;

/// <summary>
/// Decides when the vehicle_data of an online vehicle is read; a vehicle that is not online is never read, so the
/// policy never wakes one. Reading an awake vehicle keeps it awake, so after IdleTimeout in which it could have
/// slept, reading pauses for SleepWindow to let it fall asleep.
/// Not thread-safe: the caller serializes all calls.
/// </summary>
internal sealed class TeslaVehiclePollingPolicy
{
    private DateTimeOffset? _lastRead;
    private bool _wasActive;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _sleepWindowEnd;

    /// <summary>Whether vehicle_data is due. A vehicle that is not online resets the policy, so that its next wake is read immediately.</summary>
    public bool ShouldRead(TeslaVehicleState state, DateTimeOffset now, TeslaPollingSettings settings)
    {
        if (state != TeslaVehicleState.Online)
        {
            _lastRead = null;
            _idleSince = null;
            _sleepWindowEnd = null;
            return false;
        }

        if (_sleepWindowEnd is { } end)
        {
            if (now < end)
            {
                return false;
            }

            _sleepWindowEnd = null;
            _lastRead = null;
        }

        var interval = _wasActive ? settings.ActiveInterval : settings.IdleInterval;
        return _lastRead is null || now - _lastRead.Value >= interval;
    }

    /// <summary>Records a successful read with the activity it showed and starts the sleep window once the vehicle could sleep for IdleTimeout.</summary>
    public void OnRead(DateTimeOffset now, bool isActive, bool canSleep, TeslaPollingSettings settings)
    {
        _lastRead = now;
        _wasActive = isActive;

        if (!canSleep)
        {
            _idleSince = null;
            return;
        }

        _idleSince ??= now;
        if (now - _idleSince.Value >= settings.IdleTimeout)
        {
            _sleepWindowEnd = now + settings.SleepWindow;
            _idleSince = null;
        }
    }

    /// <summary>Ends a sleep window early and makes the next read due, when the cached snapshot shows that the vehicle charges, drives or has a user.</summary>
    public void OnActivityObserved()
    {
        _sleepWindowEnd = null;
        _idleSince = null;
        _lastRead = null;
    }
}
