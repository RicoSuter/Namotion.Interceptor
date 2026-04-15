using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Manages shared host assembly paths across all NuGetPluginLoader instances.
/// Uses ref-counting per assembly name so that disposal only removes an assembly
/// path when no other loader instance still references it.
/// </summary>
internal class SharedHostAssemblyRegistry : IDisposable
{
    private static readonly Lock StaticLock = new();
    private static readonly ConcurrentDictionary<string, (string Path, int RefCount)> SharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);

    private static int _instanceCount;

    private readonly HashSet<string> _ownedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
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
            SharedAssemblyPaths.AddOrUpdate(
                assemblyName,
                _ => (path, 1),
                (_, existing) => (existing.Path, existing.RefCount + 1));
        }

        _ownedAssemblyNames.Add(assemblyName);
    }

    private static Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name != null &&
            SharedAssemblyPaths.TryGetValue(assemblyName.Name, out var entry))
        {
            return context.LoadFromAssemblyPath(entry.Path);
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
                foreach (var name in _ownedAssemblyNames)
                {
                    if (SharedAssemblyPaths.TryGetValue(name, out var entry))
                    {
                        if (entry.RefCount <= 1)
                        {
                            SharedAssemblyPaths.TryRemove(name, out _);
                        }
                        else
                        {
                            SharedAssemblyPaths[name] = (entry.Path, entry.RefCount - 1);
                        }
                    }
                }
            }

            _ownedAssemblyNames.Clear();

            lock (StaticLock)
            {
                if (--_instanceCount == 0)
                {
                    AssemblyLoadContext.Default.Resolving -= OnDefaultContextResolving;
                }
            }
        }
    }
}
