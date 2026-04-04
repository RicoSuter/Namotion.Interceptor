using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Represents a loaded plugin package and its assemblies within an isolated <see cref="AssemblyLoadContext"/>.
/// </summary>
public class NuGetPlugin : IDisposable
{
    private readonly AssemblyLoadContext? _loadContext;
    private IReadOnlyList<Assembly>? _assemblies;
    private bool _disposed;

    internal NuGetPlugin(
        string packageName,
        string packageVersion,
        IReadOnlyList<Assembly> assemblies,
        AssemblyLoadContext? loadContext,
        NuGetPackageMetadata? metadata = null,
        XDocument? nuspec = null,
        JsonElement? pluginManifest = null,
        IReadOnlyList<NuGetPluginDependency>? dependencies = null)
    {
        PackageName = packageName;
        PackageVersion = packageVersion;
        _assemblies = assemblies;
        Metadata = metadata ?? new NuGetPackageMetadata();
        Nuspec = nuspec;
        PluginManifest = pluginManifest;
        Dependencies = dependencies ?? [];
        _loadContext = loadContext;
    }

    /// <summary>
    /// Gets the NuGet package identifier for this plugin.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the normalized version string of the loaded package.
    /// </summary>
    public string PackageVersion { get; }

    /// <summary>
    /// Gets the assemblies loaded for this plugin.
    /// </summary>
    public IReadOnlyList<Assembly> Assemblies
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _assemblies!;
        }
    }

    /// <summary>
    /// Gets the package metadata extracted from the nuspec.
    /// </summary>
    public NuGetPackageMetadata Metadata { get; }

    /// <summary>
    /// Gets the raw nuspec document, if available.
    /// </summary>
    public XDocument? Nuspec { get; }

    /// <summary>
    /// Gets the parsed plugin.json manifest, if present in the package.
    /// </summary>
    public JsonElement? PluginManifest { get; }

    /// <summary>
    /// Gets the classified dependencies for this plugin.
    /// </summary>
    public IReadOnlyList<NuGetPluginDependency> Dependencies { get; }

    /// <summary>
    /// Gets all non-abstract, non-interface types assignable to <typeparamref name="T"/> from the plugin's assemblies.
    /// </summary>
    public IEnumerable<Type> GetTypes<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return GetTypes(type => typeof(T).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface);
    }

    /// <summary>
    /// Gets all types matching the predicate from the plugin's assemblies.
    /// </summary>
    public IEnumerable<Type> GetTypes(Func<Type, bool> predicate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (!_disposed)
        {
            _disposed = true;
            _assemblies = null;
            if (_loadContext?.IsCollectible == true)
            {
                _loadContext.Unload();
            }
        }
    }
}
