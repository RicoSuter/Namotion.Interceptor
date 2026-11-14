using Namotion.Interceptor;

namespace HomeBlaze.Abstractions;

/// <summary>
/// A subject that is the starting point for the relative paths of everything it contains,
/// and that knows some of the subjects inside it by name.
/// </summary>
public interface IPathScope
{
    /// <summary>
    /// Returns the subject with this name inside the scope, or null when there is none.
    /// </summary>
    IInterceptorSubject? TryGetNamedSubject(string name);
}
