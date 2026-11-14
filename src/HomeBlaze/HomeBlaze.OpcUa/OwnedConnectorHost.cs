using HomeBlaze.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.OpcUa;

/// <summary>
/// The subject an <see cref="OwnedConnectorHost{TService}"/> runs a connector for: the state it reports, the
/// configuration it starts from and the instance it builds.
/// </summary>
/// <remarks>
/// Implement explicitly whatever the wrapper does not publish anyway, so owning a connector adds nothing to
/// what the subject shows a user. <see cref="Status"/>, <see cref="StatusMessage"/> and
/// <see cref="IsEnabled"/> are already the generated partial properties.
/// </remarks>
internal interface IConnectorOwner<TService> : IInterceptorSubject
    where TService : class, IHostedService
{
    ServiceStatus Status { get; set; }

    /// <summary>
    /// The text behind <see cref="ServiceStatus.Error"/>, or the connector's last error while it runs without
    /// being operational, and null otherwise.
    /// </summary>
    string? StatusMessage { get; set; }

    /// <summary>Whether a start is wanted, read when the wrapper starts and after a configuration edit.</summary>
    bool IsEnabled { get; }

    /// <summary>What the wrapper calls itself in its own log messages.</summary>
    string LogName { get; }

    /// <summary>The endpoint or path those messages name, so a host running several is readable.</summary>
    string LogTarget { get; }

    /// <summary>
    /// The message to report when the configuration cannot produce an instance, or null when it can.
    /// Read before anything the start path waits for, so an unconfigured wrapper reports at once rather
    /// than sitting in a wait whose result it cannot use.
    /// </summary>
    string? GetConfigurationError();

    /// <summary>
    /// Waits for whatever has to be in place before the instance is built, and returns false when the
    /// start must abandon, which the start then reports as Stopped.
    /// </summary>
    /// <remarks>
    /// Runs with the host's gate held, and the wrapper's own stop waits for that gate, so the wait has to
    /// observe the token, which the wrapper's stop cancels, and has to be bounded, because the Start
    /// operation passes no token of its own.
    /// </remarks>
    ValueTask<bool> WaitUntilStartableAsync(CancellationToken cancellationToken) => new(true);

    /// <summary>
    /// Builds a new instance. Invoked by every start, so it reads the configuration each time rather than
    /// capturing a snapshot.
    /// </summary>
    TService CreateInstance();

    /// <summary>Publishes the running instance's diagnostics.</summary>
    void ApplyDiagnostics(TService instance);

    /// <summary>Clears those diagnostics, which read null whenever nothing is running.</summary>
    void ResetDiagnostics();

    /// <summary>
    /// Drops what the instance published into the subject beyond its diagnostics, for a wrapper that
    /// publishes anything else. Called once nothing is running any more: after a stop and after a start
    /// that failed or was abandoned.
    /// </summary>
    void DropInstanceState()
    {
    }
}

