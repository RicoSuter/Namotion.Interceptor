using System.Collections.Concurrent;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.Services.Components;

/// <summary>
/// Registry for resolving Blazor component types for subjects.
/// </summary>
public class SubjectComponentRegistry
{
    private readonly TypeProvider _typeProvider;
    private Snapshot? _snapshot;

    public SubjectComponentRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    /// <summary>
    /// Gets a specific component registration for a subject type and component type.
    /// Supports inheritance and interface fallback for generic components.
    /// </summary>
    public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        var snapshot = GetSnapshot();
        return snapshot.ResolvedCache.GetOrAdd((subjectType, type, name),
            key => ResolveComponent(snapshot.Components, key.Item1, key.Item2, key.Item3));
    }

    private static SubjectComponentRegistration? ResolveComponent(
        Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> components,
        Type subjectType,
        SubjectComponentType type,
        string? name)
    {
        // Try exact match first
        var exact = components.GetValueOrDefault((subjectType, type, name));
        if (exact != null)
            return exact;

        // Try base classes
        var baseType = subjectType.BaseType;
        while (baseType != null && baseType != typeof(object))
        {
            var baseMatch = components.GetValueOrDefault((baseType, type, name));
            if (baseMatch != null)
                return baseMatch;
            baseType = baseType.BaseType;
        }

        // Try interfaces (for IConfigurable fallback)
        foreach (var iface in subjectType.GetInterfaces())
        {
            var ifaceMatch = components.GetValueOrDefault((iface, type, name));
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
        return GetSnapshot().Components.Values.Where(registration => registration.SubjectType == subjectType && registration.Type == type);
    }

    /// <summary>
    /// Checks if a component is registered for the given subject type and component type.
    /// </summary>
    // TODO: Align HasComponent with GetComponent - currently only checks exact match while GetComponent supports inheritance/interface fallback
    public bool HasComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return GetSnapshot().Components.ContainsKey((subjectType, type, name));
    }

    /// <summary>
    /// Gets all registered components.
    /// </summary>
    public IReadOnlyCollection<SubjectComponentRegistration> GetAllComponents()
    {
        return GetSnapshot().Components.Values.ToList();
    }

    private Snapshot GetSnapshot()
    {
        var types = _typeProvider.Types;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || !ReferenceEquals(snapshot.Source, types))
        {
            snapshot = new Snapshot(types, LoadComponents(types), new());
            Volatile.Write(ref _snapshot, snapshot);
        }

        return snapshot;
    }

    private static Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> LoadComponents(
        IReadOnlyCollection<Type> types)
    {
        var dictionary = new Dictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration>();

        foreach (var type in types)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(SubjectComponentAttribute), false).Cast<SubjectComponentAttribute>())
            {
                var key = (attribute.SubjectType, attribute.ComponentType, attribute.Name);
                dictionary[key] = new SubjectComponentRegistration(type, attribute.SubjectType, attribute.ComponentType, attribute.Name);
            }
        }

        return dictionary;
    }

    private sealed record Snapshot(
        IReadOnlyCollection<Type> Source,
        Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> Components,
        ConcurrentDictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration?> ResolvedCache);
}
