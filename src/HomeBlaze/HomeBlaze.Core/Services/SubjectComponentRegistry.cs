using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Registry for resolving Blazor component types for subjects.
/// Scans assemblies for [SubjectComponent] attributes and provides O(1) lookup by subject type and component type.
/// </summary>
public class SubjectComponentRegistry
{
    private readonly Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> _components = new();

    /// <summary>
    /// Registers a component type for a subject type and component type.
    /// </summary>
    public SubjectComponentRegistry Register<TComponent>(SubjectComponentType type, Type subjectType, string? name = null)
    {
        return Register(typeof(TComponent), type, subjectType, name);
    }

    /// <summary>
    /// Registers a component type for a subject type and component type.
    /// </summary>
    public SubjectComponentRegistry Register(Type componentType, SubjectComponentType type, Type subjectType, string? name = null)
    {
        if (componentType == null)
            throw new ArgumentNullException(nameof(componentType));
        if (subjectType == null)
            throw new ArgumentNullException(nameof(subjectType));

        var key = (subjectType, type, name);
        _components[key] = new SubjectComponentRegistration(componentType, subjectType, type, name);
        return this;
    }

    /// <summary>
    /// Scans assemblies for types with [SubjectComponent] attribute and registers them.
    /// </summary>
    public SubjectComponentRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var attr in type.GetCustomAttributes<SubjectComponentAttribute>())
                    {
                        var key = (attr.SubjectType, attr.ComponentType, attr.Name);
                        _components[key] = new SubjectComponentRegistration(type, attr.SubjectType, attr.ComponentType, attr.Name);
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be loaded
            }
        }

        return this;
    }

    /// <summary>
    /// Gets a specific component registration for a subject type and component type.
    /// Returns null if no component is registered.
    /// </summary>
    public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return _components.GetValueOrDefault((subjectType, type, name));
    }

    /// <summary>
    /// Gets all component registrations for a subject type and component type.
    /// </summary>
    public IEnumerable<SubjectComponentRegistration> GetComponents(Type subjectType, SubjectComponentType type)
    {
        return _components.Values.Where(r => r.SubjectType == subjectType && r.Type == type);
    }

    /// <summary>
    /// Checks if a component is registered for the given subject type and component type.
    /// </summary>
    public bool HasComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return _components.ContainsKey((subjectType, type, name));
    }

    /// <summary>
    /// Gets all registered components.
    /// </summary>
    public IReadOnlyCollection<SubjectComponentRegistration> GetAllComponents()
    {
        return _components.Values.ToList();
    }
}
