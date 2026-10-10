using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// Path resolution that starts at the nearest <see cref="IPathScope"/> of a subject.
/// </summary>
public static class SubjectPathResolverScopeExtensions
{
    /// <summary>
    /// Resolves a subject from a path. A relative path resolves from the scope of
    /// <paramref name="from"/>, and its first segment can be a name the scope knows.
    /// Without a scope this is <c>ResolveSubject</c> relative to <paramref name="from"/>.
    /// </summary>
    public static IInterceptorSubject? ResolveScopedSubject(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        var scope = FindScope(from);
        if (scope is not IInterceptorSubject scopeSubject)
        {
            return resolver.ResolveSubject(path, style, from);
        }

        if (string.IsNullOrEmpty(path))
        {
            return scopeSubject;
        }

        var hasPrefix = path.StartsWith('/') || path.StartsWith("./") || path.StartsWith("../") || path == "..";
        if (hasPrefix)
        {
            return resolver.ResolveSubject(path, style, scopeSubject);
        }

        var separator = path.IndexOf('/');
        var named = scope.TryGetNamedSubject(separator < 0 ? path : path[..separator]);
        if (named is null)
        {
            return resolver.ResolveSubject(path, style, scopeSubject);
        }

        return separator < 0 ? named : resolver.ResolveSubject("./" + path[(separator + 1)..], style, named);
    }

    /// <summary>
    /// Resolves a property value from a path whose last segment is the property name,
    /// with the subject part resolved by <see cref="ResolveScopedSubject"/>.
    /// </summary>
    public static object? ResolveScopedValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            var scopeSubject = FindScope(from) as IInterceptorSubject ?? from;
            return scopeSubject.TryGetRegisteredProperty(path)?.GetValue();
        }

        var propertyName = path[(lastSlash + 1)..];
        if (propertyName.Length == 0)
        {
            return null;
        }

        var subject = resolver.ResolveScopedSubject(lastSlash == 0 ? "/" : path[..lastSlash], style, from);
        return subject?.TryGetRegisteredProperty(propertyName)?.GetValue();
    }

    private static IPathScope? FindScope(IInterceptorSubject subject) =>
        subject as IPathScope ?? subject.TryGetFirstParent<IPathScope>();
}
