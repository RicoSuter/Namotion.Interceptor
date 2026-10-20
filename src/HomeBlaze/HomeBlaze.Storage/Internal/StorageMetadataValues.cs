using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Sets the metadata of a subject, in its dynamic property <see cref="KnownAttributes.Metadata"/>, and of a property,
/// in its attribute of that name. Neither has a setter, so the values change only here.
/// </summary>
internal static class StorageMetadataValues
{
    // The key in the data of a subject, by the name of the metadata property, of the value that this class added. On
    // the subject, as the registry builds new properties when a subject attaches again after a detach.
    private const string ValueKey = "HomeBlaze.MetadataValue";

    /// <summary>
    /// Sets the metadata of the subject, or of its property <paramref name="propertyName"/>. The property or attribute
    /// is added on the first value that is not null and stays, as the registry cannot remove a property, so null is set
    /// to remove the metadata. The same dictionary again changes nothing.
    /// </summary>
    /// <remarks>
    /// Holds the <see cref="IInterceptorSubject.SyncRoot"/> of the subject, which adding a property takes as well, as
    /// adding a property twice would register it twice. A caller that reads the values from state that another thread
    /// changes reads them while it holds the same lock. The change is published while the lock is held, so whatever
    /// handles it synchronously must not write a property that holds subjects: that takes the lifecycle lock of the
    /// context, while a thread that attaches a subject holds the lifecycle lock and waits for this one.
    /// </remarks>
    public static void Set(RegisteredSubject registeredSubject, string? propertyName, ImmutableDictionary<string, JsonElement>? values)
    {
        var name = propertyName is null ? KnownAttributes.Metadata : $"{propertyName}@{KnownAttributes.Metadata}";
        lock (registeredSubject.Subject.SyncRoot)
        {
            // The registration of the caller is stale when the subject was detached and attached again since it was read,
            // and a property added to it would be missing from the current one.
            if (registeredSubject.Subject.TryGetRegisteredSubject() is not { } subject)
            {
                return;
            }

            if (subject.Subject.Data.TryGetValue((name, ValueKey), out var data))
            {
                var value = (StrongBox<ImmutableDictionary<string, JsonElement>?>)data!;
                if (!ReferenceEquals(value.Value, values) && subject.TryGetProperty(name) is { } property)
                {
                    property.Reference.SetPropertyValueWithInterception(
                        values, value.Value, (_, newValue) => value.Value = (ImmutableDictionary<string, JsonElement>?)newValue);
                }
            }
            else if (values is not null && subject.TryGetProperty(name) is null)
            {
                // A property of that name that the subject has of its own is left alone.
                Add(subject, name, propertyName, values);
            }
        }
    }

    private static void Add(RegisteredSubject subject, string name, string? propertyName, ImmutableDictionary<string, JsonElement> values)
    {
        var value = new StrongBox<ImmutableDictionary<string, JsonElement>?>(values);
        if (propertyName is null)
        {
            subject.AddProperty(KnownAttributes.Metadata, typeof(ImmutableDictionary<string, JsonElement>), _ => value.Value, null);
        }
        else if (subject.TryGetProperty(propertyName) is { IsAttribute: false } attributedProperty)
        {
            attributedProperty.AddAttribute(KnownAttributes.Metadata, typeof(ImmutableDictionary<string, JsonElement>), _ => value.Value, null);
        }
        else
        {
            return;
        }

        subject.Subject.Data[(name, ValueKey)] = value;
    }
}
