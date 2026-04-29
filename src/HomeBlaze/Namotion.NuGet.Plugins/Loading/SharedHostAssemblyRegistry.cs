using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Manages shared host assembly paths across all NuGetPluginLoader instances.
/// Each instance tracks the paths it registered. On disposal, only those paths
/// are removed. The Resolving handler tries all registered paths for a given
/// assembly name until one succeeds.
/// When using multiple loader instances, sharing a single CacheDirectory is
/// recommended to avoid redundant downloads.
/// </summary>
internal class SharedHostAssemblyRegistry : IDisposable
{
    private static readonly Lock StaticLock = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> SharedAssemblyPaths
        = new(StringComparer.OrdinalIgnoreCase);

    private static int _instanceCount;

    private readonly HashSet<string> _ownedPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public SharedHostAssemblyRegistry()
    {
        lock (StaticLock)
        {
            if (++_instanceCount == 1)
            {
                AssemblyLoadContext.Default.Resolving += OnDefaultContextResolving;
            }
        }
    }

    public void AddAssemblyPath(string assemblyName, string path)
    {
        lock (StaticLock)
        {
            var paths = SharedAssemblyPaths.GetOrAdd(
                assemblyName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            paths.Add(path);
            _ownedPaths.Add(path);
        }
    }

    private static Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name != null &&
            SharedAssemblyPaths.TryGetValue(assemblyName.Name, out var paths))
        {
            string[] snapshot;
            lock (StaticLock)
            {
                snapshot = [.. paths];
            }

            foreach (var path in snapshot)
            {
                if (File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            lock (StaticLock)
            {
                foreach (var (assemblyName, paths) in SharedAssemblyPaths)
                {
                    paths.ExceptWith(_ownedPaths);
                    if (paths.Count == 0)
                    {
                        SharedAssemblyPaths.TryRemove(assemblyName, out _);
                    }
                }

                _ownedPaths.Clear();

                if (--_instanceCount == 0)
                {
                    AssemblyLoadContext.Default.Resolving -= OnDefaultContextResolving;
                }
            }
        }
    }
}
