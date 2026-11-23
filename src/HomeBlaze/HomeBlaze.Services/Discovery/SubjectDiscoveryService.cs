using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Runs the subject discoveries for the create dialog.
/// </summary>
public sealed class SubjectDiscoveryService
{
    /// <summary>
    /// The longest a scan runs. It ends earlier once every subject discovery has completed.
    /// </summary>
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<ISubjectDiscovery>> _getSubjectDiscoveries;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubjectDiscoveryService> _logger;

    // Not disposed, so that a scan still running at shutdown can release it. It holds no wait handle to free.
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public SubjectDiscoveryService(
        SubjectDiscoveryRegistry registry,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
        : this(registry.GetSubjectDiscoveries, timeProvider, logger)
    {
    }

    internal SubjectDiscoveryService(
        Func<IReadOnlyList<ISubjectDiscovery>> getSubjectDiscoveries,
        TimeProvider timeProvider,
        ILogger<SubjectDiscoveryService> logger)
    {
        _getSubjectDiscoveries = getSubjectDiscoveries;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Whether a subject discovery finds devices of one of <paramref name="subjectTypes"/>.
    /// </summary>
    public bool HasSubjectDiscoveries(IReadOnlyCollection<Type> subjectTypes) =>
        _getSubjectDiscoveries().Any(discovery => subjectTypes.Contains(discovery.SubjectType));

    /// <summary>
    /// Runs the subject discoveries of <paramref name="subjectTypes"/> in parallel and reports what they find, until
    /// all have completed or <see cref="ScanTimeout"/> has passed. Each device is reported once per subject discovery,
    /// with the first of <paramref name="existingSubjects"/> that its result matches. A subject discovery that throws,
    /// or whose result throws from <see cref="IDiscoveryResult.Matches"/>, is logged and reported to
    /// <paramref name="onFailure"/> with its subject type; the others carry on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One scan runs at a time across the application: a scan that starts while another one runs waits until that one
    /// has returned, and <see cref="ScanTimeout"/> counts from when it runs. A scan canceled while it waits runs
    /// nothing.
    /// </para>
    /// <para>
    /// The callbacks run on thread pool threads, one at a time, and must neither block nor throw. None runs once the
    /// returned task has completed: a subject discovery still running then, because it ignores its cancellation, is
    /// abandoned and what it reports later is dropped.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task ScanAsync(
        IReadOnlyCollection<Type> subjectTypes,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        // Every open dialog asks the same network and cloud services, so parallel scans would only multiply the traffic.
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            // The lock can be granted at the moment the token is canceled.
            cancellationToken.ThrowIfCancellationRequested();
            await RunScanAsync(subjectTypes, existingSubjects, onResult, onFailure, cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task RunScanAsync(
        IReadOnlyCollection<Type> subjectTypes,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        Action<DiscoveredSubject> onResult,
        Action<Type> onFailure,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ScanTimeout, _timeProvider);
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // Read once: a subject discovery task can start after the source has been disposed.
        var scanToken = scanCancellation.Token;
        var reporter = new ScanReporter(onResult, onFailure);
        var discoveries = _getSubjectDiscoveries().Where(discovery => subjectTypes.Contains(discovery.SubjectType));
        try
        {
            // Bounded by the token as well, so a subject discovery that ignores it cannot hold the scan past its time limit.
            await Task.WhenAll(discoveries.Select(discovery => Task.Run(
                    () => RunAsync(discovery, existingSubjects, reporter, scanToken))))
                .WaitAsync(scanToken);
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            // The time limit is a normal end; a cancellation by the caller is thrown below.
        }
        finally
        {
            reporter.End();

            // Ends whatever a subject discovery started with the token and did not wait for.
            await scanCancellation.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunAsync(
        ISubjectDiscovery discovery,
        IReadOnlyCollection<IInterceptorSubject> existingSubjects,
        ScanReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            var subjectType = discovery.SubjectType;
            var candidates = existingSubjects.Where(subjectType.IsInstanceOfType).ToArray();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var result in discovery.DiscoverAsync(cancellationToken))
            {
                // A device that answers on several adapters, or to several queries, is listed once.
                if (!keys.Add(result.Key))
                {
                    continue;
                }

                // Outside the reporter's lock, as Matches is implementer code.
                var discovered = new DiscoveredSubject(subjectType, result, FindExistingSubject(candidates, result));
                if (!reporter.TryReportResult(discovered))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceled by the caller or at the time limit, which is no failure of the subject discovery.
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            // A library reacting to the cancellation, such as by disposing a socket, may throw something else.
            _logger.LogDebug(exception, "The subject discovery {Discovery} ended with an error after the scan ended.", discovery.GetType());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The subject discovery {Discovery} failed.", discovery.GetType());
            reporter.ReportFailure(discovery.SubjectType);
        }
    }

    private static IInterceptorSubject? FindExistingSubject(IInterceptorSubject[] candidates, IDiscoveryResult result)
    {
        foreach (var candidate in candidates)
        {
            if (result.Matches(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Passes reports to the callbacks of one scan until it has ended.
    /// </summary>
    private sealed class ScanReporter(Action<DiscoveredSubject> onResult, Action<Type> onFailure)
    {
        // Held while a callback runs, so that no callback is still running once End has returned.
        private readonly Lock _lock = new();
        private bool _isEnded;

        /// <returns>False when the scan has ended and the result was dropped.</returns>
        public bool TryReportResult(DiscoveredSubject discovered)
        {
            lock (_lock)
            {
                if (_isEnded)
                {
                    return false;
                }

                onResult(discovered);
                return true;
            }
        }

        public void ReportFailure(Type subjectType)
        {
            lock (_lock)
            {
                if (!_isEnded)
                {
                    onFailure(subjectType);
                }
            }
        }

        public void End()
        {
            lock (_lock)
            {
                _isEnded = true;
            }
        }
    }
}
