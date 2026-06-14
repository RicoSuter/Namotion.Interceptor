using System.Reflection;
using Microsoft.Extensions.DependencyModel;
using NuGet.Versioning;

namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// Resolves the host application's dependency map for version validation.
/// </summary>
public class HostDependencyResolver
{
    private readonly Dictionary<string, NuGetVersion> _dependencies;

    internal HostDependencyResolver(Dictionary<string, NuGetVersion> dependencies)
    {
        _dependencies = dependencies;
    }

    /// <summary>
    /// All known host dependencies (package name -> NuGet version).
    /// </summary>
    public IReadOnlyDictionary<string, NuGetVersion> Dependencies => _dependencies;

    /// <summary>
    /// Whether the host has this package.
    /// </summary>
    public bool Contains(string packageName) => _dependencies.ContainsKey(packageName);

    /// <summary>
    /// Gets the host's version of a package, or null if not present.
    /// </summary>
    public NuGetVersion? GetVersion(string packageName) =>
        _dependencies.TryGetValue(packageName, out var version) ? version : null;

    /// <summary>
    /// Builds the dependency map from the running application's dependency context.
    /// Includes both NuGet packages and project references.
    /// </summary>
    public static HostDependencyResolver FromDepsJson()
    {
        var context = DependencyContext.Default
            ?? throw new InvalidOperationException(
                "DependencyContext not available. Use FromAssemblies() for AOT or single-file apps.");

        return FromDependencyContext(context);
    }

    /// <summary>
    /// Builds the dependency map from a specific assembly's dependency context.
    /// </summary>
    public static HostDependencyResolver FromDepsJson(Assembly assembly)
    {
        var context = DependencyContext.Load(assembly)
            ?? throw new InvalidOperationException(
                $"DependencyContext not available for assembly '{assembly.GetName().Name}'.");

        return FromDependencyContext(context);
    }

    /// <summary>
    /// Builds the dependency map from a deps.json file at the specified path.
    /// Includes both NuGet packages and project references.
    /// </summary>
    public static HostDependencyResolver FromDepsJson(string depsJsonPath)
    {
        var reader = new DependencyContextJsonReader();
        using var stream = File.OpenRead(depsJsonPath);
        var context = reader.Read(stream);
        return FromDependencyContext(context);
    }

    /// <summary>
    /// Builds resolver from loaded Assembly objects using their assembly versions.
    /// </summary>
    public static HostDependencyResolver FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        var dependencies = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName();
            if (name.Name != null && name.Version != null)
            {
                dependencies[name.Name] = new NuGetVersion(
                    name.Version.Major,
                    name.Version.Minor,
                    name.Version.Build >= 0 ? name.Version.Build : 0);
            }
        }

        return new HostDependencyResolver(dependencies);
    }

    /// <summary>
    /// Builds resolver from explicit name-version tuples.
    /// </summary>
    public static HostDependencyResolver FromAssemblies(params (string Name, Version Version)[] assemblies)
    {
        var dependencies = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in assemblies)
        {
            dependencies[name] = new NuGetVersion(
                version.Major, version.Minor, version.Build >= 0 ? version.Build : 0);
        }

        return new HostDependencyResolver(dependencies);
    }

    private static HostDependencyResolver FromDependencyContext(DependencyContext context)
    {
        var dependencies = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in context.RuntimeLibraries)
        {
            if (NuGetVersion.TryParse(library.Version, out var version))
            {
                dependencies[library.Name] = version;
            }
        }

        return new HostDependencyResolver(dependencies);
    }
}
