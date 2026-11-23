using System.Runtime.CompilerServices;
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices that a <typeparamref name="TSubject"/> represents. HomeBlaze finds every non-abstract derived type
/// among the loaded types and creates it through dependency injection.
/// </summary>
/// <remarks>
/// A subject discovery only reads the network and creates no subject. The create dialog passes the result the user
/// picks to the Setup component of <typeparamref name="TSubject"/>, or its Edit component when it has none, which must
/// implement <c>ISubjectDiscoveryComponent</c>; otherwise HomeBlaze does not use the subject discovery. One instance
/// serves overlapping scans, so it keeps no state between calls.
/// </remarks>
/// <typeparam name="TSubject">The type of subject that represents a found device.</typeparam>
/// <typeparam name="TResult">The type of result that describes a found device.</typeparam>
public abstract class SubjectDiscovery<TSubject, TResult> : ISubjectDiscovery
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult
{
    Type ISubjectDiscovery.SubjectType => typeof(TSubject);

    async IAsyncEnumerable<DiscoveredSubject> ISubjectDiscovery.DiscoverAsync(
        DiscoveryScan scan,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidates = existingSubjects.OfType<TSubject>().ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var result in DiscoverAsync(scan, cancellationToken))
        {
            // A device that answers on several adapters, or to several queries, is listed once.
            if (!keys.Add(result.Key))
            {
                continue;
            }

            TSubject? existingSubject = null;
            foreach (var candidate in candidates)
            {
                if (Matches(candidate, result))
                {
                    existingSubject = candidate;
                    break;
                }
            }

            yield return new DiscoveredSubject(typeof(TSubject), result, existingSubject);
        }
    }

    /// <summary>
    /// Scans and yields each device as soon as it is known. A device may be yielded more than once.
    /// </summary>
    /// <param name="scan">The scan, whose shared services answer each query once.</param>
    /// <param name="cancellationToken">Canceled when the scan is canceled or reaches its time limit.</param>
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="existing"/> already represents the device of <paramref name="result"/>.
    /// </summary>
    protected abstract bool Matches(TSubject existing, TResult result);
}
