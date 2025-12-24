using System.Collections.Concurrent;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.Services.Components;

/// <summary>
/// Registry for resolving Blazor component types for subjects.
/// </summary>
public class SubjectComponentRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly Lazy<Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration>> _components;

    // TODO: Clear cache when types are dynamically registered at runtime
    private readonly ConcurrentDictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration?> _resolvedCache = new();

    public SubjectComponentRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
        _components = new Lazy<Dictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration>>(LoadComponents);
    }

    /// <summary>
    /// Gets a specific component registration for a subject type and component type.
    /// Supports inheritance and interface fallback for generic components.
    /// </summary>
    public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return _resolvedCache.GetOrAdd((subjectType, type, name), key => ResolveComponent(key.Item1, key.Item2, key.Item3));
    }

    private SubjectComponentRegistration? ResolveComponent(Type subjectType, SubjectComponentType type, string? name)
    {
        // Try exact match first
        var exact = _components.Value.GetValueOrDefault((subjectType, type, name));
        if (exact != null)
            return exact;

        // Try base classes
        var baseType = subjectType.BaseType;
        while (baseType != null && baseType != typeof(object))
        {
            var baseMatch = _components.Value.GetValueOrDefault((baseType, type, name));
            if (baseMatch != null)
                return baseMatch;
            baseType = baseType.BaseType;
        }

        // Try interfaces (for IConfigurableSubject fallback)
        foreach (var iface in subjectType.GetInterfaces())
        {
            var ifaceMatch = _components.Value.GetValueOrDefault((iface, type, name));
            if (ifaceMatch != null)
                return ifaceMatch;
        }

        return null;
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
    // TODO: Align HasComponent with GetComponent - currently only checks exact match while GetComponent supports inheritance/interface fallback
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
