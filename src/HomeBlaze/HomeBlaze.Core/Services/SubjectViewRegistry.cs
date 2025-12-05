using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Registry for resolving Blazor component types for subject views.
/// Scans assemblies for [SubjectView] attributes and provides lookup by subject type and view type.
/// </summary>
public class SubjectViewRegistry
{
    private readonly ConcurrentDictionary<(Type SubjectType, string ViewType), Type> _viewsBySubjectAndType = new();
    private readonly ConcurrentDictionary<string, List<(Type SubjectType, Type ComponentType)>> _viewsByType = new();

    /// <summary>
    /// Gets all registered view types (e.g., "edit", "widget", "setup").
    /// </summary>
    public IReadOnlyCollection<string> RegisteredViewTypes => _viewsByType.Keys.ToList();

    /// <summary>
    /// Registers a component type for a subject type and view type.
    /// </summary>
    public SubjectViewRegistry Register<TComponent>(string viewType, Type subjectType)
    {
        return Register(typeof(TComponent), viewType, subjectType);
    }

    /// <summary>
    /// Registers a component type for a subject type and view type.
    /// </summary>
    public SubjectViewRegistry Register(Type componentType, string viewType, Type subjectType)
    {
        if (componentType == null)
            throw new ArgumentNullException(nameof(componentType));
        if (string.IsNullOrEmpty(viewType))
            throw new ArgumentException("ViewType cannot be null or empty", nameof(viewType));
        if (subjectType == null)
            throw new ArgumentNullException(nameof(subjectType));

        viewType = viewType.ToLowerInvariant();

        _viewsBySubjectAndType[(subjectType, viewType)] = componentType;

        var list = _viewsByType.GetOrAdd(viewType, _ => new List<(Type, Type)>());
        lock (list)
        {
            if (!list.Any(x => x.SubjectType == subjectType))
            {
                list.Add((subjectType, componentType));
            }
        }

        return this;
    }

    /// <summary>
    /// Scans assemblies for types with [SubjectView] attribute and registers them.
    /// </summary>
    public SubjectViewRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var attr in type.GetCustomAttributes<SubjectViewAttribute>())
                    {
                        Register(type, attr.ViewType, attr.SubjectType);
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
    /// Resolves the component type for a given subject type and view type.
    /// Returns null if no view is registered.
    /// </summary>
    public Type? ResolveView(Type subjectType, string viewType)
    {
        if (subjectType == null || string.IsNullOrEmpty(viewType))
            return null;

        viewType = viewType.ToLowerInvariant();

        // Try exact match first
        if (_viewsBySubjectAndType.TryGetValue((subjectType, viewType), out var componentType))
            return componentType;

        // Try base types
        var baseType = subjectType.BaseType;
        while (baseType != null)
        {
            if (_viewsBySubjectAndType.TryGetValue((baseType, viewType), out componentType))
                return componentType;
            baseType = baseType.BaseType;
        }

        // Try interfaces
        foreach (var iface in subjectType.GetInterfaces())
        {
            if (_viewsBySubjectAndType.TryGetValue((iface, viewType), out componentType))
                return componentType;
        }

        return null;
    }

    /// <summary>
    /// Resolves the component type for a given subject and view type.
    /// Returns null if no view is registered.
    /// </summary>
    public Type? ResolveView(object subject, string viewType)
    {
        if (subject == null)
            return null;

        return ResolveView(subject.GetType(), viewType);
    }

    /// <summary>
    /// Checks if a view is registered for the given subject type and view type.
    /// </summary>
    public bool HasView(Type subjectType, string viewType)
    {
        return ResolveView(subjectType, viewType) != null;
    }

    /// <summary>
    /// Checks if a view is registered for the given subject and view type.
    /// </summary>
    public bool HasView(object subject, string viewType)
    {
        return ResolveView(subject, viewType) != null;
    }

    /// <summary>
    /// Gets all registered views for a given view type.
    /// </summary>
    public IReadOnlyList<(Type SubjectType, Type ComponentType)> GetViewsOfType(string viewType)
    {
        if (string.IsNullOrEmpty(viewType))
            return Array.Empty<(Type, Type)>();

        viewType = viewType.ToLowerInvariant();

        if (_viewsByType.TryGetValue(viewType, out var list))
        {
            lock (list)
            {
                return list.ToList();
            }
        }

        return Array.Empty<(Type, Type)>();
    }
}
