using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that lets the store that keeps the metadata of a subject (see
/// <see cref="SubjectMetadataExtensions.FindMetadataStore"/>) apply it when the subject attaches, and apply the
/// metadata of each property that is added to the subject later.
/// </summary>
// After the method properties are added, so that applying the subject covers them.
[RunsAfter(typeof(MethodPropertyInitializer))]
public sealed class MetadataLifecycleHandler : ILifecycleHandler, IPropertyLifecycleHandler
{
    // The properties of the subject that attached last on this thread, after it was applied. The lifecycle attaches
    // each property that a subject has when it attaches after the subject itself, and those were applied with it. A
    // property that is added later replaces the properties of its subject, so it never matches. Weak, so that the
    // subject that attached last is not kept.
    [ThreadStatic]
    private static WeakReference<IReadOnlyDictionary<string, SubjectPropertyMetadata>>? _appliedProperties;

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            Apply(change.Subject, null);
            var properties = change.Subject.Properties;
            (_appliedProperties ??= new WeakReference<IReadOnlyDictionary<string, SubjectPropertyMetadata>>(properties)).SetTarget(properties);
        }
    }

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        // A property that the subject is created with, and a method, are applied with the subject; the metadata itself
        // and the attributes have no metadata of their own.
        var metadata = change.Property.Metadata;
        var propertyName = change.Property.Name;
        if (metadata.IsDynamic && metadata.Type != typeof(MethodMetadata) &&
            propertyName != KnownAttributes.Metadata && !propertyName.Contains('@') &&
            !IsAppliedWithSubject(change.Subject))
        {
            Apply(change.Subject, propertyName);
        }
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    private static bool IsAppliedWithSubject(IInterceptorSubject subject)
        => _appliedProperties is not null && _appliedProperties.TryGetTarget(out var applied) && ReferenceEquals(subject.Properties, applied);

    private static void Apply(IInterceptorSubject subject, string? propertyName)
    {
        // A store applies its own metadata from its own file.
        if (subject.FindMetadataStore() is { } store && !ReferenceEquals(store, subject))
        {
            store.ApplyStoredMetadata(subject, propertyName);
        }
    }
}
