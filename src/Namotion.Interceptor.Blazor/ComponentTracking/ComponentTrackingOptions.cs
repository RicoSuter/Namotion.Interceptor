namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Options of per-component change tracking, see <see cref="ComponentTrackingServiceCollectionExtensions.AddComponentTracking"/>.
/// </summary>
public sealed class ComponentTrackingOptions
{
    /// <summary>
    /// Gets or sets the minimum time between two rounds of renders that changes cause, in milliseconds.
    /// Each round renders each affected component once, so a burst of changes renders a component at most twice: at its
    /// start and at the end of the interval. Default is 33 (~30 rounds a second).
    /// </summary>
    public int ThrottleMilliseconds { get; set; } = 33;
}
