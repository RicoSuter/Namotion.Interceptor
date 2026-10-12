using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
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
    /// Gets whether a page expression may show the value of the property: an intercepted (partial) or a derived property
    /// that is no secret configuration property and is not marked with <see cref="ExcludeFromExpressionsAttribute"/>.
    /// </summary>
    /// <remarks>
    /// A plain public property is registered as well, but it is no tracked value of the subject and can hold internal
    /// data, such as the text of an answer while an agent writes it, so it is not shown.
    /// </remarks>
    public static bool IsDisplayable(this RegisteredSubjectProperty property)
    {
        if (property.GetConfigurationMetadata()?.IsSecret == true)
        {
            return false;
        }

        var isDerived = false;
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ExcludeFromExpressionsAttribute)
            {
                return false;
            }

            isDerived |= attribute is DerivedAttribute;
        }

        return isDerived || property.Reference.Metadata.IsIntercepted;
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
