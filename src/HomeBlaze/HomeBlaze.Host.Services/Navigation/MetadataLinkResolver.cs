using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// A link of metadata as the browser shows it.
/// </summary>
/// <param name="Title">The title to show.</param>
/// <param name="Href">The address to open; null for a broken link.</param>
/// <param name="IsExternal">Whether the address is an http or https address, to be opened in a new tab without opener and referrer.</param>
public sealed record ResolvedMetadataLink(string Title, string? Href, bool IsExternal)
{
    /// <summary>Gets whether the link leads to nothing that may be opened.</summary>
    public bool IsBroken => Href is null;
}

/// <summary>
/// Resolves the links of metadata: a web address opens in a new tab, a mailto address in place, a path to a page opens the page, a path to a
/// subject opens it in the browser, and a path to a property opens its subject in the browser with the row of the
/// property highlighted. A path that leads to nothing, and an address that <see cref="MetadataLink.IsAllowedAddress"/>
/// rejects, is a broken link.
/// </summary>
public sealed class MetadataLinkResolver
{
    /// <summary>The query parameter of a browser address that names the property whose row is highlighted.</summary>
    public const string PropertyQueryParameter = "property";

    private readonly SubjectPathResolver _pathResolver;
    private readonly SubjectComponentRegistry _componentRegistry;

    public MetadataLinkResolver(SubjectPathResolver pathResolver, SubjectComponentRegistry componentRegistry)
    {
        _pathResolver = pathResolver;
        _componentRegistry = componentRegistry;
    }

    /// <summary>Resolves the link. Without a title, a path link shows the title of its target and a web link its address.</summary>
    public ResolvedMetadataLink Resolve(MetadataLink link)
    {
        // A file edited by hand can hold any address, such as javascript:, which must never be opened.
        if (link.Url is { } url)
        {
            // A mailto address opens the mail client, which needs no tab of its own.
            var isAllowed = MetadataLink.IsAllowedAddress(url);
            return new ResolvedMetadataLink(link.Title ?? url, isAllowed ? url : null, isAllowed && MetadataLink.IsWebAddress(url));
        }

        var path = link.Path ?? string.Empty;
        if (_pathResolver.ResolveSubject(path, PathStyle.Canonical) is { } subject)
        {
            return Resolve(subject, null, link.Title);
        }

        var separator = path.LastIndexOf('/');
        if (separator >= 0 &&
            _pathResolver.ResolveSubject(separator == 0 ? "/" : path[..separator], PathStyle.Canonical) is { } owner &&
            owner.TryGetRegisteredSubject()?.TryGetProperty(path[(separator + 1)..]) is { IsAttribute: false } property)
        {
            return Resolve(owner, property.Name, link.Title);
        }

        return new ResolvedMetadataLink(link.Title ?? path, null, IsExternal: false);
    }

    /// <summary>
    /// Resolves a link to the subject, or to its property when <paramref name="propertyName"/> is given. Without a
    /// title, the link shows the title of the subject, followed by the name of the property. Broken for a subject
    /// without a path in the tree.
    /// </summary>
    public ResolvedMetadataLink Resolve(IInterceptorSubject subject, string? propertyName, string? title = null)
    {
        if (propertyName is null)
        {
            string? href = null;
            if (_componentRegistry.HasComponent(subject.GetType(), SubjectComponentType.Page))
            {
                if (_pathResolver.GetPath(subject, PathStyle.Canonical) is { } canonicalPath)
                {
                    href = $"pages{canonicalPath}";
                }
            }
            else if (_pathResolver.GetPath(subject, PathStyle.Route) is { } routePath)
            {
                href = BrowserUrls.FromRoutePath(routePath);
            }

            return new ResolvedMetadataLink(title ?? subject.GetTitle(), href, IsExternal: false);
        }

        title ??= subject.TryGetRegisteredSubject()?.TryGetProperty(propertyName) is { } property
            ? $"{subject.GetTitle()} {property.GetDisplayName()}"
            : $"{subject.GetTitle()} {propertyName}";
        var propertyHref = _pathResolver.GetPath(subject, PathStyle.Route) is { } ownerRoutePath
            ? $"{BrowserUrls.FromRoutePath(ownerRoutePath)}?{PropertyQueryParameter}={Uri.EscapeDataString(propertyName)}"
            : null;
        return new ResolvedMetadataLink(title, propertyHref, IsExternal: false);
    }
}
