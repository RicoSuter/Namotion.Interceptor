using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// Path resolution that starts at the nearest <see cref="IPathScope"/> of a subject.
/// </summary>
public static class SubjectPathResolverScopeExtensions
{
    // A named subject further below than this is not followed up to its scope.
    private const int MaximumNamedSubjectDepth = 64;

    /// <summary>
    /// Resolves a subject from a path. A relative path resolves from the scope of
    /// <paramref name="from"/>, and its first segment can be a name the scope knows.
    /// Without a scope this is <c>ResolveSubject</c> relative to <paramref name="from"/>.
    /// </summary>
    public static IInterceptorSubject? ResolveScopedSubject(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from) =>
        resolver.ResolveScopedSubject(path, style, from, permissions: null);

    /// <summary>
    /// Resolves a subject from a path for a viewer: as <see cref="ResolveScopedSubject(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/>,
    /// but null unless the viewer may read every member the path reads through, see
    /// <see cref="SubjectPathResolver.ResolveSubject(string, PathStyle, IInterceptorSubject?, IUserPermissions?)"/>. A name
    /// the scope knows reads through the members that hold its subject below the scope.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">The path to resolve.</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="from">The subject the path is relative to, through its scope.</param>
    /// <param name="permissions">The permissions of the viewer, or null to check nothing.</param>
    public static IInterceptorSubject? ResolveScopedSubject(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from,
        IUserPermissions? permissions)
    {
        var scope = from.TryGetFirstParentOrSelf<IPathScope>();
        if (scope is not IInterceptorSubject scopeSubject)
        {
            return resolver.ResolveSubject(path, style, from, permissions);
        }

        if (string.IsNullOrEmpty(path))
        {
            return scopeSubject;
        }

        var hasPrefix = path.StartsWith('/')
            || path.StartsWith("./", StringComparison.Ordinal)
            || path.StartsWith("../", StringComparison.Ordinal)
            || path == "..";
        if (hasPrefix)
        {
            return resolver.ResolveSubject(path, style, scopeSubject, permissions);
        }

        var separator = path.IndexOf('/');
        var named = scope.TryGetNamedSubject(separator < 0 ? path : path[..separator]);
        if (named is null)
        {
            return resolver.ResolveSubject(path, style, scopeSubject, permissions);
        }

        if (permissions is not null && !IsHeldReadablyBelow(named, scopeSubject, permissions))
        {
            return null;
        }

        return separator < 0 ? named : resolver.ResolveSubject("./" + path[(separator + 1)..], style, named, permissions);
    }

    /// <summary>
    /// Resolves the property a path ends at. The last segment of the path is the property name,
    /// and the part before it is resolved by <see cref="ResolveScopedSubject(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/>.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveScopedProperty(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from) =>
        resolver.ResolveScopedProperty(path, style, from, permissions: null);

    private static RegisteredSubjectProperty? ResolveScopedProperty(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from,
        IUserPermissions? permissions)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            var scopeSubject = from.TryGetFirstParentOrSelf<IPathScope>() as IInterceptorSubject ?? from;
            return scopeSubject.TryGetRegisteredProperty(path);
        }

        var propertyName = path[(lastSlash + 1)..];
        if (propertyName.Length == 0)
        {
            return null;
        }

        var subject = resolver.ResolveScopedSubject(lastSlash == 0 ? "/" : path[..lastSlash], style, from, permissions);
        return subject?.TryGetRegisteredProperty(propertyName);
    }

    /// <summary>
    /// Resolves a property for showing its value, for example in a page expression or a value widget: the same as
    /// <see cref="ResolveScopedProperty(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/>, except that a property that is not
    /// <see cref="SubjectRegistryExtensions.IsDisplayable"/>, such as a secret configuration property, resolves to null,
    /// as if it did not exist.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveScopedDisplayableProperty(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        return resolver.ResolveScopedProperty(path, style, from).AsDisplayable();
    }

    /// <summary>
    /// Resolves a property for showing its value to a viewer: as
    /// <see cref="ResolveScopedDisplayableProperty(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/>, but
    /// null unless the viewer may read every member the path reads through to the subject of the property, see
    /// <see cref="ResolveScopedSubject(SubjectPathResolver, string, PathStyle, IInterceptorSubject, IUserPermissions?)"/>.
    /// The property itself is not checked; <see cref="ResolveShownProperty"/> checks it as well.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">The path of the property.</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="from">The subject the path is relative to, through its scope.</param>
    /// <param name="permissions">The permissions of the viewer, or null to check nothing.</param>
    public static RegisteredSubjectProperty? ResolveScopedDisplayableProperty(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from,
        IUserPermissions? permissions)
    {
        return resolver.ResolveScopedProperty(path, style, from, permissions).AsDisplayable();
    }

    /// <summary>
    /// Resolves the property a canonical path ends at for showing its value to a viewer, for example in a page expression
    /// or a value widget: the displayable property that
    /// <see cref="ResolveScopedDisplayableProperty(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/> finds,
    /// when the viewer may read every member the path reads through, see
    /// <see cref="ResolveScopedSubject(SubjectPathResolver, string, PathStyle, IInterceptorSubject, IUserPermissions?)"/>,
    /// and is shown its value, see <see cref="UserPermissionsExtensions.CanReadShown"/>; otherwise null. The one place
    /// where a path that shows a value to a viewer is checked.
    /// </summary>
    /// <param name="resolver">The path resolver.</param>
    /// <param name="path">The canonical path of the property.</param>
    /// <param name="from">The subject the path is relative to, through its scope.</param>
    /// <param name="permissions">The permissions of the viewer.</param>
    public static RegisteredSubjectProperty? ResolveShownProperty(
        this SubjectPathResolver resolver,
        string path,
        IInterceptorSubject from,
        IUserPermissions permissions)
    {
        var property = resolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, from, permissions);
        return property is not null && permissions.CanReadShown(property) ? property : null;
    }

    /// <summary>
    /// Resolves the value for display of the property that <see cref="ResolveScopedDisplayableProperty(SubjectPathResolver, string, PathStyle, IInterceptorSubject)"/> finds for the
    /// path, so the value of a property that is not displayable is never returned.
    /// </summary>
    public static object? ResolveScopedValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from) =>
        resolver.ResolveScopedDisplayableProperty(path, style, from)?.GetValue();

    // A name stands for a subject somewhere below the scope, so a path through it reads the members that hold the subject
    // on the way up to the scope. A subject held by more than one parent follows the first link the viewer may read.
    private static bool IsHeldReadablyBelow(IInterceptorSubject subject, IInterceptorSubject scope, IUserPermissions permissions)
    {
        var current = subject;
        for (var depth = 0; depth < MaximumNamedSubjectDepth; depth++)
        {
            if (ReferenceEquals(current, scope))
            {
                return true;
            }

            IInterceptorSubject? next = null;
            foreach (var parent in current.TryGetRegisteredSubject()?.Parents ?? [])
            {
                if (permissions.CanRead(parent.Property))
                {
                    next = parent.Property.Subject;
                    break;
                }
            }

            if (next is null)
            {
                return false;
            }

            current = next;
        }

        return false;
    }
}
