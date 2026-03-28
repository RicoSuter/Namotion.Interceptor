using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// HomeBlaze-specific extension methods for the interceptor registry.
/// Uses registry attribute queries instead of .NET reflection for better performance.
/// </summary>
public static partial class SubjectRegistryExtensions
{
    private static readonly ConcurrentDictionary<string, string>
        CamelCaseCache = new();

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
    /// Gets the display name for a property (from StateMetadata or camelCase split).
    /// </summary>
    public static string GetDisplayName(this RegisteredSubjectProperty property)
    {
        var metadata = property.GetStateMetadata();
        if (!string.IsNullOrEmpty(metadata?.Title))
            return metadata.Title;

        return SplitCamelCase(property.Name);
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

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();

    private static string SplitCamelCase(string input)
    {
        return CamelCaseCache.GetOrAdd(input, s => CamelCaseRegex().Replace(s, "$1 $2"));
    }
}
