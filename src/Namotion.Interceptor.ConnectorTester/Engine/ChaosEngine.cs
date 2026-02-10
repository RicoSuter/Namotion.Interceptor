using Namotion.Interceptor.ConnectorTester.Chaos;
using Namotion.Interceptor.ConnectorTester.Configuration;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Randomly disrupts a participant's transport (TCP proxy) and/or connector lifecycle.
/// Tracks active disruption for forced recovery during convergence phase.
/// </summary>
public class ChaosEngine : BackgroundService
{
    private readonly string _targetName;
    private readonly ChaosConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly TcpProxy? _proxy;
    private IHostedService? _connectorService;
    private readonly ILogger _logger;
    private readonly Random _random = new();
    private readonly SemaphoreSlim _actionLock = new(1, 1);

    private string? _activeDisruption;
    private readonly List<ChaosEventRecord> _eventHistory = [];
    private DateTimeOffset _currentEventStart;

    public string TargetName => _targetName;

    public long ChaosEventCount { get; private set; }

    public IReadOnlyList<ChaosEventRecord> EventHistory => _eventHistory;

    public ChaosEngine(
        string targetName,
        ChaosConfiguration configuration,
        TestCycleCoordinator coordinator,
        TcpProxy? proxy,
        IHostedService? connectorService,
        ILogger logger)
    {
        _targetName = targetName;
        _configuration = configuration;
        _coordinator = coordinator;
        _proxy = proxy;
        _connectorService = connectorService;
        _logger = logger;
    }

    /// <summary>
    /// Sets the connector service for lifecycle chaos. Called after DI build
    /// when the service can be resolved from the container.
    /// </summary>
    public void SetConnectorService(IHostedService connectorService)
    {
        _connectorService = connectorService;
    }

    public void ResetCounters()
    {
        ChaosEventCount = 0;
        _eventHistory.Clear();
    }

    /// <summary>
    /// Recovers any active disruption. Called by verification engine before convergence check.
    /// Thread-safe: uses Interlocked.Exchange to prevent double recovery.
    /// </summary>
    public async Task RecoverActiveDisruptionAsync(CancellationToken cancellationToken)
    {
        var disruption = Interlocked.Exchange(ref _activeDisruption, null);
        if (disruption == null)
            return;

        _logger.LogInformation("Chaos: force-recovering {Disruption} on {Target}", disruption, _targetName);

        // Wait for any in-progress action (e.g. StopAsync) to finish before recovering.
        // Without this, StartAsync could race with a still-running StopAsync, and if StopAsync
        // finishes second, the server ends up stopped during convergence.
        await _actionLock.WaitAsync(cancellationToken);
        try
        {
            switch (disruption)
            {
                case "pause":
                    _proxy?.ResumeForwarding();
                    break;
                case "close":
                    // Connections already closed; nothing to recover - connector will reconnect
                    break;
                case "lifecycle":
                    if (_connectorService != null)
                    {
                        _logger.LogWarning("Chaos: restarting connector service on {Target}", _targetName);

                        // Use CancellationToken.None to avoid linking into BackgroundService's
                        // internal _stoppingCts. StartAsync returns immediately (it just kicks
                        // off ExecuteAsync), so no timeout is needed.
                        await _connectorService.StartAsync(CancellationToken.None);

                        _logger.LogWarning("Chaos: connector service restarted on {Target}", _targetName);
                    }
                    break;
            }
        }
        finally
        {
            _actionLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChaosEngine [{Target}] started in mode={Mode}",
            _targetName, _configuration.Mode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);

                // Wait random interval before next chaos event
                var interval = RandomTimeSpan(_configuration.IntervalMin, _configuration.IntervalMax);
                await Task.Delay(interval, stoppingToken);

                _coordinator.WaitIfPaused(stoppingToken);

                // Pick and execute chaos action
                var action = PickAction();
                _logger.LogWarning("Chaos: {Action} on {Target}", action, _targetName);

                // Acquire the lock before setting _activeDisruption so that
                // recovery cannot run before the disruptive action executes.
                await _actionLock.WaitAsync(stoppingToken);
                try
                {
                    _activeDisruption = action;
                    _currentEventStart = DateTimeOffset.UtcNow;
                    await ExecuteActionAsync(action, stoppingToken);
                }
                finally
                {
                    _actionLock.Release();
                }

                // Hold disruption for random duration
                var duration = RandomTimeSpan(_configuration.DurationMin, _configuration.DurationMax);
                await Task.Delay(duration, stoppingToken);

                // Recover
                await RecoverActiveDisruptionAsync(stoppingToken);
                var recoveredAt = DateTimeOffset.UtcNow;
                _eventHistory.Add(new ChaosEventRecord(action, _currentEventStart, recoveredAt));
                _logger.LogInformation("Chaos: {Target} recovered from {Action}", _targetName, action);
                ChaosEventCount++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private string PickAction()
    {
        var mode = _configuration.Mode.ToLowerInvariant();

        var actions = new List<string>();
        if (mode is "transport" or "both")
        {
            if (_proxy != null)
            {
                actions.Add("pause");
                actions.Add("close");
            }
        }
        if (mode is "lifecycle" or "both")
        {
            if (_connectorService != null)
            {
                actions.Add("lifecycle");
            }
        }

        if (actions.Count == 0)
        {
            return "noop";
        }

        return actions[_random.Next(actions.Count)];
    }

    private async Task ExecuteActionAsync(string action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "pause":
                _proxy!.PauseForwarding();
                break;
            case "close":
                _proxy!.CloseAllConnections();
                break;
            case "lifecycle":
                if (_connectorService != null)
                {
                    _logger.LogWarning("Chaos: stopping connector service {Type} on {Target}",
                        _connectorService.GetType().Name, _targetName);

                    // Await full completion — do NOT use a timeout that abandons StopAsync early.
                    // If StopAsync returns before the server fully shuts down, calling StartAsync
                    // creates a second concurrent server instance that fights for the TCP port.
                    await _connectorService.StopAsync(cancellationToken);

                    _logger.LogWarning("Chaos: connector service stopped on {Target}", _targetName);
                }
                else
                {
                    _logger.LogWarning("Chaos: lifecycle action skipped on {Target} - no connector service wired", _targetName);
                }
                break;
        }
    }

    private TimeSpan RandomTimeSpan(TimeSpan min, TimeSpan max)
    {
        var range = max - min;
        return min + TimeSpan.FromMilliseconds(_random.NextDouble() * range.TotalMilliseconds);
    }
}

public record ChaosEventRecord(string Action, DateTimeOffset DisruptedAt, DateTimeOffset RecoveredAt)
{
    public TimeSpan Duration => RecoveredAt - DisruptedAt;
}
