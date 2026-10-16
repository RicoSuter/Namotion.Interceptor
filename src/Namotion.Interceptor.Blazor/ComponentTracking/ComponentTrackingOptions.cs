namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Options of per-component change tracking, see <see cref="ComponentTrackingServiceCollectionExtensions.AddComponentTracking"/>.
/// </summary>
public sealed class ComponentTrackingOptions
{
    /// <summary>
    /// Gets or sets the minimum time between two rounds of renders that changes cause, in milliseconds.
    /// Changes within this window render each affected component once. Default is 33 (~30 rounds a second).
    /// </summary>
    public int ThrottleMilliseconds { get; set; } = 33;
}
