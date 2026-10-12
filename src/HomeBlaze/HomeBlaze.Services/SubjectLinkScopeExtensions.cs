using HomeBlaze.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Link resolution that starts at the nearest <see cref="ILinkScope"/> of a subject.
/// </summary>
public static class SubjectLinkScopeExtensions
{
    /// <summary>
    /// Resolves a link the way the link scope of the subject does, for example a page for a widget on it.
    /// Without a link scope the link is returned as written.
    /// </summary>
    public static string? ResolveScopedLink(this IInterceptorSubject subject, string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return link;
        }

        return SubjectScopeFinder.FindNearest<ILinkScope>(subject)?.ResolveLink(link) ?? link;
    }
}
