using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
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
    private readonly TimeProvider _timeProvider;

    // Replaced as a whole, so that a reader on another thread sees the items, root and time of one walk.
    private volatile RecentAppBarItems? _recentAppBarItems;

    public NavigationItemResolver(
        SubjectComponentRegistry componentRegistry,
        SubjectPathResolver pathResolver,
        TimeProvider timeProvider)
    {
        _componentRegistry = componentRegistry;
        _pathResolver = pathResolver;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets navigation items for direct children of a subject, in the order of <see cref="ChildOrdering"/>. A child whose
    /// type is excluded from user interfaces is left out with what it holds, and so is a child that the viewer may not see
    /// (<see cref="IUserPermissions.CanShow"/>) or that leads to no page the viewer may see.
    /// </summary>
    public IEnumerable<NavigationItem> GetChildItems(IInterceptorSubject parent, IUserPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return GetChildItemsFor(parent, permissions);
    }

    // Without permissions for the walk that every viewer shares, see GetSharedAppBarItems.
    private List<NavigationItem> GetChildItemsFor(IInterceptorSubject parent, IUserPermissions? permissions)
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
                if (child == null || child.IsExcludedFrom(ExclusionTarget.UserInterface) ||
                    permissions is not null && !permissions.CanShow(child))
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
                var hasNavigationBarPages = HasPageDescendants(child, sidebarOnly: true, visited, permissions);
                var hasPages = hasNavigationBarPages;
                if (!hasPages)
                {
                    visited.Clear();
                    hasPages = HasPageDescendants(child, sidebarOnly: false, visited, permissions);
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
            .Order(items, childOrder, static item => item.Key)
            .Select(static item => item.Item)
            .ToList();
    }

    /// <summary>
    /// Gets every page of the tree below <paramref name="root"/> that appears in the top bar and that the viewer may see,
    /// in navigation order: depth first, each level in the order of <see cref="GetChildItems"/>. A subject that is reached
    /// more than once is listed once, at its first place.
    /// </summary>
    public IReadOnlyList<NavigationItem> GetAppBarItems(IInterceptorSubject root, IUserPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return GetAppBarItemsFor(root, permissions);
    }

    /// <summary>
    /// Gets <see cref="GetAppBarItems"/> of <paramref name="root"/> as of a walk at most <paramref name="maximumAge"/>
    /// ago, which every caller shares, without the pages the viewer may not see: the same array as before while the items
    /// show the same subjects with the same title, icon, path and alignment and the viewer may see all of them, and
    /// <paramref name="previous"/> while the viewer sees the same items as in it, so that a caller tells a change by reference.
    /// </summary>
    public ImmutableArray<NavigationItem> GetRecentAppBarItems(
        IInterceptorSubject root, TimeSpan maximumAge, IUserPermissions permissions, ImmutableArray<NavigationItem> previous = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return FilterShown(GetSharedAppBarItems(root, maximumAge), permissions, previous);
    }

    // The items of every page of the top bar, whoever may see it, walked at most once per maximum age for all callers.
    private ImmutableArray<NavigationItem> GetSharedAppBarItems(IInterceptorSubject root, TimeSpan maximumAge)
    {
        var recent = _recentAppBarItems;
        if (recent is not null && ReferenceEquals(recent.Root, root) &&
            _timeProvider.GetElapsedTime(recent.Timestamp) < maximumAge)
        {
            return recent.Items;
        }

        var timestamp = _timeProvider.GetTimestamp();
        var items = GetAppBarItemsFor(root, permissions: null).ToImmutableArray();
        if (recent is not null && ReferenceEquals(recent.Root, root) && items.SequenceEqual(recent.Items, IsShownAlike))
        {
            items = recent.Items;
        }

        _recentAppBarItems = new RecentAppBarItems(root, items, timestamp);
        return items;
    }

    private List<NavigationItem> GetAppBarItemsFor(IInterceptorSubject root, IUserPermissions? permissions)
    {
        var items = new List<NavigationItem>();
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance) { root };
        CollectAppBarItems(root, items, visited, permissions);
        return items;
    }

    // One pass that asks CanShow once per item. The builder is only created at the first item the viewer may not see, so
    // a viewer who sees all of them gets the shared array itself, and one who sees the items of previous gets previous.
    private static ImmutableArray<NavigationItem> FilterShown(
        ImmutableArray<NavigationItem> items, IUserPermissions permissions, ImmutableArray<NavigationItem> previous)
    {
        ImmutableArray<NavigationItem>.Builder? builder = null;
        var isPrevious = !previous.IsDefault;
        var shownCount = 0;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (permissions.CanShow(item.Subject))
            {
                isPrevious = isPrevious && shownCount < previous.Length && ReferenceEquals(previous[shownCount], item);
                shownCount++;
                builder?.Add(item);
            }
            else if (builder is null)
            {
                builder = ImmutableArray.CreateBuilder<NavigationItem>(items.Length - 1);
                for (var shown = 0; shown < index; shown++)
                {
                    builder.Add(items[shown]);
                }
            }
        }

        if (builder is null)
        {
            return items;
        }

        return isPrevious && shownCount == previous.Length ? previous : builder.DrainToImmutable();
    }

    private static bool IsShownAlike(NavigationItem item, NavigationItem other)
        => ReferenceEquals(item.Subject, other.Subject) && item.Title == other.Title && item.Icon == other.Icon &&
           item.Path == other.Path && item.Alignment == other.Alignment;

    private sealed record RecentAppBarItems(IInterceptorSubject Root, ImmutableArray<NavigationItem> Items, long Timestamp);

    private void CollectAppBarItems(
        IInterceptorSubject parent, List<NavigationItem> items, HashSet<IInterceptorSubject> visited, IUserPermissions? permissions)
    {
        foreach (var item in GetChildItemsFor(parent, permissions))
        {
            if (!visited.Add(item.Subject))
                continue;

            if (item is { IsPage: true, Location: NavigationLocation.AppBar })
                items.Add(item);

            if (item.IsFolder)
                CollectAppBarItems(item.Subject, items, visited, permissions);
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
    /// Whether a page is below <paramref name="subject"/> that the viewer may see, any page without
    /// <paramref name="permissions"/>; with <paramref name="sidebarOnly"/>, only a page reached through subjects whose
    /// location is the sidebar.
    /// </summary>
    private bool HasPageDescendants(
        IInterceptorSubject subject, bool sidebarOnly, HashSet<IInterceptorSubject> visited, IUserPermissions? permissions)
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

                // Only a page is asked about: the subjects on the way to it are in the chain that CanShow checks for it.
                if (_componentRegistry.HasComponent(child.GetType(), SubjectComponentType.Page) &&
                    (permissions is null || permissions.CanShow(child)) ||
                    HasPageDescendants(child, sidebarOnly, visited, permissions))
                    return true;
            }
        }

        return false;
    }
}
