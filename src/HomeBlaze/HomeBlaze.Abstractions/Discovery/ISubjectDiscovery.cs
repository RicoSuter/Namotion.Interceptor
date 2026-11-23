using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices of one subject type. Derive from <see cref="SubjectDiscovery{TSubject, TResult}"/> instead of
/// implementing this interface.
/// </summary>
public interface ISubjectDiscovery
{
    /// <summary>
    /// The type of subject that represents the devices this subject discovery finds.
    /// </summary>
    Type SubjectType { get; }

    /// <summary>
    /// Scans and yields each device once, as soon as it is known, with the subject of
    /// <paramref name="existingSubjects"/> that already represents it.
    /// </summary>
    IAsyncEnumerable<DiscoveredSubject> DiscoverAsync(
        IReadOnlyCollection<IInterceptorSubject> existingSubjects, CancellationToken cancellationToken);
}
