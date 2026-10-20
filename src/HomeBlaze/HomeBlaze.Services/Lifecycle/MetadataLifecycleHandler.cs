using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that lets the store that keeps the metadata of a subject (see
/// <see cref="SubjectMetadataExtensions.FindMetadataStore"/>) apply it when the subject attaches, and apply the
/// metadata of each property that is added to the subject later.
/// </summary>
public sealed class MetadataLifecycleHandler : ILifecycleHandler, IPropertyLifecycleHandler
{
    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            Apply(change.Subject, null);
        }
    }

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        // A property that the subject is created with is applied with the subject; the metadata itself and the
        // attributes have no metadata of their own.
        var propertyName = change.Property.Name;
        if (change.Property.Metadata.IsDynamic && propertyName != KnownAttributes.Metadata && !propertyName.Contains('@'))
        {
            Apply(change.Subject, propertyName);
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    private static void Apply(IInterceptorSubject subject, string? propertyName)
    {
        // A store keeps the metadata of the subjects below it, not its own.
        if (subject.FindMetadataStore() is { } store && !ReferenceEquals(store, subject))
        {
            store.ApplyStoredMetadata(subject, propertyName);
        }
    }
}
