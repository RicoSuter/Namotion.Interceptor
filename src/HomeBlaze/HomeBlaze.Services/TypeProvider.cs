using System.Reflection;

namespace HomeBlaze.Services;

/// <summary>
/// Central provider for types from assemblies. Used by registries for lazy scanning.
/// </summary>
public class TypeProvider
{
    private readonly List<Type> _types = [];

    /// <summary>
    /// Gets all collected types.
    /// </summary>
    public IReadOnlyCollection<Type> Types => _types;

    /// <summary>
    /// Adds exported types from an assembly.
    /// </summary>
    public TypeProvider AddAssembly(Assembly assembly)
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

        return this;
    }

    /// <summary>
    /// Adds types directly (e.g., from plugins).
    /// </summary>
    public void AddTypes(IEnumerable<Type> types)
    {
        _types.AddRange(types);
    }
}
