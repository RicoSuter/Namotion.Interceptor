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
        {
            var matches = t.GetProperties()
                .Where(p => p.GetCustomAttribute<InlinePathsAttribute>() != null)
                .ToList();

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Type '{t.FullName}' has multiple [InlinePaths] properties " +
                    $"({string.Join(", ", matches.Select(p => p.Name))}). " +
                    $"Only one property per type may be marked with [InlinePaths].");
            }

            return matches.FirstOrDefault()?.Name;
        });
    }

    /// <summary>
    /// Returns true if the specified property has the [InlinePaths] attribute.
    /// </summary>
    public static bool IsInlinePathsProperty(Type type, string propertyName)
    {
        return GetInlinePathsPropertyName(type) == propertyName;
    }
}
