using System.Collections;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// Resolves property-based route paths for navigation URLs.
/// Uses paths like "Children/docs/Children/readme.md" that include property names.
/// </summary>
public class RoutePathResolver
{
    private readonly RootManager _rootManager;

    public RoutePathResolver(RootManager rootManager)
    {
        _rootManager = rootManager;
    }

    /// <summary>
    /// Gets the property-based route path for a subject.
    /// Returns paths like "Children/docs/Children/readme.md".
    /// </summary>
    public string? GetRoutePath(IInterceptorSubject subject)
    {
        var root = _rootManager.Root;
        if (root == null)
            return null;

        if (subject == root)
            return "";

        var segments = new List<string>();
        var current = subject;

        while (current != null && current != root)
        {
            var registered = current.TryGetRegisteredSubject();
            if (registered == null)
                return null;

            var parents = registered.Parents;
            if (parents.Length == 0)
                return null;

            var parentInfo = parents[0];
            var propertyName = parentInfo.Property.Name;
            var index = parentInfo.Index?.ToString();

            if (index != null)
                segments.Add(index);
            segments.Add(propertyName);

            current = parentInfo.Property.Parent.Subject;
        }

        if (current != root)
            return null;

        segments.Reverse();
        return string.Join("/", segments);
    }

    /// <summary>
    /// Resolves a subject from a property-based route path.
    /// </summary>
    public IInterceptorSubject? ResolveFromRoute(string routePath)
    {
        var root = _rootManager.Root;
        if (root == null)
            return null;

        if (string.IsNullOrEmpty(routePath))
            return root;

        var segments = routePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var i = 0;

        while (i < segments.Length && current != null)
        {
            var propertyName = segments[i++];
            var registered = current.TryGetRegisteredSubject();
            var property = registered?.TryGetProperty(propertyName);

            if (property == null || !property.HasChildSubjects)
                return null;

            if (property.IsSubjectReference)
            {
                current = property.GetValue() as IInterceptorSubject;
            }
            else
            {
                if (i >= segments.Length)
                    return null;
                var index = segments[i++];
                current = FindChildByIndex(property, index);
            }
        }

        return current;
    }

    public static IInterceptorSubject? FindChildByIndex(RegisteredSubjectProperty property, string indexStr)
    {
        var value = property.GetValue();
        if (value == null)
            return null;

        // Handle Dictionary<string, IInterceptorSubject>
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (key == indexStr && entry.Value is IInterceptorSubject subject)
                    return subject;
            }
        }
        // Handle List/Array with numeric index
        else if (value is IEnumerable enumerable and not string)
        {
            if (int.TryParse(indexStr, out var index))
            {
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i == index && item is IInterceptorSubject subject)
                        return subject;
                    i++;
                }
            }
        }

        return null;
    }
}
