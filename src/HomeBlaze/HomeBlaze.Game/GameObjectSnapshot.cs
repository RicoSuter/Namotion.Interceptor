using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Game;

/// <summary>
/// The values of the writable configuration and state properties of an object, which <see cref="Restore"/> writes back.
/// An object that the registry does not know captures nothing.
/// </summary>
internal sealed class GameObjectSnapshot
{
    private readonly List<(RegisteredSubjectProperty Property, object? Value)> _values = [];

    private GameObjectSnapshot()
    {
    }

    public static GameObjectSnapshot Capture(GameObject gameObject)
    {
        var snapshot = new GameObjectSnapshot();
        if (gameObject.TryGetRegisteredSubject() is { } registered)
        {
            foreach (var property in registered.Properties)
            {
                if (IsRestorable(property))
                {
                    snapshot._values.Add((property, property.GetValue()));
                }
            }
        }

        return snapshot;
    }

    public void Restore()
    {
        foreach (var (property, value) in _values)
        {
            property.SetValue(value);
        }
    }

    private static bool IsRestorable(RegisteredSubjectProperty property) =>
        !property.IsAttribute && property.HasSetter && IsConfigurationOrState(property);

    private static bool IsConfigurationOrState(RegisteredSubjectProperty property) =>
        property.TryGetAttribute(KnownAttributes.Configuration) is not null || property.TryGetAttribute(KnownAttributes.State) is not null;
}
