using System.Globalization;

namespace HomeBlaze.Services;

/// <inheritdoc />
public sealed class TimeZoneDisplayService : ITimeZoneDisplay
{
    /// <inheritdoc />
    public bool IsResolved => Zone is not null;

    /// <inheritdoc />
    public TimeZoneInfo? Zone { get; private set; }

    /// <inheritdoc />
    public TimeZonePreference Preference { get; private set; } = TimeZonePreference.Automatic;

    /// <inheritdoc />
    public string Placeholder => "…";

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void SetResolved(TimeZonePreference preference, TimeZoneInfo zone)
    {
        Preference = preference;
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public string Format(DateTimeOffset value)
    {
        if (Zone is null)
        {
            return Placeholder;
        }

        var zoned = TimeZoneInfo.ConvertTime(value, Zone);
        return $"{zoned.ToString("g", CultureInfo.CurrentCulture)} {zoned:zzz}";
    }

    /// <inheritdoc />
    public string Format(DateTime value)
    {
        if (Zone is null)
        {
            return Placeholder;
        }

        if (value.Kind == DateTimeKind.Utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(value, Zone).ToString("g", CultureInfo.CurrentCulture);
        }

        return value.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <inheritdoc />
    public DateTime ToZoned(DateTimeOffset value) =>
        Zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, Zone).DateTime;

    /// <inheritdoc />
    public DateTimeOffset ToUtc(DateTime wallClock)
    {
        if (Zone is null)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Utc));
        }

        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var offset = Zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
