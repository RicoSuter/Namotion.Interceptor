using System.Text.Json;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Changes a Hue SDK resource in a way the interception layer can see, used by the operations to
/// reflect a command locally once the bridge has accepted it.
///
/// The SDK resources are plain mutable DTOs held behind an intercepted property. Mutating one in
/// place (<c>LightResource.On.IsOn = true</c>) changes no intercepted property, so nothing recomputed
/// the derived state, nothing notified the UI and nothing recorded a history sample: the change only
/// appeared after the next bridge poll, up to a minute later. Re-assigning the same instance does not
/// help either, because the equality check vetoes a write of the value already there. So the change is
/// applied to a copy and the copy is assigned, which is exactly what the poll path does.
/// </summary>
internal static class HueResourceMutation
{
    /// <summary>
    /// Returns a copy of <paramref name="resource"/> with <paramref name="apply"/> applied to it.
    /// The copy goes through JSON because the SDK models offer no copy support; they are flat DTOs
    /// with settable properties and an extension-data bucket, so nothing is lost in the round trip.
    /// </summary>
    public static T With<T>(T resource, Action<T> apply)
    {
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(resource))!;
        apply(copy);
        return copy;
    }
}
