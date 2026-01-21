using System.Collections.Concurrent;
using System.ComponentModel;

namespace Namotion.Interceptor;

/// <summary>
/// Caches <see cref="PropertyChangedEventArgs"/> instances to avoid repeated allocations
/// when raising PropertyChanged events for the same property name.
/// </summary>
public static class PropertyChangedEventArgsCache
{
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> _cache = new();

    /// <summary>
    /// Gets a cached <see cref="PropertyChangedEventArgs"/> for the specified property name.
    /// Creates and caches a new instance if one doesn't exist.
    /// </summary>
    /// <param name="propertyName">The name of the property that changed.</param>
    /// <returns>A cached <see cref="PropertyChangedEventArgs"/> instance.</returns>
    public static PropertyChangedEventArgs Get(string propertyName) =>
        _cache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
}
