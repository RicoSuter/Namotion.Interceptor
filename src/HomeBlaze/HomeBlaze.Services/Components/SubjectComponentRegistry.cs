using System.Collections.Concurrent;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.Services.Components;

/// <summary>
/// Registry for resolving Blazor component types for subjects. The components are scanned from
/// <see cref="TypeProvider.Types"/> on first use, and types added later are picked up on the next call.
/// </summary>
public class SubjectComponentRegistry
{
    private readonly TypeProvider _typeProvider;
    private readonly Lock _rebuildLock = new();

    // Replaced as a whole when the type list changes, so the components and the resolved lookups, negative ones
    // included, always belong to the same type list.
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
        return GetSnapshot().GetComponent(subjectType, type, name);
    }

    /// <summary>
    /// Gets all component registrations for a subject type and component type.
    /// </summary>
    public IEnumerable<SubjectComponentRegistration> GetComponents(Type subjectType, SubjectComponentType type)
    {
        return GetSnapshot().Components.Values.Where(registration => registration.SubjectType == subjectType && registration.Type == type);
    }

    /// <summary>
    /// Checks whether <see cref="GetComponent"/> resolves a component for the given subject type and component type,
    /// including the inheritance and interface fallback.
    /// </summary>
    public bool HasComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        return GetComponent(subjectType, type, name) is not null;
    }

    /// <summary>
    /// Gets the component the create or edit dialog shows for a subject type: the Setup component when
    /// <paramref name="isCreating"/> is true and one is registered, otherwise the Edit component. Returns null when
    /// there is neither, in which case the dialog shows the generated properties editor.
    /// </summary>
    public SubjectComponentRegistration? GetEditorComponent(Type subjectType, bool isCreating)
    {
        var snapshot = GetSnapshot();
        return (isCreating ? snapshot.GetComponent(subjectType, SubjectComponentType.Setup, null) : null) ??
               snapshot.GetComponent(subjectType, SubjectComponentType.Edit, null);
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
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is not null && ReferenceEquals(snapshot.Types, _typeProvider.Types)
            ? snapshot
            : Rebuild();
    }

    private Snapshot Rebuild()
    {
        lock (_rebuildLock)
        {
            var types = _typeProvider.Types;
            var snapshot = _snapshot;
            if (snapshot is not null && ReferenceEquals(snapshot.Types, types))
            {
                return snapshot;
            }

            snapshot = new Snapshot(types, LoadComponents(types));
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
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

    private sealed class Snapshot(
        IReadOnlyCollection<Type> types,
        Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> components)
    {
        private readonly ConcurrentDictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration?> _resolvedCache = new();

        public IReadOnlyCollection<Type> Types { get; } = types;

        // Never written after construction, so concurrent readers need no lock.
        public Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> Components { get; } = components;

        public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name)
        {
            return _resolvedCache.GetOrAdd(
                (subjectType, type, name),
                static (key, snapshot) => snapshot.ResolveComponent(key.Item1, key.Item2, key.Item3),
                this);
        }

        private SubjectComponentRegistration? ResolveComponent(Type subjectType, SubjectComponentType type, string? name)
        {
            // Try exact match first
            var exact = Components.GetValueOrDefault((subjectType, type, name));
            if (exact != null)
                return exact;

            // Try base classes
            var baseType = subjectType.BaseType;
            while (baseType != null && baseType != typeof(object))
            {
                var baseMatch = Components.GetValueOrDefault((baseType, type, name));
                if (baseMatch != null)
                    return baseMatch;
                baseType = baseType.BaseType;
            }

            // Try interfaces (for IConfigurable fallback)
            foreach (var iface in subjectType.GetInterfaces())
            {
                var ifaceMatch = Components.GetValueOrDefault((iface, type, name));
                if (ifaceMatch != null)
                    return ifaceMatch;
            }

            return null;
        }
    }
}
