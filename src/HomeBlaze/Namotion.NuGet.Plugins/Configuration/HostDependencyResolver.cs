using System.Reflection;
using System.Text.Json;

namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// Resolves the host application's dependency map for version validation.
/// </summary>
public class HostDependencyResolver
{
    private readonly Dictionary<string, global::NuGet.Versioning.NuGetVersion> _dependencies;

    internal HostDependencyResolver(Dictionary<string, global::NuGet.Versioning.NuGetVersion> dependencies)
    {
        _dependencies = dependencies;
    }

    /// <summary>
    /// All known host dependencies (package name -> NuGet version).
    /// </summary>
    public IReadOnlyDictionary<string, global::NuGet.Versioning.NuGetVersion> Dependencies => _dependencies;

    /// <summary>
    /// Whether the host has this package.
    /// </summary>
    public bool Contains(string packageName) => _dependencies.ContainsKey(packageName);

    /// <summary>
    /// Gets the host's version of a package, or null if not present.
    /// </summary>
    public global::NuGet.Versioning.NuGetVersion? GetVersion(string packageName) =>
        _dependencies.TryGetValue(packageName, out var version) ? version : null;

    /// <summary>
    /// Parses the running application's {app}.deps.json file.
    /// Falls back to searching the base directory when the entry assembly's deps.json is not found
    /// (e.g. when running inside a test host).
    /// </summary>
    public static HostDependencyResolver FromDepsJson()
    {
        var entryAssembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("No entry assembly found.");

        var depsJsonPath = Path.ChangeExtension(entryAssembly.Location, ".deps.json");
        if (File.Exists(depsJsonPath))
        {
            return FromDepsJson(depsJsonPath);
        }

        // Fallback: search in AppContext.BaseDirectory for any deps.json file
        // (handles test runners where entry assembly is testhost)
        var baseDirectory = AppContext.BaseDirectory;
        var depsFiles = Directory.GetFiles(baseDirectory, "*.deps.json");
        if (depsFiles.Length > 0)
        {
            // Merge all deps.json files found
            var merged = new Dictionary<string, global::NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var depsFile in depsFiles)
            {
                var resolver = FromDepsJson(depsFile);
                foreach (var dependency in resolver.Dependencies)
                {
                    merged[dependency.Key] = dependency.Value;
                }
            }

            return new HostDependencyResolver(merged);
        }

        throw new FileNotFoundException(
            $"deps.json not found at '{depsJsonPath}' or in '{baseDirectory}'. Use FromAssemblies() for AOT or single-file apps.",
            depsJsonPath);
    }

    /// <summary>
    /// Parses a specific deps.json file.
    /// </summary>
    public static HostDependencyResolver FromDepsJson(string depsJsonPath)
    {
        var json = File.ReadAllText(depsJsonPath);
        var document = JsonDocument.Parse(json);
        var dependencies = new Dictionary<string, global::NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateObject())
            {
                // Skip project references (type: "project")
                if (library.Value.TryGetProperty("type", out var typeElement) &&
                    typeElement.GetString() == "project")
                {
                    continue;
                }

                // Library key format: "PackageName/Version"
                var slashIndex = library.Name.IndexOf('/');
                if (slashIndex > 0 && slashIndex < library.Name.Length - 1)
                {
                    var packageName = library.Name[..slashIndex];
                    var versionString = library.Name[(slashIndex + 1)..];

                    if (global::NuGet.Versioning.NuGetVersion.TryParse(versionString, out var version))
                    {
                        dependencies[packageName] = version;
                    }
                }
            }
        }

        return new HostDependencyResolver(dependencies);
    }

    /// <summary>
    /// Builds resolver from loaded Assembly objects using their assembly versions.
    /// </summary>
    public static HostDependencyResolver FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        var dependencies = new Dictionary<string, global::NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName();
            if (name.Name != null && name.Version != null)
            {
                dependencies[name.Name] = new global::NuGet.Versioning.NuGetVersion(
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
        var dependencies = new Dictionary<string, global::NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in assemblies)
        {
            dependencies[name] = new global::NuGet.Versioning.NuGetVersion(
                version.Major, version.Minor, version.Build >= 0 ? version.Build : 0);
        }

        return new HostDependencyResolver(dependencies);
    }
}
