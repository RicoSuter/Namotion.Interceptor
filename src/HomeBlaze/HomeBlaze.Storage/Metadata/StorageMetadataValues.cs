using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// Sets the metadata of a subject, in its dynamic property <see cref="KnownAttributes.Metadata"/>, and of a property,
/// in its attribute of that name. Neither has a setter, so the values change only here.
/// </summary>
internal static class StorageMetadataValues
{
    // The key in the data of a subject, by the name of the metadata property, of the value that this class added. On
    // the subject, as the registry builds new properties when a subject attaches again after a detach.
    private const string ValueKey = "HomeBlaze.MetadataValue";

    // Serializes the writers of the metadata of a subject. Its own and not the SyncRoot of the subject, which the library
    // takes as well, for instance while it adds a property.
    private static readonly ConditionalWeakTable<IInterceptorSubject, object> Locks = new();

    /// <summary>
    /// Sets the metadata of the subject, or of its property <paramref name="propertyName"/>. The property or attribute
    /// is added on the first value that is not null and stays, as the registry cannot remove a property, so null is set
    /// to remove the metadata. A dictionary with the same keys and values changes nothing, and so does a registration
    /// that is no longer the current one of the subject.
    /// </summary>
    /// <remarks>
    /// Holds the lock of the subject, see <see cref="GetLock"/>, as two first values at once would add the property
    /// twice, and the registry does not check that. A caller that reads the values from state that another thread
    /// changes reads them while it holds the same lock. The change is published while the lock is held, so whatever
    /// handles it synchronously must not write a property that holds subjects: that takes the lifecycle lock of the
    /// context, while a thread that attaches a subject holds the lifecycle lock and waits for this one.
    /// </remarks>
    public static void Set(RegisteredSubject subject, string? propertyName, ImmutableDictionary<string, JsonElement>? values)
    {
        var name = propertyName is null ? KnownAttributes.Metadata : $"{propertyName}@{KnownAttributes.Metadata}";
        lock (GetLock(subject.Subject))
        {
            // The registration of the caller is stale when the subject was detached and attached again since it was read,
            // or detached for good. Its values may be those of its previous place; the attach applies the current ones.
            if (!ReferenceEquals(subject.Subject.TryGetRegisteredSubject(), subject))
            {
                return;
            }

            if (subject.Subject.Data.TryGetValue((name, ValueKey), out var data))
            {
                var value = (StrongBox<ImmutableDictionary<string, JsonElement>?>)data!;
                if (!HaveSameContent(value.Value, values) && subject.TryGetProperty(name) is { } property)
                {
                    subject.Subject.Executor.SetPropertyValue(
                        property.Reference.Name, values, value.Value, (_, newValue) => value.Value = newValue);
                }
            }
            else if (values is not null && subject.TryGetProperty(name) is null)
            {
                // A property of that name that the subject has of its own is left alone.
                Add(subject, name, propertyName, values);
            }
        }
    }

    /// <summary>Gets the lock that <see cref="Set"/> holds for the subject, the same instance on each call.</summary>
    public static object GetLock(IInterceptorSubject subject)
        => Locks.GetValue(subject, static _ => new object());

    /// <summary>
    /// Sets the own metadata of a folder or of the root of a storage from its <c>.metadata.json</c>, or none without one,
    /// and gets the order of its children from it; null when it equals <paramref name="childOrder"/>, the order it has.
    /// </summary>
    public static ImmutableArray<string>? SetOwnMetadata(IInterceptorSubject container, ImmutableArray<string> childOrder, StorageMetadataFile? file)
    {
        if (container.TryGetRegisteredSubject() is { } registered)
        {
            Set(registered, null, file?.Own);
        }

        var order = file?.Order ?? [];
        return order.SequenceEqual(childOrder) ? null : order;
    }

    // The same keys with values that are equal as JSON; a file that is read again gives new instances of the entries
    // that did not change.
    private static bool HaveSameContent(ImmutableDictionary<string, JsonElement>? current, ImmutableDictionary<string, JsonElement>? values)
    {
        if (ReferenceEquals(current, values))
        {
            return true;
        }

        if (current is null || values is null || current.Count != values.Count)
        {
            return false;
        }

        foreach (var (key, value) in current)
        {
            if (!values.TryGetValue(key, out var otherValue) || !JsonElement.DeepEquals(value, otherValue))
            {
                return false;
            }
        }

        return true;
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
