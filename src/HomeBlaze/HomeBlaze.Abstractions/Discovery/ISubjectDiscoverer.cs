using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices of one subject type. Derive from <see cref="SubjectDiscoverer{TSubject, TResult}"/> instead of
/// implementing this interface.
/// </summary>
public interface ISubjectDiscoverer
{
    /// <summary>
    /// The type of subject that represents the devices this discoverer finds.
    /// </summary>
    Type SubjectType { get; }

    /// <summary>
    /// Scans and yields each device once, as soon as it is known, with the subject of
    /// <paramref name="existingSubjects"/> that already represents it.
    /// </summary>
    IAsyncEnumerable<DiscoveredSubject> DiscoverAsync(
        DiscoveryScan scan, IReadOnlyCollection<IInterceptorSubject> existingSubjects, CancellationToken cancellationToken);
}
