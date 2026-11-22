using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Runs the subject discoverers for the create dialog.
/// </summary>
public sealed class SubjectDiscoveryService
{
    /// <summary>
    /// The longest a scan runs. It ends earlier once every discoverer has completed.
    /// </summary>
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<ISubjectDiscoverer>> _getDiscoverers;
    private readonly ISsdpSearch _ssdpSearch;
    private readonly IMulticastDnsBrowser _multicastDnsBrowser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubjectDiscoveryService> _logger;

    public SubjectDiscoveryService(
        SubjectDiscovererRegistry registry,
        ISsdpSearch ssdpSearch,
        IMulticastDnsBrowser multicastDnsBrowser,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
        : this(registry.GetDiscoverers, ssdpSearch, multicastDnsBrowser, timeProvider, logger)
    {
    }

    internal SubjectDiscoveryService(
        Func<IReadOnlyList<ISubjectDiscoverer>> getDiscoverers,
        ISsdpSearch ssdpSearch,
        IMulticastDnsBrowser multicastDnsBrowser,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
    {
        _getDiscoverers = getDiscoverers;
        _ssdpSearch = ssdpSearch;
        _multicastDnsBrowser = multicastDnsBrowser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Whether a discoverer finds devices of one of <paramref name="subjectTypes"/>.
    /// </summary>
    public bool HasDiscoverers(IReadOnlyCollection<Type> subjectTypes) =>
        _getDiscoverers().Any(discoverer => subjectTypes.Contains(discoverer.SubjectType));

    /// <summary>
    /// Runs the discoverers of <paramref name="subjectTypes"/> in parallel and reports what they find, until all have
    /// completed or <see cref="ScanTimeout"/> has passed. A discoverer that throws is logged and reported to
    /// <paramref name="onFailure"/> with its subject type; the others carry on.
    /// </summary>
    /// <remarks>
    /// The callbacks run on thread pool threads, concurrently for different discoverers, and must not throw.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task ScanAsync(
        IReadOnlyCollection<Type> subjectTypes,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ScanTimeout, _timeProvider);
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var scan = new DiscoveryScan(_ssdpSearch, _multicastDnsBrowser, scanCancellation.Token);
        var discoverers = _getDiscoverers().Where(discoverer => subjectTypes.Contains(discoverer.SubjectType));
        try
        {
            await Task.WhenAll(discoverers.Select(discoverer => Task.Run(
                () => RunAsync(discoverer, scan, existingSubjects, onResult, onFailure, scanCancellation.Token),
                CancellationToken.None)));
        }
        finally
        {
            // Ends the queries the discoverers shared through the scan.
            await scanCancellation.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(
        ISubjectDiscoverer discoverer,
        DiscoveryScan scan,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var discovered in discoverer
                .DiscoverAsync(scan, existingSubjects, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                onResult(discovered);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceled by the caller or at the time limit, which is no failure of the discoverer.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The subject discoverer {Discoverer} failed.", discoverer.GetType());
            onFailure(discoverer.SubjectType);
        }
    }
}
