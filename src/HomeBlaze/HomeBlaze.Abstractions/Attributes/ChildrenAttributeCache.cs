using System.Collections.Concurrent;
using System.Reflection;

namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Static cache for [Children] attribute property name lookups.
/// Caches per-type to avoid repeated reflection. Shared across all consumers.
/// </summary>
public static class ChildrenAttributeCache
{
    private static readonly ConcurrentDictionary<Type, string?> _cache = new();

    /// <summary>
    /// Gets the property name marked with [Children] for the given type, or null if none.
    /// </summary>
    public static string? GetChildrenPropertyName(Type type)
    {
        return _cache.GetOrAdd(type, t =>
            t.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<ChildrenAttribute>() != null)
                ?.Name);
    }

    /// <summary>
    /// Returns true if the specified property has the [Children] attribute.
    /// </summary>
    public static bool IsChildrenProperty(Type type, string propertyName)
    {
        return GetChildrenPropertyName(type) == propertyName;
    }
}
