using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Randomly disrupts a participant's connector by killing or disconnecting it.
/// The connector's background loop auto-restarts after KillAsync; the SDK's
/// reconnection logic handles recovery after DisconnectAsync.
/// </summary>
public class ChaosEngine : BackgroundService
{
    private readonly string _targetName;
    private readonly ChaosConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private IChaosTarget? _target;
    private readonly ILogger _logger;
    private readonly Random _random = new();

    private volatile bool _isDisrupted;
    private readonly List<ChaosEventRecord> _eventHistory = [];
    private DateTimeOffset _currentEventStart;

    public string TargetName => _targetName;

    public long ChaosEventCount { get; private set; }

    public IReadOnlyList<ChaosEventRecord> EventHistory => _eventHistory;

    /// <summary>
    /// When false, the engine skips chaos actions but continues running.
    /// Controlled by VerificationEngine based on the active chaos profile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public ChaosEngine(
        string targetName,
        ChaosConfiguration configuration,
        TestCycleCoordinator coordinator,
        IChaosTarget? target,
        ILogger logger)
    {
        _targetName = targetName;
        _configuration = configuration;
        _coordinator = coordinator;
        _target = target;
        _logger = logger;
    }

    public void SetTarget(IChaosTarget target)
    {
        _target = target;
    }

    public void ResetCounters()
    {
        ChaosEventCount = 0;
        _eventHistory.Clear();
    }

    /// <summary>
    /// Marks active disruption as recovered. Called by verification engine before convergence check.
    /// </summary>
    public Task RecoverActiveDisruptionAsync(CancellationToken cancellationToken)
    {
        if (_isDisrupted)
        {
            _isDisrupted = false;
            _logger.LogInformation("Chaos: {Target} recovered from disruption", _targetName);
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChaosEngine [{Target}] started (mode={Mode})", _targetName, _configuration.Mode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);

                // Wait random interval before next chaos event
                var interval = RandomTimeSpan(_configuration.IntervalMin, _configuration.IntervalMax);
                await Task.Delay(interval, stoppingToken);

                _coordinator.WaitIfPaused(stoppingToken);

                if (!Enabled)
                {
                    continue;
                }

                if (_target == null)
                {
                    _logger.LogWarning("Chaos: skipped on {Target} - no target available", _targetName);
                    continue;
                }

                var action = PickAction();
                _logger.LogWarning("Chaos: {Action} on {Target}", action, _targetName);
                _currentEventStart = DateTimeOffset.UtcNow;
                _isDisrupted = true;

                switch (action)
                {
                    case "kill":
                        await _target.KillAsync();
                        break;
                    case "disconnect":
                        await _target.DisconnectAsync();
                        break;
                }

                // Hold disruption for random duration
                var duration = RandomTimeSpan(_configuration.DurationMin, _configuration.DurationMax);
                await Task.Delay(duration, stoppingToken);

                // Mark recovered
                _isDisrupted = false;
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
        return _configuration.Mode.ToLowerInvariant() switch
        {
            "kill" => "kill",
            "disconnect" => "disconnect",
            _ => _random.Next(2) == 0 ? "kill" : "disconnect"
        };
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
