using System.Collections.Concurrent;
using System.Reflection;

namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Marks a dictionary property as a transparent container for path resolution.
/// Dictionary keys become directly accessible as path segments without the property name.
/// </summary>
/// <remarks>
/// When a path segment cannot be resolved to a direct property, the path resolver
/// will fall back to looking up the segment as a key in the [InlinePaths] dictionary.
/// Only one property per class should be marked with this attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class InlinePathsAttribute : Attribute
{
    private static readonly ConcurrentDictionary<Type, string?> Cache = new();

    /// <summary>
    /// Gets the property name marked with [InlinePaths] for the given type, or null if none.
    /// </summary>
    public static string? GetInlinePathsPropertyName(Type type)
    {
        return Cache.GetOrAdd(type, t =>
            t.GetProperties()
                .FirstOrDefault(p => p.GetCustomAttribute<InlinePathsAttribute>() != null)
                ?.Name);
    }

    /// <summary>
    /// Returns true if the specified property has the [InlinePaths] attribute.
    /// </summary>
    public static bool IsInlinePathsProperty(Type type, string propertyName)
    {
        return GetInlinePathsPropertyName(type) == propertyName;
    }
}
