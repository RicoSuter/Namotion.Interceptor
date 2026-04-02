using System.Reflection;
using System.Runtime.Loader;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Represents a loaded plugin package and its assemblies within an isolated <see cref="AssemblyLoadContext"/>.
/// </summary>
public class NuGetPluginGroup : IDisposable
{
    private readonly AssemblyLoadContext? _loadContext;

    internal NuGetPluginGroup(
        string packageName,
        string packageVersion,
        IReadOnlyList<Assembly> assemblies,
        AssemblyLoadContext? loadContext)
    {
        PackageName = packageName;
        PackageVersion = packageVersion;
        Assemblies = assemblies;
        _loadContext = loadContext;
    }

    /// <summary>
    /// Gets the NuGet package identifier for this plugin group.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the normalized version string of the loaded package.
    /// </summary>
    public string PackageVersion { get; }

    /// <summary>
    /// Gets the assemblies loaded for this plugin group.
    /// </summary>
    public IReadOnlyList<Assembly> Assemblies { get; }

    /// <summary>
    /// Gets all non-abstract, non-interface types assignable to <typeparamref name="T"/> from the plugin's assemblies.
    /// </summary>
    public IEnumerable<Type> GetTypes<T>()
    {
        return GetTypes(type => typeof(T).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface);
    }

    /// <summary>
    /// Gets all types matching the predicate from the plugin's assemblies.
    /// </summary>
    public IEnumerable<Type> GetTypes(Func<Type, bool> predicate)
    {
        return Assemblies
            .SelectMany(assembly =>
            {
                try { return assembly.ExportedTypes; }
                catch { return []; }
            })
            .Where(predicate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_loadContext?.IsCollectible == true)
        {
            _loadContext.Unload();
        }
    }
}
