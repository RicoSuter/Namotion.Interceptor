using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Finds devices that a <typeparamref name="TSubject"/> represents. HomeBlaze finds every non-abstract derived type
/// among the loaded types and creates it through dependency injection, so its single public constructor takes the
/// services it uses, such as <see cref="IMulticastDnsBrowser"/> or <see cref="ISsdpSearch"/>.
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
    where TResult : class, IDiscoveryResult<TSubject>
{
    Type ISubjectDiscovery.SubjectType => typeof(TSubject);

    IAsyncEnumerable<IDiscoveryResult> ISubjectDiscovery.DiscoverAsync(CancellationToken cancellationToken) =>
        DiscoverAsync(cancellationToken);

    /// <summary>
    /// Scans and yields each device as soon as it is known. A device may be yielded more than once.
    /// </summary>
    /// <param name="cancellationToken">Canceled when the scan is canceled or reaches its time limit.</param>
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(CancellationToken cancellationToken);
}
