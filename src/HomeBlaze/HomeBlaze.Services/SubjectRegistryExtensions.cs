using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// HomeBlaze-specific extension methods for the interceptor registry.
/// Uses registry attribute queries instead of .NET reflection for better performance.
/// </summary>
public static class SubjectRegistryExtensions
{
    /// <summary>
    /// Gets all properties marked with [Configuration] attribute.
    /// </summary>
    public static IEnumerable<RegisteredSubjectProperty> GetConfigurationProperties(
        this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            yield break;

        foreach (var property in registered.Properties)
        {
            if (property.IsConfigurationProperty())
                yield return property;
        }
    }

    /// <summary>
    /// Gets all properties marked with [State] attribute.
    /// </summary>
    public static IEnumerable<RegisteredSubjectProperty> GetStateProperties(
        this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            yield break;

        foreach (var property in registered.Properties)
        {
            if (property.GetStateMetadata() != null)
                yield return property;
        }
    }

    /// <summary>
    /// Checks if a property has [Configuration] attribute via registry lookup.
    /// </summary>
    public static bool IsConfigurationProperty(this RegisteredSubjectProperty property)
    {
        return property.TryGetAttribute(KnownAttributes.Configuration) != null;
    }

    /// <summary>
    /// Gets the <see cref="StateMetadata"/> for a property, or null if not present.
    /// Uses registry attribute lookup instead of reflection.
    /// </summary>
    public static StateMetadata? GetStateMetadata(this RegisteredSubjectProperty property)
    {
        return property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
    }

    /// <summary>
    /// Gets the <see cref="ConfigurationMetadata"/> for a property, or null if not present.
    /// Uses registry attribute lookup instead of reflection.
    /// </summary>
    public static ConfigurationMetadata? GetConfigurationMetadata(this RegisteredSubjectProperty property)
    {
        return property.TryGetAttribute(KnownAttributes.Configuration)?.GetValue() as ConfigurationMetadata;
    }

    /// <summary>
    /// Gets the display name for a property: its <see cref="StateMetadata"/> title, else the property name.
    /// </summary>
    public static string GetDisplayName(this RegisteredSubjectProperty property)
    {
        var metadata = property.GetStateMetadata();

        if (!string.IsNullOrEmpty(metadata?.Title))
            return metadata.Title;

        return property.Name;
    }

    /// <summary>
    /// Gets the display position for a property (from StateMetadata).
    /// </summary>
    public static int GetDisplayPosition(this RegisteredSubjectProperty property)
    {
        return property.GetStateMetadata()?.Position ?? int.MaxValue;
    }

    /// <summary>
    /// Gets the category that the UI groups a state property under, from its <see cref="CategoryAttribute"/>.
    /// The attribute of the class property wins over the one of an interface property, also when its name is empty.
    /// </summary>
    /// <returns>The trimmed name, or null when the property has no category.</returns>
    /// <remarks>
    /// Not for the registry property of a method: read <see cref="MethodMetadata.Category"/> instead.
    /// </remarks>
    public static string? GetCategory(this RegisteredSubjectProperty property)
    {
        var attributes = property.ReflectionAttributes;
        if (attributes is not Attribute[] array)
        {
            return attributes.OfType<CategoryAttribute>().FirstOrDefault().GetNormalizedCategory();
        }

        // Runs for every value of a subject on each render, and enumerating the collection would allocate.
        foreach (var attribute in array)
        {
            if (attribute is CategoryAttribute category)
            {
                return category.GetNormalizedCategory();
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a subject has any [Configuration] properties.
    /// </summary>
    public static bool HasConfigurationProperties(this IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return false;

        foreach (var property in registered.Properties)
        {
            if (property.IsConfigurationProperty())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the configurable subject whose configuration contains the subject: the subject itself when it is
    /// configurable, else the nearest configurable parent that holds it through [Configuration] properties only.
    /// </summary>
    /// <returns>Null when the subject is part of no configuration, such as a file in a storage.</returns>
    public static IInterceptorSubject? TryGetConfigurationOwner(this IInterceptorSubject subject)
    {
        return subject.TryGetConfigurationOwner(static candidate => candidate is IConfigurable);
    }

    /// <summary>
    /// Finds the nearest subject that matches the predicate and whose configuration contains this subject: the
    /// subject itself when it matches, else a parent that holds it through [Configuration] properties only.
    /// </summary>
    /// <param name="subject">The subject to start from.</param>
    /// <param name="isOwner">Decides whether a subject is the owner that is looked for.</param>
    /// <returns>Null when there is none.</returns>
    public static IInterceptorSubject? TryGetConfigurationOwner(
        this IInterceptorSubject subject,
        Func<IInterceptorSubject, bool> isOwner)
    {
        if (isOwner(subject))
            return subject;

        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance) { subject };
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(subject);

        while (queue.TryDequeue(out var current))
        {
            foreach (var parent in current.GetParents())
            {
                // Only [Configuration] properties are serialized, so a parent beyond any other property does not
                // persist the subject.
                if (parent.Property.TryGetRegisteredProperty()?.IsConfigurationProperty() != true)
                    continue;

                var parentSubject = parent.Property.Subject;
                if (!visited.Add(parentSubject))
                    continue;

                if (isOwner(parentSubject))
                    return parentSubject;

                queue.Enqueue(parentSubject);
            }
        }

        return null;
    }
}
