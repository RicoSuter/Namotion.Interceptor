namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices of one subject type. Deriving from <see cref="SubjectDiscovery{TSubject, TResult}"/> is recommended,
/// as it types the results and their subjects.
/// </summary>
public interface ISubjectDiscovery
{
    /// <summary>
    /// The type of subject that represents the devices this subject discovery finds.
    /// </summary>
    Type SubjectType { get; }

    /// <summary>
    /// Scans and yields each device as soon as it is known. A device may be yielded more than once, with the same
    /// <see cref="IDiscoveryResult.Key"/>.
    /// </summary>
    /// <param name="cancellationToken">Canceled when the scan is canceled or reaches its time limit.</param>
    IAsyncEnumerable<IDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}
