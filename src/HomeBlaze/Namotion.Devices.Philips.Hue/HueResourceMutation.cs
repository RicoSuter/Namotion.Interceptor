using System.Text.Json;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Changes a Hue SDK resource in a way the interception layer can see, used by the operations to
/// reflect a command locally once the bridge has accepted it.
///
/// The SDK resources are plain mutable DTOs held behind an intercepted property. Mutating one in
/// place (<c>LightResource.On.IsOn = true</c>) changes no intercepted property, so nothing recomputed
/// the derived state, nothing notified the UI and nothing recorded a history sample: the change only
/// appeared once the bridge echoed it back over the event stream. Re-assigning the same instance does
/// not help either, because the equality check vetoes a write of the value already there. So the
/// change is applied to a copy and the copy is assigned, which is what the event and poll paths do.
/// </summary>
internal static class HueResourceMutation
{
    /// <summary>
    /// Returns a copy of <paramref name="resource"/> with <paramref name="apply"/> applied to it.
    /// The copy goes through JSON because the SDK models offer no copy support. It is faithful because
    /// the same serializer options that produced the resource reproduce it: anything the bridge sent
    /// that the model could not represent was already dropped when the SDK deserialized the response.
    ///
    /// <typeparamref name="T"/> is bound statically, so calling this through a base-typed reference
    /// would silently drop the derived members. Every call site passes the concrete resource type.
    /// </summary>
    public static T With<T>(T resource, Action<T> apply)
    {
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(resource))!;
        apply(copy);
        return copy;
    }
}
