using System.Runtime.CompilerServices;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Reads and writes the navigation metadata of a subject, which the registry holds in the dynamic property
/// <see cref="SubjectMetadata.PropertyName"/>.
/// </summary>
public static class SubjectMetadataExtensions
{
    /// <summary>
    /// Gets the navigation metadata of the subject, null when it has none or is not registered.
    /// </summary>
    public static SubjectMetadata? TryGetSubjectMetadata(this IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()?.TryGetProperty(SubjectMetadata.PropertyName)?.GetValue() as SubjectMetadata;

    /// <summary>
    /// Gets whether the property is the one that holds the navigation metadata of its subject.
    /// </summary>
    public static bool IsSubjectMetadata(this RegisteredSubjectProperty property)
        => property is { IsAttribute: false, Name: SubjectMetadata.PropertyName };

    /// <summary>
    /// Sets the navigation metadata of a registered subject. The property is added on the first value that is not
    /// null and stays from then on, as the registry cannot remove a property, so null is set to remove the metadata.
    /// Does nothing for a subject that is not registered.
    /// </summary>
    /// <remarks>Not thread-safe for the same subject. The storage that holds the subject calls it from its worker.</remarks>
    public static void SetSubjectMetadata(this IInterceptorSubject subject, SubjectMetadata? metadata)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered is null)
        {
            return;
        }

        var property = registered.TryGetProperty(SubjectMetadata.PropertyName);
        if (property is null)
        {
            if (metadata is not null)
            {
                // Written only through the property, so that every change is tracked.
                var holder = new StrongBox<SubjectMetadata?>(metadata);
                registered.AddProperty<SubjectMetadata>(
                    SubjectMetadata.PropertyName,
                    _ => holder.Value,
                    (_, value) => holder.Value = value);
            }

            return;
        }

        if (!Equals(property.GetValue(), metadata))
        {
            property.SetValue(metadata);
        }
    }
}