/// <summary>
/// Runs the one connector a wrapper subject owns, for the duration of the wrapper's own run: it creates
/// and starts the instance when the wrapper starts enabled or is asked to start, and stops and disposes it
/// on a stop request, on a configuration edit and when the wrapper itself stops. It also serializes those
/// paths and the diagnostics poll, and reports the wrapper's status.
/// </summary>
internal sealed class OwnedConnectorHost<TService>
    where TService : class, IHostedService
{
    private const string NotRunningMessage = "Not running in a host, so nothing was started";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly IConnectorOwner<TService> _owner;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Serializes every path that starts, stops or reads the instance or writes the status, the wrapper's
    /// own stop included, so the status each one commits is never overwritten by one that started before
    /// it. Once the run is canceled every holder releases it in bounded time: each wait a start makes
    /// observes the run's token, and a stop waits only for the instance's own stop.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Canceled when the wrapper's run stops, which ends a start in flight and is what a start reads to
    /// tell whether the wrapper is running at all. Replaced under <see cref="_gate"/> when a run starts,
    /// and null before the first one.
    /// </summary>
    private CancellationTokenSource? _run;

    /// <summary>The running instance, or null. Written only under <see cref="_gate"/>.</summary>
    private TService? _instance;

    /// <remarks>
    /// <paramref name="pollInterval"/> is null for the default, and injectable because a suite that
    /// runs in seconds cannot observe the poll on the production interval.
    /// </remarks>
    public OwnedConnectorHost(IConnectorOwner<TService> owner, ILogger logger, TimeSpan? pollInterval = null)
    {
        _owner = owner;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>The running instance, or null. Test only.</summary>
    internal TService? Current => Volatile.Read(ref _instance);

    /// <summary>
    /// Begins the wrapper's run and starts the instance when the wrapper is enabled. Called from the
    /// wrapper's own StartAsync ahead of the base call, which only schedules the run loop, so a startup
    /// completion wait covers the instance's start. Never throws: every failure is reported as a status.
    /// </summary>
    public async Task StartRunAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            // A connector a start published after a stop past its deadline gave up on the gate.
            await StopInstanceAsync(CancellationToken.None);

            // Disposed here rather than in StopRunAsync, where a second stop's Cancel would throw.
            _run?.Dispose();
            Volatile.Write(ref _run, new CancellationTokenSource());

            if (_owner.IsEnabled)
            {
                await StartInstanceAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The wrapper's own background loop, which publishes the running instance's diagnostics.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TryApplyDiagnostics();
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The wrapper's own stop: ends the run, runs <paramref name="stopExecution"/>, the base stop that
    /// waits for the run loop, then stops and disposes the instance and reports the wrapper stopped.
    /// </summary>
    public async Task StopRunAsync(Func<CancellationToken, Task> stopExecution, CancellationToken cancellationToken)
    {
        // Before the gate, so a start holding it abandons its waits and releases it.
        Volatile.Read(ref _run)?.Cancel();
        await stopExecution(cancellationToken);

        // Here rather than at the end of the run loop: a stop that lands before the loop was scheduled
        // cancels it without running it.
        if (!await TryEnterGateAsync(cancellationToken))
        {
            // Past the caller's deadline the holder is left to finish on its own: a start ends without an
            // instance once it sees the run canceled, and a stop ends stopped.
            return;
        }

        try
        {
            await StopInstanceAsync(cancellationToken);
            _owner.DropInstanceState();
            ReportStopped();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a configuration edit by stopping the instance and starting a new one from the edited
    /// configuration.
    /// </summary>
    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);

        // Guarded here as in StartRunAsync rather than left to the caller: without it an edit that
        // disables the wrapper stops it and starts it again in the same call. An edit landing while the
        // wrapper itself stops leaves it stopped rather than reporting that nothing could start.
        if (_owner.IsEnabled && Volatile.Read(ref _run) is { IsCancellationRequested: false })
        {
            await StartAsync(cancellationToken);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await StartInstanceAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            if (_instance is not null)
            {
                CommitStatus(ServiceStatus.Stopping, message: null);
                await StopInstanceAsync(cancellationToken);
                _logger.LogInformation("{Service} stopped", _owner.LogName);
            }

            // Also with nothing running: a wrapper whose start failed sits at Error, and a stop is what
            // takes it out of there.
            _owner.DropInstanceState();
            ReportStopped();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates and starts an instance, unless one is running. Must be called with <see cref="_gate"/> held.</summary>
    private async Task StartInstanceAsync(CancellationToken cancellationToken)
    {
        if (_instance is not null)
        {
            return;
        }

        if (_run is not { IsCancellationRequested: false } run)
        {
            CommitStatus(ServiceStatus.Error, NotRunningMessage);
            _logger.LogWarning(
                "{Service} for {Target} was not started: the subject is not running in a host.",
                _owner.LogName, _owner.LogTarget);
            return;
        }

        CommitStatus(ServiceStatus.Starting, message: null);

        if (_owner.GetConfigurationError() is { } configurationError)
        {
            CommitStatus(ServiceStatus.Error, configurationError);
            return;
        }

        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
        var instance = default(TService);
        try
        {
            if (await _owner.WaitUntilStartableAsync(startCancellation.Token) && !startCancellation.IsCancellationRequested)
            {
                instance = _owner.CreateInstance();
                if (!startCancellation.IsCancellationRequested)
                {
                    // The run's token rather than the caller's: the instance links its own lifetime to it.
                    await instance.StartAsync(run.Token);
                }
            }
        }
        catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
        {
            // Abandoned below, like a start that sees the cancellation between two steps.
        }
        catch (Exception exception)
        {
            await AbandonStartAsync(instance, ServiceStatus.Error, exception.Message);
            _logger.LogError(exception, "Failed to start {Service} for {Target}", _owner.LogName, _owner.LogTarget);
            return;
        }

        if (instance is null || startCancellation.IsCancellationRequested)
        {
            // A start whose instance is published after the wrapper's stop began would outlive the run.
            await AbandonStartAsync(instance, ServiceStatus.Stopped, message: null);
            return;
        }

        Volatile.Write(ref _instance, instance);
        CommitStatus(ServiceStatus.Running, message: null);
        _owner.ApplyDiagnostics(instance);
        _logger.LogInformation("{Service} started for {Target}", _owner.LogName, _owner.LogTarget);
    }

    /// <summary>
    /// Stops and disposes an instance that was never published, drops what it published and reports
    /// <paramref name="status"/>.
    /// </summary>
    private async Task AbandonStartAsync(TService? instance, ServiceStatus status, string? message)
    {
        if (instance is not null)
        {
            await StopAndDisposeAsync(instance, CancellationToken.None);
        }

        _owner.DropInstanceState();
        CommitStatus(status, message);
        _owner.ResetDiagnostics();
    }

    /// <summary>Stops and disposes the running instance, if any. Must be called with <see cref="_gate"/> held.</summary>
    private async Task StopInstanceAsync(CancellationToken cancellationToken)
    {
        if (_instance is not { } instance)
        {
            return;
        }

        Volatile.Write(ref _instance, null);
        await StopAndDisposeAsync(instance, cancellationToken);
    }

    /// <summary>
    /// Stops and disposes <paramref name="instance"/>. A canceled token cuts the instance's own stop short;
    /// the dispose still runs. Never throws.
    /// </summary>
    private async Task StopAndDisposeAsync(TService instance, CancellationToken cancellationToken)
    {
        try
        {
            await instance.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's deadline, not a failure of the instance.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop {Service}", _owner.LogName);
        }

        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose {Service}", _owner.LogName);
        }
    }

    private void ReportStopped()
    {
        CommitStatus(ServiceStatus.Stopped, message: null);
        _owner.ResetDiagnostics();
    }

    /// <summary>
    /// Publishes the running instance's diagnostics from the run loop. Skips the round rather than
    /// waiting when a start or a stop holds the gate, so the poll never stands in the way of the stop that
    /// cancels it. Ungated, a round preempted after reading the instance would publish its numbers over
    /// the reset of a stop that completed meanwhile.
    /// </summary>
    private void TryApplyDiagnostics()
    {
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_instance is { } instance)
            {
                _owner.ApplyDiagnostics(instance);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CommitStatus(ServiceStatus status, string? message)
    {
        if (status is ServiceStatus.Error)
        {
            // Status first when moving to Error, message first when moving away from it, so an error's text
            // is never readable beside a status from before or after it.
            _owner.Status = status;
            _owner.StatusMessage = message;
        }
        else
        {
            _owner.StatusMessage = message;
            _owner.Status = status;
        }
    }

    /// <summary>
    /// Takes the gate. Returns false when the caller's token was canceled before it could, in which case
    /// the caller holds nothing, must not release it and must report nothing. A free gate is taken even
    /// with a canceled token, so a stop past its deadline still stops the instance when nothing is in the
    /// way.
    /// </summary>
    private async Task<bool> TryEnterGateAsync(CancellationToken cancellationToken)
    {
        if (_gate.Wait(0))
        {
            return true;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
