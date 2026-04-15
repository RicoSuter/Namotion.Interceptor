using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Manages shared host assembly paths across all NuGetPluginLoader instances.
/// Uses ref-counting to register/unregister the AssemblyLoadContext.Default.Resolving handler.
/// </summary>
internal class SharedHostAssemblyRegistry : IDisposable
{
    private static readonly Lock StaticLock = new();
    private static readonly ConcurrentDictionary<string, string> SharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);

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
        SharedAssemblyPaths[assemblyName] = path;
        _ownedAssemblyNames.Add(assemblyName);
    }

    private static Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name != null &&
            SharedAssemblyPaths.TryGetValue(assemblyName.Name, out var path))
        {
            return context.LoadFromAssemblyPath(path);
        }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            foreach (var name in _ownedAssemblyNames)
            {
                SharedAssemblyPaths.TryRemove(name, out _);
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
