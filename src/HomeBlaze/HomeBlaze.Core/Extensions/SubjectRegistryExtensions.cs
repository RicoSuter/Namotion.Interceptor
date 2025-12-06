using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Core.Extensions;

/// <summary>
/// HomeBlaze-specific extension methods for the interceptor registry.
/// Uses registry APIs instead of .NET reflection for better performance.
/// </summary>
public static partial class SubjectRegistryExtensions
{
    // Cache by (SubjectType, PropertyName) -> attribute info
    // All instances of same subject type have identical attributes
    private static readonly ConcurrentDictionary<(Type, string), bool>
        _isConfigurationPropertyCache = new();

    private static readonly ConcurrentDictionary<(Type, string), StateAttribute?>
        _stateAttributeCache = new();

    private static readonly ConcurrentDictionary<string, string>
        _camelCaseCache = new();

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
            if (property.GetStateAttribute() != null)
                yield return property;
        }
    }

    /// <summary>
    /// Checks if a property has [Configuration] attribute.
    /// Uses cached lookup by (Type, PropertyName) for O(1) performance after first call.
    /// </summary>
    public static bool IsConfigurationProperty(this RegisteredSubjectProperty property)
    {
        var key = (property.Subject.GetType(), property.Name);
        return _isConfigurationPropertyCache.GetOrAdd(key, _ =>
        {
            foreach (var attr in property.ReflectionAttributes)
            {
                if (attr is ConfigurationAttribute)
                    return true;
            }
            return false;
        });
    }

    /// <summary>
    /// Gets the [State] attribute for a property, or null if not present.
    /// Uses cached lookup by (Type, PropertyName).
    /// </summary>
    public static StateAttribute? GetStateAttribute(this RegisteredSubjectProperty property)
    {
        var key = (property.Subject.GetType(), property.Name);
        return _stateAttributeCache.GetOrAdd(key, _ =>
        {
            foreach (var attr in property.ReflectionAttributes)
            {
                if (attr is StateAttribute sa)
                    return sa;
            }
            return null;
        });
    }

    /// <summary>
    /// Gets the display name for a property (from StateAttribute or camelCase split).
    /// </summary>
    public static string GetDisplayName(this RegisteredSubjectProperty property)
    {
        var stateAttr = property.GetStateAttribute();
        if (!string.IsNullOrEmpty(stateAttr?.Name))
            return stateAttr.Name;

        return SplitCamelCase(property.Name);
    }

    /// <summary>
    /// Gets the display order for a property (from StateAttribute).
    /// </summary>
    public static int GetDisplayOrder(this RegisteredSubjectProperty property)
    {
        return property.GetStateAttribute()?.Order ?? int.MaxValue;
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
        return _camelCaseCache.GetOrAdd(input, s => CamelCaseRegex().Replace(s, "$1 $2"));
    }
}
