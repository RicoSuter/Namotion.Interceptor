using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <summary>
/// The time zone of the viewer as tracked properties, so that a component that formats a date in its render renders
/// again when the zone resolves or changes.
/// </summary>
[InterceptorSubject]
internal partial class TimeZoneState
{
    public partial TimeZoneInfo? Zone { get; set; }

    public partial TimeZonePreference Preference { get; set; }
}
