using System.Reflection;

namespace HomeBlaze.Core;

/// <summary>
/// Central provider for types from assemblies. Used by registries for lazy scanning.
/// </summary>
public class TypeProvider
{
    private readonly List<Type> _types = new();

    /// <summary>
    /// Gets all collected types.
    /// </summary>
    public IReadOnlyCollection<Type> Types => _types;

    /// <summary>
    /// Adds exported types from assemblies.
    /// </summary>
    public void AddAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                _types.AddRange(assembly.GetExportedTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                var loadedTypes = exception.Types.Where(type => type != null);
                _types.AddRange(loadedTypes!);
            }
        }
    }

    /// <summary>
    /// Adds types directly (e.g., from plugins).
    /// </summary>
    public void AddTypes(IEnumerable<Type> types)
    {
        _types.AddRange(types);
    }
}
