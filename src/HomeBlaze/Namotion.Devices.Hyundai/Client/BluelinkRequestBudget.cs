namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// Counts the calls to the vehicle API per local day, where Hyundai enforces its undocumented daily limit (5091).
/// Thread-safe. The count lives in memory, so a restart resets it.
/// </summary>
internal sealed class BluelinkRequestBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<int> _getLimit;
    private readonly Lock _lock = new();
    private DateTime _day;
    private int _count;

    /// <param name="timeProvider">The clock; the day changes at local midnight of its time zone.</param>
    /// <param name="getLimit">Returns the current daily limit, read on every call so that configuration changes apply at once.</param>
    public BluelinkRequestBudget(TimeProvider timeProvider, Func<int> getLimit)
    {
        _timeProvider = timeProvider;
        _getLimit = getLimit;
        _day = timeProvider.GetLocalNow().Date;
    }

    /// <summary>Raised after the count changed, outside the lock.</summary>
    /// <remarks>Concurrent changes can raise out of order; read <see cref="Count"/> for the current value.</remarks>
    public event Action? CountChanged;

    /// <summary>The calls of the current local day, without resetting the counter or raising <see cref="CountChanged"/>.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _timeProvider.GetLocalNow().Date == _day ? _count : 0;
            }
        }
    }

    /// <summary>The calls left today.</summary>
    public int Remaining => Math.Max(0, _getLimit() - Update());

    /// <summary>The time until the counter resets at local midnight.</summary>
    public TimeSpan TimeUntilReset
    {
        get
        {
            var now = _timeProvider.GetLocalNow();
            var nextMidnight = DateTime.SpecifyKind(now.Date.AddDays(1), DateTimeKind.Unspecified);
            try
            {
                // The wall-clock difference is wrong on the day the clock changes.
                return TimeZoneInfo.ConvertTimeToUtc(nextMidnight, _timeProvider.LocalTimeZone) - _timeProvider.GetUtcNow();
            }
            catch (ArgumentException)
            {
                // Midnight does not exist in the time zone (a change at midnight).
                return nextMidnight - now.DateTime;
            }
        }
    }

    /// <summary>Resets the counter after local midnight.</summary>
    /// <returns>The calls of the current local day.</returns>
    public int Update()
    {
        int count;
        bool isChanged;
        lock (_lock)
        {
            isChanged = ResetIfNewDay();
            count = _count;
        }

        if (isChanged)
        {
            CountChanged?.Invoke();
        }

        return count;
    }

    /// <summary>Counts one call.</summary>
    /// <exception cref="BluelinkRequestLimitException">The limit is reached; the call is not counted and must not be sent.</exception>
    public void Consume()
    {
        var limit = _getLimit();
        lock (_lock)
        {
            ResetIfNewDay();
            if (_count >= limit)
            {
                throw new BluelinkRequestLimitException();
            }

            _count++;
        }

        CountChanged?.Invoke();
    }

    /// <returns>Whether the count changed.</returns>
    private bool ResetIfNewDay()
    {
        var today = _timeProvider.GetLocalNow().Date;
        if (today == _day)
        {
            return false;
        }

        _day = today;
        var isChanged = _count != 0;
        _count = 0;
        return isChanged;
    }
}
