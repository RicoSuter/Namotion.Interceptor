using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services;
using HomeBlaze.Host.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// Resolves navigation items from the subject tree.
/// </summary>
public class NavigationItemResolver
{
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly SubjectPathResolver _pathResolver;

    public NavigationItemResolver(
        SubjectComponentRegistry componentRegistry,
        SubjectPathResolver pathResolver)
    {
        _componentRegistry = componentRegistry;
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Gets navigation items for direct children of a subject.
    /// </summary>
    public IEnumerable<NavigationItem> GetChildItems(IInterceptorSubject parent)
    {
        var items = new List<NavigationItem>();
        var registered = parent.TryGetRegisteredSubject();
        if (registered == null)
            return items;

        foreach (var prop in registered.Properties)
        {
            if (!prop.HasChildSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null)
                    continue;

                var key = childInfo.Index?.ToString() ?? prop.Name;
                var path = _pathResolver.GetPath(child, PathFormat.Slash);
                if (path == null)
                    continue;

                var isPage = _componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page);
                var isFolder = HasPageDescendants(child);

                // Only include if it's a page or has page descendants
                if (isPage || isFolder)
                {
                    items.Add(new NavigationItem
                    {
                        Subject = child,
                        Title = child.GetNavigationTitle(key),
                        Icon = child.GetIconName(),
                        Path = path,
                        IsPage = isPage,
                        IsFolder = isFolder,
                        Order = child.GetNavigationOrder(key),
                        Location = child.GetNavigationLocation(),
                        Alignment = child.GetAppBarAlignment()
                    });
                }
            }
        }

        return items.OrderBy(i => i.Order).ThenBy(i => i.Title);
    }

    /// <summary>
    /// Checks if subject has any direct child that is a page or a folder.
    /// </summary>
    private bool HasPageDescendants(IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return false;

        foreach (var prop in registered.Properties)
        {
            if (!prop.HasChildSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;

                // Direct child is a page
                if (_componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page))
                    return true;

                // Direct child is a VirtualFolder (potential container) - check by type name to avoid assembly reference
                if (child.GetType().Name == "VirtualFolder")
                    return true;
            }
        }

        return false;
    }
}
