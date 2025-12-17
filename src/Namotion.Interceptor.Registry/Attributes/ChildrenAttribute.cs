using System.Collections.Concurrent;
using System.Reflection;

namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Marks a dictionary property as the default child container for path resolution.
/// Child keys become directly accessible in paths without the property name.
/// </summary>
/// <remarks>
/// When a path segment cannot be resolved to a direct property, the path resolver
/// will fall back to looking up the segment as a key in the [Children] dictionary.
/// Only one property per class should be marked with this attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ChildrenAttribute : Attribute
{
    private static readonly ConcurrentDictionary<Type, string?> Cache = new();

    /// <summary>
    /// Gets the property name marked with [Children] for the given type, or null if none.
    /// </summary>
    public static string? GetChildrenPropertyName(Type type)
    {
        return Cache.GetOrAdd(type, t =>
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
