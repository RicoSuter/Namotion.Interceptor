using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
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
    /// Gets navigation items for direct children of a subject, in the order of <see cref="ChildOrdering"/>. A child whose
    /// type is excluded from user interfaces is left out with what it holds.
    /// </summary>
    public IEnumerable<NavigationItem> GetChildItems(IInterceptorSubject parent)
    {
        var registered = parent.TryGetRegisteredSubject();
        if (registered == null)
            return [];

        var items = new List<(NavigationItem Item, string Key)>();
        HashSet<IInterceptorSubject>? visited = null;

        foreach (var prop in registered.Properties)
        {
            if (!prop.CanContainSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null || child.IsExcludedFrom(ExclusionTarget.UserInterface))
                    continue;

                var key = childInfo.Index?.ToString() ?? prop.Name;
                var path = _pathResolver.GetPath(child, PathStyle.Canonical);
                if (path == null)
                    continue;

                // A subject referenced more than once has a single canonical path, so further entries
                // would be the same link again, and the menu keys its items by path.
                if (ContainsPath(items, path))
                    continue;

                var isPage = _componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page);

                visited ??= new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
                visited.Clear();
                var hasNavigationBarPages = HasPageDescendants(child, sidebarOnly: true, visited);
                var hasPages = hasNavigationBarPages;
                if (!hasPages)
                {
                    visited.Clear();
                    hasPages = HasPageDescendants(child, sidebarOnly: false, visited);
                }

                // Only include if it's a page or has page descendants
                if (isPage || hasPages)
                {
                    items.Add((new NavigationItem
                    {
                        Subject = child,
                        Title = child.GetNavigationTitle(key),
                        Icon = child.GetNavigationIconName(),
                        Path = path,
                        IsPage = isPage,
                        IsFolder = hasPages,
                        HasNavigationBarPages = hasNavigationBarPages,
                        Location = child.GetNavigationLocation(),
                        Alignment = child.GetAppBarAlignment()
                    }, key));
                }
            }
        }

        var childOrder = parent is IMetadataContainer container ? container.ChildOrder : [];
        return ChildOrdering
            .Order(items, childOrder, static item => item.Key, static item => item.Item.Title)
            .Select(static item => item.Item)
            .ToList();
    }

    /// <summary>
    /// Gets every page of the tree below <paramref name="root"/> that appears in the top bar, in navigation order:
    /// depth first, each level in the order of <see cref="GetChildItems"/>. A subject that is reached more than once
    /// is listed once, at its first place.
    /// </summary>
    public IReadOnlyList<NavigationItem> GetAppBarItems(IInterceptorSubject root)
    {
        var items = new List<NavigationItem>();
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance) { root };
        CollectAppBarItems(root, items, visited);
        return items;
    }

    private void CollectAppBarItems(
        IInterceptorSubject parent, List<NavigationItem> items, HashSet<IInterceptorSubject> visited)
    {
        foreach (var item in GetChildItems(parent))
        {
            if (!visited.Add(item.Subject))
                continue;

            if (item is { IsPage: true, Location: NavigationLocation.AppBar })
                items.Add(item);

            if (item.IsFolder)
                CollectAppBarItems(item.Subject, items, visited);
        }
    }

    private static bool ContainsPath(List<(NavigationItem Item, string Key)> items, string path)
    {
        foreach (var (item, _) in items)
        {
            if (item.Path == path)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a page is below <paramref name="subject"/>; with <paramref name="sidebarOnly"/>, only a page reached
    /// through subjects whose location is the sidebar.
    /// </summary>
    private bool HasPageDescendants(
        IInterceptorSubject subject, bool sidebarOnly, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
            return false;

        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return false;

        foreach (var prop in registered.Properties)
        {
            if (!prop.CanContainSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null || child.IsExcludedFrom(ExclusionTarget.UserInterface))
                    continue;

                if (sidebarOnly && child.GetNavigationLocation() != NavigationLocation.NavBar)
                    continue;

                if (_componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page) ||
                    HasPageDescendants(child, sidebarOnly, visited))
                    return true;
            }
        }

        return false;
    }
}
