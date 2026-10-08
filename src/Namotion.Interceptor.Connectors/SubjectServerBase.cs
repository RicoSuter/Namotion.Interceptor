using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for a server, which exposes the local model to external clients. Owns the restart
/// loop and subscribes to property changes before each attempt accepts clients, so every change made
/// after a client's snapshot reaches that client.
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> is sealed. A derived server implements <see cref="CreateChangeQueueProcessor"/>
/// and <see cref="StartServerAsync"/>, and optionally <see cref="InitializeAsync"/>.
/// </remarks>
public abstract class SubjectServerBase : SubjectConnectorBase
{
    private const double MaximumRestartDelaySeconds = 30;
    private const double MaximumRestartJitterSeconds = 2;

    private readonly ILogger _logger;
    private int _consecutiveFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectServerBase"/> class.
    /// </summary>
    /// <param name="metrics">The metrics this server writes to.</param>
    /// <param name="logger">The logger for restart-loop failures.</param>
    protected SubjectServerBase(ConnectorMetrics metrics, ILogger logger)
        : base(metrics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the number of failed attempts since the last successful start.
    /// </summary>
    protected int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Sets up what lives across restarts. Called once per run, before the first attempt. Must not
    /// accept clients. A failure ends the connector and is not retried.
    /// </summary>
    /// <param name="stoppingToken">The connector's stopping token.</param>
    /// <returns>A teardown disposed after the last attempt, or <c>null</c> if there is nothing to release.</returns>
    protected virtual Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken) =>
        Task.FromResult<IAsyncDisposable?>(null);

    /// <summary>
    /// Creates the processor that publishes this server's outbound changes. Called at the start of each
    /// attempt, before <see cref="StartServerAsync"/>, and disposed when the attempt ends.
    /// </summary>
    /// <param name="dropHandler">The outbound drop reporter, to pass to the processor.</param>
    /// <returns>The processor, already subscribed to property changes.</returns>
    protected abstract ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler);

    /// <summary>
    /// Starts the protocol server for one attempt. Called once the change subscription exists, and
    /// returns once clients can connect. If it throws, it releases what it acquired before rethrowing.
    /// </summary>
    /// <param name="attempt">The attempt this start belongs to.</param>
    /// <returns>A teardown disposed when the attempt ends, or <c>null</c> if there is nothing to release.</returns>
    protected abstract Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt);

    /// <summary>
    /// Gets the delay before the next attempt after a failed one. The default grows exponentially from
    /// one second to a 30 second cap and adds up to two seconds of random jitter.
    /// </summary>
    /// <param name="consecutiveFailures">The number of failed attempts in a row, starting at 1.</param>
    /// <returns>The delay; zero restarts immediately.</returns>
    protected virtual TimeSpan GetRestartDelay(int consecutiveFailures)
    {
        var baseDelay = Math.Min(Math.Pow(2, consecutiveFailures - 1), MaximumRestartDelaySeconds);

        // Jitter keeps servers that failed together from restarting together.
        return TimeSpan.FromSeconds(baseDelay + Random.Shared.NextDouble() * MaximumRestartJitterSeconds);
    }

    /// <inheritdoc />
    protected sealed override async Task RunAsync(CancellationToken stoppingToken)
    {
        await using var initialization = await InitializeAsync(stoppingToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            var restartDelay = TimeSpan.Zero;
            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                restartDelay = await RunServerAttemptAsync(attempt, stoppingToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

            // After the attempt's teardown, so the port is free rather than held for the whole delay.
            if (restartDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(restartDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            else
            {
                // An attempt that fails synchronously would otherwise restart inline, and the first attempt
                // runs inside the host's StartAsync.
                await Task.Yield();
            }
        }
    }

    private async Task<TimeSpan> RunServerAttemptAsync(ConnectorRunAttempt attempt, CancellationToken stoppingToken)
    {
        try
        {
            using var changeQueueProcessor = CreateChangeQueueProcessor(Metrics.OutboundChanges.CreateDropReporter());

            // Declared after the processor so it is released first, which is what lets the next attempt
            // register its own: a second Register while one is still live throws.
            using var outboundRegistration = Metrics.OutboundChanges.Register(
                () => changeQueueProcessor.QueueDepth, capacity: null);

            IAsyncDisposable? serverTeardown = null;
            try
            {
                serverTeardown = await StartServerAsync(attempt).ConfigureAwait(false);
                Interlocked.Exchange(ref _consecutiveFailures, 0);

                // LastError is deliberately left in place: clearing it on recovery would erase the only
                // evidence of a transient fault.
                Metrics.MarkOperational();

                await changeQueueProcessor.ProcessAsync(attempt.Token).ConfigureAwait(false);
            }
            finally
            {
                Metrics.MarkNotOperational();
                if (serverTeardown is not null)
                {
                    await serverTeardown.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }
        catch (OperationCanceledException) when (attempt.WasForceKilled)
        {
            LogForceKill();
            return TimeSpan.Zero;
        }
        catch (Exception exception)
        {
            // A stop tears a server down with an arbitrary exception rather than a cancellation, so only
            // the stopping token tells a shutdown apart from a genuine fault.
            if (stoppingToken.IsCancellationRequested)
            {
                return TimeSpan.Zero;
            }

            return RecordFailure(exception);
        }

        // ProcessAsync returns rather than throws when its token is cancelled, so the tokens say why it ended.
        if (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }

        if (attempt.WasForceKilled)
        {
            LogForceKill();
            return TimeSpan.Zero;
        }

        return RecordFailure(new InvalidOperationException("Server processing completed unexpectedly."));
    }

    private void LogForceKill()
    {
        // Not reported as an error: an injected fault the server recovers from by restarting.
        _logger.LogWarning("Server {Server} force-killed. Restarting...", GetType().Name);
    }

    private TimeSpan RecordFailure(Exception exception)
    {
        var consecutiveFailures = Interlocked.Increment(ref _consecutiveFailures);

        // Nothing outside this loop reports its failures.
        Metrics.ReportError(exception);

        var restartDelay = GetRestartDelay(consecutiveFailures);
        _logger.LogError(exception,
            "Server {Server} failed (attempt {Attempt}). Restarting in {Delay}.",
            GetType().Name, consecutiveFailures, restartDelay);

        return restartDelay;
    }
}
