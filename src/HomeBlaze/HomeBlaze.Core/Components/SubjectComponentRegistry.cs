using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Core.Components;

/// <summary>
/// Registry for resolving Blazor component types for subjects.
/// </summary>
public class SubjectComponentRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly Lazy<Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration>> _components;

    public SubjectComponentRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
        _components = new Lazy<Dictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration>>(LoadComponents);
    }

    /// <summary>
    /// Gets a specific component registration for a subject type and component type.
    /// </summary>
    public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return _components.Value.GetValueOrDefault((subjectType, type, name));
    }

    /// <summary>
    /// Gets all component registrations for a subject type and component type.
    /// </summary>
    public IEnumerable<SubjectComponentRegistration> GetComponents(Type subjectType, SubjectComponentType type)
    {
        return _components.Value.Values.Where(registration => registration.SubjectType == subjectType && registration.Type == type);
    }

    /// <summary>
    /// Checks if a component is registered for the given subject type and component type.
    /// </summary>
    public bool HasComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return _components.Value.ContainsKey((subjectType, type, name));
    }

    /// <summary>
    /// Gets all registered components.
    /// </summary>
    public IReadOnlyCollection<SubjectComponentRegistration> GetAllComponents()
    {
        return _components.Value.Values.ToList();
    }

    private Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> LoadComponents()
    {
        var dictionary = new Dictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration>();

        foreach (var type in _typeProvider.Types)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(SubjectComponentAttribute), false).Cast<SubjectComponentAttribute>())
            {
                var key = (attribute.SubjectType, attribute.ComponentType, attribute.Name);
                dictionary[key] = new SubjectComponentRegistration(type, attribute.SubjectType, attribute.ComponentType, attribute.Name);
            }
        }

        return dictionary;
    }
}
