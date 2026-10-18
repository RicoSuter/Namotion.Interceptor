using HomeBlaze.Abstractions;
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
    /// Gets navigation items for direct children of a subject, in the order of <see cref="ChildOrdering"/>.
    /// </summary>
    public IEnumerable<NavigationItem> GetChildItems(IInterceptorSubject parent)
    {
        return GetChildItemList(parent);
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
        foreach (var item in GetChildItemList(parent))
        {
            if (!visited.Add(item.Subject))
                continue;

            if (item is { IsPage: true, Location: NavigationLocation.AppBar })
                items.Add(item);

            if (item.IsFolder)
                CollectAppBarItems(item.Subject, items, visited);
        }
    }

    private List<NavigationItem> GetChildItemList(IInterceptorSubject parent)
    {
        var registered = parent.TryGetRegisteredSubject();
        if (registered == null)
            return [];

        var items = new List<(NavigationItem Item, string Key)>();
        Dictionary<IInterceptorSubject, bool>? visited = null;

        foreach (var prop in registered.Properties)
        {
            if (!prop.CanContainSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null)
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

                visited ??= new Dictionary<IInterceptorSubject, bool>(ReferenceEqualityComparer.Instance);
                visited.Clear();
                var descendantPages = new DescendantPages();
                FindDescendantPages(child, isInNavigationBar: true, visited, ref descendantPages);

                // Only include if it's a page or has page descendants
                if (isPage || descendantPages.HasAny)
                {
                    items.Add((new NavigationItem
                    {
                        Subject = child,
                        Title = child.GetNavigationTitle(key),
                        Icon = child.GetNavigationIconName(),
                        Path = path,
                        IsPage = isPage,
                        IsFolder = descendantPages.HasAny,
                        HasNavigationBarPages = descendantPages.HasInNavigationBar,
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

    private static bool ContainsPath(List<(NavigationItem Item, string Key)> items, string path)
    {
        foreach (var (item, _) in items)
        {
            if (item.Path == path)
                return true;
        }

        return false;
    }

    private struct DescendantPages
    {
        public bool HasAny;

        public bool HasInNavigationBar;
    }

    /// <summary>
    /// Walks the descendants of a subject for pages, and for pages that the sidebar shows: those reached only
    /// through subjects whose location is the sidebar. Stops as soon as the answer to both is known. Each subject in
    /// <paramref name="visited"/> maps to whether it was walked as part of the sidebar.
    /// </summary>
    private void FindDescendantPages(
        IInterceptorSubject subject,
        bool isInNavigationBar,
        Dictionary<IInterceptorSubject, bool> visited,
        ref DescendantPages result)
    {
        // A subject first walked outside the sidebar is walked again when it is reached through the sidebar,
        // as only that walk can find the pages of the sidebar.
        if (visited.TryGetValue(subject, out var wasInNavigationBar) && (wasInNavigationBar || !isInNavigationBar))
            return;

        visited[subject] = isInNavigationBar;

        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return;

        foreach (var prop in registered.Properties)
        {
            if (!prop.CanContainSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null)
                    continue;

                var isChildInNavigationBar = isInNavigationBar &&
                    child.GetNavigationLocation() == NavigationLocation.NavBar;

                if (_componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page))
                {
                    result.HasAny = true;
                    if (isChildInNavigationBar)
                    {
                        result.HasInNavigationBar = true;
                        return;
                    }
                }

                // Once a page is found, only a walk through the sidebar can learn anything new.
                if (result.HasAny && !isChildInNavigationBar)
                    continue;

                FindDescendantPages(child, isChildInNavigationBar, visited, ref result);
                if (result.HasInNavigationBar)
                    return;
            }
        }
    }
}
