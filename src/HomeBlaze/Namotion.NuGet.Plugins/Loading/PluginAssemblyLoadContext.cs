using System.Reflection;
using System.Runtime.Loader;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// AssemblyLoadContext for a plugin group. Falls back to the default context
/// for host-classified assemblies; loads private dependencies from disk.
/// </summary>
internal class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly HashSet<string> _hostAssemblyNames;
    private readonly Dictionary<string, string> _privateAssemblyPaths;

    public PluginAssemblyLoadContext(
        string pluginName,
        HashSet<string> hostAssemblyNames,
        Dictionary<string, string> privateAssemblyPaths)
        : base(pluginName, isCollectible: true)
    {
        _hostAssemblyNames = hostAssemblyNames;
        _privateAssemblyPaths = privateAssemblyPaths;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Host assembly: return null to fall back to default context
        if (assemblyName.Name != null && _hostAssemblyNames.Contains(assemblyName.Name))
        {
            return null;
        }

        // Private assembly: load from disk
        if (assemblyName.Name != null &&
            _privateAssemblyPaths.TryGetValue(assemblyName.Name, out var path) &&
            File.Exists(path))
        {
            return LoadFromAssemblyPath(path);
        }

        // Not found: fall back to default
        return null;
    }
}
