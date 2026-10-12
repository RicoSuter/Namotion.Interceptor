using HomeBlaze.Abstractions;
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
    private const int MaximumSingleParentDepth = 64;

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

        var hasPrefix = path.StartsWith('/')
            || path.StartsWith("./", StringComparison.Ordinal)
            || path.StartsWith("../", StringComparison.Ordinal)
            || path == "..";
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
    /// Resolves the property a path ends at. The last segment of the path is the property name,
    /// and the part before it is resolved by <see cref="ResolveScopedSubject"/>.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveScopedProperty(
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
            return scopeSubject.TryGetRegisteredProperty(path);
        }

        var propertyName = path[(lastSlash + 1)..];
        if (propertyName.Length == 0)
        {
            return null;
        }

        var subject = resolver.ResolveScopedSubject(lastSlash == 0 ? "/" : path[..lastSlash], style, from);
        return subject?.TryGetRegisteredProperty(propertyName);
    }

    /// <summary>
    /// Resolves the value of the property that <see cref="ResolveScopedProperty"/> finds for the path.
    /// </summary>
    public static object? ResolveScopedValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from) =>
        resolver.ResolveScopedProperty(path, style, from)?.GetValue();

    private static IPathScope? FindScope(IInterceptorSubject subject)
    {
        // The usual chain has one parent per subject and is walked without an allocation. The search that
        // handles several parents and cycles allocates, so it only runs when the chain branches or is
        // longer than any real nesting.
        var current = subject;
        for (var depth = 0; depth < MaximumSingleParentDepth; depth++)
        {
            if (current is IPathScope scope)
            {
                return scope;
            }

            var parents = current.GetParents();
            if (parents.Length == 0)
            {
                return null;
            }

            if (parents.Length > 1)
            {
                break;
            }

            current = parents[0].Property.Subject;
        }

        return current as IPathScope ?? current.TryGetFirstParent<IPathScope>();
    }
}
