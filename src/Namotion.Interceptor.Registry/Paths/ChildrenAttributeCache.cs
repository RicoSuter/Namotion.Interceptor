using System.Collections.Concurrent;
using System.Reflection;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Caches the property name marked with [Children] attribute for each type.
/// </summary>
public static class ChildrenAttributeCache
{
    private static readonly ConcurrentDictionary<Type, string?> Cache = new();

    /// <summary>
    /// Gets the name of the property marked with [Children] attribute for the specified type.
    /// </summary>
    /// <param name="type">The type to search for a [Children] property.</param>
    /// <returns>The property name, or null if no property is marked with [Children].</returns>
    public static string? GetChildrenPropertyName(Type type)
    {
        return Cache.GetOrAdd(type, static t =>
        {
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<ChildrenAttribute>() is not null)
                {
                    return property.Name;
                }
            }
            return null;
        });
    }
}
