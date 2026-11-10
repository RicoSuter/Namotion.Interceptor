using Namotion.Interceptor;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Resolves subjects from paths and builds paths from subjects.
/// </summary>
public interface ISubjectPathResolver
{
    /// <summary>Gets the first path to the subject, or null if the subject has no path.</summary>
    string? GetPath(IInterceptorSubject subject, PathStyle style);

    /// <summary>
    /// Gets all paths to the subject, shallowest first and otherwise in the order of the parents: one for each parent,
    /// and so one for each key or position of a collection or dictionary that holds the subject.
    /// </summary>
    IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style);

    /// <summary>Resolves a subject from a path, or null if not found.</summary>
    IInterceptorSubject? ResolveSubject(string path, PathStyle style, IInterceptorSubject? relativeTo = null);
}
