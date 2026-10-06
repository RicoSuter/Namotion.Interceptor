using System.Reflection;

namespace HomeBlaze.Services;

/// <summary>
/// Central provider for types from assemblies. Used by registries for lazy scanning.
/// </summary>
public class TypeProvider
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Type> _typesByFullName = new(StringComparer.Ordinal);

    // Copy-on-write: readers take the current array without a lock, writers publish a replacement.
    // Registration is a short burst at startup while the lookups below run for the life of the process
    // and sit on request paths, so the cost belongs on the write side. Publishing a new array also means
    // a reader that is midway through an enumeration keeps walking the snapshot it started on.
    private Type[] _types = [];

    /// <summary>
    /// Gets all collected types. Returns the same instance until types are added, so callers can cache
    /// data derived from it per instance.
    /// </summary>
    public IReadOnlyCollection<Type> Types => Volatile.Read(ref _types);

    /// <summary>
    /// Raised after types were added, outside the registration lock.
    /// </summary>
    public event EventHandler? TypesChanged;

    /// <summary>
    /// Adds exported types from an assembly.
    /// </summary>
    public TypeProvider AddAssembly(Assembly assembly)
    {
        AddAssemblies([assembly]);
        return this;
    }

    /// <summary>
    /// Adds exported types from the assemblies and raises <see cref="TypesChanged"/> once when any type was added.
    /// </summary>
    /// <returns>Types skipped because a different type with the same full name is already registered.</returns>
    public IReadOnlyList<Type> AddAssemblies(IEnumerable<Assembly> assemblies)
    {
        return AddTypes(assemblies.SelectMany(GetExportedTypes));
    }

    /// <summary>
    /// Adds types directly and raises <see cref="TypesChanged"/> once when any type was added.
    /// Already registered types are ignored.
    /// </summary>
    /// <returns>Types skipped because a different type with the same full name is already registered.</returns>
    public IReadOnlyList<Type> AddTypes(IEnumerable<Type> types)
    {
        var candidateTypes = types as Type[] ?? types.ToArray();
        List<Type>? skippedTypes = null;
        var addedCount = 0;

        lock (_lock)
        {
            var addedTypes = new List<Type>(candidateTypes.Length);
            foreach (var type in candidateTypes)
            {
                var fullName = type.FullName ?? type.Name;
                if (_typesByFullName.TryGetValue(fullName, out var registeredType))
                {
                    if (registeredType != type)
                    {
                        (skippedTypes ??= []).Add(type);
                    }

                    continue;
                }

                _typesByFullName.Add(fullName, type);
                addedTypes.Add(type);
            }

            if (addedTypes.Count > 0)
            {
                var existingTypes = _types;
                var combinedTypes = new Type[existingTypes.Length + addedTypes.Count];

                existingTypes.CopyTo(combinedTypes, 0);
                addedTypes.CopyTo(combinedTypes, existingTypes.Length);

                Volatile.Write(ref _types, combinedTypes);
            }

            addedCount = addedTypes.Count;
        }

        if (addedCount > 0)
        {
            TypesChanged?.Invoke(this, EventArgs.Empty);
        }

        return (IReadOnlyList<Type>?)skippedTypes ?? [];
    }

    private static Type[] GetExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).ToArray()!;
        }
    }
}
